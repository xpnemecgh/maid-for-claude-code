using System.IO;

namespace Maid;

internal sealed record SharedInfoResult(bool Changed, string? File, string? Error);

/// <summary>
/// The text every listed project carries in its CLAUDE.md (and so in its AGENTS.md, see
/// <see cref="ClaudeMdFile"/>): one section that Maid owns, holding the text exactly as it is edited
/// in Maid. Everything outside the section markers is left as it was.
/// </summary>
internal static class SharedInfo
{
    public const string SectionStart = "<!-- maid:shared-info:start -->";
    public const string SectionEnd = "<!-- maid:shared-info:end -->";
    /// <summary>CLAUDE.md is loaded into every session's context, so the text is bounded.</summary>
    public const int MaxChars = 20000;

    /// <summary>For whoever opens the file; Claude Code leaves HTML comments out of what it loads into a session.</summary>
    private const string Notice =
        "<!-- Maid keeps the same text in the CLAUDE.md and AGENTS.md of every project on its list. Edit it in Maid (Shared info, or shared-info.md of the All projects session); changes made here are overwritten. -->";

    /// <summary>The text as it is stored and written: "\n" line ends, no blank lines around it, bounded.</summary>
    public static string Normalize(string? raw)
    {
        var text = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length > MaxChars) text = text[..MaxChars];
        return text.TrimEnd().TrimStart('\n');
    }

    /// <summary>
    /// Makes the CLAUDE.md of <paramref name="projectPath"/> carry <paramref name="info"/>; an empty
    /// text takes the section out again. Never throws.
    /// </summary>
    public static SharedInfoResult Apply(string projectPath, string? info)
    {
        try
        {
            if (!Directory.Exists(projectPath)) return new SharedInfoResult(false, null, "folder not found");
            var (file, changed) = ClaudeMdFile.Update(projectPath, text => Merge(text, info));
            return new SharedInfoResult(changed, file, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new SharedInfoResult(false, null, e.Message);
        }
    }

    /// <summary>Returns <paramref name="claudeMd"/> with the section created, updated or (for an empty text) removed.</summary>
    internal static string Merge(string claudeMd, string? info)
    {
        var newline = claudeMd.Contains("\r\n") ? "\r\n" : "\n";
        var text = Normalize(info);
        var (start, end) = FindSection(claudeMd);

        if (text.Length == 0)
        {
            if (start < 0) return claudeMd;
            // The section goes together with the blank lines that set it apart.
            var kept = claudeMd[..start].TrimEnd('\r', '\n');
            var rest = claudeMd[end..].TrimStart('\r', '\n');
            if (kept.Length == 0) return rest;
            return kept + newline + (rest.Length == 0 ? "" : newline + rest);
        }

        // The text is the user's own Markdown and goes in as it is; it only must not be able to end the section.
        var section = $"{SectionStart}\n{Notice}\n{text.Replace("<!-- maid:", "&lt;!-- maid:")}\n{SectionEnd}".Replace("\n", newline);
        if (start >= 0) return claudeMd[..start] + section + claudeMd[end..];

        // First time: at the end, but ahead of the conversation history, which is long and keeps growing.
        int history = claudeMd.IndexOf(ConversationHistory.SectionStart, StringComparison.Ordinal);
        var before = (history < 0 ? claudeMd : claudeMd[..history]).TrimEnd('\r', '\n');
        var after = history < 0 ? "" : newline + claudeMd[history..];
        return (before.Length == 0 ? "" : before + newline + newline) + section + newline + after;
    }

    /// <summary>Where the section is (start marker up to just past the end marker), or (-1, -1).</summary>
    private static (int Start, int End) FindSection(string claudeMd)
    {
        int first = claudeMd.IndexOf(SectionStart, StringComparison.Ordinal);
        int end = first < 0 ? -1 : claudeMd.IndexOf(SectionEnd, first, StringComparison.Ordinal);
        if (end < 0) return (-1, -1);
        // A start marker that lost its end (the file was edited by hand) must not swallow what follows it.
        int start = claudeMd.LastIndexOf(SectionStart, end, StringComparison.Ordinal);
        return (start, end + SectionEnd.Length);
    }
}
