using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Maid.Terminal;

/// <summary>
/// Hears Claude Code's hooks, which tell for certain what a session is doing: a prompt was submitted
/// (working), the turn ended (waiting for the user, with the answer's text), it asks the user something.
/// Each Claude Code session gets them as HTTP hooks through <c>--settings</c> (<see cref="SettingsJson"/>).
/// Listens on the loopback address only, each session has an unguessable path of its own, and a hook can
/// only say how that session is doing: it never makes Maid do anything.
/// </summary>
internal sealed class HookServer : IDisposable
{
    /// <summary>The events Maid asks for. PreToolUse/PostToolUse only for the tools that wait for the user.</summary>
    public static readonly string[] Events = ["UserPromptSubmit", "Stop", "StopFailure", "Notification", "PreToolUse", "PostToolUse"];
    public const string AskingTools = "AskUserQuestion|ExitPlanMode";
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly Action<string, string, JsonElement> _received;
    private volatile bool _disposed;

    /// <summary><paramref name="received"/> gets (token, event, body) on a background thread.</summary>
    public HookServer(Action<string, string, JsonElement> received)
    {
        _received = received;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    /// <summary>A fresh, unguessable token: the session's own path on this server.</summary>
    public static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>The settings Claude Code gets with <c>--settings</c>: the hooks, pointed at this server.</summary>
    public static string SettingsJson(int port, string token)
    {
        object Hook(string name) => new { type = "http", url = $"http://127.0.0.1:{port}/hook/{token}/{name}", timeout = 5 };
        var hooks = new Dictionary<string, object>();
        foreach (var name in Events)
        {
            hooks[name] = name is "PreToolUse" or "PostToolUse"
                ? new object[] { new { matcher = AskingTools, hooks = new[] { Hook(name) } } }
                : new object[] { new { hooks = new[] { Hook(name) } } };
        }
        return JsonSerializer.Serialize(new { hooks }, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task AcceptLoop()
    {
        while (!_disposed)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (Exception) when (_disposed) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var request = await ReadRequest(stream, timeout.Token);
                var status = request is { Method: "POST" } && Route(request.Path) is { } route ? 200 : 404;
                var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(reply, timeout.Token);
                if (status != 200 || request is null) return;
                // Answered first: the hook holds Claude Code up until it has its reply.
                var (token, name) = Route(request.Path)!.Value;
                JsonElement body;
                try { using var document = JsonDocument.Parse(request.Body); body = document.RootElement.Clone(); }
                catch (JsonException) { return; }
                if (body.ValueKind == JsonValueKind.Object) _received(token, name, body);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    /// <summary>/hook/&lt;token&gt;/&lt;event&gt; → (token, event); anything else is null.</summary>
    internal static (string Token, string Event)? Route(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "hook" || parts[1].Length != 32 || !parts[1].All(char.IsAsciiHexDigitLower)) return null;
        return Events.Contains(parts[2]) ? (parts[1], parts[2]) : null;
    }

    internal sealed record Request(string Method, string Path, byte[] Body);

    /// <summary>Just enough HTTP/1.1 for a hook: a request line, headers with Content-Length, a body.</summary>
    internal static async Task<Request?> ReadRequest(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[MaxHeaderBytes];
        int filled = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (filled == buffer.Length) return null;
            int read = await stream.ReadAsync(buffer.AsMemory(filled), cancel);
            if (read == 0) return null;
            filled += read;
            headerEnd = IndexOf(buffer, filled, "\r\n\r\n"u8);
        }
        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var requestLine = head[0].Split(' ');
        if (requestLine.Length != 3) return null;
        long length = 0;
        foreach (var line in head.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && !long.TryParse(line[(colon + 1)..].Trim(), out length)) return null;
        }
        if (length < 0 || length > MaxBodyBytes) return null;
        var body = new byte[length];
        int already = Math.Min(filled - (headerEnd + 4), (int)length);
        Array.Copy(buffer, headerEnd + 4, body, 0, already);
        if (already < length) await stream.ReadExactlyAsync(body.AsMemory(already), cancel);
        return new Request(requestLine[0], requestLine[1], body);
    }

    private static int IndexOf(byte[] buffer, int count, ReadOnlySpan<byte> pattern) => buffer.AsSpan(0, count).IndexOf(pattern);

    public void Dispose()
    {
        _disposed = true;
        _listener.Stop();
    }
}
