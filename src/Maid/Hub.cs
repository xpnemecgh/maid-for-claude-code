using System.IO;
using System.Text;

namespace Maid;

/// <summary>
/// The folder of the "all projects" session: a Claude Code session that is about every listed project
/// at once rather than one of them. Its CLAUDE.md tells it so and lists the projects; its
/// <c>shared-info.md</c> is the shared info (<see cref="SharedInfo"/>) as a file — Maid writes it when the
/// text changes in Maid, and takes it over (into every project) when the session edits it.
/// </summary>
internal sealed class Hub : IDisposable
{
    /// <summary>The id the page launches the hub's sessions with; entry ids are hex, so it never clashes.</summary>
    public const string EntryId = "~hub";
    public const string Name = "All projects";
    public const string SharedInfoFileName = "shared-info.md";
    public const string SectionStart = "<!-- maid:hub:start -->";
    public const string SectionEnd = "<!-- maid:hub:end -->";

    private FileSystemWatcher? _watcher;

    public Hub(string directory) => Directory = directory;

    public string Directory { get; }
    public string SharedInfoFile => Path.Combine(Directory, SharedInfoFileName);
    public bool Exists => System.IO.Directory.Exists(Directory);

    /// <summary>The hub of a state: <c>settings.hubPath</c>, or "Hub" in Maid's data folder.</summary>
    public static string GetDirectory(StateStore store)
    {
        var configured = Environment.ExpandEnvironmentVariables(store.State.Settings.HubPath ?? "").Trim();
        try
        {
            if (configured.Length > 0 && Path.IsPathFullyQualified(configured)) return Path.GetFullPath(configured).TrimEnd('\\', '/');
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        return Path.Combine(store.DataDirectory, "Hub");
    }

    /// <summary>Sets the folder up for a session: its instructions with the current list, and the shared info file.</summary>
    public void Prepare(AppState state)
    {
        System.IO.Directory.CreateDirectory(Directory);
        WriteSharedInfo(state.SharedInfo);
        ClaudeMdFile.Update(Directory, text => Merge(text, Describe(state)));
    }

    // ---- shared-info.md -----------------------------------------------------------------------------

    /// <summary>The text of shared-info.md as it would be stored; null when there is no file (or no hub).</summary>
    public string? ReadSharedInfo()
    {
        try
        {
            if (!File.Exists(SharedInfoFile)) return null;
            // Another program may be writing it right now.
            using var stream = new FileStream(SharedInfoFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return SharedInfo.Normalize(reader.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Puts the text in shared-info.md, unless it holds that text already (an editor there would see a needless change).</summary>
    public void WriteSharedInfo(string text)
    {
        if (!Exists || ReadSharedInfo() == text) return;
        try
        {
            File.WriteAllText(SharedInfoFile, text.Length == 0 ? "" : text.Replace("\n", "\r\n") + "\r\n", new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Calls <paramref name="changed"/> (on a background thread) whenever shared-info.md may have changed.</summary>
    public void Watch(Action changed)
    {
        if (_watcher is not null || !Exists) return;
        _watcher = new FileSystemWatcher(Directory, SharedInfoFileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => changed();
        _watcher.Created += (_, _) => changed();
        _watcher.Renamed += (_, _) => changed(); // editors that save through a temporary file
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose() => _watcher?.Dispose();

    // ---- CLAUDE.md --------------------------------------------------------------------------------

    /// <summary>What a session in the hub is told: what the folder is for, how the shared info works, the projects.</summary>
    internal static string Describe(AppState state)
    {
        var text = new StringBuilder();
        text.Append($"""
            <!-- Maid writes this section whenever it starts a session here; changes made to it are overwritten. -->
            # Maid: all projects

            This folder belongs to Maid, the dashboard that runs the user's Claude Code sessions, one project folder each. A session here is about all of the projects listed below at once rather than about one of them: the user starts it to change something across their projects, most often the instructions they all share.

            ## Instructions shared by all projects

            - `{SharedInfoFileName}` in this folder is the text Maid keeps in the `CLAUDE.md` and `AGENTS.md` of every listed project (and of every project added later). To add, change or remove an instruction for all projects, edit `{SharedInfoFileName}`: as soon as it is saved, Maid writes it into every project. It is Markdown that goes in as it is written, up to {SharedInfo.MaxChars:N0} characters.
            - Do not edit that text in the projects' own files (the part between the `maid:shared-info` comments): Maid overwrites it there. The part between the `maid:conversation-history` comments is Maid's as well.
            - Maid keeps each project's `AGENTS.md` identical to its `CLAUDE.md`; a change to one of them reaches the other.

            ## Other changes across the projects

            For anything else that should apply to several or all projects (a file each of them should have, a setting, the same fix everywhere), work in the project folders listed below, and say which ones you changed. Only the projects listed here are the user's projects in Maid.

            ## Handing work to the projects' own sessions

            `maidctl` (on the PATH of Maid's sessions; `maidctl help` lists everything) controls Maid itself: `maidctl windows` shows what runs and what each session is doing, `maidctl ask <project> "<task>"` gives a project's session a task and prints its answer (a session of that project that waits for a prompt takes it, else a new one starts), `maidctl launch`, `send`, `wait`, `screen` and `stop` do the rest. A project's own session works with that project's instructions, so hand it work that belongs there rather than doing it from here.

            ## Projects

            """);
        if (state.Entries.Count == 0) text.Append("No projects are listed yet.\n");
        foreach (var (group, _) in Grouping.Tree(state))
        {
            var members = state.Entries.Where(entry => Grouping.SameName(entry.Group, group.Name)).ToList();
            if (members.Count == 0) continue;
            text.Append($"\n### {Escape(Grouping.PathOf(state, group))}\n\n");
            foreach (var entry in members) text.Append($"- **{Escape(entry.Name)}**: `{entry.Path}`\n");
        }
        return text.ToString().Replace("\r\n", "\n").TrimEnd();
    }

    /// <summary>Names are the user's: they must not be able to end the section.</summary>
    private static string Escape(string name) => name.Replace("<!--", "&lt;!--");

    /// <summary>Returns <paramref name="claudeMd"/> with Maid's section in it (first in the file), carrying <paramref name="description"/>.</summary>
    internal static string Merge(string claudeMd, string description)
    {
        var newline = claudeMd.Contains("\r\n") ? "\r\n" : "\n";
        var section = $"{SectionStart}\n{description}\n{SectionEnd}".Replace("\n", newline);
        int start = claudeMd.IndexOf(SectionStart, StringComparison.Ordinal);
        int end = start < 0 ? -1 : claudeMd.IndexOf(SectionEnd, start, StringComparison.Ordinal);
        if (end >= 0) return claudeMd[..start] + section + claudeMd[(end + SectionEnd.Length)..];
        var rest = claudeMd.TrimStart('\r', '\n');
        return section + newline + (rest.Length == 0 ? "" : newline + rest);
    }
}
