using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Windows.Threading;
using Maid.Remote;
using Maid.Terminal;
using QRCoder;

namespace Maid;

/// <summary>
/// The phone's side of the dashboard. The phone mirrors the page's tiles (the page sends them as
/// <c>tiles</c>), watches one of them at a time (<c>view</c>: its recent output, then the live
/// stream), types into it and asks the page to do what its buttons do (<c>act</c> → <c>remote</c>
/// → <c>remoteResult</c>). A console has one size: the session the phone watches is sized for the
/// phone until somebody types at the PC (<c>claim</c> from the page), and back for the PC when the
/// phone looks elsewhere or goes away. A dictation recorded on the phone (<c>transcribe</c>) is put
/// into words here, with the PC's ElevenLabs key (<see cref="ElevenLabs"/>).
/// </summary>
internal sealed partial class Dashboard
{
    private static readonly TimeSpan PairingTime = TimeSpan.FromMinutes(5);
    private const int MaxRemoteLog = 60;
    private const long MaxRemoteLogFile = 512 * 1024;
    private const string FirewallRule = "Maid remote access";
    /// <summary>A recording longer than that (minutes of speech) is not what a dictation is.</summary>
    private const int MaxRecordingBytes = 2 * 1024 * 1024;

    private RemoteServer? _remote;
    private RemoteConnection? _phone;
    /// <summary>The tile the phone shows, if any.</summary>
    private string? _phoneViewing;
    /// <summary>The page's tiles as it last sent them (a JSON array), passed on to the phone as they are.</summary>
    private JsonElement _tiles = JsonDocument.Parse("[]").RootElement.Clone();
    /// <summary>The output of tiles whose session has ended, while the page still shows them.</summary>
    private readonly Dictionary<string, TerminalReplay> _endedReplays = [];
    private readonly LinkedList<(DateTime Time, string Text)> _remoteLog = new();
    private DispatcherTimer? _remoteTimer;
    private TailscaleSelf? _tailscale;
    private string? _tailscaleError;
    private List<string> _listenProblems = [];
    private Pairing? _pairing;
    private bool _refreshing;

    private sealed record Pairing(string Code, DateTime ExpiresUtc, string Uri, string Image);

    /// <summary>Who is behind an address; Tailscale's command line unless a test puts something else here.</summary>
    public IPeerDirectory? PeerDirectory { get; init; }

    /// <summary>Puts a recording (bytes, media type) into words; ElevenLabs with the PC's key unless a test puts something else here.</summary>
    public Func<byte[], string, CancellationToken, Task<string>>? Transcribe { get; init; }

    private RemoteSettings RemoteSettings => _store.State.Remote;

    /// <summary>At start: listens for the phone if that is switched on.</summary>
    public void StartRemote()
    {
        if (RemoteSettings.Enabled) EnableRemote();
    }

    private void HandleRemoteSetup(string op)
    {
        switch (op)
        {
            case "enable":
                RemoteSettings.Enabled = true;
                _store.Save();
                EnableRemote();
                break;
            case "disable":
                RemoteSettings.Enabled = false;
                _store.Save();
                DisableRemote();
                RemoteLog("Remote access switched off.");
                _ = Task.Run(() => RunNetsh(["advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}"]));
                break;
            case "pair":
                StartPairing();
                break;
            case "cancelPair":
                _pairing = null;
                _remote?.CancelPairing();
                break;
            case "forget":
                if (RemoteSettings.Phone is { } phone) RemoteLog($"Forgot {phone.Name}: it cannot connect any more.");
                RemoteSettings.Phone = null;
                _store.Save();
                _remote?.SetPhone(null);
                break;
        }
        PostRemoteStatus();
    }

    private void EnableRemote()
    {
        if (_remote is null)
        {
            X509Certificate2 certificate;
            try
            {
                certificate = ServerCertificate.LoadOrCreate(_store.DataDirectory);
            }
            catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
            {
                _listenProblems = [$"No certificate for the connection: {e.Message}"];
                PostRemoteStatus();
                return;
            }
            _remote = new RemoteServer(certificate, PeerDirectory ?? new TailscaleCli(RemoteSettings.TailscalePath), RemoteSettings.AllowLoopback)
            {
                Connected = connection => _dispatcher.BeginInvoke(() => OnPhoneConnected(connection)),
                Message = (connection, json) => _dispatcher.BeginInvoke(() => OnPhoneMessage(connection, json)),
                Closed = connection => _dispatcher.BeginInvoke(() => OnPhoneClosed(connection)),
                Paired = phone => _dispatcher.BeginInvoke(() => OnPaired(phone)),
                Log = text => _dispatcher.BeginInvoke(() => RemoteLog(text)),
            };
            _remote.SetPhone(RemoteSettings.Phone);
            RemoteLog("Remote access switched on.");
            // The rule names Maid's program, so Windows does not ask about it (a "Cancel" there would block it)
            // and goes in before Maid listens.
            if (Elevation.IsElevated && RemoteSettings.Port > 0 && Environment.ProcessPath is { } program)
            {
                int port = RemoteSettings.Port;
                Task.Run(() => EnsureFirewallRule(port, program))
                    .ContinueWith(_ => _dispatcher.BeginInvoke(StartRemoteTimer), TaskScheduler.Default);
                return;
            }
        }
        StartRemoteTimer();
    }

    /// <summary>Tailscale's addresses can come and go (not logged in yet, reconnecting): looked at again now and then.</summary>
    private void StartRemoteTimer()
    {
        if (_remote is null || _shutDown) return;
        _remoteTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => RefreshRemote(), _dispatcher);
        _remoteTimer.Start();
        RefreshRemote();
    }

    private void DisableRemote()
    {
        _remoteTimer?.Stop();
        _pairing = null;
        PhoneUnview();
        _phone = null;
        _remote?.Dispose();
        _remote = null;
        _listenProblems = [];
    }

    /// <summary>Asks Tailscale where this PC is (in the background) and listens there.</summary>
    private void RefreshRemote()
    {
        if (_remote is null || _refreshing) return;
        _refreshing = true;
        var peers = PeerDirectory ?? new TailscaleCli(RemoteSettings.TailscalePath);
        Task.Run(() =>
        {
            var self = peers.Self(out var error);
            _dispatcher.BeginInvoke(() =>
            {
                _refreshing = false;
                if (_remote is null || _shutDown) return;
                _tailscale = self;
                _tailscaleError = error;
                var addresses = (self?.Addresses ?? []).ToList();
                if (RemoteSettings.AllowLoopback) addresses.Add(IPAddress.Loopback);
                var problems = _remote.Listen(addresses, RemoteSettings.Port);
                foreach (var problem in problems.Except(_listenProblems)) RemoteLog($"Could not listen on {problem}");
                _listenProblems = problems;
                if (_pairing is { } pairing && pairing.ExpiresUtc <= DateTime.UtcNow) _pairing = null;
                PostRemoteStatus();
            });
        });
    }

    private void StartPairing()
    {
        if (_remote is null) return;
        var endpoints = _remote.Endpoints;
        var hosts = new List<string>();
        // The phone tries them in this order: the Tailscale address, its name, and for tests the PC
        // as an emulator (10.0.2.2) or "adb reverse" (127.0.0.1) sees it.
        foreach (var endpoint in endpoints.Where(endpoint => TailscaleCli.IsTailnetAddress(endpoint.Address)).OrderBy(endpoint => endpoint.Address.AddressFamily))
            hosts.Add(endpoint.Address.ToString());
        if (_tailscale is { DnsName.Length: > 0 } self && hosts.Count > 0) hosts.Add(self.DnsName);
        if (RemoteSettings.AllowLoopback && endpoints.Any(endpoint => IPAddress.IsLoopback(endpoint.Address)))
            hosts.AddRange(["127.0.0.1", "10.0.2.2"]);
        if (hosts.Count == 0)
        {
            RemoteLog("Cannot pair: Maid is not reachable through Tailscale yet" + (_tailscaleError is { } error ? $" ({error})." : "."));
            return;
        }
        int port = endpoints.First(endpoint => hosts.Contains(endpoint.Address.ToString())
                                               || (IPAddress.IsLoopback(endpoint.Address) && hosts.Contains("127.0.0.1"))).Port;
        var code = ServerCertificate.Base64Url(RandomNumberGenerator.GetBytes(16));
        var uri = $"maid://pair?v={RemoteServer.ProtocolVersion}&h={Uri.EscapeDataString(string.Join(",", hosts))}&p={port}" +
                  $"&k={_remote.Pin}&c={code}&n={Uri.EscapeDataString(Environment.MachineName)}";
        var png = new PngByteQRCode(QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.M)).GetGraphic(8);
        _pairing = new Pairing(code, DateTime.UtcNow + PairingTime, uri, "data:image/png;base64," + Convert.ToBase64String(png));
        _remote.OfferPairing(code, _pairing.ExpiresUtc);
        RemoteLog("Pairing offered for 5 minutes.");
    }

    private void OnPaired(PairedPhone phone)
    {
        RemoteSettings.Phone = phone;
        _store.Save();
        _pairing = null;
        PostRemoteStatus();
    }

    private void OnPhoneConnected(RemoteConnection connection)
    {
        if (_remote is null || _shutDown)
        {
            connection.Close();
            return;
        }
        PhoneUnview();
        _phone = connection;
        if (RemoteSettings.Phone is { } phone)
        {
            phone.LastSeenUtc = DateTime.UtcNow;
            _store.Save();
        }
        connection.Send(Serialize(new
        {
            type = "init",
            v = RemoteServer.ProtocolVersion,
            host = Environment.MachineName,
            entries = _store.State.Entries,
            groups = _store.State.Groups,
            hub = new { id = Hub.EntryId, name = Hub.Name, path = _hub.Directory },
            tiles = _tiles,
            osBuild = Environment.OSVersion.Version.Build,
            dictation = DictationStatus(),
        }));
        PostRemoteStatus();
    }

    private void OnPhoneClosed(RemoteConnection connection)
    {
        if (!ReferenceEquals(_phone, connection)) return;
        PhoneUnview();
        _phone = null;
        if (RemoteSettings.Phone is { } phone)
        {
            phone.LastSeenUtc = DateTime.UtcNow;
            _store.Save();
        }
        PostRemoteStatus();
    }

    // ---- messages from the phone ---------------------------------------------------------------------

    private void OnPhoneMessage(RemoteConnection connection, string json)
    {
        if (!ReferenceEquals(_phone, connection) || _shutDown) return;
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return; }
        using (document)
        {
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object) return;
            var id = GetString(message, "id");
            switch (GetString(message, "type"))
            {
                case "view":
                    PhoneView(id, PhoneCols(message), PhoneRows(message));
                    break;
                case "resize":
                    if (id == _phoneViewing && _sessions.TryGetValue(id, out var resized))
                    {
                        (resized.PhoneCols, resized.PhoneRows) = (PhoneCols(message), PhoneRows(message));
                        if (resized.PhoneOwns) ApplySize(resized);
                    }
                    break;
                case "claim":
                    if (id == _phoneViewing && _sessions.TryGetValue(id, out var claimed) && !claimed.PhoneOwns)
                    {
                        claimed.PhoneOwns = true;
                        ApplySize(claimed);
                    }
                    break;
                case "yield":
                    if (id == _phoneViewing && _sessions.TryGetValue(id, out var yielded) && yielded.PhoneOwns)
                    {
                        yielded.PhoneOwns = false;
                        ApplySize(yielded);
                    }
                    break;
                case "unview":
                    PhoneUnview();
                    break;
                case "input":
                    var data = GetString(message, "data");
                    if (data.Length == 0 || data.Length > 64 * 1024 || !_sessions.TryGetValue(id, out var target)) break;
                    // The size follows whoever types.
                    if (id == _phoneViewing && !target.PhoneOwns)
                    {
                        target.PhoneOwns = true;
                        ApplySize(target);
                    }
                    target.Pty?.Write(data);
                    Post(new { type = "remoteInput", id, escape = data == "\u001b" });
                    break;
                case "transcribe":
                    PhoneTranscribe(Bounded(GetString(message, "ref")), GetString(message, "mime"), GetString(message, "audio"));
                    break;
                case "api":
                    // Jarvis (or another app on the phone the phone app lets in) controls Maid: Dashboard.Api.cs.
                    PhoneApi(Bounded(GetString(message, "ref")), message);
                    break;
                case "voice":
                    // A voice assistant on the phone heard "on computer, …" and passed it on through the phone app.
                    PhoneVoice(Bounded(GetString(message, "ref")), GetString(message, "text"));
                    break;
                case "act":
                    var action = GetString(message, "action");
                    if (action is not ("launchEntry" or "relaunch" or "stop" or "close")) break;
                    Post(new
                    {
                        type = "remote",
                        action,
                        @ref = Bounded(GetString(message, "ref")),
                        id = Bounded(id),
                        entry = Bounded(GetString(message, "entry")),
                        resume = GetBool(message, "resume"),
                    });
                    break;
            }
        }
    }

    private static string Bounded(string text) => text.Length > 64 ? text[..64] : text;

    // ---- dictation --------------------------------------------------------------------------------------

    /// <summary>Whether the phone's dictation can be put into words, and where the key for that comes from.</summary>
    private object DictationStatus()
    {
        if (Transcribe is not null) return new { available = true, source = "test" };
        var key = ElevenLabs.ApiKey(_store.State.Settings, out var source);
        return new { available = key is not null, source };
    }

    /// <summary>
    /// The phone recorded a dictation (<paramref name="base64"/>, of type <paramref name="mime"/>): its words go
    /// back as <c>transcribed</c>, or why there are none as <c>transcribeFailed</c>, both with the phone's <paramref name="reference"/>.
    /// </summary>
    private void PhoneTranscribe(string reference, string mime, string base64)
    {
        void Fail(string message)
        {
            _phone?.Send(Serialize(new { type = "transcribeFailed", @ref = reference, message }));
            RemoteLog($"Dictation failed: {message}");
        }

        if (ElevenLabs.ExtensionFor(mime) == ".bin")
        {
            Fail("The recording is of a kind Maid does not know.");
            return;
        }
        if (base64.Length > MaxRecordingBytes * 4 / 3 + 4)
        {
            Fail("The recording is too long.");
            return;
        }
        byte[] audio;
        try
        {
            audio = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            Fail("The recording did not arrive intact.");
            return;
        }
        if (audio.Length == 0)
        {
            Fail("The recording is empty.");
            return;
        }
        var transcribe = Transcribe;
        if (transcribe is null)
        {
            var settings = _store.State.Settings;
            if (ElevenLabs.ApiKey(settings, out _) is not { } key)
            {
                Fail($"Maid has no ElevenLabs API key: set {ElevenLabs.EnvironmentVariable} (or settings.elevenLabsApiKey in state.json) on the PC and start Maid again.");
                return;
            }
            var (model, language) = (settings.TranscriptionModel, settings.DictationLanguage);
            transcribe = (bytes, type, token) => ElevenLabs.TranscribeAsync(key, bytes, type, model, language, token);
        }
        Task.Run(async () =>
        {
            string text = "";
            string? error = null;
            try
            {
                text = await transcribe(audio, mime, CancellationToken.None);
            }
            catch (Exception e) when (e is TranscriptionException or HttpRequestException or IOException or OperationCanceledException or JsonException)
            {
                error = e.Message;
            }
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_shutDown) return;
                // To whichever connection the phone has now: it is the same page, waiting under the same reference.
                if (error is null) _phone?.Send(Serialize(new { type = "transcribed", @ref = reference, text }));
                else Fail(error);
            });
        });
    }
    private static int PhoneCols(JsonElement message) => Math.Clamp(GetInt(message, "cols", 60), 20, 400);
    private static int PhoneRows(JsonElement message) => Math.Clamp(GetInt(message, "rows", 30), 5, 200);

    /// <summary>The phone shows a tile: its recent output first, then (when it runs) the stream, at the phone's size.</summary>
    private void PhoneView(string id, int cols, int rows)
    {
        if (_phone is null) return;
        if (_phoneViewing != id) PhoneUnview();
        if (_sessions.TryGetValue(id, out var session))
        {
            // Output still on its way belongs to the replay at the size it was written at.
            Flush(session);
            _phoneViewing = id;
            (session.PhoneCols, session.PhoneRows) = (cols, rows);
            _phone.Send(Serialize(new { type = "screen", id, run = session.Run, live = true, pieces = session.Replay.Snapshot() }));
            session.PhoneOwns = true;
            ApplySize(session);
        }
        else
        {
            _phoneViewing = id;
            var pieces = _endedReplays.TryGetValue(id, out var replay) ? replay.Snapshot() : [];
            _phone.Send(Serialize(new { type = "screen", id, run = 0, live = false, pieces }));
        }
    }

    /// <summary>The phone looks at no tile any more: the one it showed is sized for the PC again.</summary>
    private void PhoneUnview()
    {
        if (_phoneViewing is not { } id) return;
        _phoneViewing = null;
        if (_sessions.TryGetValue(id, out var session) && session.PhoneOwns)
        {
            session.PhoneOwns = false;
            ApplySize(session);
        }
    }

    /// <summary>Gives the console the size of whoever has it, and tells the page and the phone.</summary>
    private void ApplySize(RunningSession session, bool notifyPage = true)
    {
        var (cols, rows) = session.PhoneOwns ? (session.PhoneCols, session.PhoneRows) : (session.DesktopCols, session.DesktopRows);
        if (cols != session.Cols || rows != session.Rows)
        {
            Flush(session);
            (session.Cols, session.Rows) = (cols, rows);
            session.Pty?.Resize(cols, rows);
            session.Replay.Resize(cols, rows);
        }
        var owner = session.PhoneOwns ? "phone" : "pc";
        if (notifyPage) Post(new { type = "sized", id = session.Id, owner, cols, rows });
        if (_phone is { } phone && _phoneViewing == session.Id)
            phone.Send(Serialize(new { type = "sized", id = session.Id, owner, cols, rows }));
    }

    // ---- from the page, for the phone ------------------------------------------------------------------

    private void OnTiles(JsonElement message)
    {
        if (!message.TryGetProperty("tiles", out var tiles) || tiles.ValueKind != JsonValueKind.Array) return;
        _tiles = tiles.Clone();
        var ids = new HashSet<string>();
        foreach (var tile in _tiles.EnumerateArray())
        {
            if (tile.ValueKind == JsonValueKind.Object && GetString(tile, "id") is { Length: > 0 } tileId) ids.Add(tileId);
        }
        foreach (var gone in _endedReplays.Keys.Where(key => !ids.Contains(key)).ToList()) _endedReplays.Remove(gone);
        if (_phoneViewing is { } viewing && !ids.Contains(viewing)) PhoneUnview();
        TrackTilesForApi(ids);
        _phone?.Send(Serialize(new { type = "tiles", tiles = _tiles }));
    }

    /// <summary>The page did (or could not do) what the phone asked for.</summary>
    private void OnRemoteResult(JsonElement message)
    {
        string reference = GetString(message, "ref"), id = GetString(message, "id"), error = GetString(message, "error");
        if (OnApiPageResult(reference, message)) return;
        if (OnVoicePlaced(reference, id, error)) return;
        _phone?.Send(Serialize(new { type = "actResult", @ref = reference, id, error }));
    }

    /// <summary>Somebody types at the PC: the session is sized for the PC again.</summary>
    private void OnClaim(string id)
    {
        if (!_sessions.TryGetValue(id, out var session) || !session.PhoneOwns) return;
        session.PhoneOwns = false;
        ApplySize(session);
    }

    /// <summary>Messages every listener has to hear: the page always, the phone when one is connected.</summary>
    private void PostAll(object message)
    {
        Post(message);
        _phone?.Send(Serialize(message));
    }

    private void SendListToPhone() =>
        _phone?.Send(Serialize(new { type = "list", entries = _store.State.Entries, groups = _store.State.Groups }));

    // ---- status for the page ------------------------------------------------------------------------

    private void PostRemoteStatus()
    {
        var phone = RemoteSettings.Phone;
        var pairing = _pairing is { } offer && offer.ExpiresUtc > DateTime.UtcNow ? offer : null;
        Post(new
        {
            type = "remoteStatus",
            enabled = RemoteSettings.Enabled,
            elevated = Elevation.IsElevated,
            port = RemoteSettings.Port,
            listening = _remote?.Endpoints.Select(endpoint => endpoint.ToString()) ?? [],
            problems = _listenProblems,
            tailscale = new
            {
                ok = _tailscale is not null,
                name = _tailscale?.DnsName ?? "",
                addresses = _tailscale?.Addresses.Select(address => address.ToString()) ?? [],
                error = _tailscaleError,
            },
            phone = phone is null ? null : new { phone.Name, phone.NodeName, phone.PairedUtc, phone.LastSeenUtc, loopback = phone.NodeId.Length == 0 },
            connected = _phone is null ? null : new { name = _phone.DeviceName, from = _phone.Describe, since = _phone.ConnectedUtc },
            pairing = pairing is null ? null : new { image = pairing.Image, uri = pairing.Uri, expires = pairing.ExpiresUtc },
            dictation = DictationStatus(),
            log = _remoteLog.Reverse().Take(25).Select(line => new { time = line.Time, text = line.Text }),
        });
    }

    private void RemoteLog(string text)
    {
        var now = DateTime.UtcNow;
        _remoteLog.AddLast((now, text));
        while (_remoteLog.Count > MaxRemoteLog) _remoteLog.RemoveFirst();
        try
        {
            var file = Path.Combine(_store.DataDirectory, "remote.log");
            if (File.Exists(file) && new FileInfo(file).Length > MaxRemoteLogFile)
            {
                var lines = File.ReadAllLines(file);
                File.WriteAllLines(file, lines.Skip(lines.Length / 2));
            }
            File.AppendAllText(file, $"{now.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (!_shutDown) PostRemoteStatus();
    }

    // ---- Windows Firewall --------------------------------------------------------------------------------

    /// <summary>Lets the phone in through Windows Firewall: Maid's program, its port, from Tailscale addresses only.</summary>
    private static void EnsureFirewallRule(int port, string program)
    {
        RunNetsh(["advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}"]);
        RunNetsh(["advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow", "protocol=TCP",
            $"program={program}", $"localport={port}", "remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48", "profile=any",
            "description=The Maid phone app, through Tailscale (added by Maid; removed when remote access is switched off)"]);
    }

    private static void RunNetsh(string[] arguments)
    {
        var start = new ProcessStartInfo("netsh.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            process?.StandardOutput.ReadToEnd();
            process?.WaitForExit(10000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static string Serialize(object message) => JsonSerializer.Serialize(message, JsonOptions);
}
