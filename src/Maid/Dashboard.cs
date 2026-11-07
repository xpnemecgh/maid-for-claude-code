using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Maid.Terminal;

namespace Maid;

/// <summary>
/// The host side of the dashboard: owns the saved entries and the running sessions, and talks to the
/// web UI through JSON messages (<c>{ "type": ... }</c>) in both directions.
/// </summary>
internal sealed partial class Dashboard
{
    /// <summary>Beyond this much undelivered output the PTY reader is paused until the UI catches up.</summary>
    private const int MaxPendingOutputChars = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Maid's version as written in Maid.csproj (<c>&lt;Version&gt;</c>), without the commit the SDK appends after '+'.</summary>
    internal static readonly string Version = typeof(Dashboard).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "";

    private sealed class RunningSession(string id, string entryId, int run, string projectPath, int historyMaxChars, int cols, int rows)
    {
        /// <summary>Chosen by the page (one per tile); an entry can have several sessions at once.</summary>
        public string Id { get; } = id;
        /// <summary>A listed entry's id, or <see cref="Hub.EntryId"/> for a session of all projects.</summary>
        public string EntryId { get; } = entryId;
        /// <summary>Distinguishes successive launches in the same tile, so the UI can drop stale events.</summary>
        public int Run { get; } = run;
        public string ProjectPath { get; } = projectPath;
        public DateTime LaunchedUtc { get; } = DateTime.UtcNow;
        /// <summary>0 when conversations are not to be saved. Captured at launch: it is read on a background thread.</summary>
        public int HistoryMaxChars { get; } = historyMaxChars;
        public PtySession? Pty { get; set; }
        public StringBuilder Pending { get; } = new();
        public bool FlushScheduled { get; set; }
        /// <summary>Completes once the process is gone and its conversation has been saved.</summary>
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Recent output, for the phone when it starts watching.</summary>
        public Remote.TerminalReplay Replay { get; } = new(cols, rows);
        /// <summary>The console's size now, and the sizes the page's tile and the phone would like.</summary>
        public int Cols { get; set; } = cols;
        public int Rows { get; set; } = rows;
        public int DesktopCols { get; set; } = cols;
        public int DesktopRows { get; set; } = rows;
        public int PhoneCols { get; set; } = cols;
        public int PhoneRows { get; set; } = rows;
        /// <summary>The phone shows this session and it is sized for the phone (until somebody types at the PC).</summary>
        public bool PhoneOwns { get; set; }
        /// <summary>This session's path on the hook server (Claude Code only); null when it runs without hooks.</summary>
        public string? HookToken { get; set; }
        public string? HookSettingsFile { get; set; }
        /// <summary>Jarvis's own tile in All projects, where spoken requests (phone and PC) go.</summary>
        public bool Jarvis { get; set; }
        /// <summary>Voice requests typed into this session whose answer the phone or a voice pipe client is waiting for.</summary>
        public List<VoiceAsker> VoiceRefs { get; } = [];

        // What the API tells about it (Dashboard.Api.cs): from Claude Code's hooks, or without hooks from the page's tiles.
        /// <summary>A turn is running.</summary>
        public bool Working { get; set; }
        /// <summary>What it waits for the user's answer to ("" while it doesn't): a question tool, or a prompt's kind.</summary>
        public string Asking { get; set; } = "";
        public string? AskingText { get; set; }
        public JsonElement? AskingInput { get; set; }
        /// <summary>Its last answer (the Stop hook's last_assistant_message), and when it came.</summary>
        public string? Answer { get; set; }
        public DateTime? AnswerUtc { get; set; }
        public string? TurnFailure { get; set; }
        /// <summary>Esc ended its last turn.</summary>
        public bool Interrupted { get; set; }
        /// <summary>Prompts the API gave it whose turns haven't started yet, and until when they are waited for.</summary>
        public int PendingPrompts { get; set; }
        public DateTime ExpectUntilUtc { get; set; }
        /// <summary>API callers waiting until its turn is over.</summary>
        public List<ApiWaiter> Waiters { get; } = [];
        public int? ExitCode { get; set; }
    }

    private readonly StateStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _postJson;
    /// <summary>Running sessions by session id (not entry id: a project can run several at once).</summary>
    private readonly Dictionary<string, RunningSession> _sessions = [];
    /// <summary>Entries whose folder is being moved (that one and the ones inside it); they cannot be started or removed meanwhile.</summary>
    private readonly HashSet<string> _relocating = [];
    private readonly Hub _hub;
    private DispatcherTimer? _hubFileTimer;
    private bool _hubStarted;
    private int _lastRun;
    private bool _shutDown;

    /// <summary>Shows a folder picker (initial folder, title); returns null when cancelled.</summary>
    public Func<string?, string, string?>? BrowseForFolder { get; set; }

    public int RunningCount => _sessions.Count;

    public Dashboard(StateStore store, Dispatcher dispatcher, Action<string> postJson)
    {
        _store = store;
        _dispatcher = dispatcher;
        _postJson = postJson;
        _hub = new Hub(Hub.GetDirectory(store));
    }

    // ---- messages from the UI -------------------------------------------------------------------

    public void HandleMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var message = document.RootElement;
        if (message.ValueKind != JsonValueKind.Object) return;

        switch (GetString(message, "type"))
        {
            case "ready":
                SendInit();
                StartHub();
                PostRemoteStatus();
                break;
            case "browse":
                var purpose = GetString(message, "purpose");
                var title = purpose switch
                {
                    "move" => "Choose the folder to move the project into",
                    "rename" => "Choose the folder to rename",
                    _ => "Choose the project folder to add",
                };
                if (BrowseForFolder?.Invoke(NormalizePath(GetString(message, "path")), title) is { } picked)
                    Post(new { type = "browseResult", path = picked, purpose });
                break;
            case "create":
                Create(GetString(message, "path"), GetString(message, "group"));
                break;
            case "moveEntry":
                MoveEntry(GetString(message, "id"), GetString(message, "group"));
                break;
            case "renameEntry":
                RenameEntry(GetString(message, "id"), GetString(message, "name"));
                break;
            case "reorder":
                Reorder(GetString(message, "id"), GetString(message, "group"), GetInt(message, "index", int.MaxValue));
                break;
            case "addGroup":
                AddGroup(GetString(message, "name"), GetString(message, "parent"));
                break;
            case "renameGroup":
                RenameGroup(GetString(message, "name"), GetString(message, "newName"));
                break;
            case "removeGroup":
                RemoveGroup(GetString(message, "name"));
                break;
            case "reorderGroup":
                // Without a parent the group stays where it is nested; "" is the top level.
                ReorderGroup(GetString(message, "name"), message.TryGetProperty("parent", out _) ? GetString(message, "parent") : null,
                    GetInt(message, "index", int.MaxValue));
                break;
            case "toggleGroup":
                ToggleGroup(GetString(message, "name"), GetBool(message, "collapsed"));
                break;
            case "setGroupColor":
                SetGroupColor(GetString(message, "name"), GetString(message, "color"));
                break;
            case "sharedInfo":
                SetSharedInfo(GetString(message, "text"));
                break;
            case "launch":
                Launch(GetString(message, "id"), GetString(message, "entry"), GetInt(message, "cols", 120), GetInt(message, "rows", 30),
                    GetBool(message, "resume"), GetBool(message, "jarvis"), GetString(message, "prompt"));
                break;
            case "interrupted":
                OnInterrupted(GetString(message, "id"));
                break;
            case "relocate":
                Relocate(GetString(message, "id"), GetString(message, "path"));
                break;
            case "renameFolder":
                RenameFolder(GetString(message, "path"), GetString(message, "name"), GetBool(message, "renameProject"));
                break;
            case "input":
                if (_sessions.TryGetValue(GetString(message, "id"), out var target))
                    target.Pty?.Write(GetString(message, "data"));
                break;
            case "resize":
                // While the phone has the session, the tile's size waits until it is the PC's turn again.
                if (_sessions.TryGetValue(GetString(message, "id"), out var resized))
                {
                    (resized.DesktopCols, resized.DesktopRows) = (Math.Clamp(GetInt(message, "cols", 120), 2, 1000), Math.Clamp(GetInt(message, "rows", 30), 1, 500));
                    if (!resized.PhoneOwns) ApplySize(resized, notifyPage: false);
                }
                break;
            case "claim":
                OnClaim(GetString(message, "id"));
                break;
            case "tiles":
                OnTiles(message);
                break;
            case "remoteResult":
                OnRemoteResult(message);
                break;
            case "remoteSetup":
                HandleRemoteSetup(GetString(message, "op"));
                break;
            case "kill":
                if (_sessions.TryGetValue(GetString(message, "id"), out var killed))
                    killed.Pty?.Kill();
                break;
            case "remove":
                Remove(GetString(message, "id"));
                break;
            case "settings":
                UpdateSettings(message);
                break;
            case "copy":
                SetClipboardText(GetString(message, "text"));
                break;
            case "paste":
                Post(new { type = "paste", id = GetString(message, "id"), text = GetClipboardText() });
                break;
            case "openExternal":
                OpenExternal(GetString(message, "url"));
                break;
        }
    }

    private void SendInit()
    {
        Post(new
        {
            type = "init",
            entries = _store.State.Entries,
            groups = _store.State.Groups,
            settings = new
            {
                _store.State.Settings.FontSize,
                _store.State.Settings.Columns,
                _store.State.Settings.SidebarCollapsed,
                _store.State.Settings.SidebarWidth,
                _store.State.Settings.Command,
                _store.State.Settings.AutoTrust,
                _store.State.Settings.SaveHistory,
                _store.State.Settings.RunAsAdmin,
                _store.State.Settings.HelpSeen,
            },
            // For the help screen: Maid's version, where its settings are, and whether Claude Code can be found.
            version = Version,
            stateFile = Path.Combine(_store.DataDirectory, "state.json"),
            claude = new { command = _store.State.Settings.Command, path = LaunchCommand.Locate(_store.State.Settings.Command) },
            sharedInfo = _store.State.SharedInfo,
            hub = new { id = Hub.EntryId, name = Hub.Name, path = _hub.Directory },
            relocating = _relocating,
            // Sessions that outlived the page (it was reloaded); the UI re-attaches to them.
            running = _sessions.Values.OrderBy(session => session.Run)
                .Select(session => new
                {
                    id = session.Id, entry = session.EntryId, run = session.Run, pid = session.Pty?.ProcessId ?? 0,
                    hooks = session.HookToken is not null, jarvis = session.Jarvis,
                }),
            osBuild = Environment.OSVersion.Version.Build,
            elevationFailure = Elevation.Failure,
        });
    }

    private void Create(string rawPath, string rawGroup)
    {
        var (entry, added, error) = CreateEntry(rawPath, rawGroup);
        if (entry is null)
        {
            Post(new { type = "createFailed", message = error });
            return;
        }
        Post(new { type = "entryReady", entry, added });
    }

    /// <summary>
    /// Lists a project folder (once: a folder already listed gives its entry, <c>added</c> false) and gives it the shared
    /// info; null and why not when the path is no project folder.
    /// </summary>
    private (Entry? Entry, bool Added, string? Error) CreateEntry(string rawPath, string rawGroup)
    {
        var path = NormalizePath(rawPath);
        if (path is null) return (null, false, "Enter the full path of a project folder, e.g. C:\\Projects\\my-app");
        if (!Directory.Exists(path)) return (null, false, $"Folder not found: {path}");
        path = GetActualCasing(path);

        // A folder is listed once; more sessions in it are started from its row.
        var entry = _store.State.Entries.FirstOrDefault(existing =>
            string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase));
        bool added = entry is null;
        if (entry is null)
        {
            entry = new Entry
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Path = path,
                Name = UniqueName(path),
                Group = Grouping.NormalizeName(rawGroup) ?? Grouping.DefaultName,
            };
            _store.State.Entries.Add(entry);
            CommitList(); // also creates the group when the name is new
        }

        // A project that joins the list gets the shared info like the ones already on it.
        if (added && SharedInfo.Apply(entry.Path, _store.State.SharedInfo).Error is { } error)
            Post(new { type = "sharedInfoFailed", message = $"The shared info could not be written to the CLAUDE.md of {entry.Name}: {error}" });
        return (entry, added, null);
    }

    // ---- list edits: names, order, groups ---------------------------------------------------------

    /// <summary>Upper bound for a project's display name; longer ones are cut.</summary>
    private const int MaxEntryNameLength = 60;

    /// <summary>Gives an entry a name of the user's choosing; an empty name goes back to the folder's name.</summary>
    private void RenameEntry(string id, string rawName)
    {
        var entry = _store.State.Entries.FirstOrDefault(candidate => candidate.Id == id);
        if (entry is null) return;
        var name = Grouping.NormalizeName(rawName, MaxEntryNameLength) ?? UniqueName(entry.Path, except: entry);
        var clash = _store.State.Entries.FirstOrDefault(other =>
            !ReferenceEquals(other, entry) && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            ListFailed($"There is already a project named \"{clash.Name}\".");
            return;
        }
        entry.Name = name;
        CommitList();
    }

    /// <summary>
    /// Puts an entry at a position (0-based) among the entries of a group — its own or another one, which
    /// also moves it there. The list is kept flat; only the order within a group is meaningful.
    /// </summary>
    private void Reorder(string id, string rawGroup, int index)
    {
        var entry = _store.State.Entries.FirstOrDefault(candidate => candidate.Id == id);
        if (entry is null) return;
        var entries = _store.State.Entries;
        var group = Grouping.NormalizeName(rawGroup) ?? entry.Group;
        entries.Remove(entry);
        entry.Group = group;
        var members = entries.Where(other => Grouping.SameName(other.Group, group)).ToList();
        if (index < 0) index = 0;
        if (index >= members.Count) entries.Add(entry);
        else entries.Insert(entries.IndexOf(members[index]), entry);
        CommitList();
    }

    private void MoveEntry(string id, string rawGroup)
    {
        var entry = _store.State.Entries.FirstOrDefault(candidate => candidate.Id == id);
        if (entry is null) return;
        var name = Grouping.NormalizeName(rawGroup);
        if (name is null)
        {
            ListFailed("Enter a name for the group.");
            return;
        }
        entry.Group = name;
        CommitList();
    }

    /// <summary>Adds an empty group, last among the groups in <paramref name="rawParent"/> (empty: at the top level).</summary>
    private void AddGroup(string rawName, string rawParent)
    {
        var name = Grouping.NormalizeName(rawName);
        if (name is null)
        {
            ListFailed("Enter a name for the group.");
            return;
        }
        if (Grouping.Find(_store.State, name) is { } existing)
        {
            ListFailed($"There is already a group named \"{existing.Name}\".");
            return;
        }
        var parent = Grouping.Find(_store.State, rawParent);
        if (parent is null && Grouping.NormalizeName(rawParent) is not null)
        {
            ListFailed($"There is no group named \"{rawParent.Trim()}\" any more.");
            return;
        }
        _store.State.Groups.Add(new Group { Name = name, Parent = parent?.Name ?? "" });
        CommitList();
    }

    private void RenameGroup(string rawName, string rawNewName)
    {
        var group = Grouping.Find(_store.State, rawName);
        if (group is null) return;
        var newName = Grouping.NormalizeName(rawNewName);
        if (newName is null)
        {
            ListFailed("Enter a name for the group.");
            return;
        }
        var clash = Grouping.Find(_store.State, newName);
        if (clash is not null && !ReferenceEquals(clash, group))
        {
            ListFailed($"There is already a group named \"{clash.Name}\".");
            return;
        }
        foreach (var entry in _store.State.Entries.Where(entry => Grouping.SameName(entry.Group, group.Name)))
            entry.Group = newName;
        foreach (var child in Grouping.Children(_store.State, group.Name))
            child.Parent = newName;
        group.Name = newName;
        CommitList();
    }

    /// <summary>
    /// Removes a group. Its projects go to the group it was nested in (last there), or to the default
    /// group when it was at the top level; the groups nested in it move up a level, into its place.
    /// </summary>
    private void RemoveGroup(string rawName)
    {
        var group = Grouping.Find(_store.State, rawName);
        if (group is null) return;
        var members = _store.State.Entries.Where(entry => Grouping.SameName(entry.Group, group.Name)).ToList();
        if (members.Count > 0 && Grouping.IsDefault(group.Name))
        {
            ListFailed($"\"{group.Name}\" is where projects without a group go; move its projects somewhere else first.");
            return;
        }
        var target = group.Parent.Length > 0 ? group.Parent : Grouping.DefaultName;
        foreach (var entry in members)
        {
            _store.State.Entries.Remove(entry);
            _store.State.Entries.Add(entry);
            entry.Group = target;
        }
        var children = Grouping.Children(_store.State, group.Name);
        var groups = _store.State.Groups;
        groups.RemoveAll(children.Contains);
        groups.InsertRange(groups.IndexOf(group), children);
        foreach (var child in children)
            child.Parent = group.Parent;
        groups.Remove(group);
        CommitList();
    }

    /// <summary>
    /// Puts a group at a position (0-based) among the groups nested in <paramref name="rawParent"/>
    /// (empty: the top level; null: the group's own parent), which also moves it there with everything in it.
    /// </summary>
    private void ReorderGroup(string rawName, string? rawParent, int index)
    {
        var group = Grouping.Find(_store.State, rawName);
        if (group is null) return;
        var parent = Grouping.Find(_store.State, rawParent ?? group.Parent);
        if (parent is null && Grouping.NormalizeName(rawParent ?? group.Parent) is not null) return;
        if (parent is not null && Grouping.IsWithin(_store.State, parent, group))
        {
            ListFailed($"\"{group.Name}\" cannot be put inside itself.");
            return;
        }
        var groups = _store.State.Groups;
        groups.Remove(group);
        group.Parent = parent?.Name ?? "";
        var siblings = Grouping.Children(_store.State, group.Parent);
        if (index < 0) index = 0;
        if (index >= siblings.Count) groups.Add(group);
        else groups.Insert(groups.IndexOf(siblings[index]), group);
        CommitList();
    }

    private void ToggleGroup(string rawName, bool collapsed)
    {
        var group = Grouping.Find(_store.State, rawName);
        if (group is null || group.Collapsed == collapsed) return;
        group.Collapsed = collapsed;
        _store.Save();
    }

    /// <summary>
    /// Gives a group the colour its projects' terminals write in (and those of the groups nested in it
    /// without a colour of their own); anything but "#rrggbb" takes it away.
    /// </summary>
    private void SetGroupColor(string rawName, string rawColor)
    {
        var group = Grouping.Find(_store.State, rawName);
        if (group is null) return;
        group.Color = Grouping.NormalizeColor(rawColor);
        CommitList();
    }

    // ---- shared info ------------------------------------------------------------------------------

    /// <summary>
    /// Stores the text and brings the CLAUDE.md (and so the AGENTS.md) of every listed project in line
    /// with it. <paramref name="fromHub"/>: the text comes from the hub's shared-info.md, not from the page.
    /// </summary>
    private void SetSharedInfo(string raw, bool fromHub = false)
    {
        var text = SharedInfo.Normalize(raw);
        _store.State.SharedInfo = text;
        _store.Save();
        _hub.WriteSharedInfo(text);

        int changed = 0;
        var failed = new List<object>();
        foreach (var entry in _store.State.Entries)
        {
            var result = SharedInfo.Apply(entry.Path, text);
            if (result.Error is not null) failed.Add(new { name = entry.Name, message = result.Error });
            else if (result.Changed) changed++;
        }
        Post(new { type = "sharedInfoSaved", text, changed, failed, source = fromHub ? "hub" : "page" });
    }

    // ---- the hub: a session about all projects ------------------------------------------------------

    /// <summary>
    /// Once the page is there: takes over what was saved to the hub's shared-info.md while Maid was not
    /// watching, and from then on watches it.
    /// </summary>
    private void StartHub()
    {
        if (_hubStarted) return;
        _hubStarted = true;
        if (!_hub.Exists) return;
        if (_hub.ReadSharedInfo() is { } text && text != _store.State.SharedInfo) SetSharedInfo(text, fromHub: true);
        WatchHub();
    }

    /// <summary>
    /// Brings the CLAUDE.md and AGENTS.md of every project up to date, in the background (folders can
    /// be slow to reach): every project has its AGENTS.md without waiting for a session to start.
    /// </summary>
    public void SyncProjects()
    {
        var paths = _store.State.Entries.Select(entry => entry.Path).ToList();
        var info = _store.State.SharedInfo;
        Task.Run(() => { foreach (var path in paths) SharedInfo.Apply(path, info); });
    }

    private void WatchHub()
    {
        // Saving a file raises several events in a row; the file is read once they settle.
        _hub.Watch(() => _dispatcher.BeginInvoke(() =>
        {
            if (_shutDown) return;
            _hubFileTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => OnHubFileChanged(), _dispatcher);
            _hubFileTimer.Stop();
            _hubFileTimer.Start();
        }));
    }

    /// <summary>shared-info.md was saved (by the hub's session, or anyone): its text goes into every project.</summary>
    internal void OnHubFileChanged()
    {
        _hubFileTimer?.Stop();
        // Deleting the file is not taken as emptying the text; the file comes back at the next session.
        if (_hub.ReadSharedInfo() is { } text && text != _store.State.SharedInfo)
            SetSharedInfo(text, fromHub: true);
    }

    /// <summary>Saves the list after a change to entries or groups and sends the page the new one.</summary>
    private void CommitList()
    {
        Grouping.Reconcile(_store.State);
        _store.Save();
        Post(new { type = "list", entries = _store.State.Entries, groups = _store.State.Groups });
        SendListToPhone();
    }

    private void ListFailed(string message) => Post(new { type = "listFailed", message });

    /// <summary>
    /// Starts a session of an entry (or of the hub); <paramref name="id"/> names the session (the page's
    /// tile).
    /// </summary>
    private void Launch(string id, string entryId, int cols, int rows, bool resume, bool jarvis = false, string prompt = "")
    {
        jarvis = jarvis && entryId == Hub.EntryId;
        if (id.Length == 0) return;
        if (_sessions.ContainsKey(id))
        {
            LaunchFailed(id, "This session is still running.");
            return;
        }

        string path;
        if (entryId == Hub.EntryId)
        {
            try
            {
                // Its instructions carry the list as it is now, and shared-info.md the current text.
                _hub.Prepare(_store.State);
                WatchHub();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                LaunchFailed(id, $"Could not set up {_hub.Directory}: {e.Message}");
                return;
            }
            path = _hub.Directory;
        }
        else
        {
            var entry = _store.State.Entries.FirstOrDefault(candidate => candidate.Id == entryId);
            if (entry is null)
            {
                LaunchFailed(id, "This entry no longer exists.");
                return;
            }
            if (_relocating.Contains(entry.Id))
            {
                LaunchFailed(id, "This project's folder is being moved; start it once that is done.");
                return;
            }
            if (!Directory.Exists(entry.Path))
            {
                LaunchFailed(id, $"Folder not found: {entry.Path}");
                return;
            }
            // Claude Code reads CLAUDE.md when it starts: make sure it carries the shared
            // info by then, also where the file was replaced meanwhile or the folder was out of reach
            // when the text changed.
            SharedInfo.Apply(entry.Path, _store.State.SharedInfo);
            path = entry.Path;
        }

        var settings = _store.State.Settings;
        var command = settings.Command;
        if (resume) command = LaunchCommand.WithResume(command);

        var session = new RunningSession(id, entryId, ++_lastRun, path, settings.SaveHistory ? Math.Max(4000, settings.HistoryMaxChars) : 0, cols, rows)
        {
            Jarvis = jarvis,
        };
        // The voice tile gets its own options (by default Opus at extra-high effort).
        if (jarvis && settings.VoiceArguments.Trim() is { Length: > 0 } voiceArguments) command += " " + voiceArguments;
        // Claude Code says through its hooks when it works and when it waits.
        command += HookArguments(session, jarvis, jarvis ? prompt : "");
        // A first prompt (from the API) goes last.
        if (!jarvis && prompt.Trim().Length > 0
            && FirstPromptArgument(prompt, LaunchCommand.RunsDirectly(command)) is { Length: > 0 } first)
            command += " " + first;
        var environment = LaunchCommand.BuildEnvironment();
        AddApiEnvironment(environment, id);
        try
        {
            session.Pty = PtySession.Start(
                LaunchCommand.BuildCommandLine(command), path, environment, cols, rows,
                text => OnOutput(session, text),
                exitCode => OnExited(session, exitCode));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            if (session.HookSettingsFile is { } unused) TryDelete(unused);
            LaunchFailed(id, $"Could not start \"{command}\": {e.Message}");
            return;
        }

        _sessions[id] = session;
        _endedReplays.Remove(id);
        _endedSessions.Remove(id);
        _launchFailures.Remove(id);
        PostAll(new { type = "started", id, entry = entryId, run = session.Run, pid = session.Pty.ProcessId, command,
            hooks = session.HookToken is not null, jarvis });
        // The phone watching this tile sees the new session from its start, at its own size.
        if (_phoneViewing == id) PhoneView(id, session.PhoneCols, session.PhoneRows);
    }

    private void LaunchFailed(string id, string message)
    {
        _launchFailures[id] = message;
        Post(new { type = "launchFailed", id, message });
    }

    // ---- moving a project's folder ---------------------------------------------------------------------

    /// <summary>
    /// Moves an entry's folder, with all its files, to <paramref name="rawPath"/> and points the entry
    /// (and any listed project inside that folder) there. The move runs in the background; the page
    /// hears <c>relocating</c>, <c>relocateProgress</c> (a copy to another drive) and then
    /// <c>relocated</c> (after a <c>list</c>) or <c>relocateFailed</c>.
    /// </summary>
    private void Relocate(string id, string rawPath)
    {
        var entry = _store.State.Entries.FirstOrDefault(candidate => candidate.Id == id);
        if (entry is null) return;
        void Fail(string message) => Post(new { type = "relocateFailed", id, message });

        if (_relocating.Contains(id))
        {
            Fail("This project's folder is being moved already.");
            return;
        }
        var target = NormalizePath(rawPath);
        if (target is null)
        {
            Fail("Enter the full path of the new folder, e.g. C:\\Projects\\my-app");
            return;
        }
        var source = entry.Path;
        if (SamePath(target, source))
        {
            Fail("That is where the project is already.");
            return;
        }
        if (IsInside(target, source))
        {
            Fail("A folder cannot be moved into itself.");
            return;
        }
        if (_store.State.Entries.FirstOrDefault(other => !ReferenceEquals(other, entry) && SamePath(other.Path, target)) is { } clash)
        {
            Fail($"{clash.Name} is listed with that folder already.");
            return;
        }
        if (!Directory.Exists(source))
        {
            if (!Directory.Exists(target))
            {
                Fail($"Folder not found: {source}");
                return;
            }
            // Moved by hand already: the entry only has to follow it.
            entry.Path = GetActualCasing(target);
            ClaudeMdFile.MoveBases(source, entry.Path);
            CommitList();
            Post(new { type = "relocated", id, path = entry.Path, note = $"{source} was not there any more; {entry.Name} now points to {entry.Path} (nothing had to be moved)." });
            return;
        }
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
        {
            Fail($"{target} already exists. Choose a folder that does not exist yet (or an empty one).");
            return;
        }

        // The listed projects inside the folder move with it.
        var moves = ProjectsIn(source, target);
        int running = _sessions.Values.Count(session => moves.Any(move => move.Id == session.EntryId));
        if (running > 0)
        {
            Fail(running == 1
                ? "Stop its session first: a running session keeps the folder in use."
                : $"Stop its {running} sessions first: a running session keeps the folder in use.");
            return;
        }

        foreach (var move in moves) _relocating.Add(move.Id);
        Post(new { type = "relocating", id, path = target });
        long lastProgress = 0;
        Task.Run(() =>
        {
            string? warning = null, error = null;
            try
            {
                warning = FolderMove.Move(source, target, files =>
                {
                    if (Environment.TickCount64 - lastProgress < 250) return;
                    lastProgress = Environment.TickCount64;
                    _dispatcher.BeginInvoke(() => Post(new { type = "relocateProgress", id, files }));
                });
                foreach (var move in moves) FolderMove.MoveTranscripts(move.Old, move.New);
                ClaudeMdFile.MoveBases(source, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = e.Message + InUseHint(e);
            }
            _dispatcher.BeginInvoke(() =>
            {
                foreach (var move in moves) _relocating.Remove(move.Id);
                if (error is not null)
                {
                    Post(new { type = "relocateFailed", id, message = $"{source} could not be moved: {error}" });
                    return;
                }
                foreach (var move in moves)
                {
                    if (_store.State.Entries.FirstOrDefault(candidate => candidate.Id == move.Id) is { } moved)
                        moved.Path = move.New;
                }
                CommitList();
                Post(new { type = "relocated", id, path = target, warning });
            });
        });
    }

    // ---- renaming a folder ------------------------------------------------------------------------------

    /// <summary>
    /// Gives a folder — a project's own, or one further up that holds projects — another name where it
    /// is, and points every listed project in it at its new path (Claude Code's transcripts of them go
    /// along). With <paramref name="renameProject"/> the project listed with that very folder is named
    /// after it again. Answered with a <c>list</c> and <c>folderRenamed { from, to, projects, note? }</c>,
    /// or with <c>renameFolderFailed { message }</c>. A rename is instant, so it is done right here.
    /// </summary>
    private void RenameFolder(string rawPath, string rawName, bool renameProject)
    {
        void Fail(string message) => Post(new { type = "renameFolderFailed", message });

        var source = NormalizePath(rawPath);
        if (source is null)
        {
            Fail("Enter the full path of the folder to rename, e.g. C:\\Projects\\my-app");
            return;
        }
        if (Path.GetDirectoryName(source) is null)
        {
            Fail($"{source} is a drive, not a folder.");
            return;
        }
        var name = rawName.Trim();
        if (FolderMove.CheckName(name) is { } problem)
        {
            Fail(problem);
            return;
        }
        bool exists = Directory.Exists(source);
        if (exists) source = GetActualCasing(source);
        var parent = Path.GetDirectoryName(source)!;
        var target = Path.Combine(parent, name);
        if (string.Equals(Path.GetFileName(source), name, StringComparison.Ordinal))
        {
            Fail($"The folder is called {name} already.");
            return;
        }
        // Only the case of letters changes: still the same folder as far as Windows is concerned.
        bool caseOnly = SamePath(source, target);
        if (!exists)
        {
            if (!Directory.Exists(target) || ProjectsIn(source, target).Count == 0)
            {
                Fail($"Folder not found: {source}");
                return;
            }
            // Renamed by hand already: the projects only have to follow it.
            target = GetActualCasing(target);
        }
        else if (!caseOnly && (Directory.Exists(target) || File.Exists(target)))
        {
            Fail($"There is a {(File.Exists(target) ? "file" : "folder")} named {name} in {parent} already.");
            return;
        }
        else if (MaidFolderIn(source) is { } own)
        {
            Fail($"{source} cannot be renamed while Maid is running: {own} is in it.");
            return;
        }

        var moves = ProjectsIn(source, target);
        var movedIds = moves.Select(move => move.Id).ToHashSet();
        var running = _sessions.Values.Where(session => movedIds.Contains(session.EntryId)).ToList();
        if (running.Count > 0)
        {
            var names = string.Join(", ", running.Select(session => NameOf(session.EntryId)).Distinct());
            Fail(running.Count == 1
                ? $"Stop the session of {names} first: a running session keeps the folder in use."
                : $"Stop the {running.Count} sessions running in it first ({names}): a running session keeps the folder in use.");
            return;
        }
        if (moves.Any(move => _relocating.Contains(move.Id)))
        {
            Fail("A project in it is being moved; rename the folder once that is done.");
            return;
        }
        if (_store.State.Entries.FirstOrDefault(other => !movedIds.Contains(other.Id) && moves.Any(move => SamePath(move.New, other.Path))) is { } clash)
        {
            Fail($"{clash.Name} is listed with {clash.Path} already, where a project in the folder would end up; remove one of them from the list first.");
            return;
        }

        if (exists)
        {
            // Maid's own working folder in there would keep it in use; it follows the rename.
            var workingDirectory = Environment.CurrentDirectory;
            bool leave = SamePath(workingDirectory, source) || IsInside(workingDirectory, source);
            try
            {
                if (leave) Directory.SetCurrentDirectory(parent);
                Directory.Move(source, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (leave) TrySetCurrentDirectory(workingDirectory);
                Fail($"{source} could not be renamed: {e.Message}{InUseHint(e)}");
                return;
            }
            if (leave) TrySetCurrentDirectory(target + workingDirectory[source.Length..]);
        }

        foreach (var move in moves)
        {
            if (_store.State.Entries.FirstOrDefault(candidate => candidate.Id == move.Id) is { } moved)
                moved.Path = move.New;
            FolderMove.MoveTranscripts(move.Old, move.New);
        }
        ClaudeMdFile.MoveBases(source, target);
        if (renameProject && _store.State.Entries.FirstOrDefault(entry => SamePath(entry.Path, target)) is { } project)
            project.Name = UniqueName(target, except: project);
        CommitList();
        Post(new
        {
            type = "folderRenamed",
            from = source,
            to = target,
            projects = moves.Count,
            note = exists ? null : $"{source} was not there any more; the projects in it now point to {target} (nothing had to be renamed).",
        });
    }

    /// <summary>
    /// The listed projects in <paramref name="folder"/> (it and the ones below it), each with its path
    /// once that folder is <paramref name="target"/>.
    /// </summary>
    private List<(string Id, string Old, string New)> ProjectsIn(string folder, string target) =>
        _store.State.Entries
            .Where(entry => SamePath(entry.Path, folder) || IsInside(entry.Path, folder))
            .Select(entry => (entry.Id, Old: entry.Path,
                New: SamePath(entry.Path, folder) ? target : target + entry.Path[folder.TrimEnd('\\', '/').Length..]))
            .ToList();

    /// <summary>Which of the folders Maid keeps in use while it runs is in <paramref name="folder"/>, if any.</summary>
    private string? MaidFolderIn(string folder)
    {
        foreach (var (path, what) in new[]
        {
            (AppContext.BaseDirectory, "Maid's program folder"),
            (_store.DataDirectory, "Maid's data folder"),
            (_hub.Directory, "the All projects folder"),
        })
        {
            if (SamePath(path, folder) || IsInside(path, folder)) return $"{what} ({path.TrimEnd('\\', '/')})";
        }
        return null;
    }

    private string NameOf(string entryId) =>
        entryId == Hub.EntryId ? Hub.Name : _store.State.Entries.FirstOrDefault(entry => entry.Id == entryId)?.Name ?? entryId;

    private static void TrySetCurrentDirectory(string path)
    {
        try { Directory.SetCurrentDirectory(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    /// <summary>
    /// Access denied or a sharing violation: something has a file in the folder, or the folder itself,
    /// open. (Renaming a folder like that is an IOException with the access-denied code.)
    /// </summary>
    private static string InUseHint(Exception e) =>
        e is UnauthorizedAccessException || (e.HResult & 0xFFFF) is 5 or 32
            ? " A program (an editor, a terminal, Explorer) may still be using something in it."
            : "";

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is somewhere below <paramref name="folder"/>.</summary>
    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);

    private void Remove(string id)
    {
        if (_relocating.Contains(id))
        {
            ListFailed("This project's folder is being moved; remove it once that is done.");
            return;
        }
        foreach (var session in _sessions.Values.Where(session => session.EntryId == id))
            session.Pty?.Kill();
        if (_store.State.Entries.Find(entry => entry.Id == id) is { } removed)
        {
            _store.State.Entries.Remove(removed);
            _store.Save();
            // The shared info is for the projects on the list; nobody would update it there any more.
            SharedInfo.Apply(removed.Path, null);
        }
        Post(new { type = "entryRemoved", id });
        SendListToPhone();
    }

    /// <summary>The session list's width limits, as the page enforces them while it's dragged.</summary>
    private const int SidebarMinWidth = 160, SidebarMaxWidth = 700;

    private void UpdateSettings(JsonElement message)
    {
        var settings = _store.State.Settings;
        if (message.TryGetProperty("fontSize", out var fontSize) && fontSize.TryGetInt32(out var size))
            settings.FontSize = Math.Clamp(size, 8, 28);
        if (message.TryGetProperty("columns", out var columns) && columns.TryGetInt32(out var count))
            settings.Columns = Math.Clamp(count, 0, 6);
        if (message.TryGetProperty("sidebarCollapsed", out var collapsed) && collapsed.ValueKind is JsonValueKind.True or JsonValueKind.False)
            settings.SidebarCollapsed = collapsed.GetBoolean();
        if (message.TryGetProperty("sidebarWidth", out var width) && width.TryGetInt32(out var pixels))
            settings.SidebarWidth = Math.Clamp(pixels, SidebarMinWidth, SidebarMaxWidth);
        if (message.TryGetProperty("helpSeen", out var helpSeen) && helpSeen.ValueKind is JsonValueKind.True or JsonValueKind.False)
            settings.HelpSeen = helpSeen.GetBoolean();
        _store.Save();
    }

    // ---- session events (background threads) ------------------------------------------------------

    private void OnOutput(RunningSession session, string text)
    {
        lock (session.Pending)
        {
            // Coalesce: many small reads become one message per dispatcher turn.
            while (session.Pending.Length > MaxPendingOutputChars)
                Monitor.Wait(session.Pending, 100);
            session.Pending.Append(text);
            if (session.FlushScheduled) return;
            session.FlushScheduled = true;
        }
        _dispatcher.BeginInvoke(() => Flush(session));
    }

    private void Flush(RunningSession session)
    {
        string data;
        lock (session.Pending)
        {
            data = session.Pending.ToString();
            session.Pending.Clear();
            session.FlushScheduled = false;
            Monitor.PulseAll(session.Pending);
        }
        if (data.Length == 0) return;
        session.Replay.Append(data);
        Post(new { type = "output", id = session.Id, run = session.Run, data });
        if (_phone is { } phone && _phoneViewing == session.Id)
            phone.Send(Serialize(new { type = "output", id = session.Id, run = session.Run, data }));
    }

    /// <summary>Runs on the session's wait thread, whichever way the session ended.</summary>
    private void OnExited(RunningSession session, int exitCode)
    {
        // Claude Code appends to its transcript as the conversation goes, so it is complete by now.
        var history = session.HistoryMaxChars > 0
            ? ConversationHistory.Save(session.ProjectPath, session.LaunchedUtc, session.HistoryMaxChars)
            : null;
        // The session may have edited CLAUDE.md or AGENTS.md itself; the other one follows. (Saving the
        // history went through the same, so that is done already when something was saved.)
        if (history?.File is null) ClaudeMdFile.Sync(session.ProjectPath);
        session.Finished.TrySetResult();

        _dispatcher.BeginInvoke(() =>
        {
            Flush(session);
            ForgetHooks(session);
            session.ExitCode = exitCode;
            session.Working = false;
            if (_sessions.TryGetValue(session.Id, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(session.Id);
                // The tile stays on the page with its last screen; the phone can still look at it.
                _endedReplays[session.Id] = session.Replay;
                _endedSessions[session.Id] = session;
            }
            EvaluateWaiters(session, ended: true);
            PostAll(new
            {
                type = "exited",
                id = session.Id,
                entry = session.EntryId,
                run = session.Run,
                exitCode,
                historySaved = history?.Conversations ?? 0,
                historyFile = history?.File,
                historyError = history?.Error,
            });
        });
    }

    /// <summary>
    /// Stops every session and waits (bounded) until they are gone and their conversations are saved.
    /// The page is going away with the window, so from here on nothing is posted to it any more.
    /// </summary>
    public void Shutdown()
    {
        _shutDown = true;
        _hookServer?.Dispose();
        _voicePipe?.Dispose();
        _apiPipe?.Dispose();
        _apiTimer?.Stop();
        _hubFileTimer?.Stop();
        _hub.Dispose();
        DisableRemote();
        var finishing = _sessions.Values.Where(session => session.Pty is not null).Select(session => session.Finished.Task).ToArray();
        foreach (var session in _sessions.Values)
            session.Pty?.Kill();
        _sessions.Clear();
        Task.WaitAll(finishing, TimeSpan.FromSeconds(12));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private void Post(object message)
    {
        // Sessions outlive the page by a moment when the window closes: their last output and exit
        // notifications have nowhere to go then.
        if (!_shutDown)
            _postJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private static string? NormalizePath(string raw)
    {
        var path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"').Trim());
        if (path.Length == 0) return null;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            path = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var root = Path.GetPathRoot(path) ?? "";
        return path.Length > root.Length ? path.TrimEnd('\\', '/') : path;
    }

    /// <summary>Returns the path as it is spelled on disk ("x:\maid" → "X:\Maid").</summary>
    private static string GetActualCasing(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (directory.Parent is null) return directory.FullName.ToUpperInvariant();
            var actualName = directory.Parent.GetFileSystemInfos(directory.Name).FirstOrDefault()?.Name ?? directory.Name;
            return Path.Combine(GetActualCasing(directory.Parent.FullName), actualName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    /// <summary>The folder's name, with a counter when another entry already carries it.</summary>
    private string UniqueName(string path, Entry? except = null)
    {
        var baseName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(baseName)) baseName = path;
        var name = baseName;
        bool Taken(string candidate) => _store.State.Entries.Any(entry =>
            !ReferenceEquals(entry, except) && string.Equals(entry.Name, candidate, StringComparison.OrdinalIgnoreCase));
        for (int suffix = 2; Taken(name); suffix++)
            name = $"{baseName} ({suffix})";
        return name;
    }

    private static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try { Elevation.OpenUrl(uri.AbsoluteUri); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

    private static void SetClipboardText(string text)
    {
        if (text.Length == 0) return;
        // The clipboard is a shared resource; another process may briefly hold it open.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(text, copy: true); return; }
            catch (COMException) { Thread.Sleep(20); }
        }
    }

    private static string GetClipboardText()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (COMException) { Thread.Sleep(20); }
        }
        return "";
    }

    private static string GetString(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int GetInt(JsonElement message, string name, int fallback) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    private static bool GetBool(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
