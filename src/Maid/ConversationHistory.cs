using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Maid;

internal sealed record HistorySaveResult(int Conversations, string? File, string? Error);

internal sealed record Turn(bool FromUser, string Text);

internal sealed class Conversation
{
    public required string SessionId { get; init; }
    public required string Title { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required IReadOnlyList<Turn> Turns { get; init; }
    /// <summary>Who replied: "Claude" for Claude Code's transcripts, "Codex" for Codex's.</summary>
    public string Assistant { get; init; } = "Claude";
}

/// <summary>
/// Copies the conversations of a project from Claude Code's transcripts
/// (<c>~/.claude/projects/&lt;encoded folder&gt;/&lt;session&gt;.jsonl</c>) and Codex's
/// (<c>~/.codex/sessions/&lt;yyyy&gt;/&lt;mm&gt;/&lt;dd&gt;/rollout-*.jsonl</c>, matched by the folder they ran in)
/// into a section of the project's CLAUDE.md that Maid owns. Everything outside the section markers is
/// left byte for byte as it was.
/// </summary>
internal static partial class ConversationHistory
{
    public const string SectionStart = "<!-- maid:conversation-history:start -->";
    public const string SectionEnd = "<!-- maid:conversation-history:end -->";
    private const string SessionStartPrefix = "<!-- maid:session ";
    private const string SessionEnd = "<!-- /maid:session -->";

    /// <summary>CLAUDE.md is loaded into every session's context, so single messages are kept to a digestible size.</summary>
    private const int MaxPromptChars = 2000;
    private const int MaxReplyChars = 3000;

    /// <summary>User lines that start with one of these wrappers are produced by Claude Code, not typed by the user.</summary>
    private static readonly string[] GeneratedWrappers =
    [
        "<local-command-stdout>", "<local-command-stderr>", "<local-command-caveat>", "<task-notification>",
        "<bash-stdout>", "<bash-stderr>", "<user-prompt-submit-hook>",
    ];

    [GeneratedRegex("<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex SystemReminder();

    [GeneratedRegex("<command-name>(.*?)</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandName();

    [GeneratedRegex("<command-args>(.*?)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArguments();

    [GeneratedRegex("<bash-input>(.*?)</bash-input>", RegexOptions.Singleline)]
    private static partial Regex BashInput();

    [GeneratedRegex(@"<!-- maid:session (\S+) (\S+) -->\n.*?\n<!-- /maid:session -->", RegexOptions.Singleline)]
    private static partial Regex SessionBlock();

    /// <summary>
    /// Saves every conversation of <paramref name="projectPath"/> whose transcript changed since
    /// <paramref name="sinceUtc"/> (the launch of the session that just stopped; this also covers
    /// conversations it switched to with /clear or /resume). Never throws.
    /// </summary>
    public static HistorySaveResult Save(string projectPath, DateTime sinceUtc, int maxSectionChars,
        string? claudeConfigDirectory = null, string? codexHome = null)
    {
        try
        {
            var since = sinceUtc.AddSeconds(-2);
            var transcripts = GetTranscriptDirectory(projectPath, claudeConfigDirectory);
            var conversations = new List<Conversation>();
            if (Directory.Exists(transcripts))
            {
                conversations.AddRange(Directory.EnumerateFiles(transcripts, "*.jsonl")
                    .Where(file => File.GetLastWriteTimeUtc(file) >= since)
                    .Select(Parse)
                    .OfType<Conversation>());
            }
            var codexSessions = GetCodexSessionDirectory(codexHome);
            if (Directory.Exists(codexSessions))
            {
                // Codex files its sessions by date, not by folder: those written to since are read far
                // enough to see where they ran. A resumed one is appended to where it was first filed.
                conversations.AddRange(new DirectoryInfo(codexSessions)
                    .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .Where(file => file.LastWriteTimeUtc >= since)
                    .Select(file => ParseCodex(file.FullName, projectPath))
                    .OfType<Conversation>());
            }
            if (conversations.Count == 0) return new HistorySaveResult(0, null, null);

            var (target, _) = ClaudeMdFile.Update(projectPath, text => Merge(text, conversations, maxSectionChars, transcripts));
            return new HistorySaveResult(conversations.Count, target, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new HistorySaveResult(0, null, e.Message);
        }
    }

    /// <summary>Claude Code names a project's transcript folder after its path, every non-alphanumeric character replaced by '-'.</summary>
    public static string GetTranscriptDirectory(string projectPath, string? claudeConfigDirectory = null)
    {
        var configDirectory = claudeConfigDirectory;
        if (string.IsNullOrEmpty(configDirectory))
            configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"); // Claude Code's own override
        if (string.IsNullOrEmpty(configDirectory))
            configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var encoded = new string(projectPath.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return Path.Combine(configDirectory, "projects", encoded);
    }

    /// <summary>Where Codex keeps its session transcripts (<c>$CODEX_HOME/sessions</c>, by default under ~/.codex).</summary>
    public static string GetCodexSessionDirectory(string? codexHome = null)
    {
        var home = codexHome;
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrEmpty(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(home, "sessions");
    }

    // ---- reading a transcript ------------------------------------------------------------------------

    /// <summary>Returns the prompts and replies of one transcript, or null when it holds no conversation.</summary>
    internal static Conversation? Parse(string transcriptFile)
    {
        var turns = new List<Turn>();
        var reply = new StringBuilder();
        string? title = null;
        DateTime? started = null;

        void FlushReply()
        {
            if (reply.Length > 0) turns.Add(new Turn(false, reply.ToString().Trim()));
            reply.Clear();
        }

        // Another session may still be appending to the file.
        using var stream = new FileStream(transcriptFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // e.g. a line that is still being written

            using (document)
            {
                var entry = document.RootElement;
                if (entry.ValueKind != JsonValueKind.Object) continue;

                switch (GetString(entry, "type"))
                {
                    case "ai-title":
                        if (GetString(entry, "aiTitle") is { Length: > 0 } aiTitle) title = aiTitle;
                        break;

                    case "user" when IsTypedByUser(entry) && GetPromptText(entry) is { } prompt:
                        FlushReply();
                        turns.Add(new Turn(true, prompt));
                        started ??= GetTimestamp(entry);
                        break;

                    case "assistant" when !IsTrue(entry, "isSidechain") && !IsTrue(entry, "isApiErrorMessage"):
                        foreach (var text in GetTextBlocks(entry))
                        {
                            if (reply.Length > 0) reply.Append("\n\n");
                            reply.Append(text.Trim());
                        }
                        break;
                }
            }
        }
        FlushReply();

        if (!turns.Any(turn => turn.FromUser)) return null;
        return new Conversation
        {
            SessionId = Path.GetFileNameWithoutExtension(transcriptFile),
            Title = SingleLine(title ?? turns.First(turn => turn.FromUser).Text, 90),
            StartedUtc = started ?? File.GetLastWriteTimeUtc(transcriptFile),
            Turns = turns,
        };
    }

    /// <summary>
    /// Returns the prompts and replies of one Codex session, or null when it holds no conversation or
    /// ran in another folder than <paramref name="projectPath"/>. Codex records each typed prompt and
    /// each message of its own as an event, apart from what it sends the model (instructions, context).
    /// </summary>
    internal static Conversation? ParseCodex(string rolloutFile, string projectPath)
    {
        var turns = new List<Turn>();
        var reply = new StringBuilder();
        string? sessionId = null;
        DateTime? started = null;

        void FlushReply()
        {
            if (reply.Length > 0) turns.Add(new Turn(false, reply.ToString().Trim()));
            reply.Clear();
        }

        using var stream = new FileStream(rolloutFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (document)
            {
                var entry = document.RootElement;
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                    continue;

                switch (GetString(entry, "type"))
                {
                    case "session_meta":
                        // The first line: a session of another folder is done with right here.
                        if (!SamePath(GetString(payload, "cwd"), projectPath)) return null;
                        sessionId ??= GetString(payload, "id");
                        break;

                    case "event_msg" when sessionId is not null:
                        var text = GetString(payload, "message")?.Trim();
                        switch (GetString(payload, "type"))
                        {
                            case "user_message":
                                if (payload.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                                    text = (text + "\n\n[image attached]").Trim();
                                if (string.IsNullOrEmpty(text) || text.StartsWith("<environment_context>", StringComparison.Ordinal)) break;
                                FlushReply();
                                turns.Add(new Turn(true, text));
                                started ??= GetTimestamp(entry);
                                break;
                            case "agent_message" when !string.IsNullOrEmpty(text):
                                if (reply.Length > 0) reply.Append("\n\n");
                                reply.Append(text);
                                break;
                        }
                        break;
                }
            }
        }
        FlushReply();

        if (sessionId is null || !turns.Any(turn => turn.FromUser)) return null;
        return new Conversation
        {
            SessionId = sessionId,
            Title = SingleLine(turns.First(turn => turn.FromUser).Text, 90),
            StartedUtc = started ?? File.GetLastWriteTimeUtc(rolloutFile),
            Turns = turns,
            Assistant = "Codex",
        };
    }

    private static bool SamePath(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;
        try
        {
            static string Normal(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
            return string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Tool results, hook output, sub-agent traffic and notifications are all recorded as "user" lines too.</summary>
    private static bool IsTypedByUser(JsonElement entry)
    {
        if (IsTrue(entry, "isMeta") || IsTrue(entry, "isSidechain") || IsTrue(entry, "isCompactSummary") || IsTrue(entry, "isVisibleInTranscriptOnly"))
            return false;
        if (entry.TryGetProperty("toolUseResult", out _)) return false;
        return !entry.TryGetProperty("origin", out var origin) || origin.ValueKind != JsonValueKind.Object || GetString(origin, "kind") == "human";
    }

    private static string? GetPromptText(JsonElement entry)
    {
        var text = string.Join("\n\n", GetTextBlocks(entry, attachmentPlaceholders: true));
        text = SystemReminder().Replace(text, "").Trim();
        if (text.Length == 0) return null;

        if (GeneratedWrappers.Any(wrapper => text.StartsWith(wrapper, StringComparison.Ordinal))) return null;
        if (CommandName().Match(text) is { Success: true } command)
            return $"{command.Groups[1].Value.Trim()} {CommandArguments().Match(text).Groups[1].Value.Trim()}".Trim();
        if (BashInput().Match(text) is { Success: true } shell)
            return "! " + shell.Groups[1].Value.Trim();
        return text;
    }

    private static IEnumerable<string> GetTextBlocks(JsonElement entry, bool attachmentPlaceholders = false)
    {
        if (!entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) yield break;
        if (!message.TryGetProperty("content", out var content)) yield break;

        if (content.ValueKind == JsonValueKind.String)
        {
            yield return content.GetString() ?? "";
            yield break;
        }
        if (content.ValueKind != JsonValueKind.Array) yield break;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            var kind = GetString(block, "type");
            if (kind == "text" && GetString(block, "text") is { Length: > 0 } text) yield return text;
            else if (attachmentPlaceholders && kind is "image" or "document") yield return $"[{kind} attached]";
        }
    }

    // ---- writing the section -------------------------------------------------------------------------

    /// <summary>Returns <paramref name="claudeMd"/> with the history section created or updated.</summary>
    internal static string Merge(string claudeMd, IReadOnlyList<Conversation> conversations, int maxSectionChars, string transcriptDirectory)
    {
        var newline = claudeMd.Contains("\r\n") ? "\r\n" : "\n";
        int start = claudeMd.IndexOf(SectionStart, StringComparison.Ordinal);
        int end = start < 0 ? -1 : claudeMd.IndexOf(SectionEnd, start, StringComparison.Ordinal);

        string before, oldSection, after;
        if (end >= 0)
        {
            before = claudeMd[..start];
            oldSection = claudeMd[start..end].Replace("\r\n", "\n");
            after = claudeMd[(end + SectionEnd.Length)..];
        }
        else
        {
            // First save: the section goes to the end, one blank line after whatever is there.
            before = claudeMd.Length == 0 ? "" : claudeMd.TrimEnd('\r', '\n') + newline + newline;
            oldSection = "";
            after = newline;
        }

        // Blocks are keyed by session id: saving a conversation again replaces its block.
        var blocks = SessionBlock().Matches(oldSection).ToDictionary(
            match => match.Groups[1].Value,
            match => (Started: match.Groups[2].Value, Text: match.Value));
        var renderable = conversations.ToDictionary(conversation => conversation.SessionId);
        foreach (var conversation in conversations)
            blocks[conversation.SessionId] = (ToMarker(conversation.StartedUtc), Render(conversation, int.MaxValue));

        var header = $"{SectionStart}\n## Conversation history\n\n" +
            "_Saved automatically by Maid whenever a Claude Code or Codex session in this folder stops: the prompts and the replies, without tool calls. " +
            $"The oldest entries are dropped once this section grows beyond about {maxSectionChars:N0} characters; " +
            $"the complete transcripts stay in `{transcriptDirectory}` (Codex keeps its own in its `sessions` folder)._\n";

        var ordered = blocks.OrderBy(block => block.Value.Started, StringComparer.Ordinal).ToList();
        int Total() => header.Length + SectionEnd.Length + ordered.Sum(block => block.Value.Text.Length + 2);
        while (ordered.Count > 1 && Total() > maxSectionChars)
            ordered.RemoveAt(0);
        if (Total() > maxSectionChars && renderable.TryGetValue(ordered[0].Key, out var newest))
        {
            int budget = Math.Max(2000, maxSectionChars - header.Length - SectionEnd.Length - 2);
            ordered[0] = new(ordered[0].Key, (ordered[0].Value.Started, Render(newest, budget)));
        }

        var section = new StringBuilder(header);
        foreach (var block in ordered)
            section.Append('\n').Append(block.Value.Text).Append('\n');
        section.Append(SectionEnd);

        return before + section.ToString().Replace("\n", newline) + after;
    }

    /// <summary>Renders one conversation; when it exceeds <paramref name="budget"/> the oldest messages go first.</summary>
    private static string Render(Conversation conversation, int budget)
    {
        var turns = conversation.Turns.Select(turn =>
        {
            var text = turn.FromUser ? KeepStart(turn.Text, MaxPromptChars) : KeepEnd(turn.Text, MaxReplyChars);
            return $"**{(turn.FromUser ? "User" : conversation.Assistant)}:**\n\n{Quote(text)}\n";
        }).ToList();

        var heading = $"{SessionStartPrefix}{conversation.SessionId} {ToMarker(conversation.StartedUtc)} -->\n" +
            $"### {conversation.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} — " +
            $"{(conversation.Assistant == "Claude" ? "" : conversation.Assistant + ": ")}{conversation.Title}\n";

        int omitted = 0;
        while (turns.Count > 2 && heading.Length + turns.Sum(turn => turn.Length + 1) + SessionEnd.Length > budget)
        {
            turns.RemoveAt(0);
            omitted++;
        }

        var block = new StringBuilder(heading);
        if (omitted > 0) block.Append($"\n_[{omitted} earlier message{(omitted == 1 ? "" : "s")} omitted]_\n");
        foreach (var turn in turns) block.Append('\n').Append(turn);
        return block.Append('\n').Append(SessionEnd).ToString();
    }

    /// <summary>
    /// Message text is quoted so its own headings do not restructure CLAUDE.md, and comment openers are
    /// escaped so it can never be mistaken for one of the section markers.
    /// </summary>
    private static string Quote(string text) =>
        string.Join("\n", Escape(text).Replace("\r\n", "\n").Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line));

    private static string Escape(string text) => text.Replace("<!--", "&lt;!--");

    private static string KeepStart(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + " […]";

    /// <summary>The end of a long reply is where Claude sums up, so that is the part worth keeping.</summary>
    private static string KeepEnd(string text, int max)
    {
        if (text.Length <= max) return text;
        var tail = text[^max..];
        int lineStart = tail.IndexOf('\n');
        if (lineStart >= 0 && lineStart < max / 4) tail = tail[(lineStart + 1)..];
        return "[…]\n" + tail.TrimStart();
    }

    private static string SingleLine(string text, int max)
    {
        var line = Escape(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }

    private static string ToMarker(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static DateTime? GetTimestamp(JsonElement entry) =>
        DateTime.TryParse(GetString(entry, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
