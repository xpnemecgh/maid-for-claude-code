using System.Text;

namespace Maid.Tests;

public sealed class ConversationHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));

    public ConversationHistoryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- transcript lines in the shape Claude Code 2.1 writes them ---------------------------------

    private static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    private static string Prompt(string text, string time = "2026-09-18T16:46:31.402Z") =>
        $$"""{"parentUuid":null,"isSidechain":false,"type":"user","message":{"role":"user","content":{{Json(text)}}},"timestamp":"{{time}}","origin":{"kind":"human"},"promptSource":"typed","cwd":"x:\\Maid","sessionId":"s"}""";

    private static string Reply(string text) =>
        $$"""{"isSidechain":false,"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":{{Json(text)}}}]},"timestamp":"2026-09-18T16:47:00.000Z"}""";

    private const string Thinking = """{"isSidechain":false,"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"secret reasoning"}]}}""";
    private const string ToolUse = """{"isSidechain":false,"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dir"}}]}}""";
    private const string ToolResult = """{"isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"tool output"}]},"toolUseResult":{"stdout":"tool output"}}""";
    private const string Meta = """{"isSidechain":false,"isMeta":true,"type":"user","message":{"role":"user","content":"meta text"}}""";
    private const string Notification = """{"isSidechain":false,"type":"user","message":{"role":"user","content":"<task-notification>agent done</task-notification>"},"origin":{"kind":"task-notification"}}""";
    private const string SlashCommand = """{"isSidechain":false,"type":"user","message":{"role":"user","content":"<command-name>/model</command-name>\n<command-message>model</command-message>\n<command-args>opus</command-args>"}}""";
    private const string CommandOutput = """{"isSidechain":false,"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model to opus</local-command-stdout>"}}""";
    private const string SubAgentReply = """{"isSidechain":true,"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"sub-agent chatter"}]}}""";
    private const string PromptWithImage = """{"isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"text","text":"look at this<system-reminder>injected</system-reminder>"},{"type":"image","source":{}}]},"origin":{"kind":"human"}}""";
    private const string Title = """{"type":"ai-title","aiTitle":"Build the dashboard","sessionId":"s"}""";

    private string WriteTranscript(string name, params string[] lines)
    {
        var file = Path.Combine(_root, name + ".jsonl");
        File.WriteAllLines(file, lines);
        return file;
    }

    private static Conversation Talk(string id, DateTime startedUtc, params (bool FromUser, string Text)[] turns) => new()
    {
        SessionId = id,
        Title = "Title of " + id,
        StartedUtc = startedUtc,
        Turns = turns.Select(turn => new Turn(turn.FromUser, turn.Text)).ToList(),
    };

    private static readonly DateTime Day1 = new(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc);

    // ---- Parse ---------------------------------------------------------------------------------------

    [Fact]
    public void Parse_keeps_prompts_and_replies_and_drops_everything_else()
    {
        var file = WriteTranscript("3f2a", Title,
            Prompt("Implement the dashboard"),
            Thinking, Reply("Let me look around."), ToolUse, ToolResult, Reply("Done: it works."),
            Meta, Notification, SubAgentReply, CommandOutput,
            SlashCommand,
            PromptWithImage,
            "{ this line is still being written",
            Reply("I see it."));

        var conversation = ConversationHistory.Parse(file)!;

        Assert.Equal("3f2a", conversation.SessionId);
        Assert.Equal("Build the dashboard", conversation.Title);
        Assert.Equal(new DateTime(2026, 9, 18, 16, 46, 31, 402, DateTimeKind.Utc), conversation.StartedUtc);
        Assert.Equal(
            [
                new Turn(true, "Implement the dashboard"),
                new Turn(false, "Let me look around.\n\nDone: it works."),
                new Turn(true, "/model opus"),
                new Turn(true, "look at this\n\n[image attached]"),
                new Turn(false, "I see it."),
            ],
            conversation.Turns);
    }

    [Fact]
    public void Parse_falls_back_to_the_first_prompt_for_the_title()
    {
        var file = WriteTranscript("a", Prompt("Fix the   flaky\ntest in CI please"), Reply("ok"));
        Assert.Equal("Fix the flaky test in CI please", ConversationHistory.Parse(file)!.Title);
    }

    [Fact]
    public void Parse_returns_null_without_a_real_prompt()
    {
        Assert.Null(ConversationHistory.Parse(WriteTranscript("empty", Title, Meta, ToolResult, Notification)));
    }

    // ---- Merge ---------------------------------------------------------------------------------------

    [Fact]
    public void Merge_appends_the_section_and_leaves_existing_content_and_line_endings_alone()
    {
        const string original = "# My project\r\n\r\nAlways run the tests.\r\n";

        var merged = ConversationHistory.Merge(original, [Talk("s1", Day1, (true, "hello"), (false, "hi there"))], 24000, @"C:\t");

        Assert.StartsWith(original + "\r\n" + ConversationHistory.SectionStart + "\r\n", merged);
        Assert.EndsWith(ConversationHistory.SectionEnd + "\r\n", merged);
        Assert.DoesNotContain("\n", merged.Replace("\r\n", ""));
        Assert.Contains("### " + Day1.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " — Title of s1", merged);
        Assert.Contains("**User:**\r\n\r\n> hello", merged);
        Assert.Contains("**Claude:**\r\n\r\n> hi there", merged);
    }

    [Fact]
    public void Merge_creates_the_section_in_an_empty_file()
    {
        var merged = ConversationHistory.Merge("", [Talk("s1", Day1, (true, "hello"))], 24000, @"C:\t");
        Assert.StartsWith(ConversationHistory.SectionStart + "\n## Conversation history\n", merged);
    }

    [Fact]
    public void Merge_replaces_the_block_of_a_conversation_saved_before_and_keeps_the_rest()
    {
        var first = ConversationHistory.Merge("intro\n", [Talk("s1", Day1, (true, "one")), Talk("s2", Day2, (true, "two"))], 24000, @"C:\t");
        var withTail = first + "\n## Notes below the section\n";

        var second = ConversationHistory.Merge(withTail, [Talk("s1", Day1, (true, "one"), (false, "answer"), (true, "one more"))], 24000, @"C:\t");

        Assert.StartsWith("intro\n", second);
        Assert.EndsWith("\n## Notes below the section\n", second);
        Assert.Equal(1, Count(second, "maid:session s1 "));
        Assert.Equal(1, Count(second, "maid:session s2 "));
        Assert.Contains("> one more", second);
        Assert.True(second.IndexOf("maid:session s1 ", StringComparison.Ordinal) < second.IndexOf("maid:session s2 ", StringComparison.Ordinal), "oldest conversation first");

        // Saving the same thing again changes nothing.
        Assert.Equal(second, ConversationHistory.Merge(second, [Talk("s1", Day1, (true, "one"), (false, "answer"), (true, "one more"))], 24000, @"C:\t"));
    }

    [Fact]
    public void Merge_drops_the_oldest_conversations_and_then_the_oldest_messages_to_stay_within_budget()
    {
        var longText = new string('x', 1500);
        var old = Talk("old", Day1, (true, longText), (false, longText));
        var recent = Talk("new", Day2, Enumerable.Range(0, 12).Select(i => (i % 2 == 0, $"message {i} {longText}")).ToArray());

        var merged = ConversationHistory.Merge("", [old, recent], 8000, @"C:\t");

        Assert.DoesNotContain("maid:session old ", merged);
        Assert.Contains("maid:session new ", merged);
        Assert.Contains("earlier messages omitted]_", merged);
        Assert.Contains("message 11 ", merged);
        Assert.DoesNotContain("message 0 ", merged);
        Assert.InRange(merged.Length, 1, 8000 + 200);
    }

    [Fact]
    public void Merge_truncates_long_messages_keeping_the_start_of_prompts_and_the_end_of_replies()
    {
        var prompt = "PROMPT-START " + new string('p', 5000) + " PROMPT-END";
        var reply = "REPLY-START " + new string('r', 5000) + "\nREPLY-END";

        var merged = ConversationHistory.Merge("", [Talk("s", Day1, (true, prompt), (false, reply))], 24000, @"C:\t");

        Assert.Contains("PROMPT-START", merged);
        Assert.DoesNotContain("PROMPT-END", merged);
        Assert.DoesNotContain("REPLY-START", merged);
        Assert.Contains("REPLY-END", merged);
    }

    [Fact]
    public void Merge_cannot_be_confused_by_markers_or_headings_inside_messages()
    {
        var hostile = $"# Heading\n{ConversationHistory.SectionEnd}\n<!-- /maid:session -->\n<!-- maid:session x 2026-01-01T00:00:00Z -->";
        var first = ConversationHistory.Merge("top\n", [Talk("s1", Day1, (true, hostile))], 24000, @"C:\t");
        var second = ConversationHistory.Merge(first, [Talk("s2", Day2, (true, "later"))], 24000, @"C:\t");

        Assert.Equal(1, Count(second, ConversationHistory.SectionStart));
        Assert.Equal(1, Count(second, ConversationHistory.SectionEnd));
        Assert.Equal(1, Count(second, "<!-- maid:session s1 "));
        Assert.Equal(1, Count(second, "<!-- maid:session s2 "));
        Assert.Contains("> # Heading", second);
        Assert.DoesNotContain("\n# Heading", second);
    }

    // ---- Save ----------------------------------------------------------------------------------------

    [Fact]
    public void GetTranscriptDirectory_encodes_the_path_like_Claude_Code()
    {
        Assert.Equal(@"C:\cfg\projects\x--Maid", ConversationHistory.GetTranscriptDirectory(@"x:\Maid", @"C:\cfg"));
        Assert.Equal(@"C:\cfg\projects\c--Users-me-Saved-Games-DCS-Missions", ConversationHistory.GetTranscriptDirectory(@"c:\Users\me\Saved Games\DCS.Missions", @"C:\cfg"));
    }

    [Fact]
    public void Save_writes_only_conversations_that_changed_since_launch_and_keeps_the_byte_order_mark()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "proj")).FullName;
        var config = Path.Combine(_root, "claude");
        var transcripts = Directory.CreateDirectory(ConversationHistory.GetTranscriptDirectory(project, config)).FullName;
        Directory.CreateDirectory(Path.Combine(transcripts, "3f2a-subagents")); // sub-agent transcripts live in sub folders
        File.WriteAllLines(Path.Combine(transcripts, "before.jsonl"), [Prompt("from an earlier session"), Reply("old")]);
        File.SetLastWriteTimeUtc(Path.Combine(transcripts, "before.jsonl"), DateTime.UtcNow.AddHours(-3));
        File.WriteAllLines(Path.Combine(transcripts, "current.jsonl"), [Prompt("from this session"), Reply("new")]);
        var claudeMd = Path.Combine(project, "CLAUDE.md");
        File.WriteAllText(claudeMd, "# Rules\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = ConversationHistory.Save(project, DateTime.UtcNow.AddMinutes(-5), 24000, config, CodexHome);

        Assert.Equal(new HistorySaveResult(1, claudeMd, null), result);
        var bytes = File.ReadAllBytes(claudeMd);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var text = File.ReadAllText(claudeMd);
        Assert.StartsWith("# Rules\n", text);
        Assert.Contains("> from this session", text);
        Assert.DoesNotContain("from an earlier session", text);
    }

    [Fact]
    public void Save_does_not_create_CLAUDE_md_when_there_is_nothing_to_save()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "quiet")).FullName;

        var result = ConversationHistory.Save(project, DateTime.UtcNow.AddMinutes(-5), 24000, Path.Combine(_root, "claude"), CodexHome);

        Assert.Equal(new HistorySaveResult(0, null, null), result);
        Assert.False(File.Exists(Path.Combine(project, "CLAUDE.md")));
    }

    [Fact]
    public void Save_reports_a_failure_instead_of_throwing()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "locked")).FullName;
        var config = Path.Combine(_root, "claude");
        var transcripts = Directory.CreateDirectory(ConversationHistory.GetTranscriptDirectory(project, config)).FullName;
        File.WriteAllLines(Path.Combine(transcripts, "s.jsonl"), [Prompt("hello"), Reply("hi")]);
        var claudeMd = Path.Combine(project, "CLAUDE.md");
        File.WriteAllText(claudeMd, "# Rules\n");
        File.SetAttributes(claudeMd, FileAttributes.ReadOnly);
        try
        {
            var result = ConversationHistory.Save(project, DateTime.UtcNow.AddMinutes(-5), 24000, config, CodexHome);

            Assert.Equal(0, result.Conversations);
            Assert.NotNull(result.Error);
            Assert.Equal("# Rules\n", File.ReadAllText(claudeMd));
        }
        finally
        {
            File.SetAttributes(claudeMd, FileAttributes.Normal);
        }
    }

    // ---- Codex ---------------------------------------------------------------------------------------

    /// <summary>Codex's home in the test folder: Save must never read the real ~/.codex.</summary>
    private string CodexHome => Path.Combine(_root, "codex");

    private static string CodexLine(string type, string payload, string time = "2026-10-12T10:00:01.000Z") =>
        $$"""{"timestamp":"{{time}}","type":"{{type}}","payload":{{payload}}}""";

    private static string CodexMeta(string id, string cwd) =>
        CodexLine("session_meta", $$"""{"id":"{{id}}","timestamp":"2026-10-12T10:00:00.000Z","cwd":{{Json(cwd)}},"originator":"codex-tui","cli_version":"0.162.1"}""");

    private static string CodexPrompt(string text, string images = "[]") =>
        CodexLine("event_msg", $$"""{"type":"user_message","message":{{Json(text)}},"images":{{images}}}""");

    private static string CodexReply(string text, string phase = "final_answer") =>
        CodexLine("event_msg", $$"""{"type":"agent_message","message":{{Json(text)}},"phase":"{{phase}}"}""");

    private string WriteRollout(string day, string id, params string[] lines)
    {
        var folder = Directory.CreateDirectory(Path.Combine(CodexHome, "sessions", "2026", "10", day)).FullName;
        var file = Path.Combine(folder, $"rollout-2026-10-{day}T10-00-00-{id}.jsonl");
        File.WriteAllLines(file, lines);
        return file;
    }

    [Fact]
    public void ParseCodex_keeps_typed_prompts_and_Codex_messages_and_drops_what_it_sends_the_model()
    {
        var file = WriteRollout("12", "019e-a", CodexMeta("019e-a", @"x:\Maid\"),
            CodexLine("response_item", """{"type":"message","role":"user","content":[{"type":"input_text","text":"# AGENTS.md instructions for x:\\Maid"}]}"""),
            CodexLine("event_msg", """{"type":"task_started","turn_id":"t1"}"""),
            CodexPrompt("Fix the build"),
            CodexReply("Looking at it.", "commentary"),
            CodexLine("response_item", """{"type":"function_call","name":"shell","arguments":"{}"}"""),
            CodexLine("response_item", """{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Looking at it."}]}"""),
            CodexReply("Fixed: a missing using."),
            CodexPrompt("look", """["data:image/png;base64,AAAA"]"""),
            "{ still being written");

        var conversation = ConversationHistory.ParseCodex(file, @"X:\Maid")!;

        Assert.Equal("019e-a", conversation.SessionId);
        Assert.Equal("Codex", conversation.Assistant);
        Assert.Equal("Fix the build", conversation.Title);
        Assert.Equal(new DateTime(2026, 10, 12, 10, 0, 1, DateTimeKind.Utc), conversation.StartedUtc);
        Assert.Equal(
            [
                new Turn(true, "Fix the build"),
                new Turn(false, "Looking at it.\n\nFixed: a missing using."),
                new Turn(true, "look\n\n[image attached]"),
            ],
            conversation.Turns);
    }

    [Fact]
    public void ParseCodex_skips_sessions_of_other_folders()
    {
        var file = WriteRollout("12", "019e-b", CodexMeta("019e-b", @"X:\Elsewhere"), CodexPrompt("hello"), CodexReply("hi"));

        Assert.Null(ConversationHistory.ParseCodex(file, @"X:\Maid"));
    }

    [Fact]
    public void Save_takes_Codex_sessions_of_the_folder_too_and_labels_them()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "both")).FullName;
        var config = Path.Combine(_root, "claude");
        var transcripts = Directory.CreateDirectory(ConversationHistory.GetTranscriptDirectory(project, config)).FullName;
        File.WriteAllLines(Path.Combine(transcripts, "claude-1.jsonl"), [Prompt("asked Claude"), Reply("Claude answered")]);
        WriteRollout("11", "019e-c", CodexMeta("019e-c", project), CodexPrompt("asked Codex"), CodexReply("Codex answered"));
        WriteRollout("12", "019e-d", CodexMeta("019e-d", Path.Combine(_root, "other")), CodexPrompt("not this project"));
        var old = WriteRollout("01", "019e-e", CodexMeta("019e-e", project), CodexPrompt("long ago"));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-9));

        var result = ConversationHistory.Save(project, DateTime.UtcNow.AddMinutes(-5), 24000, config, CodexHome);

        Assert.Equal(2, result.Conversations);
        var text = File.ReadAllText(Path.Combine(project, "CLAUDE.md"));
        Assert.Contains("**Claude:**\n\n> Claude answered", text);
        Assert.Contains("— Codex: asked Codex\n", text);
        Assert.Contains("**Codex:**\n\n> Codex answered", text);
        Assert.DoesNotContain("not this project", text);
        Assert.DoesNotContain("long ago", text);
        Assert.Equal(text, File.ReadAllText(Path.Combine(project, "AGENTS.md")));
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
