using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Maid.Remote;
using Maid.Terminal;

namespace Maid;

/// <summary>
/// Maid's API: Claude Code (in any of Maid's sessions, through <c>maidctl</c>), other programs of the user's at this PC,
/// and a voice assistant on the phone (through the phone app) control Maid with it. They list the projects and windows, start,
/// stop and close sessions, type into them, read their screens, and hand a session a prompt and wait for its answer.
///
/// Two ways in, both existing ones: the local API pipe (<see cref="Settings.ApiPipe"/>, guarded like the voice pipe:
/// only the user's programs, and only elevated ones while Maid is elevated; nothing from the network), and the phone's
/// link (message <c>api</c>), which only the paired phone gets through. The phone may do what its own buttons do,
/// not change Maid's list.
///
/// A request is <c>{ id, op, …arguments }</c>, its answer <c>{ id, ok: true, result }</c> or <c>{ id, ok: false, error }</c>
/// (on the phone's link wrapped as <c>api { ref, request }</c> → <c>apiResult { ref, ok, result, error }</c>). The tiles are
/// the page's, so what changes them goes through the page (<c>remote</c> → <c>remoteResult</c>), like the phone's buttons;
/// whether a Claude Code session works, waits for an answer, or is done, and what it answered, comes from its hooks.
/// </summary>
internal sealed partial class Dashboard
{
    internal const int ApiVersion = 1;
    /// <summary>The longest text the API types into a session or gives a new one as its first prompt.</summary>
    internal const int MaxApiText = 16_000;
    internal const int MaxApiWaitSeconds = 3600;
    internal const int DefaultApiWaitSeconds = 600;
    private const int MaxApiCallers = 16;
    private static readonly TimeSpan PageAnswerTime = TimeSpan.FromSeconds(10);
    /// <summary>A prompt typed into a session that has not started a turn after this long did not take (it went into a menu, say).</summary>
    private static readonly TimeSpan TypedTurnGrace = TimeSpan.FromSeconds(20);
    /// <summary>A new session's first prompt waits for its start (the trust prompt, MCP servers) before its turn shows up.</summary>
    private static readonly TimeSpan LaunchTurnGrace = TimeSpan.FromSeconds(120);

    /// <summary>The operations, for the error that names an unknown one.</summary>
    internal static readonly string[] ApiOps =
        ["hello", "status", "projects", "windows", "window", "launch", "send", "ask", "wait", "type", "keys", "screen", "stop", "close", "relaunch", "show", "addProject"];

    private LocalPipeServer? _apiPipe;
    private readonly Dictionary<string, Action<JsonElement?>> _apiPage = [];
    private int _apiLastRef;
    private DispatcherTimer? _apiTimer;
    /// <summary>Why the page's last launch of a tile failed (launchFailed), for the API that asked for it.</summary>
    private readonly Dictionary<string, string> _launchFailures = [];
    /// <summary>Sessions that ended while their tile is still on the page: what they answered last, their exit.</summary>
    private readonly Dictionary<string, RunningSession> _endedSessions = [];

    /// <summary>The API pipe's name while Maid listens on it; null otherwise (tests, or a name another program holds).</summary>
    internal string? ApiPipeName => _apiPipe?.Name;

    /// <summary>Someone using the API: a program on the API pipe, or Jarvis by way of the phone app.</summary>
    internal sealed class ApiCaller(string name, bool phone, Action<string, bool, object?, string?> reply, Func<bool> closed)
    {
        public string Name { get; } = name;
        /// <summary>The phone may do what its own buttons do, not change Maid's list.</summary>
        public bool Phone { get; } = phone;
        /// <summary>Sends an answer: (request id, ok, result, error).</summary>
        public Action<string, bool, object?, string?> Reply { get; } = reply;
        public Func<bool> IsClosed { get; } = closed;
    }

    /// <summary>One request: its answer goes back to its caller under its id, once.</summary>
    internal sealed class ApiRequest(ApiCaller caller, string id, JsonElement message)
    {
        public ApiCaller Caller { get; } = caller;
        public string Id { get; } = id;
        public JsonElement Message { get; } = message;
        /// <summary>The tile the caller itself runs in (maidctl sends MAID_WINDOW), when it says.</summary>
        public string Self { get; } = Bounded(GetString(message, "caller"));
        public bool Answered { get; set; }
    }

    /// <summary>Somebody waiting until a session's turn is over.</summary>
    private sealed class ApiWaiter(ApiRequest request, DateTime deadlineUtc, Func<RunningSession, bool, object> result)
    {
        public ApiRequest Request { get; } = request;
        public DateTime DeadlineUtc { get; } = deadlineUtc;
        public Func<RunningSession, bool, object> Result { get; } = result;
    }

    // ---- the pipe -------------------------------------------------------------------------------------------

    /// <summary>Starts listening on the API pipe (<see cref="Settings.ApiPipe"/>; empty: never).</summary>
    public void StartApiPipe()
    {
        var name = _store.State.Settings.ApiPipe.Trim();
        if (name.Length == 0 || _apiPipe is not null || _shutDown) return;
        try
        {
            _apiPipe = new LocalPipeServer(name, Elevation.IsElevated, "API pipe", MaxApiCallers, MaxApiText * 4 + 4096)
            {
                Message = (client, json) => _dispatcher.BeginInvoke(() => OnApiClientMessage(client, json)),
                Closed = client => _dispatcher.BeginInvoke(() => OnApiClientClosed(client)),
                Log = text => _dispatcher.BeginInvoke(() => RemoteLog("API pipe: " + text)),
            };
            _apiPipe.Start();
        }
        catch (IOException e)
        {
            RemoteLog("API pipe: " + e.Message);
        }
    }

    private readonly Dictionary<LocalPipeClient, ApiCaller> _apiClients = [];

    private void OnApiClientMessage(LocalPipeClient client, string json)
    {
        if (_shutDown || client.IsClosed) return;
        if (!_apiClients.TryGetValue(client, out var caller))
        {
            caller = new ApiCaller($"pipe {client.Number}", phone: false,
                (id, ok, result, error) => client.Send(Serialize(new { id, ok, result, error })), () => client.IsClosed);
            _apiClients[client] = caller;
        }
        HandleApi(caller, json);
    }

    private void OnApiClientClosed(LocalPipeClient client)
    {
        if (!_apiClients.Remove(client, out var caller)) return;
        foreach (var session in _sessions.Values) session.Waiters.RemoveAll(waiter => waiter.Request.Caller == caller);
    }

    /// <summary>The phone app passes on a request of Jarvis's (or another app on the phone it lets in).</summary>
    private void PhoneApi(string reference, JsonElement message)
    {
        if (reference.Length == 0) return;
        if (!message.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
        {
            _phone?.Send(Serialize(new { type = "apiResult", @ref = reference, ok = false, result = (object?)null, error = "No request." }));
            return;
        }
        // Answers go to whichever connection the phone has by then: the app waits for them under its ref.
        _phoneCaller ??= new ApiCaller("phone", phone: true,
            (id, ok, result, error) => _phone?.Send(Serialize(new { type = "apiResult", @ref = id, ok, result, error })), () => false);
        HandleApi(_phoneCaller, request.Clone(), reference);
    }

    private ApiCaller? _phoneCaller;

    // ---- requests ---------------------------------------------------------------------------------------------

    /// <summary>A request as text (one line of the pipe); answered whatever is wrong with it, as long as it has an id.</summary>
    internal void HandleApi(ApiCaller caller, string json)
    {
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(json);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            caller.Reply("", false, null, "That is not JSON.");
            return;
        }
        if (message.ValueKind != JsonValueKind.Object)
        {
            caller.Reply("", false, null, "A request is a JSON object: { \"id\": …, \"op\": … }.");
            return;
        }
        HandleApi(caller, message, Bounded(GetString(message, "id")));
    }

    private void HandleApi(ApiCaller caller, JsonElement message, string id)
    {
        var request = new ApiRequest(caller, id, message);
        var op = GetString(message, "op");
        switch (op)
        {
            case "hello":
                Answer(request, new { app = "Maid", version = MainWindow.AppVersion, api = ApiVersion, elevated = Elevation.IsElevated, window = request.Self });
                break;
            case "status":
                Answer(request, ApiStatus(request));
                break;
            case "projects":
                Answer(request, ApiProjects());
                break;
            case "windows":
                Answer(request, new { windows = ApiTiles().Select(tile => DescribeWindow(tile, full: false)) });
                break;
            case "window":
                if (ResolveWindow(request, GetString(message, "window"), out var windowError) is { } found) Answer(request, DescribeWindow(found, full: true));
                else Fail(request, windowError);
                break;
            case "launch":
                ApiLaunch(request);
                break;
            case "send":
                ApiSend(request, wait: false);
                break;
            case "ask":
                ApiSend(request, wait: true);
                break;
            case "wait":
                ApiWait(request);
                break;
            case "type":
                ApiType(request);
                break;
            case "keys":
                ApiKeys(request);
                break;
            case "screen":
                ApiScreen(request);
                break;
            case "stop" or "close" or "relaunch" or "show":
                ApiTileAction(request, op);
                break;
            case "addProject":
                ApiAddProject(request);
                break;
            default:
                Fail(request, op.Length == 0 ? "The request names no \"op\"." : $"There is no op \"{op}\". The ops are: {string.Join(", ", ApiOps)}.");
                break;
        }
    }

    private static void Answer(ApiRequest request, object? result)
    {
        if (request.Answered) return;
        request.Answered = true;
        request.Caller.Reply(request.Id, true, result, null);
    }

    private static void Fail(ApiRequest request, string error)
    {
        if (request.Answered) return;
        request.Answered = true;
        request.Caller.Reply(request.Id, false, null, error);
    }

    // ---- reading -------------------------------------------------------------------------------------------------

    private object ApiStatus(ApiRequest request)
    {
        var tiles = ApiTiles();
        var states = tiles.Select(tile => StateOf(tile)).ToList();
        return new
        {
            app = "Maid",
            version = MainWindow.AppVersion,
            api = ApiVersion,
            elevated = Elevation.IsElevated,
            apiPipe = ApiPipeName,
            window = request.Self,
            projects = _store.State.Entries.Count,
            windows = new
            {
                open = tiles.Count,
                running = tiles.Count(tile => tile.Status == "running"),
                working = states.Count(state => state == "working"),
                asking = states.Count(state => state == "asking"),
            },
            phone = new
            {
                enabled = RemoteSettings.Enabled,
                paired = RemoteSettings.Phone?.Name,
                connected = _phone is not null,
                viewing = _phone is not null && _phoneViewing is { } viewing ? tiles.FirstOrDefault(tile => tile.Id == viewing)?.Name : null,
            },
            hub = _hub.Directory,
        };
    }

    private object ApiProjects()
    {
        var state = _store.State;
        var tiles = ApiTiles();
        return new
        {
            projects = state.Entries.Select(entry => new
            {
                id = entry.Id,
                name = entry.Name,
                path = entry.Path,
                group = Grouping.Find(state, entry.Group) is { } group ? Grouping.PathOf(state, group) : entry.Group,
                windows = tiles.Count(tile => tile.EntryId == entry.Id),
                running = _sessions.Values.Count(session => session.EntryId == entry.Id),
                moving = _relocating.Contains(entry.Id),
            }),
            groups = Grouping.Tree(state).Select(item => new
            {
                name = item.Group.Name,
                path = Grouping.PathOf(state, item.Group),
                parent = item.Group.Parent,
                color = item.Group.Color,
            }),
            hub = new { id = Hub.EntryId, name = Hub.Name, path = _hub.Directory, running = _sessions.Values.Count(session => session.EntryId == Hub.EntryId) },
        };
    }

    /// <summary>A tile as the page last described it.</summary>
    internal sealed record ApiTile(string Id, string Name, string Project, string EntryId, string Path, string Group,
        string Status, bool Busy, bool Jarvis, string Title, string Message);

    /// <summary>The page's tiles, in its order (the order of the list).</summary>
    private List<ApiTile> ApiTiles()
    {
        var tiles = new List<ApiTile>();
        foreach (var tile in _tiles.EnumerateArray())
        {
            if (tile.ValueKind != JsonValueKind.Object || GetString(tile, "id") is not { Length: > 0 } id) continue;
            tiles.Add(new ApiTile(id, GetString(tile, "name"), GetString(tile, "project"), GetString(tile, "entry"), GetString(tile, "path"),
                GetString(tile, "group"), GetString(tile, "status"),
                tile.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.True,
                tile.TryGetProperty("jarvis", out var jarvis) && jarvis.ValueKind == JsonValueKind.True,
                GetString(tile, "title"), GetString(tile, "message")));
        }
        return tiles;
    }

    private RunningSession? SessionOf(string id) =>
        _sessions.TryGetValue(id, out var session) ? session : _endedSessions.GetValueOrDefault(id);

    /// <summary>
    /// What a window is doing: <c>starting</c>, <c>working</c>, <c>asking</c> (waits for the user's answer to a question or
    /// a permission prompt), <c>idle</c> (waits for the next prompt), <c>ended</c> or <c>failed</c>.
    /// </summary>
    private string StateOf(ApiTile tile)
    {
        if (tile.Status is "failed") return "failed";
        if (_sessions.TryGetValue(tile.Id, out var session)) return TurnState(session);
        return tile.Status is "starting" ? "starting" : tile.Status is "running" ? "idle" : "ended";
    }

    private static string TurnState(RunningSession session)
    {
        if (session.Asking.Length > 0) return "asking";
        if (session.Working) return "working";
        // A prompt it was given is on its way: its turn just hasn't shown up yet (or it waits behind the one that ran).
        if (session.PendingPrompts > 0 && DateTime.UtcNow < session.ExpectUntilUtc) return "working";
        // One that never started a turn (it went into a menu, say) is not waited for any longer.
        session.PendingPrompts = 0;
        return "idle";
    }

    /// <summary>A prompt went into the session: its turn is due within <paramref name="grace"/> (or once the running one is over).</summary>
    private static void ExpectPrompt(RunningSession session, TimeSpan grace)
    {
        session.PendingPrompts++;
        var until = DateTime.UtcNow + grace;
        if (until > session.ExpectUntilUtc) session.ExpectUntilUtc = until;
        session.Interrupted = false;
    }

    private object DescribeWindow(ApiTile tile, bool full)
    {
        var session = SessionOf(tile.Id);
        var answer = session?.Answer;
        if (!full && answer is { Length: > 400 }) answer = answer[..400] + "…";
        return new
        {
            id = tile.Id,
            name = tile.Name,
            project = tile.Project,
            projectId = tile.EntryId,
            path = tile.Path,
            group = tile.Group,
            jarvis = tile.Jarvis,
            state = StateOf(tile),
            title = tile.Title,
            message = tile.Message.Length > 0 ? tile.Message : null,
            answer,
            answeredAt = session?.AnswerUtc,
            question = QuestionOf(session),
            hooks = session?.HookToken is not null,
        };
    }

    /// <summary>What a session asks the user, while it does: the question tool's input, or the prompt's message.</summary>
    private static object? QuestionOf(RunningSession? session)
    {
        if (session is null || session.Asking.Length == 0) return null;
        return new { kind = session.Asking, message = session.AskingText, input = session.AskingInput };
    }

    // ---- finding projects and windows ----------------------------------------------------------------------------

    /// <summary>Letters and digits only, lower case: "My app" and "MyApp" are the same (spoken names).</summary>
    private static string Folded(string text) => new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// A project by its id, its name, its folder, or — when only one fits — part of its name (letters and digits only:
    /// spoken names come out spelled in all sorts of ways). "hub" or "All projects" is the All projects folder.
    /// </summary>
    private string? ResolveProject(string raw, out string error)
    {
        error = "";
        var spec = raw.Trim();
        if (spec.Length == 0)
        {
            error = "Name a project.";
            return null;
        }
        var folded = Folded(spec);
        if (spec == Hub.EntryId || folded is "hub" or "allprojects" || Folded(Hub.Name) == folded) return Hub.EntryId;
        var entries = _store.State.Entries;
        if (entries.FirstOrDefault(entry => entry.Id == spec) is { } byId) return byId.Id;
        if (entries.FirstOrDefault(entry => string.Equals(entry.Name, spec, StringComparison.OrdinalIgnoreCase)) is { } byName) return byName.Id;
        if (NormalizePath(spec) is { } path && entries.FirstOrDefault(entry => SamePath(entry.Path, path)) is { } byPath) return byPath.Id;
        if (folded.Length == 0)
        {
            error = $"There is no project \"{spec}\".";
            return null;
        }
        foreach (var match in new Func<Entry, bool>[]
                 {
                     entry => Folded(entry.Name) == folded,
                     entry => Folded(entry.Name).StartsWith(folded, StringComparison.Ordinal),
                     entry => Folded(entry.Name).Contains(folded, StringComparison.Ordinal),
                 })
        {
            var found = entries.Where(match).ToList();
            if (found.Count == 1) return found[0].Id;
            if (found.Count > 1)
            {
                error = $"\"{spec}\" could be {string.Join(", ", found.Take(8).Select(entry => entry.Name))}: which one?";
                return null;
            }
        }
        error = $"There is no project \"{spec}\". Ask for the projects to see their names.";
        return null;
    }

    /// <summary>
    /// A window by its id, its name ("MyApp #2"), "self" (the caller's own), "jarvis", or a project's name when that
    /// project has one window (or one whose session runs).
    /// </summary>
    private ApiTile? ResolveWindow(ApiRequest request, string raw, out string error)
    {
        error = "";
        var spec = raw.Trim();
        var tiles = ApiTiles();
        if (spec.Length == 0)
        {
            error = "Name a window.";
            return null;
        }
        if (spec is "self" or "me" or "this")
        {
            if (request.Self.Length > 0 && tiles.FirstOrDefault(tile => tile.Id == request.Self) is { } itself) return itself;
            error = "This caller does not run in one of Maid's windows (MAID_WINDOW is not set).";
            return null;
        }
        if (tiles.FirstOrDefault(tile => tile.Id == spec) is { } byId) return byId;
        if (tiles.FirstOrDefault(tile => string.Equals(tile.Name, spec, StringComparison.OrdinalIgnoreCase)) is { } byName) return byName;
        var folded = Folded(spec);
        if (folded is "jarvis" or "voice" && tiles.FirstOrDefault(tile => tile.Jarvis) is { } jarvis) return jarvis;
        if (folded.Length > 0 && tiles.Where(tile => Folded(tile.Name) == folded).ToList() is [var spoken]) return spoken;
        var project = ResolveProject(spec, out var projectError);
        if (project is null)
        {
            error = projectError.StartsWith("There is no project", StringComparison.Ordinal)
                ? $"There is no window or project \"{spec}\". " + (tiles.Count == 0
                    ? "No windows are open."
                    : $"Open windows: {string.Join(", ", tiles.Take(12).Select(tile => tile.Name))}.")
                : projectError;
            return null;
        }
        var own = tiles.Where(tile => tile.EntryId == project).ToList();
        if (own.Count == 1) return own[0];
        var live = own.Where(tile => _sessions.ContainsKey(tile.Id)).ToList();
        if (live.Count == 1) return live[0];
        var name = NameOf(project);
        error = own.Count == 0
            ? $"{name} has no window open."
            : $"{name} has {own.Count} windows: {string.Join(", ", own.Select(tile => $"{tile.Name} ({StateOf(tile)})"))}. Name one of them.";
        return null;
    }

    /// <summary>A window whose session runs, or why not.</summary>
    private RunningSession? RunningWindow(ApiRequest request, string spec, out ApiTile? tile, out string error)
    {
        tile = ResolveWindow(request, spec, out error);
        if (tile is null) return null;
        if (_sessions.TryGetValue(tile.Id, out var session)) return session;
        error = $"The session in {tile.Name} is not running ({StateOf(tile)}). Launch or relaunch it first.";
        return null;
    }

    // ---- starting, typing, waiting ---------------------------------------------------------------------------------

    /// <summary>Starts a session of a project (in one of its ended tiles, or a new one), optionally with its first prompt.</summary>
    private void ApiLaunch(ApiRequest request, Action<RunningSession>? started = null)
    {
        var message = request.Message;
        var entry = ResolveProject(GetString(message, "project"), out var error);
        if (entry is null)
        {
            Fail(request, error);
            return;
        }
        var resume = GetBool(message, "resume");
        var prompt = GetString(message, started is null ? "prompt" : "text").Trim();
        if (prompt.Length > MaxApiText)
        {
            Fail(request, $"The prompt is longer than {MaxApiText:N0} characters: put it in a file and ask the session to read that.");
            return;
        }
        if (_relocating.Contains(entry))
        {
            Fail(request, $"{NameOf(entry)}'s folder is being moved; start it once that is done.");
            return;
        }
        AskPage(new Dictionary<string, object?>
        {
            ["action"] = "launchEntry", ["entry"] = entry, ["resume"] = resume, ["prompt"] = prompt, ["quiet"] = true,
        }, reply =>
        {
            var id = reply is { } answer ? GetString(answer, "id") : "";
            if (reply is null || id.Length == 0)
            {
                Fail(request, reply is { } failed ? GetString(failed, "error") : "Maid's window did not answer (is it still starting?).");
                return;
            }
            if (!_sessions.TryGetValue(id, out var session))
            {
                Fail(request, _launchFailures.GetValueOrDefault(id) ?? "The session did not start.");
                return;
            }
            if (prompt.Length > 0) ExpectPrompt(session, LaunchTurnGrace);
            if (started is not null) started(session);
            else Answer(request, new { window = id, project = NameOf(entry), launched = true });
        });
    }

    /// <summary>
    /// <c>send</c> / <c>ask</c>: a prompt for a window (typed in, then Enter), or for a project (into one of its sessions that
    /// waits for a prompt, else a new one gets it as its first prompt); <c>ask</c> then waits for the answer.
    /// </summary>
    private void ApiSend(ApiRequest request, bool wait)
    {
        var message = request.Message;
        var text = GetString(message, "text").Trim();
        if (text.Length == 0)
        {
            Fail(request, "There is no text to send.");
            return;
        }
        if (text.Length > MaxApiText)
        {
            Fail(request, $"The text is longer than {MaxApiText:N0} characters: put it in a file and ask the session to read that.");
            return;
        }
        var timeout = WaitSeconds(message);

        void Sent(RunningSession session, bool launched)
        {
            if (!wait)
            {
                Answer(request, new { window = session.Id, launched });
                return;
            }
            AddWaiter(request, session, timeout, (done, timedOut) => WaitResult(done, timedOut, launched));
        }

        // A window named outright, or one that the name points to without doubt.
        var windowSpec = GetString(message, "window");
        var projectSpec = GetString(message, "project");
        var to = GetString(message, "to");
        ApiTile? tile = null;
        if (windowSpec.Length > 0)
        {
            tile = ResolveWindow(request, windowSpec, out var error);
            if (tile is null)
            {
                Fail(request, error);
                return;
            }
        }
        else if (projectSpec.Length == 0 && to.Length > 0)
        {
            var tiles = ApiTiles();
            tile = tiles.FirstOrDefault(candidate => candidate.Id == to || string.Equals(candidate.Name, to, StringComparison.OrdinalIgnoreCase))
                   ?? (to is "self" or "me" or "this" or "jarvis" ? ResolveWindow(request, to, out _) : null);
            if (tile is not null && tile.Name.Contains(" #", StringComparison.Ordinal) is false && !_sessions.ContainsKey(tile.Id))
                tile = null; // a project's only tile, ended: the project starts again below
            projectSpec = to;
        }

        if (tile is not null)
        {
            if (!_sessions.TryGetValue(tile.Id, out var session))
            {
                Fail(request, $"The session in {tile.Name} is not running ({StateOf(tile)}). Launch or relaunch it first.");
                return;
            }
            if (wait && session.Id == request.Self)
            {
                Fail(request, "A session can't ask itself and wait for its own answer.");
                return;
            }
            TypePrompt(session, text);
            Sent(session, launched: false);
            return;
        }

        var entry = ResolveProject(projectSpec, out var projectError);
        if (entry is null)
        {
            Fail(request, projectError);
            return;
        }
        // One of its sessions that waits for a prompt takes it (not Jarvis's, nor the caller's own).
        var idle = ApiTiles()
            .Where(candidate => candidate.EntryId == entry && !candidate.Jarvis && candidate.Id != request.Self)
            .Select(candidate => _sessions.GetValueOrDefault(candidate.Id))
            .FirstOrDefault(candidate => candidate is not null && TurnState(candidate) == "idle");
        if (idle is not null)
        {
            TypePrompt(idle, text);
            Sent(idle, launched: false);
            return;
        }
        ApiLaunch(new ApiRequestForwarder(request, entry, text).Request, session => Sent(session, launched: true));
    }

    /// <summary>A launch on behalf of <c>send</c>/<c>ask</c>: the same caller and id, with the project and the text as the first prompt.</summary>
    private sealed class ApiRequestForwarder
    {
        public ApiRequest Request { get; }

        public ApiRequestForwarder(ApiRequest original, string entry, string text)
        {
            var message = JsonSerializer.SerializeToElement(new { op = "launch", project = entry, text });
            Request = new ApiRequest(new ApiCaller(original.Caller.Name, original.Caller.Phone,
                (id, ok, result, error) => { if (!ok) Fail(original, error ?? "The session did not start."); },
                original.Caller.IsClosed), original.Id, message);
        }
    }

    private void ApiWait(ApiRequest request)
    {
        var tile = ResolveWindow(request, GetString(request.Message, "window"), out var error);
        if (tile is null)
        {
            Fail(request, error);
            return;
        }
        if (tile.Id == request.Self)
        {
            Fail(request, "A session can't wait for its own turn to end.");
            return;
        }
        var timeout = WaitSeconds(request.Message);
        if (_sessions.TryGetValue(tile.Id, out var session))
        {
            AddWaiter(request, session, timeout, (done, timedOut) => WaitResult(done, timedOut, launched: false));
            return;
        }
        var ended = _endedSessions.GetValueOrDefault(tile.Id);
        Answer(request, new { window = tile.Id, name = tile.Name, state = StateOf(tile), answer = ended?.Answer, answeredAt = ended?.AnswerUtc, timedOut = false, exitCode = ended?.ExitCode });
    }

    private static int WaitSeconds(JsonElement message) => Math.Clamp(GetInt(message, "timeout", DefaultApiWaitSeconds), 0, MaxApiWaitSeconds);

    private object WaitResult(RunningSession session, bool timedOut, bool launched)
    {
        var running = _sessions.TryGetValue(session.Id, out var current) && ReferenceEquals(current, session);
        return new
        {
            window = session.Id,
            name = ApiTiles().FirstOrDefault(tile => tile.Id == session.Id)?.Name ?? NameOf(session.EntryId),
            state = running ? TurnState(session) : "ended",
            answer = session.Answer,
            answeredAt = session.AnswerUtc,
            question = QuestionOf(session),
            failure = session.TurnFailure,
            interrupted = session.Interrupted ? true : (bool?)null,
            timedOut,
            launched,
            exitCode = running ? null : session.ExitCode,
            // A session without hooks (a command other than Claude Code) reports no answers: they are on its screen.
            hooks = session.HookToken is not null,
        };
    }

    /// <summary>
    /// Answers once the session's turn is over (done, asking the user, interrupted, ended) or the time is up; at once
    /// when it is not working (a timeout of 0 just looks).
    /// </summary>
    private void AddWaiter(ApiRequest request, RunningSession session, int timeoutSeconds, Func<RunningSession, bool, object> result)
    {
        var waiter = new ApiWaiter(request, DateTime.UtcNow.AddSeconds(timeoutSeconds), result);
        session.Waiters.Add(waiter);
        EvaluateWaiters(session);
        if (session.Waiters.Count == 0) return;
        _apiTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => OnApiTimer(), _dispatcher);
        _apiTimer.Start();
    }

    private void OnApiTimer()
    {
        bool any = false;
        foreach (var session in _sessions.Values.ToList())
        {
            EvaluateWaiters(session);
            any |= session.Waiters.Count > 0;
        }
        if (!any) _apiTimer?.Stop();
    }

    /// <summary>Answers the waiters whose wait is over; a session that ended answers them all.</summary>
    private void EvaluateWaiters(RunningSession session, bool ended = false)
    {
        if (session.Waiters.Count == 0) return;
        var state = ended ? "ended" : TurnState(session);
        var now = DateTime.UtcNow;
        foreach (var waiter in session.Waiters.ToList())
        {
            if (waiter.Request.Caller.IsClosed())
            {
                session.Waiters.Remove(waiter);
                continue;
            }
            if (state == "working" && now < waiter.DeadlineUtc) continue;
            session.Waiters.Remove(waiter);
            Answer(waiter.Request, waiter.Result(session, state == "working"));
        }
    }

    /// <summary>A prompt into a session: typed in (pasted when it has several lines), then Enter.</summary>
    private void TypePrompt(RunningSession session, string text)
    {
        TypeInto(session, text, enter: true);
        ExpectPrompt(session, TypedTurnGrace);
    }

    /// <summary>
    /// Types text into a session as if at its keyboard; text of several lines goes in as one paste (otherwise each line
    /// would be sent on its own). Enter follows a moment later, so that it is not taken for part of a paste.
    /// </summary>
    private void TypeInto(RunningSession session, string text, bool enter)
    {
        var multiline = text.Contains('\n') || text.Contains('\r');
        var data = multiline ? "\u001b[200~" + text.Replace("\r\n", "\r").Replace('\n', '\r') + "\u001b[201~" : text;
        if (data.Length > 0) session.Pty?.Write(data);
        // Typed in like the phone does: prompts are answered, "waiting" is over.
        session.Asking = "";
        session.AskingInput = null;
        session.AskingText = null;
        Post(new { type = "remoteInput", id = session.Id, escape = false });
        if (!enter) return;
        Later(TimeSpan.FromMilliseconds(multiline ? 200 : 100), () =>
        {
            if (_sessions.TryGetValue(session.Id, out var current) && ReferenceEquals(current, session)) session.Pty?.Write("\r");
        });
    }

    private void Later(TimeSpan delay, Action action) =>
        Task.Delay(delay).ContinueWith(_ => _dispatcher.BeginInvoke(() => { if (!_shutDown) action(); }), TaskScheduler.Default);

    private void ApiType(ApiRequest request)
    {
        var session = RunningWindow(request, GetString(request.Message, "window"), out _, out var error);
        if (session is null)
        {
            Fail(request, error);
            return;
        }
        var text = GetString(request.Message, "text");
        var enter = GetBool(request.Message, "enter");
        if (text.Length == 0 && !enter)
        {
            Fail(request, "There is no text to type.");
            return;
        }
        if (text.Length > MaxApiText)
        {
            Fail(request, $"The text is longer than {MaxApiText:N0} characters.");
            return;
        }
        // Typed as is (an answer to a menu, a command line): a prompt that starts a turn is send's / ask's business.
        TypeInto(session, text, enter);
        Answer(request, new { window = session.Id });
    }

    /// <summary>Key names → what a terminal sends for them.</summary>
    internal static string? KeySequence(string raw)
    {
        var key = raw.Trim().ToLowerInvariant().Replace(" ", "");
        string? named = key switch
        {
            "enter" or "return" => "\r",
            "esc" or "escape" => "\u001b",
            "tab" => "\t",
            "shift+tab" or "backtab" => "\u001b[Z",
            "up" => "\u001b[A",
            "down" => "\u001b[B",
            "right" => "\u001b[C",
            "left" => "\u001b[D",
            "home" => "\u001b[H",
            "end" => "\u001b[F",
            "pageup" or "pgup" => "\u001b[5~",
            "pagedown" or "pgdn" => "\u001b[6~",
            "backspace" => "\u007f",
            "delete" or "del" => "\u001b[3~",
            "space" => " ",
            _ => null,
        };
        if (named is not null) return named;
        if (key.Length == 6 && key.StartsWith("ctrl+", StringComparison.Ordinal) && key[5] is >= 'a' and <= 'z') return ((char)(key[5] - 'a' + 1)).ToString();
        // A single character stands for itself ("y", "1"): as typed, so not lower-cased.
        var trimmed = raw.Trim();
        return trimmed.Length == 1 && !char.IsControl(trimmed[0]) ? trimmed : null;
    }

    private void ApiKeys(ApiRequest request)
    {
        var session = RunningWindow(request, GetString(request.Message, "window"), out _, out var error);
        if (session is null)
        {
            Fail(request, error);
            return;
        }
        var names = new List<string>();
        if (request.Message.TryGetProperty("keys", out var keys))
        {
            if (keys.ValueKind == JsonValueKind.Array)
                names.AddRange(keys.EnumerateArray().Where(key => key.ValueKind == JsonValueKind.String).Select(key => key.GetString()!));
            else if (keys.ValueKind == JsonValueKind.String)
                names.AddRange(keys.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        if (names.Count is 0 or > 64)
        {
            Fail(request, "Name the keys to press (at most 64), e.g. [\"down\", \"enter\"].");
            return;
        }
        var sequences = new List<string>();
        foreach (var name in names)
        {
            if (KeySequence(name) is not { } sequence)
            {
                Fail(request, $"\"{name}\" is not a key Maid knows. Keys: enter, esc, tab, shift+tab, up, down, left, right, home, end, pageup, pagedown, backspace, delete, space, ctrl+a … ctrl+z, or a single character.");
                return;
            }
            sequences.Add(sequence);
        }
        session.Asking = "";
        session.AskingInput = null;
        session.AskingText = null;
        Post(new { type = "remoteInput", id = session.Id, escape = sequences.Contains("\u001b") });
        // One at a time, as a hand would press them: a terminal app takes a burst for a paste.
        void Press(int index)
        {
            if (!_sessions.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, session))
            {
                Fail(request, "The session ended.");
                return;
            }
            session.Pty?.Write(sequences[index]);
            if (index + 1 < sequences.Count) Later(TimeSpan.FromMilliseconds(80), () => Press(index + 1));
            else Answer(request, new { window = session.Id, pressed = sequences.Count });
        }
        Press(0);
    }

    // ---- through the page -----------------------------------------------------------------------------------------

    /// <summary>Asks the page to do something with its tiles (<c>remote</c>); <paramref name="then"/> gets its <c>remoteResult</c>, or null when it did not answer.</summary>
    private void AskPage(Dictionary<string, object?> fields, Action<JsonElement?> then)
    {
        var key = $"api{++_apiLastRef}";
        _apiPage[key] = then;
        fields["type"] = "remote";
        fields["ref"] = key;
        Post(fields);
        Later(PageAnswerTime, () =>
        {
            if (_apiPage.Remove(key, out var late)) late(null);
        });
    }

    /// <summary>The page answered one of the API's requests; false when the answer is for somebody else.</summary>
    private bool OnApiPageResult(string reference, JsonElement message)
    {
        if (!_apiPage.Remove(reference, out var then)) return false;
        then(message);
        return true;
    }

    private void ApiScreen(ApiRequest request)
    {
        var tile = ResolveWindow(request, GetString(request.Message, "window"), out var error);
        if (tile is null)
        {
            Fail(request, error);
            return;
        }
        AskPage(new Dictionary<string, object?> { ["action"] = "screen", ["id"] = tile.Id, ["lines"] = Math.Clamp(GetInt(request.Message, "lines", 0), 0, 5000) }, reply =>
        {
            if (reply is not { } answer || GetString(answer, "error") is { Length: > 0 })
            {
                Fail(request, reply is { } failed ? GetString(failed, "error") : "Maid's window did not answer.");
                return;
            }
            Answer(request, new
            {
                window = tile.Id,
                name = tile.Name,
                state = StateOf(tile),
                text = GetString(answer, "text"),
                cols = GetInt(answer, "cols", 0),
                rows = GetInt(answer, "rows", 0),
            });
        });
    }

    private void ApiTileAction(ApiRequest request, string op)
    {
        var tile = ResolveWindow(request, GetString(request.Message, "window"), out var error);
        if (tile is null)
        {
            Fail(request, error);
            return;
        }
        var running = _sessions.ContainsKey(tile.Id);
        if (op == "stop" && !running)
        {
            Answer(request, new { window = tile.Id, name = tile.Name, state = StateOf(tile) });
            return;
        }
        var resume = GetBool(request.Message, "resume");
        AskPage(new Dictionary<string, object?> { ["action"] = op, ["id"] = tile.Id, ["resume"] = resume, ["quiet"] = true }, reply =>
        {
            if (reply is not { } answer || GetString(answer, "error") is { Length: > 0 })
            {
                Fail(request, reply is { } failed ? GetString(failed, "error") : "Maid's window did not answer.");
                return;
            }
            if (op == "relaunch" && !_sessions.ContainsKey(tile.Id))
            {
                Fail(request, _launchFailures.GetValueOrDefault(tile.Id) ?? "The session did not start.");
                return;
            }
            Answer(request, new { window = tile.Id, name = tile.Name });
        });
    }

    // ---- the list ------------------------------------------------------------------------------------------------

    private void ApiAddProject(ApiRequest request)
    {
        if (request.Caller.Phone)
        {
            Fail(request, "The phone can't change Maid's list of projects.");
            return;
        }
        var (entry, added, error) = CreateEntry(GetString(request.Message, "path"), GetString(request.Message, "group"));
        if (entry is null)
        {
            Fail(request, error ?? "The project could not be added.");
            return;
        }
        Post(new { type = "entryReady", entry, added });
        Answer(request, new { project = new { id = entry.Id, name = entry.Name, path = entry.Path, group = entry.Group }, added });
    }

    // ---- what the sessions do (hooks, the page) ----------------------------------------------------------------

    /// <summary>Claude Code's hooks say what its session does: the API's states and answers come from them.</summary>
    private void TrackTurn(RunningSession session, string name, JsonElement body, bool background, string kind)
    {
        switch (name)
        {
            case "UserPromptSubmit":
                session.Working = true;
                if (session.PendingPrompts > 0) session.PendingPrompts--;
                session.Interrupted = false;
                session.TurnFailure = null;
                ClearAsking(session);
                break;
            case "Stop":
                if (background) break; // it carries on by itself when its background work reports back
                session.Working = false;
                // A prompt typed in while it worked waits in its queue: that turn comes next.
                if (session.PendingPrompts > 0) session.ExpectUntilUtc = DateTime.UtcNow + TypedTurnGrace;
                session.Answer = GetString(body, "last_assistant_message").Trim();
                session.AnswerUtc = DateTime.UtcNow;
                ClearAsking(session);
                break;
            case "StopFailure":
                session.Working = false;
                session.PendingPrompts = 0;
                session.TurnFailure = new[] { GetString(body, "error"), GetString(body, "error_type"), GetString(body, "message") }
                    .FirstOrDefault(text => text.Length > 0) ?? "The turn ended with an error.";
                ClearAsking(session);
                break;
            case "Notification":
                if (kind is "permission_prompt" or "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input")
                {
                    session.Asking = kind;
                    session.AskingText = GetString(body, "message");
                    session.AskingInput = null;
                }
                break;
            case "PreToolUse":
                if (kind is "AskUserQuestion" or "ExitPlanMode")
                {
                    session.Asking = kind;
                    session.AskingText = null;
                    session.AskingInput = body.TryGetProperty("tool_input", out var input) ? input.Clone() : null;
                }
                break;
            case "PostToolUse":
                if (kind is "AskUserQuestion" or "ExitPlanMode") ClearAsking(session);
                break;
        }
        EvaluateWaiters(session);
    }

    private static void ClearAsking(RunningSession session)
    {
        session.Asking = "";
        session.AskingText = null;
        session.AskingInput = null;
    }

    /// <summary>Esc ended the turn of a session without a Stop hook (the page saw "esc to interrupt" go).</summary>
    private void OnInterrupted(string id)
    {
        if (!_sessions.TryGetValue(id, out var session) || !session.Working) return;
        session.Working = false;
        session.PendingPrompts = 0;
        session.Interrupted = true;
        EvaluateWaiters(session);
    }

    /// <summary>
    /// Sessions without hooks (a command other than Claude Code) work as long as the page sees them writing: its tiles say so. Ended tiles that
    /// left the page are forgotten.
    /// </summary>
    private void TrackTilesForApi(HashSet<string> ids)
    {
        foreach (var tile in ApiTiles())
        {
            if (!_sessions.TryGetValue(tile.Id, out var session) || session.HookToken is not null) continue;
            if (tile.Busy)
            {
                // Without hooks there is no telling one prompt's turn from the next: writing means it took them.
                session.Working = true;
                session.PendingPrompts = 0;
            }
            else if (session.Working)
            {
                session.Working = false;
            }
            EvaluateWaiters(session);
        }
        foreach (var gone in _endedSessions.Keys.Where(key => !ids.Contains(key)).ToList()) _endedSessions.Remove(gone);
        foreach (var gone in _launchFailures.Keys.Where(key => !ids.Contains(key)).ToList()) _launchFailures.Remove(gone);
    }

    // ---- what a session gets -----------------------------------------------------------------------------------

    /// <summary>
    /// A session can use the API: <c>MAID_WINDOW</c> is its own window, <c>MAID_API_PIPE</c> the pipe, and maidctl (in
    /// <c>cli</c> next to Maid.exe) comes first on its PATH.
    /// </summary>
    private void AddApiEnvironment(Dictionary<string, string> environment, string sessionId)
    {
        environment["MAID_WINDOW"] = sessionId;
        if (ApiPipeName is { } pipe) environment["MAID_API_PIPE"] = pipe;
        else environment.Remove("MAID_API_PIPE");
        var cli = Path.Combine(AppContext.BaseDirectory, "cli");
        if (!File.Exists(Path.Combine(cli, "maidctl.exe"))) return;
        var path = environment.TryGetValue("PATH", out var current) ? current : "";
        var parts = path.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(part => !SamePath(part.Trim('"'), cli));
        environment["PATH"] = string.Join(';', parts.Prepend(cli));
    }

    /// <summary>
    /// A first prompt on the command line of a session that is not the voice tile's: quoted the way the program parses
    /// its arguments, line breaks and all, when it runs directly; on one line, without what cmd.exe would expand, when a
    /// script shim runs it. Never taken for an option.
    /// </summary>
    internal static string FirstPromptArgument(string prompt, bool direct)
    {
        var text = prompt.Trim().TrimStart('-').Trim();
        if (text.Length == 0) return "";
        if (!direct)
        {
            text = Regex.Replace(new string(text.Select(c => char.IsControl(c) || c is '"' or '%' ? ' ' : c).ToArray()), @"\s+", " ").Trim();
            if (text.Length == 0) return "";
        }
        // The rules of CommandLineToArgvW: backslashes are literal unless they come before a quote.
        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in text)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }
}
