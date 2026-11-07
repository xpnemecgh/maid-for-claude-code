using System.IO;
using System.Text.Json;
using Maid.Terminal;

namespace Maid;

/// <summary>A saved dashboard entry: a project folder that a Claude Code session can be launched in.</summary>
internal sealed class Entry
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The label the entry is listed under; <see cref="Grouping.DefaultName"/> when none was chosen.</summary>
    public string Group { get; set; } = "";
}

/// <summary>A label the session list groups entries under. Kept on its own so a group can exist while empty.</summary>
internal sealed class Group
{
    public string Name { get; set; } = "";
    /// <summary>The group this one is nested in; empty at the top level of the list.</summary>
    public string Parent { get; set; } = "";
    /// <summary>A collapsed group shows only its header in the list (its nested groups fold away with it).</summary>
    public bool Collapsed { get; set; }
    /// <summary>
    /// The text colour of the terminals of its projects ("#rrggbb"), those of its nested groups included
    /// unless they have one of their own; null leaves them as they are.
    /// </summary>
    public string? Color { get; set; }
}

internal sealed class Settings
{
    /// <summary>The command every Claude Code session runs. Editable in state.json.</summary>
    public string Command { get; set; } = LaunchCommand.Default;
    /// <summary>The folder of the "all projects" session (<see cref="Hub"/>); empty for the default under the data folder.</summary>
    public string HubPath { get; set; } = "";
    public int FontSize { get; set; } = 13;
    /// <summary>Grid columns; 0 picks a layout automatically.</summary>
    public int Columns { get; set; }
    public bool SidebarCollapsed { get; set; }
    /// <summary>The session list's width in pixels, dragged at its right edge.</summary>
    public int SidebarWidth { get; set; } = 250;
    /// <summary>The help screen was closed once: it no longer opens by itself while the list is empty (F1 still opens it).</summary>
    public bool HelpSeen { get; set; }
    /// <summary>Confirm Claude Code's "trust this folder?" prompt automatically when a session starts.</summary>
    public bool AutoTrust { get; set; } = true;
    /// <summary>When a session stops, save its conversation into the project's CLAUDE.md.</summary>
    public bool SaveHistory { get; set; } = true;
    /// <summary>Upper bound for the history section; CLAUDE.md is loaded into every session's context.</summary>
    public int HistoryMaxChars { get; set; } = 24000;
    /// <summary>Run Maid, and so every session, as administrator (<see cref="Elevation"/>). Read at start.</summary>
    public bool RunAsAdmin { get; set; } = true;
    /// <summary>
    /// The ElevenLabs API key that puts the phone's dictation into words (<see cref="ElevenLabs"/>); empty takes
    /// the ELEVENLABS_API_KEY environment variable. Never sent to the page or the phone.
    /// </summary>
    public string ElevenLabsApiKey { get; set; } = "";
    /// <summary>ElevenLabs' speech-to-text model for the dictation.</summary>
    public string TranscriptionModel { get; set; } = ElevenLabs.DefaultModel;
    /// <summary>The language dictated (ISO 639, e.g. "cs" or "en"); empty: told apart for each recording.</summary>
    public string DictationLanguage { get; set; } = "";
    /// <summary>
    /// The local named pipe a push-to-talk program at this PC hands spoken requests to
    /// (<see cref="Remote.LocalPipeServer"/>); empty: none. Read at start.
    /// </summary>
    public string VoicePipe { get; set; } = Remote.LocalPipeServer.VoiceName;
    /// <summary>
    /// The local named pipe of Maid's API (<c>maidctl</c>, <see cref="Dashboard"/>'s Api part), through which Claude Code
    /// sessions and other programs of the user control Maid; empty: none. Read at start.
    /// </summary>
    public string ApiPipe { get; set; } = Remote.LocalPipeServer.ApiName;
    /// <summary>
    /// Added to <see cref="Command"/> for the voice tile, which takes the spoken requests of push-to-talk at this PC and
    /// of a voice assistant on the phone.
    /// </summary>
    public string VoiceArguments { get; set; } = DefaultVoiceArguments;

    /// <summary>The voice tile starts with Opus at extra-high effort.</summary>
    public const string DefaultVoiceArguments = "--model opus --effort xhigh";

    /// <summary>
    /// Earlier defaults. Every state.json carries the value it was saved with, so on load these are taken
    /// for "the default" and replaced by <see cref="DefaultVoiceArguments"/>; anything else is the user's own.
    /// </summary>
    public static readonly string[] FormerVoiceArguments = ["--model sonnet --effort medium"];
}

/// <summary>Remote control from the phone app (<see cref="Remote.RemoteServer"/>).</summary>
internal sealed class RemoteSettings
{
    /// <summary>Whether Maid listens for the phone at all; it does so only on its Tailscale addresses.</summary>
    public bool Enabled { get; set; }
    public int Port { get; set; } = Remote.RemoteServer.DefaultPort;
    /// <summary>The one phone that may connect; null when none is paired.</summary>
    public PairedPhone? Phone { get; set; }
    /// <summary>
    /// Tests only, set by hand in state.json: also listen on 127.0.0.1 and take connections from there
    /// without asking Tailscale who they are (an emulator reaches the PC that way). The phone's key is
    /// still required.
    /// </summary>
    public bool AllowLoopback { get; set; }
    /// <summary>tailscale.exe; empty to look for it on PATH and in Program Files.</summary>
    public string TailscalePath { get; set; } = "";
}

internal sealed class PairedPhone
{
    /// <summary>The device's name as the app reports it (e.g. "Pixel 8").</summary>
    public string Name { get; set; } = "";
    /// <summary>The phone's public key (EC P-256, SubjectPublicKeyInfo, base64); its private key never leaves the phone.</summary>
    public string PublicKey { get; set; } = "";
    /// <summary>Tailscale's stable id of the phone; connections from any other device are refused. Empty for a loopback (test) pairing.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>The phone's name in the tailnet, e.g. "pixel-8.tail1234.ts.net".</summary>
    public string NodeName { get; set; } = "";
    public DateTime PairedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
}

internal sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

internal sealed class AppState
{
    public List<Entry> Entries { get; set; } = [];
    public List<Group> Groups { get; set; } = [];
    public Settings Settings { get; set; } = new();
    /// <summary>The text Maid keeps in the CLAUDE.md of every entry (<see cref="Maid.SharedInfo"/>); empty for none.</summary>
    public string SharedInfo { get; set; } = "";
    public RemoteSettings Remote { get; set; } = new();
    public WindowPlacement? Window { get; set; }
}

/// <summary>
/// The rules for the labels entries are grouped under: names are trimmed and unique regardless of case
/// (across all levels), and a group can be nested in another. The flat list of groups is in the order
/// the user put them (new ones last), and that is the order among the groups of the same parent.
/// Entries that never got a label (those saved before groups existed) belong to the default group.
/// </summary>
internal static class Grouping
{
    public const string DefaultName = "Misc";
    public const int MaxNameLength = 40;

    public static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool IsDefault(string? name) => SameName(name, DefaultName);

    /// <summary>Trims, collapses inner whitespace and bounds the length; null when nothing is left.</summary>
    public static string? NormalizeName(string? raw, int maxLength = MaxNameLength)
    {
        if (raw is null) return null;
        var name = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > maxLength) name = name[..maxLength].TrimEnd();
        return name.Length == 0 ? null : name;
    }

    /// <summary>A colour as "#rrggbb" in lower case; null for anything else (no colour).</summary>
    public static string? NormalizeColor(string? raw)
    {
        var color = raw?.Trim().ToLowerInvariant();
        return color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit) ? color : null;
    }

    public static Group? Find(AppState state, string? name) =>
        state.Groups.Find(group => SameName(group.Name, name));

    /// <summary>The groups nested directly in <paramref name="parent"/> (empty: the top level), in their order.</summary>
    public static List<Group> Children(AppState state, string parent) =>
        state.Groups.Where(group => SameName(group.Parent, parent)).ToList();

    /// <summary>Whether <paramref name="group"/> is <paramref name="ancestor"/> or nested in it, at any depth.</summary>
    public static bool IsWithin(AppState state, Group group, Group ancestor)
    {
        var seen = new HashSet<Group>();
        for (Group? current = group; current is not null && seen.Add(current); current = Find(state, current.Parent))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    /// <summary>The groups as the list shows them: each one followed by the groups nested in it, with its depth (0 = top level).</summary>
    public static IEnumerable<(Group Group, int Depth)> Tree(AppState state)
    {
        IEnumerable<(Group, int)> Below(string parent, int depth) =>
            Children(state, parent).SelectMany(group => Below(group.Name, depth + 1).Prepend((group, depth)));
        return Below("", 0);
    }

    /// <summary>A group's name with the names of the groups it is nested in, e.g. "Work / Clients".</summary>
    public static string PathOf(AppState state, Group group)
    {
        var names = new List<string>();
        var seen = new HashSet<Group>();
        for (Group? current = group; current is not null && seen.Add(current); current = Find(state, current.Parent))
            names.Insert(0, current.Name);
        return string.Join(" / ", names);
    }

    /// <summary>
    /// Makes the state consistent: every entry is in a group (blank → default), every group an entry
    /// refers to exists exactly once (created at the top level, at the end, when missing), entries and
    /// nested groups carry their group's spelling, and a group nested in one that is not there (or in
    /// itself, also by way of others) is moved to the top level. The order of the groups is the user's and is kept.
    /// </summary>
    public static void Reconcile(AppState state)
    {
        state.Entries ??= [];
        var groups = new List<Group>();
        foreach (var group in state.Groups ?? [])
        {
            var name = NormalizeName(group.Name);
            if (name is not null && !groups.Any(existing => SameName(existing.Name, name)))
                groups.Add(new Group { Name = name, Parent = NormalizeName(group.Parent) ?? "", Collapsed = group.Collapsed, Color = NormalizeColor(group.Color) });
        }
        foreach (var group in groups)
        {
            var parent = groups.Find(candidate => SameName(candidate.Name, group.Parent));
            group.Parent = parent is null || ReferenceEquals(parent, group) ? "" : parent.Name;
        }
        // A loop (A in B, B in A) is cut at the first of its groups in the list.
        foreach (var group in groups)
        {
            var seen = new HashSet<Group> { group };
            for (var parent = groups.Find(candidate => SameName(candidate.Name, group.Parent)); parent is not null;
                 parent = groups.Find(candidate => SameName(candidate.Name, parent.Parent)))
            {
                if (ReferenceEquals(parent, group)) { group.Parent = ""; break; }
                if (!seen.Add(parent)) break; // a loop further up; cut when its own turn comes
            }
        }
        foreach (var entry in state.Entries)
        {
            var name = NormalizeName(entry.Group) ?? DefaultName;
            var group = groups.Find(candidate => SameName(candidate.Name, name));
            if (group is null) groups.Add(group = new Group { Name = name });
            entry.Group = group.Name;
        }
        state.Groups = groups;
    }
}

/// <summary>Loads and saves <see cref="AppState"/> as JSON under %APPDATA%\Maid (or MAID_DATA_DIR).</summary>
internal sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string DataDirectory { get; }
    private string StateFile => Path.Combine(DataDirectory, "state.json");

    public AppState State { get; private set; } = new();

    public StateStore() : this(Environment.GetEnvironmentVariable("MAID_DATA_DIR")) { }

    public StateStore(string? dataDirectory)
    {
        DataDirectory = !string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.GetFullPath(dataDirectory)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maid");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(StateFile))
                State = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StateFile), JsonOptions) ?? new AppState();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable state file should not prevent the dashboard from starting. Keep it around
            // for inspection instead of silently overwriting it on the next save.
            try { File.Copy(StateFile, StateFile + ".corrupt", overwrite: true); } catch (Exception) { }
            State = new AppState();
        }

        State.Entries ??= [];
        State.Entries.RemoveAll(entry => string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Path));
        Grouping.Reconcile(State);
        State.Settings ??= new Settings();
        State.SharedInfo = SharedInfo.Normalize(State.SharedInfo);
        if (string.IsNullOrWhiteSpace(State.Settings.Command))
            State.Settings.Command = LaunchCommand.Default;
        State.Settings.HubPath ??= "";
        State.Settings.VoicePipe ??= "";
        State.Settings.ApiPipe ??= "";
        State.Settings.VoiceArguments ??= Settings.DefaultVoiceArguments;
        if (Settings.FormerVoiceArguments.Contains(State.Settings.VoiceArguments.Trim()))
            State.Settings.VoiceArguments = Settings.DefaultVoiceArguments;
        State.Remote ??= new RemoteSettings();
        State.Remote.TailscalePath ??= "";
        if (State.Remote.Port is < 1024 or > 65535) State.Remote.Port = Remote.RemoteServer.DefaultPort;
        if (State.Remote.Phone is { } phone && string.IsNullOrWhiteSpace(phone.PublicKey)) State.Remote.Phone = null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var temporary = StateFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(State, JsonOptions));
            File.Move(temporary, StateFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Persistence is best effort; running sessions matter more than the saved list.
        }
    }
}
