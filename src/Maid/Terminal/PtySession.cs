using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using static Maid.Terminal.NativeMethods;

namespace Maid.Terminal;

/// <summary>
/// A process running inside a Windows pseudo console (ConPTY). The terminal side of the console is
/// exposed as a stream of VT text (<c>onOutput</c>) and <see cref="Write"/>, which is exactly what
/// xterm.js on the UI side speaks.
/// </summary>
internal sealed class PtySession
{
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(3);

    private readonly object _ptyLock = new();
    private IntPtr _hPC;
    private readonly IntPtr _hProcess;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Action<string> _onOutput;
    private readonly Action<int> _onExited;
    private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ManualResetEvent _killRequested = new(false);
    private readonly Thread _reader;

    public int ProcessId { get; }

    private PtySession(IntPtr hPC, IntPtr hProcess, int processId, SafeFileHandle inputWrite, SafeFileHandle outputRead,
        Action<string> onOutput, Action<int> onExited)
    {
        _hPC = hPC;
        _hProcess = hProcess;
        ProcessId = processId;
        _onOutput = onOutput;
        _onExited = onExited;
        _input = new FileStream(inputWrite, FileAccess.Write, bufferSize: 0, isAsync: false);
        _output = new FileStream(outputRead, FileAccess.Read, bufferSize: 0, isAsync: false);

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"pty-read-{processId}" };
        _reader.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = $"pty-wait-{processId}" }.Start();
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// Starts <paramref name="commandLine"/> attached to a new pseudo console of the given size.
    /// <paramref name="onOutput"/> is raised on a background thread; <paramref name="onExited"/> is raised
    /// once, after all output has been delivered.
    /// </summary>
    public static PtySession Start(string commandLine, string workingDirectory, IReadOnlyDictionary<string, string> environment,
        int cols, int rows, Action<string> onOutput, Action<int> onExited)
    {
        SafeFileHandle? inRead = null, inWrite = null, outRead = null, outWrite = null;
        IntPtr hPC = 0, attributeList = 0, commandLinePtr = 0, environmentPtr = 0;
        bool attributeListInitialized = false;
        try
        {
            if (!CreatePipe(out inRead, out inWrite, 0, 0) || !CreatePipe(out outRead, out outWrite, 0, 0))
                throw new Win32Exception();

            int hr = CreatePseudoConsole(ToCoord(cols, rows), inRead, outWrite, 0, out hPC);
            if (hr != 0)
                throw new InvalidOperationException($"CreatePseudoConsole failed (0x{hr:X8}).", Marshal.GetExceptionForHR(hr));

            IntPtr size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                throw new Win32Exception();
            attributeListInitialized = true;
            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, IntPtr.Size, 0, 0))
                throw new Win32Exception();

            var startupInfo = new STARTUPINFOEXW();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            // Without this the child inherits our std handles whenever they happen to be redirected
            // (e.g. Maid started from a script), bypassing the pseudo console entirely.
            startupInfo.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startupInfo.lpAttributeList = attributeList;

            commandLinePtr = Marshal.StringToHGlobalUni(commandLine);
            environmentPtr = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(environment));

            if (!CreateProcessW(null, commandLinePtr, 0, 0, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    environmentPtr, workingDirectory, ref startupInfo, out var processInfo))
                throw new Win32Exception();

            CloseHandle(processInfo.hThread);

            var session = new PtySession(hPC, processInfo.hProcess, processInfo.dwProcessId, inWrite, outRead, onOutput, onExited);
            hPC = 0;
            inWrite = null;
            outRead = null;
            return session;
        }
        finally
        {
            // The console host holds its own duplicates of the client-side pipe ends; ours must be closed
            // or the reader would never observe end-of-stream.
            inRead?.Dispose();
            outWrite?.Dispose();
            inWrite?.Dispose();
            outRead?.Dispose();
            if (hPC != 0) ClosePseudoConsole(hPC);
            if (attributeListInitialized) DeleteProcThreadAttributeList(attributeList);
            if (attributeList != 0) Marshal.FreeHGlobal(attributeList);
            if (commandLinePtr != 0) Marshal.FreeHGlobal(commandLinePtr);
            if (environmentPtr != 0) Marshal.FreeHGlobal(environmentPtr);
        }
    }

    public void Write(string data)
    {
        if (data.Length > 0)
            _writes.Writer.TryWrite(Encoding.UTF8.GetBytes(data));
    }

    public void Resize(int cols, int rows)
    {
        lock (_ptyLock)
        {
            if (_hPC != 0)
                ResizePseudoConsole(_hPC, ToCoord(cols, rows));
        }
    }

    /// <summary>
    /// Closes the console the way closing a terminal window would (clients get CTRL_CLOSE_EVENT) and
    /// terminates the process if it ignores that. Completion is reported through <c>onExited</c>.
    /// </summary>
    public void Kill() => _killRequested.Set();

    private void ClosePty()
    {
        IntPtr handle;
        lock (_ptyLock)
        {
            handle = _hPC;
            _hPC = 0;
        }
        // Outside the lock: on older Windows builds this blocks until the console host has drained.
        if (handle != 0)
            ClosePseudoConsole(handle);
    }

    private void ReadLoop()
    {
        var bytes = new byte[32 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        // Stateful decoder: multi-byte sequences are routinely split across reads.
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (true)
            {
                int read = _output.Read(bytes, 0, bytes.Length);
                if (read <= 0) break;
                int count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count > 0)
                    _onOutput(new string(chars, 0, count));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var chunk in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                _input.Write(chunk, 0, chunk.Length);
                _input.Flush();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void WaitLoop()
    {
        using var process = new ProcessWaitHandle(_hProcess);
        if (WaitHandle.WaitAny([process, _killRequested]) == 1)
        {
            ClosePty();
            if (!process.WaitOne(GracefulCloseTimeout))
            {
                TerminateProcess(_hProcess, 1);
                process.WaitOne(GracefulCloseTimeout);
            }
        }

        GetExitCodeProcess(_hProcess, out uint exitCode);

        // The console host only flushes its final frame and closes the output pipe once the pseudo
        // console is closed; the reader has to keep draining while that happens.
        ClosePty();
        _reader.Join(GracefulCloseTimeout);

        _writes.Writer.TryComplete();
        _input.Dispose();
        _output.Dispose();
        CloseHandle(_hProcess);

        _onExited(unchecked((int)exitCode));
    }

    private static COORD ToCoord(int cols, int rows) => new()
    {
        X = (short)Math.Clamp(cols, 2, 1000),
        Y = (short)Math.Clamp(rows, 1, 500),
    };

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(name).Append('=').Append(value).Append('\0');
        block.Append('\0');
        return block.ToString();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr processHandle) =>
            SafeWaitHandle = new SafeWaitHandle(processHandle, ownsHandle: false);
    }
}
