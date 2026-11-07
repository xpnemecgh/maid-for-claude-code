using System.Text;

namespace Maid.Tests;

/// <summary>AGENTS.md (what Codex reads) is kept identical to CLAUDE.md, whichever of the two was edited.</summary>
public sealed class ClaudeMdFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));

    public ClaudeMdFileTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Project(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string Claude(string project) => Path.Combine(project, "CLAUDE.md");

    private static string Agents(string project) => Path.Combine(project, "AGENTS.md");

    [Fact]
    public void Update_writes_AGENTS_md_with_the_same_text_and_each_file_keeps_its_byte_order_mark()
    {
        var project = Project("both");
        File.WriteAllText(Claude(project), "# Rules\r\n", new UTF8Encoding(true));

        var (file, changed) = ClaudeMdFile.Update(project, text => text + "added\r\n");

        Assert.Equal(Claude(project), file);
        Assert.True(changed);
        Assert.Equal("# Rules\r\nadded\r\n", File.ReadAllText(Claude(project)));
        Assert.Equal(File.ReadAllBytes(Claude(project)), File.ReadAllBytes(Agents(project)));
        Assert.False(ClaudeMdFile.Update(project, text => text).Changed);
    }

    [Fact]
    public void Sync_gives_a_project_with_only_one_of_the_files_the_other_one()
    {
        var claudeOnly = Project("claude-only");
        File.WriteAllText(Claude(claudeOnly), "# Claude rules\n");
        var agentsOnly = Project("agents-only");
        File.WriteAllText(Agents(agentsOnly), "# Codex rules\n");
        var neither = Project("neither");

        Assert.Null(ClaudeMdFile.Sync(claudeOnly));
        Assert.Null(ClaudeMdFile.Sync(agentsOnly));
        Assert.Null(ClaudeMdFile.Sync(neither));

        Assert.Equal("# Claude rules\n", File.ReadAllText(Agents(claudeOnly)));
        Assert.Equal("# Codex rules\n", File.ReadAllText(Claude(agentsOnly)));
        Assert.Empty(Directory.GetFiles(neither));
        Assert.NotNull(ClaudeMdFile.Sync(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void An_edit_to_either_file_reaches_the_other()
    {
        var project = Project("edited");
        File.WriteAllText(Claude(project), "one\n");
        ClaudeMdFile.Sync(project);

        File.WriteAllText(Agents(project), "one\ntwo (from Codex)\n");
        ClaudeMdFile.Sync(project);
        Assert.Equal("one\ntwo (from Codex)\n", File.ReadAllText(Claude(project)));

        File.WriteAllText(Claude(project), "one\ntwo (from Codex)\nthree (from Claude)\n");
        ClaudeMdFile.Update(project, text => text + "four (Maid)\n");
        Assert.Equal("one\ntwo (from Codex)\nthree (from Claude)\nfour (Maid)\n", File.ReadAllText(Agents(project)));
        Assert.Equal(File.ReadAllText(Agents(project)), File.ReadAllText(Claude(project)));
    }

    [Fact]
    public void After_a_folder_is_renamed_an_edit_to_either_file_still_reaches_the_other()
    {
        var parent = Project("renamed-parent");
        var project = Directory.CreateDirectory(Path.Combine(parent, "app")).FullName;
        File.WriteAllText(Claude(project), "one\n");
        ClaudeMdFile.Sync(project);
        var renamed = Path.Combine(_root, "renamed-parent-2");
        Directory.Move(parent, renamed);
        project = Path.Combine(renamed, "app");

        ClaudeMdFile.MoveBases(parent, renamed);
        File.WriteAllText(Agents(project), "one\ntwo (from Codex)\n");
        ClaudeMdFile.Sync(project);

        // Not taken for a first meeting (where CLAUDE.md wins and AGENTS.md is kept aside).
        Assert.Equal("one\ntwo (from Codex)\n", File.ReadAllText(Claude(project)));
        Assert.Equal(2, Directory.GetFiles(project).Length);
    }

    [Fact]
    public void Meeting_an_AGENTS_md_of_its_own_makes_it_a_copy_of_CLAUDE_md_and_keeps_the_old_one_aside()
    {
        var project = Project("own");
        File.WriteAllText(Claude(project), "# Rules\r\nWrite solution_claude.txt.\r\n");
        File.WriteAllText(Agents(project), "# Rules\nWrite solution_codex.txt.\n", new UTF8Encoding(true));

        ClaudeMdFile.Sync(project);

        Assert.Equal("# Rules\r\nWrite solution_claude.txt.\r\n", File.ReadAllText(Claude(project)));
        Assert.Equal(File.ReadAllText(Claude(project)), File.ReadAllText(Agents(project)));
        var kept = Path.Combine(project, "AGENTS.maid-original.md");
        Assert.Equal([.. Encoding.UTF8.GetPreamble(), .. "# Rules\nWrite solution_codex.txt.\n"u8.ToArray()], File.ReadAllBytes(kept));

        // Nothing is kept aside when nothing of it would be lost.
        var contained = Project("contained");
        File.WriteAllText(Claude(contained), "# Rules\nUse tabs.\nMore.\n");
        File.WriteAllText(Agents(contained), "Use tabs.\n");
        ClaudeMdFile.Sync(contained);
        Assert.Equal("# Rules\nUse tabs.\nMore.\n", File.ReadAllText(Agents(contained)));
        Assert.Equal(2, Directory.GetFiles(contained).Length);
    }

    [Fact]
    public void A_CLAUDE_md_that_imports_AGENTS_md_gets_its_text_and_an_empty_one_gives_way()
    {
        var imported = Project("imported");
        File.WriteAllText(Claude(imported), "# Project\r\n@AGENTS.md\r\nMore for Claude.\r\n");
        File.WriteAllText(Agents(imported), "Use tabs.\nNo $1 here.\n");

        ClaudeMdFile.Sync(imported);

        Assert.Equal("# Project\r\nUse tabs.\r\nNo $1 here.\r\nMore for Claude.\r\n", File.ReadAllText(Claude(imported)));
        Assert.Equal(File.ReadAllText(Claude(imported)), File.ReadAllText(Agents(imported)));
        Assert.Equal(2, Directory.GetFiles(imported).Length);

        var empty = Project("empty");
        File.WriteAllText(Claude(empty), "\n");
        File.WriteAllText(Agents(empty), "# Codex rules\n");
        ClaudeMdFile.Sync(empty);
        Assert.Equal("# Codex rules\n", File.ReadAllText(Claude(empty)));
    }

    [Fact]
    public void When_both_were_edited_the_later_edit_wins_and_the_other_is_kept_beside_it()
    {
        var project = Project("conflict");
        File.WriteAllText(Claude(project), "base\n");
        ClaudeMdFile.Sync(project);
        File.WriteAllText(Claude(project), "base\nClaude's edit\n");
        File.WriteAllText(Agents(project), "base\nCodex's edit\n");
        File.SetLastWriteTimeUtc(Claude(project), DateTime.UtcNow.AddMinutes(-1));

        ClaudeMdFile.Sync(project);

        Assert.Equal("base\nCodex's edit\n", File.ReadAllText(Claude(project)));
        Assert.Equal("base\nCodex's edit\n", File.ReadAllText(Agents(project)));
        Assert.Equal("base\nClaude's edit\n", File.ReadAllText(Path.Combine(project, "CLAUDE.maid-conflict.md")));
    }

    [Fact]
    public void Nothing_left_deletes_both_files_and_dot_claude_CLAUDE_md_is_mirrored_to_the_project_folder()
    {
        var project = Project("dotclaude");
        var inDotClaude = Path.Combine(Directory.CreateDirectory(Path.Combine(project, ".claude")).FullName, "CLAUDE.md");
        File.WriteAllText(inDotClaude, "# Rules\n");

        ClaudeMdFile.Sync(project);
        Assert.Equal("# Rules\n", File.ReadAllText(Agents(project)));
        Assert.False(File.Exists(Claude(project)));

        Assert.True(ClaudeMdFile.Update(project, _ => "").Changed);
        Assert.False(File.Exists(inDotClaude));
        Assert.False(File.Exists(Agents(project)));
    }
}
