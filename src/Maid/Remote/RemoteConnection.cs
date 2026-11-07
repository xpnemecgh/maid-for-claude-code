using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Maid.Remote;

/// <summary>
/// The paired phone, connected and authenticated: JSON text messages in both directions over the
/// WebSocket. <see cref="Send"/> and <see cref="Close"/> may be called from any thread.
/// </summary>
internal sealed class RemoteConnection
{
    /// <summary>A phone this far behind (a slow mobile connection) is dropped; it reconnects and gets the screen afresh.</summary>
    public const int MaxQueuedChars = 16 * 1024 * 1024;
    /// <summary>A dictation comes as one message: minutes of AAC speech, base64 (<see cref="Dashboard"/> bounds the recording).</summary>
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _abort = new();
    private long _queuedChars;
    private string _closeReason = "";

    public IPAddress Address { get; }
    /// <summary>The tailnet device; null for a loopback connection (tests).</summary>
    public TailscalePeer? Peer { get; }
    public string DeviceName { get; }
    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;

    /// <summary>How the connection is named in the log and on the page.</summary>
    public string Describe => Peer is { } peer ? $"{DeviceName} ({(peer.Name.Length > 0 ? peer.Name : Address.ToString())})" : $"{DeviceName} ({Address})";

    internal RemoteConnection(WebSocket socket, IPAddress address, TailscalePeer? peer, string deviceName)
    {
        _socket = socket;
        Address = address;
        Peer = peer;
        DeviceName = deviceName;
    }

    public void Send(string json)
    {
        if (_abort.IsCancellationRequested) return;
        if (Interlocked.Add(ref _queuedChars, json.Length) > MaxQueuedChars)
        {
            Abort();
            return;
        }
        _outgoing.Writer.TryWrite(json);
    }

    /// <summary>Sends what is queued, then closes the connection politely.</summary>
    public void Close(string reason = "")
    {
        if (reason.Length > 0 && _closeReason.Length == 0) _closeReason = reason.Length > 120 ? reason[..120] : reason;
        _outgoing.Writer.TryComplete();
        // A phone that does not answer the close is cut off.
        try { _abort.CancelAfter(TimeSpan.FromSeconds(3)); } catch (ObjectDisposedException) { }
    }

    /// <summary>Drops the connection at once.</summary>
    public void Abort()
    {
        _outgoing.Writer.TryComplete();
        try { _abort.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Runs until the connection is closed by either side; messages arrive on this task's threads.</summary>
    internal async Task RunAsync(Action<RemoteConnection, string> onMessage, CancellationToken serverStopping)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token, serverStopping);
        var sending = SendLoopAsync(stop.Token);
        try
        {
            await ReceiveLoopAsync(onMessage, stop.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            // The phone hung up (or broke off): the sender says goodbye if it still can, briefly.
            _outgoing.Writer.TryComplete();
            try { stop.CancelAfter(TimeSpan.FromSeconds(2)); } catch (ObjectDisposedException) { }
            try { await sending; }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
            _abort.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(Action<RemoteConnection, string> onMessage, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MaxMessageBytes)
            {
                Abort();
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            onMessage(this, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        await foreach (var json in _outgoing.Reader.ReadAllAsync(token))
        {
            Interlocked.Add(ref _queuedChars, -json.Length);
            await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token);
        }
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, _closeReason, token);
    }
}
