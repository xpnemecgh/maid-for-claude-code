using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Maid.Tests;

public sealed class SharedInfoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));

    public SharedInfoTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Project(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    // ---- Merge ---------------------------------------------------------------------------------------

    [Fact]
    public void Merge_appends_the_section_and_leaves_existing_content_and_line_endings_alone()
    {
        const string original = "# My project\r\n\r\nAlways run the tests.\r\n";

        var merged = SharedInfo.Merge(original, "## House rules\n\nAnswer in Czech.\n");

        Assert.StartsWith(original + "\r\n" + SharedInfo.SectionStart + "\r\n", merged);
        Assert.EndsWith("\r\n## House rules\r\n\r\nAnswer in Czech.\r\n" + SharedInfo.SectionEnd + "\r\n", merged);
        Assert.DoesNotContain("\n", merged.Replace("\r\n", ""));
    }

    [Fact]
    public void Merge_creates_the_section_in_an_empty_file()
    {
        var merged = SharedInfo.Merge("", "Be brief.");

        Assert.StartsWith(SharedInfo.SectionStart + "\n", merged);
        Assert.EndsWith("\nBe brief.\n" + SharedInfo.SectionEnd + "\n", merged);
    }

    [Fact]
    public void Merge_replaces_the_text_in_place_and_changing_nothing_changes_nothing()
    {
        var first = SharedInfo.Merge("intro\n", "old text") + "\n## Notes below the section\n";

        var second = SharedInfo.Merge(first, "new text");

        Assert.StartsWith("intro\n\n" + SharedInfo.SectionStart, second);
        Assert.EndsWith(SharedInfo.SectionEnd + "\n\n## Notes below the section\n", second);
        Assert.Contains("\nnew text\n", second);
        Assert.DoesNotContain("old text", second);
        Assert.Equal(second, SharedInfo.Merge(second, "new text\r\n\r\n"));
    }

    [Fact]
    public void Merge_with_an_empty_text_takes_the_section_out_again()
    {
        const string original = "# My project\n\nAlways run the tests.\n";

        Assert.Equal(original, SharedInfo.Merge(SharedInfo.Merge(original, "something"), "  \n"));
        Assert.Equal("", SharedInfo.Merge(SharedInfo.Merge("", "something"), ""));
        Assert.Equal(original, SharedInfo.Merge(original, "")); // nothing to take out
    }

    [Fact]
    public void Merge_puts_a_new_section_ahead_of_the_conversation_history()
    {
        var withHistory = "intro\n\n" + ConversationHistory.SectionStart + "\n## Conversation history\n" + ConversationHistory.SectionEnd + "\n";

        var merged = SharedInfo.Merge(withHistory, "shared");

        Assert.StartsWith("intro\n\n" + SharedInfo.SectionStart, merged);
        Assert.EndsWith(SharedInfo.SectionEnd + "\n\n" + withHistory["intro\n\n".Length..], merged);
        Assert.Equal(withHistory, SharedInfo.Merge(merged, ""));
    }

    [Fact]
    public void Merge_cannot_be_confused_by_markers_inside_the_text_or_by_a_start_marker_without_its_end()
    {
        var hostile = $"before\n{SharedInfo.SectionEnd}\n{ConversationHistory.SectionStart}\nafter";
        var first = SharedInfo.Merge("top\n", hostile);
        var second = SharedInfo.Merge(first + "\nbelow\n", "calm");

        Assert.Equal("top\n\n" + SharedInfo.Merge("", "calm") + "\nbelow\n", second);

        // Someone deleted the end marker by hand: what follows the orphaned start is theirs, not the section's.
        var broken = $"top\n{SharedInfo.SectionStart}\nkeep me\n";
        var repaired = SharedInfo.Merge(SharedInfo.Merge(broken, "one"), "two");
        Assert.Contains("keep me", repaired);
        Assert.Contains("\ntwo\n", repaired);
        Assert.DoesNotContain("one", repaired);
    }

    // ---- Apply ---------------------------------------------------------------------------------------

    [Fact]
    public void Apply_creates_CLAUDE_md_and_removes_it_again_when_nothing_else_is_in_it()
    {
        var project = Project("fresh");
        var claudeMd = Path.Combine(project, "CLAUDE.md");

        Assert.Equal(new SharedInfoResult(true, claudeMd, null), SharedInfo.Apply(project, "Be brief."));
        Assert.Contains("Be brief.", File.ReadAllText(claudeMd));
        Assert.Equal(new SharedInfoResult(false, claudeMd, null), SharedInfo.Apply(project, "Be brief."));

        Assert.True(SharedInfo.Apply(project, "").Changed);
        Assert.False(File.Exists(claudeMd));
    }

    [Fact]
    public void Apply_does_not_create_CLAUDE_md_for_an_empty_text()
    {
        var project = Project("quiet");

        Assert.Equal(new SharedInfoResult(false, Path.Combine(project, "CLAUDE.md"), null), SharedInfo.Apply(project, ""));
        Assert.False(File.Exists(Path.Combine(project, "CLAUDE.md")));
    }

    [Fact]
    public void Apply_keeps_the_byte_order_mark_and_uses_the_file_in_dot_claude_when_only_that_exists()
    {
        var project = Project("dotclaude");
        var claudeMd = Path.Combine(Directory.CreateDirectory(Path.Combine(project, ".claude")).FullName, "CLAUDE.md");
        File.WriteAllText(claudeMd, "# Rules\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = SharedInfo.Apply(project, "Be brief.");

        Assert.Equal(new SharedInfoResult(true, claudeMd, null), result);
        Assert.Equal(Encoding.UTF8.GetPreamble(), File.ReadAllBytes(claudeMd)[..3]);
        Assert.StartsWith("# Rules\n\n" + SharedInfo.SectionStart, File.ReadAllText(claudeMd));
        Assert.False(File.Exists(Path.Combine(project, "CLAUDE.md")));
    }

    [Fact]
    public void Apply_reports_a_failure_instead_of_throwing()
    {
        var project = Project("locked");
        var claudeMd = Path.Combine(project, "CLAUDE.md");
        File.WriteAllText(claudeMd, "# Rules\n");
        File.SetAttributes(claudeMd, FileAttributes.ReadOnly);
        try
        {
            var result = SharedInfo.Apply(project, "Be brief.");

            Assert.False(result.Changed);
            Assert.NotNull(result.Error);
            Assert.Equal("# Rules\n", File.ReadAllText(claudeMd));
        }
        finally
        {
            File.SetAttributes(claudeMd, FileAttributes.Normal);
        }

        Assert.NotNull(SharedInfo.Apply(Path.Combine(_root, "missing"), "Be brief.").Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "missing")));
    }
}

/// <summary>The shared info as the page drives it: edited in one place, kept in every listed project.</summary>
public sealed class DashboardSharedInfoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly List<JsonDocument> _posted = [];
    private readonly Dashboard _dashboard;

    public DashboardSharedInfoTests()
    {
        _store = new StateStore(Path.Combine(_root, "data"));
        _store.Load();
        _store.State.Entries.AddRange(
        [
            new Entry { Id = "a", Path = Project("Alpha"), Name = "Alpha" },
            new Entry { Id = "b", Path = Project("Beta"), Name = "Beta" },
        ]);
        Grouping.Reconcile(_store.State);
        File.WriteAllText(ClaudeMd("Beta"), "# Beta\n");
        _dashboard = new Dashboard(_store, Dispatcher.CurrentDispatcher, json => _posted.Add(JsonDocument.Parse(json)));
    }

    public void Dispose()
    {
        foreach (var document in _posted) document.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Project(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private string ClaudeMd(string project) => Path.Combine(_root, project, "CLAUDE.md");

    private void Send(object message) => _dashboard.HandleMessage(JsonSerializer.Serialize(message));

    private JsonElement Last => _posted.Last().RootElement;

    [Fact]
    public void Saving_the_text_writes_it_to_every_listed_project_and_remembers_it()
    {
        Send(new { type = "sharedInfo", text = "Answer in Czech.\r\n\r\n" });

        Assert.Equal("sharedInfoSaved", Last.GetProperty("type").GetString());
        Assert.Equal("Answer in Czech.", Last.GetProperty("text").GetString());
        Assert.Equal(2, Last.GetProperty("changed").GetInt32());
        Assert.Equal(0, Last.GetProperty("failed").GetArrayLength());
        Assert.Contains("\nAnswer in Czech.\n", File.ReadAllText(ClaudeMd("Alpha")));
        Assert.StartsWith("# Beta\n\n" + SharedInfo.SectionStart, File.ReadAllText(ClaudeMd("Beta")));

        var reloaded = new StateStore(Path.Combine(_root, "data"));
        reloaded.Load();
        Assert.Equal("Answer in Czech.", reloaded.State.SharedInfo);
    }

    [Fact]
    public void Changing_the_text_changes_it_everywhere_and_clearing_it_takes_it_out()
    {
        Send(new { type = "sharedInfo", text = "first" });
        Send(new { type = "sharedInfo", text = "second" });

        Assert.All(new[] { "Alpha", "Beta" }, project =>
        {
            var text = File.ReadAllText(ClaudeMd(project));
            Assert.Contains("\nsecond\n", text);
            Assert.DoesNotContain("first", text);
        });

        Send(new { type = "sharedInfo", text = "" });

        Assert.False(File.Exists(ClaudeMd("Alpha"))); // Maid made it, and nothing else is in it
        Assert.Equal("# Beta\n", File.ReadAllText(ClaudeMd("Beta")));
    }

    [Fact]
    public void A_project_that_cannot_be_written_is_reported_and_the_others_are_still_updated()
    {
        _store.State.Entries.Add(new Entry { Id = "g", Path = Path.Combine(_root, "Gone"), Name = "Gone", Group = "Misc" });

        Send(new { type = "sharedInfo", text = "hello" });

        Assert.Equal(2, Last.GetProperty("changed").GetInt32());
        var failure = Assert.Single(Last.GetProperty("failed").EnumerateArray());
        Assert.Equal("Gone", failure.GetProperty("name").GetString());
        Assert.Contains("hello", File.ReadAllText(ClaudeMd("Alpha")));
    }

    [Fact]
    public void A_project_added_later_gets_the_text_too()
    {
        Send(new { type = "sharedInfo", text = "hello" });

        Send(new { type = "create", path = Project("Gamma"), group = "" });

        Assert.Equal("entryReady", Last.GetProperty("type").GetString());
        Assert.Contains("\nhello\n", File.ReadAllText(ClaudeMd("Gamma")));
    }

    [Fact]
    public void A_project_taken_off_the_list_loses_the_text()
    {
        Send(new { type = "sharedInfo", text = "hello" });

        Send(new { type = "remove", id = "b" });

        Assert.Equal("# Beta\n", File.ReadAllText(ClaudeMd("Beta")));
        Assert.Contains("hello", File.ReadAllText(ClaudeMd("Alpha")));
    }

    [Fact]
    public void The_page_is_told_the_text_when_it_starts()
    {
        Send(new { type = "sharedInfo", text = "hello" });

        Send(new { type = "ready" });

        var init = _posted.Last(document => document.RootElement.GetProperty("type").GetString() == "init").RootElement;
        Assert.Equal("hello", init.GetProperty("sharedInfo").GetString());
    }
}
