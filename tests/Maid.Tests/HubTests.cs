using System.Text.Json;
using System.Windows.Threading;
using Maid.Terminal;

namespace Maid.Tests;

public sealed class LaunchCommandTests
{
    [Fact]
    public void Resuming_continues_the_last_conversation()
    {
        Assert.Equal("claude --dangerously-skip-permissions --continue",
            LaunchCommand.WithResume(" claude --dangerously-skip-permissions "));
    }
}

/// <summary>The folder of the session about all projects, and its shared-info.md that feeds every project.</summary>
public sealed class HubTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly List<JsonDocument> _posted = [];

    public HubTests()
    {
        _store = new StateStore(Path.Combine(_root, "data"));
        _store.Load();
        _store.State.Entries.AddRange(
        [
            new Entry { Id = "a", Path = Project("Alpha"), Name = "Alpha", Group = "Work" },
            new Entry { Id = "b", Path = Project("Beta"), Name = "Beta <!-- maid:hub:end -->" },
        ]);
        Grouping.Reconcile(_store.State);
    }

    public void Dispose()
    {
        foreach (var document in _posted) document.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Project(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private Hub Hub => new(Hub.GetDirectory(_store));

    [Fact]
    public void The_hub_lives_in_the_data_folder_unless_settings_say_otherwise()
    {
        Assert.Equal(Path.Combine(_root, "data", "Hub"), Hub.GetDirectory(_store));

        _store.State.Settings.HubPath = Path.Combine(_root, "Elsewhere") + "\\";
        Assert.Equal(Path.Combine(_root, "Elsewhere"), Hub.GetDirectory(_store));

        _store.State.Settings.HubPath = "relative\\path";
        Assert.Equal(Path.Combine(_root, "data", "Hub"), Hub.GetDirectory(_store));
    }

    [Fact]
    public void Prepare_writes_the_instructions_with_the_projects_and_the_shared_info_file()
    {
        _store.State.SharedInfo = "Answer in Czech.";
        var hub = Hub;

        hub.Prepare(_store.State);

        var claudeMd = File.ReadAllText(Path.Combine(hub.Directory, "CLAUDE.md"));
        Assert.StartsWith(Hub.SectionStart + "\n", claudeMd);
        Assert.Contains("shared-info.md", claudeMd);
        Assert.Contains($"### Work\n\n- **Alpha**: `{Path.Combine(_root, "Alpha")}`\n", claudeMd);
        Assert.Contains("- **Beta &lt;!-- maid:hub:end -->**", claudeMd); // a name cannot end the section
        Assert.Equal(1, claudeMd.Split(Hub.SectionEnd).Length - 1);
        Assert.Equal(claudeMd, File.ReadAllText(Path.Combine(hub.Directory, "AGENTS.md")));
        Assert.Equal("Answer in Czech.\r\n", File.ReadAllText(hub.SharedInfoFile));
        Assert.Equal("Answer in Czech.", hub.ReadSharedInfo());
    }

    [Fact]
    public void Projects_of_nested_groups_are_listed_under_the_path_of_their_group_in_the_order_of_the_list()
    {
        _store.State.Entries.Add(new Entry { Id = "c", Path = Project("Gamma"), Name = "Gamma", Group = "Clients" });
        _store.State.Groups.Add(new Group { Name = "Clients", Parent = "Work" });
        _store.State.Groups.Insert(0, new Group { Name = "Misc" }); // Misc first, Work and the group nested in it after
        Grouping.Reconcile(_store.State);

        var text = Hub.Describe(_store.State);

        int misc = text.IndexOf("### Misc\n", StringComparison.Ordinal);
        int work = text.IndexOf("### Work\n", StringComparison.Ordinal);
        int clients = text.IndexOf($"### Work / Clients\n\n- **Gamma**: `{Path.Combine(_root, "Gamma")}`", StringComparison.Ordinal);
        Assert.True(misc >= 0 && misc < work && work < clients, text);
    }

    [Fact]
    public void Prepare_again_updates_the_list_in_place_and_leaves_the_rest_of_the_file_alone()
    {
        var hub = Hub;
        hub.Prepare(_store.State);
        var claudeMd = Path.Combine(hub.Directory, "CLAUDE.md");
        File.AppendAllText(claudeMd, "\nMy own notes.\n");

        _store.State.Entries.RemoveAt(0);
        hub.Prepare(_store.State);

        var text = File.ReadAllText(claudeMd);
        Assert.DoesNotContain("Alpha", text);
        Assert.Contains("Beta", text);
        Assert.EndsWith(Hub.SectionEnd + "\n\nMy own notes.\n", text);
    }

    [Fact]
    public void A_changed_shared_info_file_goes_into_every_project_and_the_page_hears_of_it()
    {
        var dashboard = new Dashboard(_store, Dispatcher.CurrentDispatcher, json => _posted.Add(JsonDocument.Parse(json)));
        var hub = Hub;
        hub.Prepare(_store.State);

        File.WriteAllText(hub.SharedInfoFile, "# House rules\r\n\r\nBe brief.\r\n\r\n");
        dashboard.OnHubFileChanged();

        Assert.Equal("# House rules\n\nBe brief.", _store.State.SharedInfo);
        var saved = _posted.Last().RootElement;
        Assert.Equal("sharedInfoSaved", saved.GetProperty("type").GetString());
        Assert.Equal("hub", saved.GetProperty("source").GetString());
        foreach (var project in new[] { "Alpha", "Beta" })
        {
            Assert.Contains("\nBe brief.\n", File.ReadAllText(Path.Combine(_root, project, "CLAUDE.md")));
            Assert.Contains("\nBe brief.\n", File.ReadAllText(Path.Combine(_root, project, "AGENTS.md")));
        }

        // Unchanged, or deleted: nothing happens.
        int posted = _posted.Count;
        dashboard.OnHubFileChanged();
        File.Delete(hub.SharedInfoFile);
        dashboard.OnHubFileChanged();
        Assert.Equal(posted, _posted.Count);
        Assert.Equal("# House rules\n\nBe brief.", _store.State.SharedInfo);
    }

    [Fact]
    public void Saving_the_shared_info_in_Maid_rewrites_the_file_once_the_hub_exists()
    {
        var dashboard = new Dashboard(_store, Dispatcher.CurrentDispatcher, json => _posted.Add(JsonDocument.Parse(json)));
        dashboard.HandleMessage(JsonSerializer.Serialize(new { type = "sharedInfo", text = "first" }));
        Assert.False(Hub.Exists);

        Hub.Prepare(_store.State);
        dashboard.HandleMessage(JsonSerializer.Serialize(new { type = "sharedInfo", text = "second" }));

        Assert.Equal("second", Hub.ReadSharedInfo());
        Assert.Equal("page", _posted.Last().RootElement.GetProperty("source").GetString());
    }
}

/// <summary>Moving a project's folder, files and all, and the entry with it.</summary>
public sealed class RelocateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<JsonDocument> _posted = [];

    public RelocateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var document in _posted) document.Dispose();
        FolderMove.DeleteTree(_root);
    }

    private string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private (Dashboard Dashboard, StateStore Store) Start(params Entry[] entries)
    {
        var store = new StateStore(Path.Combine(_root, "data"));
        store.Load();
        store.State.Entries.AddRange(entries);
        Grouping.Reconcile(store.State);
        return (new Dashboard(store, Dispatcher.CurrentDispatcher, json => _posted.Add(JsonDocument.Parse(json))), store);
    }

    private void Send(Dashboard dashboard, object message) => dashboard.HandleMessage(JsonSerializer.Serialize(message));

    /// <summary>The move runs in the background and reports back through the dispatcher, which is pumped here.</summary>
    private JsonElement WaitFor(int after, params string[] types)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var found = _posted.Skip(after).FirstOrDefault(document => types.Contains(document.RootElement.GetProperty("type").GetString()));
            if (found is not null) return found.RootElement;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        throw new TimeoutException(string.Join(", ", types));
    }

    [Fact]
    public void Relocate_moves_the_folder_with_its_files_and_points_the_entry_and_projects_inside_it_there()
    {
        var source = Folder("old", "App");
        File.WriteAllText(Path.Combine(source, "CLAUDE.md"), "# App\n");
        var readOnly = Path.Combine(Folder("old", "App", ".git", "objects"), "ab");
        File.WriteAllText(readOnly, "object");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var nested = Folder("old", "App", "tools");
        var (dashboard, store) = Start(
            new Entry { Id = "app", Path = source, Name = "App" },
            new Entry { Id = "tools", Path = nested, Name = "Tools" });
        var target = Path.Combine(_root, "new", "Renamed");

        Send(dashboard, new { type = "relocate", id = "app", path = target + "\\" });

        Assert.Equal("relocating", _posted.Last().RootElement.GetProperty("type").GetString());
        var done = WaitFor(0, "relocated", "relocateFailed");
        Assert.Equal("relocated", done.GetProperty("type").GetString());
        Assert.Equal(target, done.GetProperty("path").GetString());
        Assert.False(Directory.Exists(source));
        Assert.Equal("# App\n", File.ReadAllText(Path.Combine(target, "CLAUDE.md")));
        Assert.Equal("object", File.ReadAllText(Path.Combine(target, ".git", "objects", "ab")));
        Assert.Equal(target, store.State.Entries.Single(entry => entry.Id == "app").Path);
        Assert.Equal(Path.Combine(target, "tools"), store.State.Entries.Single(entry => entry.Id == "tools").Path);
        Assert.Equal("App", store.State.Entries.Single(entry => entry.Id == "app").Name); // the name is the user's
        Assert.Contains(_posted, document => document.RootElement.GetProperty("type").GetString() == "list");

        var reloaded = new StateStore(Path.Combine(_root, "data"));
        reloaded.Load();
        Assert.Equal(target, reloaded.State.Entries.Single(entry => entry.Id == "app").Path);
    }

    [Fact]
    public void Relocate_refuses_what_it_cannot_do_and_leaves_the_folder_alone()
    {
        var source = Folder("Project");
        File.WriteAllText(Path.Combine(source, "file.txt"), "x");
        var occupied = Folder("Occupied");
        File.WriteAllText(Path.Combine(occupied, "other.txt"), "y");
        var listed = Folder("Listed");
        var (dashboard, store) = Start(
            new Entry { Id = "p", Path = source, Name = "Project" },
            new Entry { Id = "l", Path = listed, Name = "Listed" });

        string Refusal(string path)
        {
            Send(dashboard, new { type = "relocate", id = "p", path });
            var last = _posted.Last().RootElement;
            Assert.Equal("relocateFailed", last.GetProperty("type").GetString());
            return last.GetProperty("message").GetString()!;
        }

        Assert.Contains("already exists", Refusal(occupied));
        Assert.Contains("into itself", Refusal(Path.Combine(source, "sub")));
        Assert.Contains("already", Refusal(source.ToUpperInvariant()));
        Assert.Contains("Listed", Refusal(listed));
        Assert.Contains("full path", Refusal("relative"));
        Assert.True(File.Exists(Path.Combine(source, "file.txt")));
        Assert.Equal(source, store.State.Entries[0].Path);
    }

    [Fact]
    public void A_folder_moved_by_hand_is_followed_and_an_empty_target_folder_is_used()
    {
        var gone = Path.Combine(_root, "Gone");
        var there = Folder("There");
        var source = Folder("Source");
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        var empty = Folder("Empty");
        var (dashboard, store) = Start(
            new Entry { Id = "g", Path = gone, Name = "Gone" },
            new Entry { Id = "s", Path = source, Name = "Source" });

        Send(dashboard, new { type = "relocate", id = "g", path = there });
        Assert.Equal("relocated", _posted.Last().RootElement.GetProperty("type").GetString());
        Assert.Contains("nothing had to be moved", _posted.Last().RootElement.GetProperty("note").GetString());
        Assert.Equal(there, store.State.Entries[0].Path);

        int sent = _posted.Count;
        Send(dashboard, new { type = "relocate", id = "s", path = empty });
        Assert.Equal("relocated", WaitFor(sent, "relocated", "relocateFailed").GetProperty("type").GetString());
        Assert.Equal("a", File.ReadAllText(Path.Combine(empty, "a.txt")));
    }

    [Fact]
    public void Renaming_a_folder_above_projects_points_every_project_in_it_to_the_new_path()
    {
        var app = Folder("Work", "Clients", "App");
        File.WriteAllText(Path.Combine(app, "CLAUDE.md"), "# App\n");
        var tools = Folder("Work", "Clients", "App", "tools");
        var site = Folder("Work", "Clients", "Site");
        var other = Folder("Work", "ClientsOld");
        var (dashboard, store) = Start(
            new Entry { Id = "app", Path = app, Name = "App" },
            new Entry { Id = "tools", Path = tools, Name = "Tools" },
            new Entry { Id = "site", Path = site, Name = "Site" },
            new Entry { Id = "other", Path = other, Name = "ClientsOld" });
        var renamed = Path.Combine(_root, "Work", "Customers");

        Send(dashboard, new { type = "renameFolder", path = Path.Combine(_root, "work", "clients") + "\\", name = " Customers ", renameProject = true });

        var done = _posted.Last().RootElement;
        Assert.Equal("folderRenamed", done.GetProperty("type").GetString());
        Assert.Equal(Path.Combine(_root, "Work", "Clients"), done.GetProperty("from").GetString());
        Assert.Equal(renamed, done.GetProperty("to").GetString());
        Assert.Equal(3, done.GetProperty("projects").GetInt32());
        Assert.False(Directory.Exists(Path.Combine(_root, "Work", "Clients")));
        Assert.Equal("# App\n", File.ReadAllText(Path.Combine(renamed, "App", "CLAUDE.md")));
        string PathOf(string id) => store.State.Entries.Single(entry => entry.Id == id).Path;
        Assert.Equal(Path.Combine(renamed, "App"), PathOf("app"));
        Assert.Equal(Path.Combine(renamed, "App", "tools"), PathOf("tools"));
        Assert.Equal(Path.Combine(renamed, "Site"), PathOf("site"));
        Assert.Equal(other, PathOf("other")); // only a similar name
        Assert.Equal(["App", "Tools", "Site", "ClientsOld"], store.State.Entries.Select(entry => entry.Name)); // no project had that folder itself
        Assert.Equal("list", _posted[^2].RootElement.GetProperty("type").GetString());

        var reloaded = new StateStore(Path.Combine(_root, "data"));
        reloaded.Load();
        Assert.Equal(Path.Combine(renamed, "Site"), reloaded.State.Entries.Single(entry => entry.Id == "site").Path);
    }

    [Fact]
    public void Renaming_a_projects_own_folder_renames_the_project_only_when_asked_and_a_change_of_case_is_a_rename_too()
    {
        var first = Folder("first-app");
        var second = Folder("second-app");
        var (dashboard, store) = Start(
            new Entry { Id = "first", Path = first, Name = "first-app" },
            new Entry { Id = "second", Path = second, Name = "My own name" });

        Send(dashboard, new { type = "renameFolder", path = first, name = "First-App", renameProject = true });
        Send(dashboard, new { type = "renameFolder", path = second, name = "second", renameProject = false });

        Assert.Equal(2, _posted.Count(document => document.RootElement.GetProperty("type").GetString() == "folderRenamed"));
        Assert.Equal("First-App", new DirectoryInfo(_root).GetDirectories("first-app").Single().Name);
        Assert.Equal(Path.Combine(_root, "First-App"), store.State.Entries[0].Path);
        Assert.Equal("First-App", store.State.Entries[0].Name);
        Assert.Equal(Path.Combine(_root, "second"), store.State.Entries[1].Path);
        Assert.Equal("My own name", store.State.Entries[1].Name);
    }

    [Fact]
    public void Renaming_a_folder_refuses_what_it_cannot_do_and_leaves_everything_alone()
    {
        var source = Folder("Work", "Project");
        File.WriteAllText(Path.Combine(source, "file.txt"), "x");
        Folder("Work", "Taken");
        File.WriteAllText(Path.Combine(_root, "Work", "file.txt"), "y");
        var (dashboard, store) = Start(new Entry { Id = "p", Path = source, Name = "Project" });

        string Refusal(string path, string name)
        {
            Send(dashboard, new { type = "renameFolder", path, name });
            var last = _posted.Last().RootElement;
            Assert.Equal("renameFolderFailed", last.GetProperty("type").GetString());
            return last.GetProperty("message").GetString()!;
        }

        Assert.Contains("already", Refusal(source, "taken"));
        Assert.Contains("file named", Refusal(source, "file.txt"));
        Assert.Contains("called Project already", Refusal(source, "Project"));
        Assert.Contains("not a path", Refusal(source, @"..\Elsewhere"));
        Assert.Contains("cannot contain", Refusal(source, "a:b"));
        Assert.Contains("dot or a space", Refusal(source, "name."));
        Assert.Contains("reserved", Refusal(source, "con"));
        Assert.Contains("new name", Refusal(source, "  "));
        Assert.Contains("drive", Refusal(Path.GetPathRoot(_root)!, "Other"));
        Assert.Contains("full path", Refusal("relative", "Other"));
        Assert.Contains("not found", Refusal(Path.Combine(_root, "Missing"), "Other"));
        Assert.Contains("Maid's data folder", Refusal(_root, "Other"));
        Assert.True(File.Exists(Path.Combine(source, "file.txt")));
        Assert.Equal(source, store.State.Entries[0].Path);
    }

    [Fact]
    public void A_folder_renamed_by_hand_is_followed_and_a_folder_in_use_is_reported()
    {
        var gone = Path.Combine(_root, "Before", "App");
        Folder("After", "App");
        var busy = Folder("Busy");
        var (dashboard, store) = Start(
            new Entry { Id = "a", Path = gone, Name = "App" },
            new Entry { Id = "b", Path = busy, Name = "Busy" });

        Send(dashboard, new { type = "renameFolder", path = Path.Combine(_root, "Before"), name = "after" });
        var followed = _posted.Last().RootElement;
        Assert.Equal("folderRenamed", followed.GetProperty("type").GetString());
        Assert.Contains("nothing had to be renamed", followed.GetProperty("note").GetString());
        Assert.Equal(Path.Combine(_root, "After", "App"), store.State.Entries[0].Path);

        using (File.Open(Path.Combine(busy, "open.txt"), FileMode.Create))
            Send(dashboard, new { type = "renameFolder", path = busy, name = "Free" });
        var refused = _posted.Last().RootElement;
        Assert.Equal("renameFolderFailed", refused.GetProperty("type").GetString());
        Assert.Contains("may still be using", refused.GetProperty("message").GetString());
        Assert.Equal(busy, store.State.Entries[1].Path);
    }

    [Fact]
    public void A_copy_keeps_files_attributes_and_times_and_the_original_is_deleted_read_only_files_and_all()
    {
        var source = Folder("CopySource");
        var file = Path.Combine(Folder("CopySource", "deep", "er"), "data.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var written = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);
        var target = Path.Combine(_root, "CopyTarget");
        int copied = 0;

        FolderMove.CopyTree(source, target, count => copied = count);
        var (left, _) = FolderMove.DeleteTree(source);

        var copy = Path.Combine(target, "deep", "er", "data.bin");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(copy));
        Assert.True(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly | FileAttributes.Hidden));
        Assert.Equal(written, File.GetLastWriteTimeUtc(copy));
        Assert.Equal(1, copied);
        Assert.Equal(0, left);
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public void Claude_Codes_transcripts_move_along_with_the_project()
    {
        var config = Path.Combine(_root, "claude");
        var from = Directory.CreateDirectory(ConversationHistory.GetTranscriptDirectory(@"X:\Old\App", config)).FullName;
        File.WriteAllText(Path.Combine(from, "s1.jsonl"), "{}");
        var to = Directory.CreateDirectory(ConversationHistory.GetTranscriptDirectory(@"Y:\New\App", config)).FullName;
        File.WriteAllText(Path.Combine(to, "s1.jsonl"), "newer");
        File.WriteAllText(Path.Combine(from, "s2.jsonl"), "{}");

        FolderMove.MoveTranscripts(@"X:\Old\App", @"Y:\New\App", config);

        Assert.Equal("newer", File.ReadAllText(Path.Combine(to, "s1.jsonl"))); // never overwritten
        Assert.True(File.Exists(Path.Combine(to, "s2.jsonl")));

        FolderMove.MoveTranscripts(@"Y:\New\App", @"Z:\Third", config);
        Assert.False(Directory.Exists(to));
        Assert.True(File.Exists(Path.Combine(ConversationHistory.GetTranscriptDirectory(@"Z:\Third", config), "s2.jsonl")));
    }
}
