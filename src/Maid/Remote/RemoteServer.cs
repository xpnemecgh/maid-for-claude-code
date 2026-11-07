using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Maid.Remote;

/// <summary>
/// Maid's endpoint for the phone app. Every connection has to get past, in this order:
/// <list type="number">
/// <item>the address: a Tailscale address (100.64.0.0/10, fd7a:115c:a1e0::/48) — Maid listens on
/// nothing else — whose device Tailscale names as the paired phone (WireGuard ties the address to that
/// device's key), unless a pairing is on offer;</item>
/// <item>TLS with Maid's own certificate, which the app pins (it got the fingerprint from the QR code);</item>
/// <item>a WebSocket upgrade on <see cref="Path"/> without an Origin (no browser page);</item>
/// <item>the phone's signature over a fresh nonce and the certificate's fingerprint, made with the
/// key the phone generated when it was paired (kept in its hardware keystore, never exported) — or,
/// while a pairing is on offer, the one-time code from the QR code together with a new key.</item>
/// </list>
/// Only one phone is paired and connected at a time: a new pairing replaces the old phone, a new
/// connection the old one.
/// </summary>
internal sealed class RemoteServer : IDisposable
{
    public const int DefaultPort = 47800;
    public const string Path = "/maid";
    public const int ProtocolVersion = 1;
    private const string SignaturePrefix = "maid-remote/1";
    /// <summary>Connections still being checked (TLS, upgrade, signature) at once; more are turned away.</summary>
    private const int MaxPending = 4;
    /// <summary>After this many failed attempts within <see cref="FailureWindow"/>, nobody new gets in for that long.</summary>
    private const int MaxFailures = 8;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    private readonly X509Certificate2 _certificate;
    private readonly string _pin;
    private readonly IPeerDirectory _peers;
    private readonly bool _allowLoopback;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<IPEndPoint, TcpListener> _listeners = [];
    private readonly Queue<DateTime> _failures = new();
    private readonly object _lock = new();
    private volatile PairedPhone? _phone;
    private PairingOffer? _pairing;
    private RemoteConnection? _current;
    private int _pending;
    private DateTime _lockedOutUntil;

    /// <summary>Raised (on a background thread) once a connection is authenticated, after the previous one was closed.</summary>
    public Action<RemoteConnection>? Connected { get; init; }
    public Action<RemoteConnection, string>? Message { get; init; }
    public Action<RemoteConnection>? Closed { get; init; }
    /// <summary>A phone used the pairing code: it is the paired phone from now on (save it).</summary>
    public Action<PairedPhone>? Paired { get; init; }
    public Action<string>? Log { get; init; }

    public string Pin => _pin;

    public RemoteServer(X509Certificate2 certificate, IPeerDirectory peers, bool allowLoopback)
    {
        _certificate = certificate;
        _pin = ServerCertificate.PinOf(certificate);
        _peers = peers;
        _allowLoopback = allowLoopback;
    }

    private sealed record PairingOffer(string Code, DateTime ExpiresUtc);

    /// <summary>The phone that may connect (null: none). Taking it away also drops its connection.</summary>
    public void SetPhone(PairedPhone? phone)
    {
        _phone = phone;
        if (phone is null)
        {
            RemoteConnection? current;
            lock (_lock) current = _current;
            current?.Close("Maid no longer knows this phone.");
        }
    }

    /// <summary>Lets one phone pair with <paramref name="code"/> until <paramref name="expiresUtc"/>.</summary>
    public void OfferPairing(string code, DateTime expiresUtc)
    {
        lock (_lock) _pairing = new PairingOffer(code, expiresUtc);
    }

    public void CancelPairing()
    {
        lock (_lock) _pairing = null;
    }

    /// <summary>Where it listens now.</summary>
    public IReadOnlyList<IPEndPoint> Endpoints
    {
        get { lock (_lock) return _listeners.Values.Select(listener => (IPEndPoint)listener.LocalEndpoint).ToList(); }
    }

    /// <summary>
    /// Listens on exactly these addresses (others are let go of); port 0 picks a free one per address
    /// (tests). Returns the problems, one per address that could not be bound.
    /// </summary>
    public List<string> Listen(IEnumerable<IPAddress> addresses, int port)
    {
        var problems = new List<string>();
        var wanted = addresses.Distinct().ToList();
        lock (_lock)
        {
            if (_stopping.IsCancellationRequested) return problems;
            foreach (var (endpoint, listener) in _listeners.ToList())
            {
                if (wanted.Contains(endpoint.Address) && (port == 0 || endpoint.Port == port)) continue;
                listener.Stop();
                _listeners.Remove(endpoint);
            }
            foreach (var address in wanted)
            {
                if (_listeners.Keys.Any(endpoint => endpoint.Address.Equals(address))) continue;
                var listener = new TcpListener(address, port);
                try
                {
                    if (address.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = false;
                    listener.Start(8);
                }
                catch (SocketException e)
                {
                    problems.Add($"{address}:{port}: {e.Message}");
                    continue;
                }
                var bound = (IPEndPoint)listener.LocalEndpoint;
                _listeners[bound] = listener;
                _ = AcceptLoopAsync(listener);
            }
        }
        return problems;
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                return; // stopped
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var address = client.Client.RemoteEndPoint is IPEndPoint endpoint
                ? (endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address)
                : IPAddress.None;
            if (!Admit(address)) return;
            var slot = new PendingSlot(this);
            try
            {
                await HandleAdmittedAsync(client, address, slot);
            }
            catch (Exception e) when (e is IOException or AuthenticationException or WebSocketException or OperationCanceledException
                                          or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (!_stopping.IsCancellationRequested) Failed($"Connection from {address} broke off: {e.Message}");
            }
            finally
            {
                slot.Release();
            }
        }
    }

    /// <summary>A connection's place among those still being checked; given back once (when it is in, or gone).</summary>
    private sealed class PendingSlot(RemoteServer server)
    {
        private int _released;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (server._lock) server._pending--;
        }
    }

    /// <summary>Whether a new connection may even start (not too many at once, not after many failures).</summary>
    private bool Admit(IPAddress address)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow < _lockedOutUntil) return false;
            if (_pending >= MaxPending) return false;
            _pending++;
            return true;
        }
    }

    private async Task HandleAdmittedAsync(TcpClient client, IPAddress address, PendingSlot slot)
    {
        // 1. Who is it? Only the paired phone, by its Tailscale identity (or anyone while pairing).
        TailscalePeer? peer = null;
        bool loopback = IPAddress.IsLoopback(address);
        if (loopback)
        {
            if (!_allowLoopback)
            {
                Failed($"Refused {address}: connections from this PC itself are not accepted.");
                return;
            }
        }
        else
        {
            if (TailscaleCli.IsTailnetAddress(address)) peer = await Task.Run(() => _peers.WhoIs(address));
            if (RefusePeer(address, peer) is { } reason)
            {
                Failed($"Refused {Who(peer, address)}: {reason}.");
                return;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        timeout.CancelAfter(HandshakeTimeout);
        client.NoDelay = true;

        // 2. TLS with the pinned certificate.
        await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, timeout.Token);

        // 3. The WebSocket upgrade.
        var head = await ReadRequestHeadAsync(tls, timeout.Token);
        var upgrade = CheckUpgrade(head);
        if (upgrade.Problem is not null)
        {
            await WriteAsync(tls, $"HTTP/1.1 {upgrade.Status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n", timeout.Token);
            Failed($"Refused {Who(peer, address)}: {upgrade.Problem}.");
            return;
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(upgrade.Key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await WriteAsync(tls, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                              $"Sec-WebSocket-Accept: {accept}\r\n" +
                              (upgrade.DeflateAnswer is { } answer ? $"Sec-WebSocket-Extensions: {answer}\r\n" : "") + "\r\n", timeout.Token);
        using var socket = WebSocket.CreateFromStream(tls, new WebSocketCreationOptions
        {
            IsServer = true,
            // Pings the phone and gives up on it when it does not answer: a phone that went out of
            // reach hands its session's size back to the PC within a minute.
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            KeepAliveTimeout = TimeSpan.FromSeconds(25),
            DangerousDeflateOptions = upgrade.Deflate,
        });

        // 4. The phone proves it holds its key (or pairs with the code and a new one).
        var nonce = ServerCertificate.Base64Url(RandomNumberGenerator.GetBytes(32));
        await SendAsync(socket, new { type = "hello", v = ProtocolVersion, nonce, host = Environment.MachineName }, timeout.Token);
        var first = await ReceiveAsync(socket, 16 * 1024, timeout.Token);
        if (first is null) return;
        var (device, paired, refusal) = Authenticate(first, nonce, peer, loopback);
        if (refusal is not null)
        {
            await SendAsync(socket, new { type = "denied", reason = refusal.Value.Code, message = refusal.Value.Text }, timeout.Token);
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, refusal.Value.Code, timeout.Token);
            Failed($"Refused {Who(peer, address)}: {refusal.Value.Text}");
            return;
        }

        var connection = new RemoteConnection(socket, address, peer, device);
        RemoteConnection? previous;
        lock (_lock)
        {
            previous = _current;
            _current = connection;
        }
        slot.Release(); // an authenticated phone does not hold up others
        previous?.Close("Connected again from elsewhere.");
        if (paired is not null)
        {
            Log?.Invoke($"Paired with {connection.Describe}.");
            Paired?.Invoke(paired);
        }
        Log?.Invoke($"{connection.Describe} connected.");
        Connected?.Invoke(connection);
        try
        {
            await connection.RunAsync((from, json) => Message?.Invoke(from, json), _stopping.Token);
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_current, connection)) _current = null;
            }
            Log?.Invoke($"{connection.Describe} disconnected.");
            Closed?.Invoke(connection);
        }
    }

    /// <summary>
    /// Why a connection from <paramref name="address"/> (not this PC), whose tailnet device is
    /// <paramref name="peer"/>, is not even let start; null when it may go on to prove itself.
    /// </summary>
    internal string? RefusePeer(IPAddress address, TailscalePeer? peer)
    {
        if (!TailscaleCli.IsTailnetAddress(address)) return "not a Tailscale address";
        if (peer is null) return "Tailscale does not know that device";
        if (PairingOpen()) return null; // the phone to be paired could be any device of the tailnet
        var phone = _phone;
        if (phone is null) return "no phone is paired";
        return phone.NodeId == peer.NodeId ? null : "not the paired phone";
    }

    private bool PairingOpen()
    {
        lock (_lock) return _pairing is { } offer && offer.ExpiresUtc > DateTime.UtcNow;
    }

    private readonly record struct Refusal(string Code, string Text);

    /// <summary>Checks the phone's first message: a signed <c>auth</c>, or a <c>pair</c> with the code and a new key.</summary>
    private (string Device, PairedPhone? Paired, Refusal? Refusal) Authenticate(string json, string nonce, TailscalePeer? peer, bool loopback)
    {
        string type, name, signature, key = "", code = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            type = Text(root, "type");
            name = CleanName(Text(root, "name"));
            signature = Text(root, "sig");
            if (type == "pair")
            {
                key = Text(root, "key");
                code = Text(root, "code");
            }
        }
        catch (JsonException)
        {
            return ("", null, new Refusal("protocol", "the first message was not understood."));
        }
        var payload = Encoding.UTF8.GetBytes($"{SignaturePrefix}|{type}|{nonce}|{_pin}");

        if (type == "auth")
        {
            var phone = _phone;
            if (phone is null) return (name, null, new Refusal("not-paired", "no phone is paired with Maid."));
            if (!loopback && phone.NodeId != peer?.NodeId) return (name, null, new Refusal("other-device", "not the paired phone."));
            if (!Verify(phone.PublicKey, payload, signature)) return (name, null, new Refusal("bad-signature", "the phone's key does not match the paired one."));
            return (name.Length > 0 ? name : phone.Name, null, null);
        }
        if (type == "pair")
        {
            PairingOffer? offer;
            lock (_lock) offer = _pairing;
            if (offer is null || offer.ExpiresUtc <= DateTime.UtcNow)
                return (name, null, new Refusal("no-pairing", "no pairing is on offer (Maid → Phone → Pair shows a new code)."));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(offer.Code)))
                return (name, null, new Refusal("wrong-code", "the pairing code is not the one Maid shows."));
            if (!Verify(key, payload, signature)) return (name, null, new Refusal("bad-signature", "the new key's signature does not check out."));
            lock (_lock)
            {
                // One code, one phone.
                if (!ReferenceEquals(_pairing, offer)) return (name, null, new Refusal("no-pairing", "the pairing code was used already."));
                _pairing = null;
            }
            var paired = new PairedPhone
            {
                Name = name.Length > 0 ? name : "Phone",
                PublicKey = key,
                NodeId = peer?.NodeId ?? "",
                NodeName = peer?.Name ?? "",
                PairedUtc = DateTime.UtcNow,
            };
            _phone = paired;
            return (paired.Name, paired, null);
        }
        return (name, null, new Refusal("protocol", "the phone did not sign in."));
    }

    /// <summary>An ECDSA P-256 signature (DER, as Android makes them) over <paramref name="payload"/>.</summary>
    internal static bool Verify(string publicKey, byte[] payload, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (key.KeySize != 256) return false;
            return key.VerifyData(payload, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    internal static string SignedText(string type, string nonce, string pin) => $"{SignaturePrefix}|{type}|{nonce}|{pin}";

    private static string CleanName(string raw)
    {
        var name = new string(raw.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return name.Length > 60 ? name[..60] : name;
    }

    private void Failed(string message)
    {
        Log?.Invoke(message);
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _failures.Enqueue(now);
            while (_failures.Count > 0 && now - _failures.Peek() > FailureWindow) _failures.Dequeue();
            if (_failures.Count < MaxFailures || now < _lockedOutUntil) return;
            _lockedOutUntil = now + FailureWindow;
            _failures.Clear();
        }
        Log?.Invoke($"Too many failed attempts: no new connections for {FailureWindow.TotalMinutes:0} minutes.");
    }

    private static string Who(TailscalePeer? peer, IPAddress address) => peer is { Name.Length: > 0 } ? $"{peer.Name} ({address})" : address.ToString();

    // ---- HTTP upgrade ---------------------------------------------------------------------------------

    private readonly record struct Upgrade(string? Problem, string Status, string Key, WebSocketDeflateOptions? Deflate, string? DeflateAnswer);

    private static async Task<string> ReadRequestHeadAsync(Stream stream, CancellationToken token)
    {
        // Byte by byte: nothing after the head may be taken from the stream (the client waits for the 101 anyway).
        var head = new StringBuilder();
        var one = new byte[1];
        while (head.Length < 8192)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("closed during the request");
            head.Append((char)one[0]);
            if (head.Length >= 4 && head[^1] == '\n' && head[^2] == '\r' && head[^3] == '\n' && head[^4] == '\r') return head.ToString();
        }
        throw new IOException("request head too long");
    }

    private static Upgrade CheckUpgrade(string head)
    {
        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || !request[2].StartsWith("HTTP/1.1", StringComparison.Ordinal))
            return new Upgrade("not a WebSocket request", "400 Bad Request", "", null, null);
        if (request[1] != Path) return new Upgrade($"asked for {request[1]}", "404 Not Found", "", null, null);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var earlier) ? $"{earlier}, {value}" : value;
        }
        // A web page can open WebSockets anywhere; the app never sends an Origin.
        if (headers.ContainsKey("Origin")) return new Upgrade("a browser page", "403 Forbidden", "", null, null);
        if (!headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Connection", out var connection) || !connection.Contains("upgrade", StringComparison.OrdinalIgnoreCase)
            || headers.GetValueOrDefault("Sec-WebSocket-Version") != "13"
            || !headers.TryGetValue("Sec-WebSocket-Key", out var key) || key.Length is < 16 or > 64)
            return new Upgrade("not a WebSocket request", "400 Bad Request", "", null, null);
        var deflate = NegotiateDeflate(headers.GetValueOrDefault("Sec-WebSocket-Extensions"), out var answer);
        return new Upgrade(null, "101", key, deflate, answer);
    }

    /// <summary>
    /// Terminal output compresses well, which matters on a mobile connection: takes the client's first
    /// permessage-deflate offer that .NET can serve, and answers it (null: no compression).
    /// </summary>
    internal static WebSocketDeflateOptions? NegotiateDeflate(string? header, out string? answer)
    {
        answer = null;
        if (string.IsNullOrWhiteSpace(header)) return null;
        foreach (var offer in header.Split(','))
        {
            var parts = offer.Split(';').Select(part => part.Trim()).ToArray();
            if (!parts[0].Equals("permessage-deflate", StringComparison.OrdinalIgnoreCase)) continue;
            var options = new WebSocketDeflateOptions();
            var answers = new List<string> { "permessage-deflate" };
            bool usable = true;
            foreach (var parameter in parts.Skip(1))
            {
                var equals = parameter.IndexOf('=');
                var name = (equals < 0 ? parameter : parameter[..equals]).Trim().ToLowerInvariant();
                var value = equals < 0 ? null : parameter[(equals + 1)..].Trim().Trim('"');
                int bits = 0;
                bool validBits = value is not null && int.TryParse(value, out bits) && bits is >= 9 and <= 15;
                switch (name)
                {
                    case "server_no_context_takeover":
                        options.ServerContextTakeover = false;
                        answers.Add(name);
                        break;
                    case "client_no_context_takeover":
                        options.ClientContextTakeover = false;
                        answers.Add(name);
                        break;
                    case "server_max_window_bits" when validBits:
                        options.ServerMaxWindowBits = bits;
                        answers.Add($"{name}={bits}");
                        break;
                    case "client_max_window_bits" when value is null:
                        break; // the client could take a limit; it gets none
                    case "client_max_window_bits" when validBits:
                        options.ClientMaxWindowBits = bits;
                        answers.Add($"{name}={bits}");
                        break;
                    default:
                        usable = false;
                        break;
                }
            }
            if (!usable) continue;
            answer = string.Join("; ", answers);
            return options;
        }
        return null;
    }

    private static Task WriteAsync(Stream stream, string text, CancellationToken token) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();

    private static Task SendAsync(WebSocket socket, object message, CancellationToken token) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, token);

    private static async Task<string?> ReceiveAsync(WebSocket socket, int maxBytes, CancellationToken token)
    {
        var buffer = new byte[maxBytes];
        int length = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), token);
            if (result.MessageType != WebSocketMessageType.Text) return null;
            length += result.Count;
            if (result.EndOfMessage) return Encoding.UTF8.GetString(buffer, 0, length);
            if (length >= maxBytes) return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public void Dispose()
    {
        RemoteConnection? current;
        lock (_lock)
        {
            if (_stopping.IsCancellationRequested) return;
            _stopping.Cancel();
            foreach (var listener in _listeners.Values) listener.Stop();
            _listeners.Clear();
            current = _current;
            _current = null;
        }
        current?.Close("Maid closed.");
    }
}
