using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Maid.Remote;

/// <summary>
/// A way in for programs on this PC: a local named pipe that only the user's own programs can open, and, when
/// Maid runs as administrator, only those that run as administrator too: the same programs that could already
/// type into Maid's elevated window. Remote clients are refused by the pipe itself and by its access list.
/// Nothing here touches the phone's way in. Messages are JSON, one per line, both ways (<see cref="Dashboard"/>
/// reads and writes them). Two of them: the voice pipe (push-to-talk at the PC, <see cref="VoiceName"/>) and
/// the API pipe (<c>maidctl</c> and anything else that controls Maid, <see cref="ApiName"/>).
/// </summary>
internal sealed class LocalPipeServer : IDisposable
{
    public const string VoiceName = "Maid.Voice";
    public const string ApiName = "Maid.Api";

    private readonly string _name;
    private readonly string _label;
    private readonly int _maxClients;
    private readonly int _maxLineBytes;
    private readonly string _securityDescriptor;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly HashSet<LocalPipeClient> _clients = [];
    private int _lastNumber;

    /// <summary>A client connected (on a pool thread).</summary>
    public Action<LocalPipeClient>? Connected { get; init; }
    /// <summary>A line arrived from a client (on a pool thread).</summary>
    public Action<LocalPipeClient, string>? Message { get; init; }
    /// <summary>A client went away (on a pool thread).</summary>
    public Action<LocalPipeClient>? Closed { get; init; }
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Takes the pipe's name; throws when another program (or another Maid) already has it. <paramref name="label"/>
    /// names the pipe in messages ("voice pipe"); a line longer than <paramref name="maxLineBytes"/> drops its client.
    /// </summary>
    /// <exception cref="IOException">The name is taken or the pipe could not be made.</exception>
    public LocalPipeServer(string name, bool elevated, string label = "voice pipe", int maxClients = 4, int maxLineBytes = 64 * 1024)
    {
        _name = name;
        _label = label;
        _maxClients = maxClients;
        _maxLineBytes = maxLineBytes;
        _securityDescriptor = SecurityDescriptor(WindowsIdentity.GetCurrent().User!.Value, elevated);
        // The first instance is made here, with FIRST_PIPE_INSTANCE: if anybody already holds the name, we
        // learn it now instead of sharing it with them.
        _first = Create(first: true);
    }

    private NamedPipeServerStream? _first;

    public string Name => _name;

    /// <summary>Starts taking connections (after the callbacks are in place).</summary>
    public void Start()
    {
        var first = Interlocked.Exchange(ref _first, null);
        if (first is not null) _ = Task.Run(() => AcceptLoopAsync(first));
    }

    /// <summary>
    /// Nobody from the network (NU denied, besides PIPE_REJECT_REMOTE_CLIENTS), full access for the user and
    /// nobody else; an elevated Maid labels the pipe High so a program of the user's that runs without
    /// administrator rights can't write to it (no write up).
    /// </summary>
    internal static string SecurityDescriptor(string userSid, bool elevated) =>
        $"D:P(D;;GA;;;NU)(A;;GA;;;{userSid})" + (elevated ? "S:(ML;;NW;;;HI)" : "");

    private NamedPipeServerStream Create(bool first)
    {
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(_securityDescriptor, 1, out var descriptor, out _))
            throw new IOException($"The {_label}'s security could not be set up (error {Marshal.GetLastWin32Error()}).");
        try
        {
            var attributes = new Native.SecurityAttributes
            {
                Length = Marshal.SizeOf<Native.SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };
            var openMode = Native.PipeAccessDuplex | Native.FileFlagOverlapped | (first ? Native.FileFlagFirstPipeInstance : 0);
            var pipeMode = Native.PipeTypeByte | Native.PipeReadModeByte | Native.PipeWait | Native.PipeRejectRemoteClients;
            var handle = Native.CreateNamedPipeW(@"\\.\pipe\" + _name, openMode, pipeMode, (uint)_maxClients + 1, 64 * 1024, 64 * 1024, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(error switch
                {
                    5 when first => $"Another program already uses the pipe \"{_name}\".",
                    231 => $"All of the {_label}'s connections are in use.",
                    _ => $"The {_label} \"{_name}\" could not be made (error {error}).",
                });
            }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            Native.LocalFree(descriptor);
        }
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream? next)
    {
        while (!_stop.IsCancellationRequested)
        {
            if (next is null)
            {
                try
                {
                    next = Create(first: false);
                }
                catch (IOException e)
                {
                    Log?.Invoke(e.Message);
                    try { await Task.Delay(1000, _stop.Token); } catch (OperationCanceledException) { return; }
                    continue;
                }
            }
            var pipe = next;
            next = null;
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                await pipe.DisposeAsync();
                continue;
            }
            LocalPipeClient client;
            lock (_gate)
            {
                if (_stop.IsCancellationRequested || _clients.Count >= _maxClients)
                {
                    pipe.Dispose();
                    continue;
                }
                client = new LocalPipeClient(++_lastNumber, pipe);
                _clients.Add(client);
            }
            Connected?.Invoke(client);
            _ = Task.Run(() => ReadLoopAsync(client));
        }
        next?.Dispose();
    }

    private async Task ReadLoopAsync(LocalPipeClient client)
    {
        var buffer = new byte[4096];
        var line = new MemoryStream();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await client.Pipe.ReadAsync(buffer, _stop.Token);
                if (read == 0) break;
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).Trim();
                    line.SetLength(0);
                    if (text.Length > 0) Message?.Invoke(client, text);
                }
                line.Write(buffer, start, read - start);
                if (line.Length > _maxLineBytes)
                {
                    Log?.Invoke($"Client {client.Number} of the {_label} sent a line that is too long; disconnected.");
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_gate) _clients.Remove(client);
            client.Close();
            Closed?.Invoke(client);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Interlocked.Exchange(ref _first, null)?.Dispose();
        List<LocalPipeClient> clients;
        lock (_gate) clients = [.. _clients];
        foreach (var client in clients) client.Close();
    }

    private static class Native
    {
        public const uint PipeAccessDuplex = 0x3;
        public const uint FileFlagOverlapped = 0x40000000;
        public const uint FileFlagFirstPipeInstance = 0x00080000;
        public const uint PipeTypeByte = 0x0;
        public const uint PipeReadModeByte = 0x0;
        public const uint PipeWait = 0x0;
        public const uint PipeRejectRemoteClients = 0x8;

        [StructLayout(LayoutKind.Sequential)]
        public struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
            uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr memory);
    }
}

/// <summary>One program connected to a local pipe (a push-to-talk program, maidctl). Sending never blocks the caller.</summary>
internal sealed class LocalPipeClient(int number, NamedPipeServerStream pipe)
{
    private readonly SemaphoreSlim _writing = new(1, 1);
    private volatile bool _closed;

    /// <summary>Tells clients apart in Maid's own bookkeeping (refs from different clients may be the same).</summary>
    public int Number { get; } = number;
    internal NamedPipeServerStream Pipe { get; } = pipe;
    public bool IsClosed => _closed;

    /// <summary>Writes one JSON message and a newline, after any still being written; a broken pipe just closes it.</summary>
    public void Send(string json)
    {
        if (_closed) return;
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        _ = Task.Run(async () =>
        {
            await _writing.WaitAsync();
            try
            {
                if (_closed) return;
                await Pipe.WriteAsync(bytes);
                await Pipe.FlushAsync();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Close();
            }
            finally
            {
                _writing.Release();
            }
        });
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { Pipe.Dispose(); } catch (IOException) { }
    }
}
