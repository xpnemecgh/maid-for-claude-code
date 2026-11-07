using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Maid;

/// <summary>
/// A project's CLAUDE.md as Maid edits it, together with its AGENTS.md (what Codex reads), which Maid
/// keeps identical to it. Both sections Maid owns in that file (the shared info and the conversation
/// history) are written through here, so two writers never interleave.
/// </summary>
/// <remarks>
/// Either file may be edited (by hand, by Claude Code, by Codex): the one that changed since Maid last
/// made them agree is copied over the other. That is told by a hash of the text they last agreed on,
/// kept in <see cref="BaseFile"/>. A project seen for the first time with an AGENTS.md of its own that
/// differs gets CLAUDE.md's text in it too; the old AGENTS.md is kept as <c>AGENTS.maid-original.md</c>
/// (rather than merged). When both files changed since, the more recent one wins and the other one's
/// text is kept beside it as <c>*.maid-conflict.md</c>.
/// </remarks>
internal static partial class ClaudeMdFile
{
    public const string AgentsFileName = "AGENTS.md";

    private static readonly object WriteLock = new();
    private static Dictionary<string, string>? _bases;

    /// <summary>Where the hashes of the texts last mirrored are kept; null keeps them in memory only (tests).</summary>
    public static string? BaseFile { get; set; }

    [GeneratedRegex(@"^[ \t]*@(?:\.{1,2}[\\/])*AGENTS\.md[ \t]*(?=\r?$)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AgentsImport();

    /// <summary>The file in the project folder, or the one in its .claude folder when only that one exists.</summary>
    public static string Resolve(string projectPath)
    {
        var atRoot = Path.Combine(projectPath, "CLAUDE.md");
        var inDotClaude = Path.Combine(projectPath, ".claude", "CLAUDE.md");
        return !File.Exists(atRoot) && File.Exists(inDotClaude) ? inDotClaude : atRoot;
    }

    /// <summary>Codex reads the AGENTS.md in the project folder.</summary>
    public static string AgentsPath(string projectPath) => Path.Combine(projectPath, AgentsFileName);

    /// <summary>
    /// Replaces the text of CLAUDE.md and AGENTS.md with what <paramref name="edit"/> makes of it ("" when
    /// there is neither file yet), keeping each file's byte order mark. Nothing is written where the text
    /// stays the same; an empty result deletes both files. Changed is true when any file was written.
    /// Throws what the file system throws.
    /// </summary>
    public static (string File, bool Changed) Update(string projectPath, Func<string, string> edit)
    {
        lock (WriteLock)
        {
            var claude = Read(Resolve(projectPath));
            var agents = Read(AgentsPath(projectPath));
            var current = Reconcile(projectPath, claude, agents);

            var edited = edit(current);
            bool changed = Write(claude, edited, agents.Bom);
            changed |= Write(agents, edited, claude.Bom);
            RememberBase(projectPath, edited);
            return (claude.Path, changed);
        }
    }

    /// <summary>Brings AGENTS.md in line with CLAUDE.md (or the other way round). Never throws; returns the error, if any.</summary>
    public static string? Sync(string projectPath)
    {
        try
        {
            if (!Directory.Exists(projectPath)) return "folder not found";
            Update(projectPath, text => text);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return e.Message;
        }
    }

    // ---- the two files ------------------------------------------------------------------------------

    private sealed record FileText(string Path, bool Exists, string Text, bool Bom, DateTime WrittenUtc);

    private static FileText Read(string path)
    {
        if (!File.Exists(path)) return new FileText(path, false, "", false, DateTime.MinValue);
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        return new FileText(path, true, text, bom, File.GetLastWriteTimeUtc(path));
    }

    private static bool Write(FileText file, string text, bool otherBom)
    {
        if (text.Length == 0)
        {
            // Only a section of Maid's was in it: no file is tidier than an empty one.
            if (!file.Exists) return false;
            File.Delete(file.Path);
            return true;
        }
        if (file.Exists && file.Text == text) return false;
        // Written in place (not via rename) so that a symlinked or hard-linked file stays one.
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file.Path)!);
        File.WriteAllText(file.Path, text, new UTF8Encoding(file.Exists ? file.Bom : otherBom));
        return true;
    }

    /// <summary>The text both files should carry before Maid's edit, as far as they disagree now.</summary>
    private static string Reconcile(string projectPath, FileText claude, FileText agents)
    {
        if (!agents.Exists) return claude.Text;
        if (!claude.Exists || claude.Text == agents.Text) return agents.Text;

        var last = GetBase(projectPath);
        if (last == Hash(claude.Text)) return agents.Text; // only AGENTS.md was edited since
        if (last == Hash(agents.Text)) return claude.Text; // only CLAUDE.md was
        if (last is null) return FirstMeeting(claude, agents);

        // Both were edited since: the later edit wins, the other one is kept for the user to look at.
        var (winner, loser) = claude.WrittenUtc >= agents.WrittenUtc ? (claude, agents) : (agents, claude);
        KeepAside(loser, "maid-conflict");
        return winner.Text;
    }

    /// <summary>
    /// A CLAUDE.md and an AGENTS.md of the project's own that differ: CLAUDE.md is what both carry from
    /// now on, and the AGENTS.md is kept aside as AGENTS.maid-original.md when its text is not in there.
    /// Only a CLAUDE.md with nothing in it gives way, and one that imports AGENTS.md gets its text instead.
    /// </summary>
    private static string FirstMeeting(FileText claude, FileText agents)
    {
        if (claude.Text.Trim().Length == 0) return agents.Text;
        var text = InlineAgentsImport(claude.Text, agents.Text);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var own = agents.Text.Replace("\r\n", "\n").Replace("\n", newline).Trim();
        if (own.Length > 0 && !text.Contains(own, StringComparison.Ordinal)) KeepAside(agents, "maid-original");
        return text;
    }

    /// <summary>"@AGENTS.md" in CLAUDE.md pulls that file in: its text is put there instead, since AGENTS.md becomes CLAUDE.md's copy.</summary>
    internal static string InlineAgentsImport(string claudeMd, string agentsMd)
    {
        if (!AgentsImport().IsMatch(claudeMd)) return claudeMd;
        var newline = claudeMd.Contains("\r\n") ? "\r\n" : "\n";
        var agents = agentsMd.Replace("\r\n", "\n").Replace("\n", newline).Trim('\r', '\n');
        return AgentsImport().Replace(claudeMd, agents.Replace("$", "$$"));
    }

    /// <summary>Saves a file's text beside it as "&lt;name&gt;.&lt;label&gt;.md" (numbered rather than overwriting an earlier one).</summary>
    private static void KeepAside(FileText file, string label)
    {
        var folder = System.IO.Path.GetDirectoryName(file.Path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(file.Path);
        var kept = System.IO.Path.Combine(folder, $"{name}.{label}.md");
        for (int number = 2; File.Exists(kept); number++)
            kept = System.IO.Path.Combine(folder, $"{name}.{label}-{number}.md");
        File.WriteAllText(kept, file.Text, new UTF8Encoding(file.Bom));
    }

    // ---- what the files last agreed on --------------------------------------------------------------

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static string Key(string projectPath) => Path.GetFullPath(projectPath).TrimEnd('\\', '/').ToUpperInvariant();

    private static Dictionary<string, string> Bases()
    {
        if (_bases is not null) return _bases;
        _bases = [];
        try
        {
            if (BaseFile is not null && File.Exists(BaseFile))
                _bases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BaseFile)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Without it the next disagreement is treated as a first meeting: both texts are kept.
        }
        return _bases;
    }

    private static string? GetBase(string projectPath) => Bases().GetValueOrDefault(Key(projectPath));

    private static void RememberBase(string projectPath, string text)
    {
        var bases = Bases();
        var key = Key(projectPath);
        bool changed = text.Length == 0 ? bases.Remove(key) : bases.GetValueOrDefault(key) != Hash(text);
        if (text.Length > 0) bases[key] = Hash(text);
        if (changed) SaveBases(bases);
    }

    /// <summary>
    /// A folder was renamed or moved: what the files of the projects in it last agreed on goes with them,
    /// so the next edit there is not taken for a first meeting.
    /// </summary>
    public static void MoveBases(string fromFolder, string toFolder)
    {
        lock (WriteLock)
        {
            var bases = Bases();
            var from = Key(fromFolder);
            var to = Key(toFolder);
            var moved = bases.Where(pair => pair.Key == from || pair.Key.StartsWith(from + "\\", StringComparison.Ordinal)).ToList();
            if (moved.Count == 0 || from == to) return;
            foreach (var (key, hash) in moved)
            {
                bases.Remove(key);
                bases[to + key[from.Length..]] = hash;
            }
            SaveBases(bases);
        }
    }

    private static void SaveBases(Dictionary<string, string> bases)
    {
        if (BaseFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BaseFile)!);
            var temporary = BaseFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(bases));
            File.Move(temporary, BaseFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort, like the rest of Maid's own state.
        }
    }
}
