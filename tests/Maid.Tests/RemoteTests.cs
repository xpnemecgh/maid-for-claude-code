using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Maid.Remote;

namespace Maid.Tests;

/// <summary>Tailscale stand-in: this PC has no tailnet address, and every device is the one named here.</summary>
internal sealed class FakePeers(string nodeId = "phone-node") : IPeerDirectory
{
    public TailscaleSelf? Self(out string? error)
    {
        error = "Tailscale is not running (test)";
        return null;
    }

    public TailscalePeer? WhoIs(IPAddress address) => new(nodeId, "phone.tailnet.ts.net", "phone", "android");
}

/// <summary>The phone app's side of the protocol, as far as the tests need it.</summary>
internal static class PhoneProtocol
{
    public static async Task<ClientWebSocket> OpenAsync(int port, string pin, bool deflate = false, string? origin = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is X509Certificate2 presented && ServerCertificate.PinOf(presented) == pin;
        if (deflate) socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{port}{RemoteServer.Path}"), timeout.Token);
        return socket;
    }

    public static Task SendAsync(ClientWebSocket socket, object message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next text message (null once the server closed).</summary>
    public static async Task<JsonElement?> ReceiveAsync(ClientWebSocket socket, int seconds = 15)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonDocument.Parse(message.ToArray()).RootElement.Clone();
        }
    }

    /// <summary>Messages until one of <paramref name="type"/> comes (the others are dropped).</summary>
    public static async Task<JsonElement> ReceiveUntilAsync(ClientWebSocket socket, string type, Func<JsonElement, bool>? where = null)
    {
        while (true)
        {
            var message = await ReceiveAsync(socket) ?? throw new InvalidOperationException($"closed while waiting for {type}");
            if (message.GetProperty("type").GetString() == type && (where is null || where(message))) return message;
        }
    }

    public static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    public static string Sign(ECDsa key, string type, string nonce, string pin) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(RemoteServer.SignedText(type, nonce, pin)),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    /// <summary>Opens a connection and pairs (with <paramref name="code"/>) or signs in; returns the server's answer.</summary>
    public static async Task<(ClientWebSocket Socket, JsonElement? Answer)> SignInAsync(int port, string pin, ECDsa key, string? code = null, bool deflate = false)
    {
        var socket = await OpenAsync(port, pin, deflate);
        var hello = await ReceiveAsync(socket) ?? throw new InvalidOperationException("no hello");
        Assert.Equal("hello", hello.GetProperty("type").GetString());
        var nonce = hello.GetProperty("nonce").GetString()!;
        if (code is null)
            await SendAsync(socket, new { type = "auth", name = "Test phone", sig = Sign(key, "auth", nonce, pin) });
        else
            await SendAsync(socket, new { type = "pair", name = "Test phone", code, key = PublicKey(key), sig = Sign(key, "pair", nonce, pin) });
        return (socket, await ReceiveAsync(socket));
    }
}

public sealed class RemoteServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly List<RemoteConnection> _connected = [];
    private readonly List<(RemoteConnection Connection, string Json)> _messages = [];
    private readonly RemoteServer _server;
    private readonly int _port;
    private PairedPhone? _paired;

    public RemoteServerTests()
    {
        Directory.CreateDirectory(_root);
        _server = new RemoteServer(ServerCertificate.LoadOrCreate(_root), new FakePeers(), allowLoopback: true)
        {
            Connected = connection =>
            {
                lock (_connected) _connected.Add(connection);
                connection.Send("""{"type":"init"}""");
            },
            Message = (connection, json) => { lock (_messages) _messages.Add((connection, json)); },
            Paired = phone => _paired = phone,
            Log = text => { lock (_log) _log.Add(text); },
        };
        Assert.Empty(_server.Listen([IPAddress.Loopback], 0));
        _port = _server.Endpoints.Single().Port;
    }

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static string Type(JsonElement? message) => message?.GetProperty("type").GetString() ?? "(closed)";

    [Fact]
    public async Task Pairing_with_the_code_makes_the_phone_the_paired_one_and_lets_it_in()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _server.OfferPairing("secret-code", DateTime.UtcNow.AddMinutes(5));

        var (socket, answer) = await PhoneProtocol.SignInAsync(_port, _server.Pin, key, code: "secret-code");

        using (socket)
        {
            Assert.Equal("init", Type(answer));
            Assert.NotNull(_paired);
            Assert.Equal(PhoneProtocol.PublicKey(key), _paired!.PublicKey);
            Assert.Equal("Test phone", _paired.Name);
            Assert.Single(_connected);
        }
    }

    [Fact]
    public async Task The_paired_phone_signs_in_with_its_key_and_its_messages_arrive()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _server.SetPhone(new PairedPhone { Name = "Phone", PublicKey = PhoneProtocol.PublicKey(key), NodeId = "phone-node" });

        var (socket, answer) = await PhoneProtocol.SignInAsync(_port, _server.Pin, key, deflate: true);

        using (socket)
        {
            Assert.Equal("init", Type(answer));
            await PhoneProtocol.SendAsync(socket, new { type = "view", id = "s1" });
            for (int i = 0; i < 200 && _messages.Count == 0; i++) await Task.Delay(20);
            Assert.Contains("\"view\"", _messages.Single().Json);
        }
    }

    [Fact]
    public async Task Another_key_is_refused()
    {
        using var paired = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _server.SetPhone(new PairedPhone { Name = "Phone", PublicKey = PhoneProtocol.PublicKey(paired), NodeId = "phone-node" });

        var (socket, answer) = await PhoneProtocol.SignInAsync(_port, _server.Pin, other);

        using (socket)
        {
            Assert.Equal("denied", Type(answer));
            Assert.Equal("bad-signature", answer!.Value.GetProperty("reason").GetString());
            Assert.Empty(_connected);
        }
    }

    [Fact]
    public async Task Nothing_signs_in_while_no_phone_is_paired()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var (socket, answer) = await PhoneProtocol.SignInAsync(_port, _server.Pin, key);

        using (socket) Assert.Equal("not-paired", answer!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_wrong_pairing_code_is_refused_and_the_right_one_works_only_once()
    {
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _server.OfferPairing("the-code", DateTime.UtcNow.AddMinutes(5));

        var (wrong, wrongAnswer) = await PhoneProtocol.SignInAsync(_port, _server.Pin, first, code: "not-the-code");
        var (right, rightAnswer) = await PhoneProtocol.SignInAsync(_port, _server.Pin, first, code: "the-code");
        var (again, againAnswer) = await PhoneProtocol.SignInAsync(_port, _server.Pin, second, code: "the-code");

        using (wrong) using (right) using (again)
        {
            Assert.Equal("wrong-code", wrongAnswer!.Value.GetProperty("reason").GetString());
            Assert.Equal("init", Type(rightAnswer));
            Assert.Equal("no-pairing", againAnswer!.Value.GetProperty("reason").GetString());
            Assert.Equal(PhoneProtocol.PublicKey(first), _paired!.PublicKey);
        }
    }

    [Fact]
    public async Task An_expired_pairing_code_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _server.OfferPairing("old-code", DateTime.UtcNow.AddSeconds(-1));

        var (socket, answer) = await PhoneProtocol.SignInAsync(_port, _server.Pin, key, code: "old-code");

        using (socket) Assert.Equal("no-pairing", answer!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_new_connection_of_the_phone_replaces_the_old_one()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _server.SetPhone(new PairedPhone { Name = "Phone", PublicKey = PhoneProtocol.PublicKey(key), NodeId = "phone-node" });

        var (old, _) = await PhoneProtocol.SignInAsync(_port, _server.Pin, key);
        var (current, answer) = await PhoneProtocol.SignInAsync(_port, _server.Pin, key);

        using (old) using (current)
        {
            Assert.Equal("init", Type(answer));
            Assert.Null(await PhoneProtocol.ReceiveAsync(old)); // closed by the server
        }
    }

    [Fact]
    public async Task Forgetting_the_phone_drops_its_connection()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _server.SetPhone(new PairedPhone { Name = "Phone", PublicKey = PhoneProtocol.PublicKey(key), NodeId = "phone-node" });
        var (socket, _) = await PhoneProtocol.SignInAsync(_port, _server.Pin, key);

        _server.SetPhone(null);

        using (socket) Assert.Null(await PhoneProtocol.ReceiveAsync(socket));
    }

    [Fact]
    public async Task A_web_page_cannot_connect()
    {
        await Assert.ThrowsAsync<WebSocketException>(() => PhoneProtocol.OpenAsync(_port, _server.Pin, origin: "https://example.com"));
    }

    [Fact]
    public async Task The_phone_does_not_talk_to_a_server_with_another_certificate()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => PhoneProtocol.OpenAsync(_port, "some-other-pin"));
    }

    [Fact]
    public async Task Connections_from_this_PC_are_refused_unless_tests_allow_them()
    {
        using var server = new RemoteServer(ServerCertificate.LoadOrCreate(_root), new FakePeers(), allowLoopback: false);
        server.Listen([IPAddress.Loopback], 0);

        await Assert.ThrowsAnyAsync<Exception>(() => PhoneProtocol.OpenAsync(server.Endpoints.Single().Port, server.Pin));
    }

    [Fact]
    public void Only_the_paired_phone_gets_past_the_address_check_unless_a_pairing_is_on_offer()
    {
        var phone = new TailscalePeer("phone-node", "phone.ts.net", "phone", "android");
        var laptop = new TailscalePeer("laptop-node", "laptop.ts.net", "laptop", "windows");
        var tailnet = IPAddress.Parse("100.101.102.103");

        Assert.Equal("no phone is paired", _server.RefusePeer(tailnet, phone));
        _server.SetPhone(new PairedPhone { Name = "Phone", PublicKey = "x", NodeId = "phone-node" });
        Assert.Null(_server.RefusePeer(tailnet, phone));
        Assert.Equal("not the paired phone", _server.RefusePeer(tailnet, laptop));
        Assert.Equal("Tailscale does not know that device", _server.RefusePeer(tailnet, null));
        Assert.Equal("not a Tailscale address", _server.RefusePeer(IPAddress.Parse("192.168.1.20"), phone));
        _server.OfferPairing("code", DateTime.UtcNow.AddMinutes(1));
        Assert.Null(_server.RefusePeer(tailnet, laptop));
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("fd7a:115c:a1e0::6b01:c4dc", true)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    [InlineData("::ffff:100.65.12.34", true)]
    public void Tailnet_addresses_are_recognised(string address, bool tailnet) =>
        Assert.Equal(tailnet, TailscaleCli.IsTailnetAddress(IPAddress.Parse(address)));

    [Fact]
    public void Compression_is_agreed_to_as_offered_or_not_at_all()
    {
        Assert.NotNull(RemoteServer.NegotiateDeflate("permessage-deflate", out var plain));
        Assert.Equal("permessage-deflate", plain);

        var limited = RemoteServer.NegotiateDeflate("permessage-deflate; client_max_window_bits; server_max_window_bits=12; server_no_context_takeover", out var answer);
        Assert.Equal("permessage-deflate; server_max_window_bits=12; server_no_context_takeover", answer);
        Assert.Equal(12, limited!.ServerMaxWindowBits);
        Assert.False(limited.ServerContextTakeover);

        Assert.Null(RemoteServer.NegotiateDeflate("permessage-deflate; server_max_window_bits=8", out _));
        Assert.Null(RemoteServer.NegotiateDeflate("x-webkit-deflate-frame", out _));
        Assert.Null(RemoteServer.NegotiateDeflate(null, out _));
        Assert.NotNull(RemoteServer.NegotiateDeflate("permessage-deflate; unknown=1, permessage-deflate", out var second));
        Assert.Equal("permessage-deflate", second);
    }
}

public sealed class TerminalReplayTests
{
    [Fact]
    public void Only_the_output_since_the_last_size_change_is_kept_with_that_size()
    {
        var replay = new TerminalReplay(80, 24);
        replay.Append("one");
        replay.Resize(40, 20);
        replay.Append("two");
        replay.Resize(40, 20); // the same size: nothing changes
        replay.Append("three");

        var pieces = JsonSerializer.SerializeToElement(replay.Snapshot());
        Assert.Equal(1, pieces.GetArrayLength());
        Assert.Equal(40, pieces[0].GetProperty("cols").GetInt32());
        Assert.Equal(20, pieces[0].GetProperty("rows").GetInt32());
        Assert.Equal("twothree", pieces[0].GetProperty("data").GetString());
    }

    [Fact]
    public void The_oldest_output_goes_first()
    {
        var replay = new TerminalReplay(80, 24, maxChars: 10);
        replay.Append("aaaaaa");
        replay.Append("bbbbbbbb");

        var pieces = JsonSerializer.SerializeToElement(replay.Snapshot());
        Assert.Equal("aabbbbbbbb", pieces[0].GetProperty("data").GetString());
    }
}

/// <summary>The phone and the page together, around a real console session (cmd.exe).</summary>
public sealed class DashboardRemoteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<JsonElement> _posted = [];
    private readonly StateStore _store;
    private readonly Dashboard _dashboard;

    public DashboardRemoteTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "project"));
        _store = new StateStore(Path.Combine(_root, "data"));
        _store.Load();
        _store.State.Settings.Command = "cmd.exe /k echo hello from the session";
        _store.State.Settings.SaveHistory = false;
        _store.State.Remote = new RemoteSettings { Enabled = true, Port = 0, AllowLoopback = true };
        _store.State.Entries.Add(new Entry { Id = "e1", Path = Path.Combine(_root, "project"), Name = "Project" });
        Grouping.Reconcile(_store.State);
        _dashboard = new Dashboard(_store, Dispatcher.CurrentDispatcher, json => _posted.Add(JsonDocument.Parse(json).RootElement.Clone()))
        {
            PeerDirectory = new FakePeers(),
            // Speech to text, without ElevenLabs: says what it got, or refuses a recording that says "fail".
            Transcribe = (audio, mime, _) => Encoding.UTF8.GetString(audio) == "fail"
                ? Task.FromException<string>(new TranscriptionException("ElevenLabs refused (test)."))
                : Task.FromResult($"Heard {audio.Length} bytes of {mime}"),
        };
    }

    public void Dispose()
    {
        _dashboard.Shutdown();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Send(object message) => _dashboard.HandleMessage(JsonSerializer.Serialize(message));

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Await(Task task) => Await(task.ContinueWith(done => { done.GetAwaiter().GetResult(); return 0; }));

    /// <summary>Keeps the dispatcher going (the server reports through it) until the task is done.</summary>
    private static T Await<T>(Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            Pump();
            Thread.Sleep(5);
        }
        return task.GetAwaiter().GetResult();
    }

    private JsonElement WaitForPage(string type, Func<JsonElement, bool>? where = null, int after = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var found = _posted.Skip(after).FirstOrDefault(message => message.GetProperty("type").GetString() == type && (where is null || where(message)));
            if (found.ValueKind != JsonValueKind.Undefined) return found;
            Pump();
            Thread.Sleep(5);
        }
        throw new TimeoutException(type);
    }

    [Fact]
    public void The_phone_pairs_watches_a_session_types_into_it_and_hands_its_size_back()
    {
        _dashboard.StartRemote();
        WaitForPage("remoteStatus", message => message.GetProperty("listening").GetArrayLength() > 0);
        Send(new { type = "launch", id = "t1", entry = "e1", cols = 100, rows = 30 });
        WaitForPage("output", message => message.GetProperty("data").GetString()!.Contains("hello from the session"));

        // Pairing: the page shows the code, the phone uses it.
        Send(new { type = "remoteSetup", op = "pair" });
        var status = WaitForPage("remoteStatus", message => message.GetProperty("pairing").ValueKind == JsonValueKind.Object);
        var uri = new Uri(status.GetProperty("pairing").GetProperty("uri").GetString()!);
        var query = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
        Assert.Contains("127.0.0.1", query["h"].Split(','));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (socket, init) = Await(PhoneProtocol.SignInAsync(int.Parse(query["p"]), query["k"], key, code: query["c"], deflate: true));
        using var _ = socket;
        Assert.Equal("init", init!.Value.GetProperty("type").GetString());
        Assert.Equal("Project", init.Value.GetProperty("entries")[0].GetProperty("name").GetString());
        WaitForPage("remoteStatus", message => message.GetProperty("connected").ValueKind == JsonValueKind.Object);
        Assert.Equal(PhoneProtocol.PublicKey(key), _store.State.Remote.Phone!.PublicKey);

        // The page's tiles reach the phone as they are.
        Send(new { type = "tiles", tiles = new[] { new { id = "t1", entry = "e1", name = "Project", status = "running" } } });
        var tiles = Await(PhoneProtocol.ReceiveUntilAsync(socket, "tiles"));
        Assert.Equal("t1", tiles.GetProperty("tiles")[0].GetProperty("id").GetString());

        // Watching: what was written so far, then the console at the phone's size.
        Await(PhoneProtocol.SendAsync(socket, new { type = "view", id = "t1", cols = 50, rows = 20 }));
        var screen = Await(PhoneProtocol.ReceiveUntilAsync(socket, "screen"));
        Assert.Contains("hello from the session", string.Concat(screen.GetProperty("pieces").EnumerateArray().Select(piece => piece.GetProperty("data").GetString())));
        Assert.Equal(100, screen.GetProperty("pieces")[0].GetProperty("cols").GetInt32());
        var sized = Await(PhoneProtocol.ReceiveUntilAsync(socket, "sized"));
        Assert.Equal("phone", sized.GetProperty("owner").GetString());
        Assert.Equal(50, sized.GetProperty("cols").GetInt32());
        WaitForPage("sized", message => message.GetProperty("owner").GetString() == "phone");

        // Typing on the phone reaches the console, and the page hears about it.
        Await(PhoneProtocol.SendAsync(socket, new { type = "input", id = "t1", data = "echo typed on the phone\r" }));
        Await(PhoneProtocol.ReceiveUntilAsync(socket, "output", message => message.GetProperty("data").GetString()!.Contains("typed on the phone")));
        WaitForPage("remoteInput");

        // Somebody types at the PC: the size goes back to the tile's.
        int mark = _posted.Count;
        Send(new { type = "claim", id = "t1" });
        Assert.Equal("pc", WaitForPage("sized", after: mark).GetProperty("owner").GetString());
        var back = Await(PhoneProtocol.ReceiveUntilAsync(socket, "sized", message => message.GetProperty("owner").GetString() == "pc"));
        Assert.Equal(100, back.GetProperty("cols").GetInt32());

        // The phone's buttons are the page's to carry out.
        Await(PhoneProtocol.SendAsync(socket, new { type = "act", @ref = "r1", action = "stop", id = "t1" }));
        var asked = WaitForPage("remote");
        Assert.Equal("stop", asked.GetProperty("action").GetString());
        Send(new { type = "remoteResult", @ref = "r1", id = "t1" });
        Assert.Equal("t1", Await(PhoneProtocol.ReceiveUntilAsync(socket, "actResult")).GetProperty("id").GetString());

        // Something the page does not offer is not passed on.
        mark = _posted.Count;
        Await(PhoneProtocol.SendAsync(socket, new { type = "act", @ref = "r2", action = "remove", entry = "e1" }));
        Await(PhoneProtocol.SendAsync(socket, new { type = "claim", id = "t1" }));
        WaitForPage("sized", message => message.GetProperty("owner").GetString() == "phone", after: mark);
        Assert.DoesNotContain(_posted.Skip(mark), message => message.GetProperty("type").GetString() == "remote");
    }

    /// <summary>Switches remote access on and pairs the phone (as the test above does step by step); its connection.</summary>
    private ClientWebSocket PairPhone(ECDsa key, out JsonElement init)
    {
        _dashboard.StartRemote();
        WaitForPage("remoteStatus", message => message.GetProperty("listening").GetArrayLength() > 0);
        Send(new { type = "remoteSetup", op = "pair" });
        var status = WaitForPage("remoteStatus", message => message.GetProperty("pairing").ValueKind == JsonValueKind.Object);
        var uri = new Uri(status.GetProperty("pairing").GetProperty("uri").GetString()!);
        var query = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
        var (socket, answer) = Await(PhoneProtocol.SignInAsync(int.Parse(query["p"]), query["k"], key, code: query["c"], deflate: true));
        init = answer!.Value;
        Assert.Equal("init", init.GetProperty("type").GetString());
        WaitForPage("remoteStatus", message => message.GetProperty("connected").ValueKind == JsonValueKind.Object);
        return socket;
    }

    [Fact]
    public void A_dictation_from_the_phone_comes_back_as_words()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var socket = PairPhone(key, out var init);
        // The phone (and the Phone dialog) hear whether dictation can be put into words at all.
        Assert.True(init.GetProperty("dictation").GetProperty("available").GetBoolean());
        WaitForPage("remoteStatus", message => message.GetProperty("dictation").GetProperty("available").GetBoolean());

        var audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("spoken words"));
        Await(PhoneProtocol.SendAsync(socket, new { type = "transcribe", @ref = "d1", mime = "audio/mp4", audio }));
        var words = Await(PhoneProtocol.ReceiveUntilAsync(socket, "transcribed"));
        Assert.Equal("d1", words.GetProperty("ref").GetString());
        Assert.Equal("Heard 12 bytes of audio/mp4", words.GetProperty("text").GetString());

        // What the service will not do is said in words, under the phone's reference.
        Await(PhoneProtocol.SendAsync(socket, new { type = "transcribe", @ref = "d2", mime = "audio/mp4", audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fail")) }));
        var refused = Await(PhoneProtocol.ReceiveUntilAsync(socket, "transcribeFailed"));
        Assert.Equal("d2", refused.GetProperty("ref").GetString());
        Assert.Equal("ElevenLabs refused (test).", refused.GetProperty("message").GetString());

        // A recording of an unknown kind, a broken one or none at all goes nowhere.
        Await(PhoneProtocol.SendAsync(socket, new { type = "transcribe", @ref = "d3", mime = "text/plain", audio }));
        Assert.Contains("kind", Await(PhoneProtocol.ReceiveUntilAsync(socket, "transcribeFailed", message => message.GetProperty("ref").GetString() == "d3")).GetProperty("message").GetString());
        Await(PhoneProtocol.SendAsync(socket, new { type = "transcribe", @ref = "d4", mime = "audio/mp4", audio = "not base64!" }));
        Assert.Contains("intact", Await(PhoneProtocol.ReceiveUntilAsync(socket, "transcribeFailed", message => message.GetProperty("ref").GetString() == "d4")).GetProperty("message").GetString());
        Await(PhoneProtocol.SendAsync(socket, new { type = "transcribe", @ref = "d5", mime = "audio/mp4", audio = "" }));
        Assert.Contains("empty", Await(PhoneProtocol.ReceiveUntilAsync(socket, "transcribeFailed", message => message.GetProperty("ref").GetString() == "d5")).GetProperty("message").GetString());
    }

    [Fact]
    public void Jarvis_hands_a_request_to_its_tile_and_hears_the_answer_from_the_Stop_hook()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var socket = PairPhone(key, out _);

        // The phone passes on what Jarvis heard (on one line); the page is asked to place it.
        Await(PhoneProtocol.SendAsync(socket, new { type = "voice", @ref = "v1", text = "open the \"calculator\"\r\n please" }));
        var asked = WaitForPage("remote", message => message.GetProperty("action").GetString() == "voice");
        Assert.Equal("open the \"calculator\" please", asked.GetProperty("text").GetString());

        // The page starts Jarvis's tile with it as the first prompt, and the phone hears it went.
        int mark = _posted.Count;
        Send(new { type = "launch", id = "j1", entry = Hub.EntryId, cols = 100, rows = 30, jarvis = true, prompt = asked.GetProperty("text").GetString() });
        var started = WaitForPage("started", after: mark);
        Assert.True(started.GetProperty("hooks").GetBoolean());
        Assert.True(started.GetProperty("jarvis").GetBoolean());
        var command = started.GetProperty("command").GetString()!;
        Assert.Contains("--settings \"", command);
        Assert.Contains("--append-system-prompt-file \"", command);
        Assert.EndsWith(" \"open the calculator please\"", command);
        Send(new { type = "remoteResult", @ref = "v1", id = "j1" });
        Assert.Equal("v1", Await(PhoneProtocol.ReceiveUntilAsync(socket, "voiceSent")).GetProperty("ref").GetString());

        // Claude Code's hooks reach the page, and the end of the turn brings the answer to the phone.
        var settings = Directory.GetFiles(Path.Combine(_root, "data", "hooks"), "*.json").Single();
        var stop = JsonDocument.Parse(File.ReadAllText(settings)).RootElement
            .GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("url").GetString()!;
        using var http = new HttpClient();
        Await(http.PostAsync(stop.Replace("/Stop", "/UserPromptSubmit"), new StringContent("{\"prompt\":\"x\"}", Encoding.UTF8, "application/json")));
        WaitForPage("hook", message => message.GetProperty("event").GetString() == "UserPromptSubmit");
        Await(http.PostAsync(stop, new StringContent("{\"last_assistant_message\":\"Calculator is open.\",\"background_tasks\":[]}", Encoding.UTF8, "application/json")));
        var reply = Await(PhoneProtocol.ReceiveUntilAsync(socket, "voiceReply"));
        Assert.Equal("v1", reply.GetProperty("ref").GetString());
        Assert.Equal("Calculator is open.", reply.GetProperty("text").GetString());
        Assert.False(WaitForPage("hook", message => message.GetProperty("event").GetString() == "Stop").GetProperty("background").GetBoolean());

        // A page that couldn't place a request says why, under the phone's reference.
        Await(PhoneProtocol.SendAsync(socket, new { type = "voice", @ref = "v2", text = "again" }));
        WaitForPage("remote", message => message.GetProperty("ref").GetString() == "v2");
        Send(new { type = "remoteResult", @ref = "v2", id = "", error = "Maid has no All projects folder." });
        Assert.Equal("Maid has no All projects folder.", Await(PhoneProtocol.ReceiveUntilAsync(socket, "voiceFailed")).GetProperty("message").GetString());

        // When the session ends, its hook settings go.
        Send(new { type = "kill", id = "j1" });
        WaitForPage("exited", message => message.GetProperty("id").GetString() == "j1");
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data", "hooks"), "*.json"));
    }
}
