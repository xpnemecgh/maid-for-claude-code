using System.Text.Json;
using System.Windows.Threading;

namespace Maid.Tests;

/// <summary>The list edits the page can ask for (order, names, groups), driven through the message bridge.</summary>
public sealed class DashboardListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly List<JsonDocument> _posted = [];
    private readonly Dashboard _dashboard;

    public DashboardListTests()
    {
        Directory.CreateDirectory(_root);
        _store = new StateStore(_root);
        _store.Load();
        _store.State.Entries.AddRange(
        [
            new Entry { Id = "a", Path = @"X:\Alpha", Name = "Alpha", Group = "Work" },
            new Entry { Id = "b", Path = @"X:\Beta", Name = "Beta", Group = "Work" },
            new Entry { Id = "c", Path = @"X:\Gamma", Name = "Gamma", Group = "Work" },
            new Entry { Id = "m", Path = @"X:\Other", Name = "Other" },
        ]);
        Grouping.Reconcile(_store.State);
        _dashboard = new Dashboard(_store, Dispatcher.CurrentDispatcher, json => _posted.Add(JsonDocument.Parse(json)));
    }

    public void Dispose()
    {
        foreach (var document in _posted) document.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private void Send(object message) => _dashboard.HandleMessage(JsonSerializer.Serialize(message));

    private IEnumerable<string> Names(string group) =>
        _store.State.Entries.Where(entry => entry.Group == group).Select(entry => entry.Name);

    private Entry Entry(string id) => _store.State.Entries.Single(entry => entry.Id == id);

    private string? LastType => _posted.LastOrDefault()?.RootElement.GetProperty("type").GetString();

    private string? LastMessage => _posted.LastOrDefault()?.RootElement.GetProperty("message").GetString();

    // ---- order --------------------------------------------------------------------------------------

    [Fact]
    public void Reorder_moves_an_entry_within_its_group()
    {
        Send(new { type = "reorder", id = "c", group = "Work", index = 0 });

        Assert.Equal(["Gamma", "Alpha", "Beta"], Names("Work"));
        Assert.Equal("list", LastType);
    }

    [Fact]
    public void Reorder_past_the_end_puts_the_entry_last()
    {
        Send(new { type = "reorder", id = "a", group = "Work", index = 99 });

        Assert.Equal(["Beta", "Gamma", "Alpha"], Names("Work"));
    }

    [Fact]
    public void Reorder_into_another_group_moves_the_entry_there_at_the_position()
    {
        Send(new { type = "reorder", id = "m", group = "Work", index = 1 });

        Assert.Equal("Work", Entry("m").Group);
        Assert.Equal(["Alpha", "Other", "Beta", "Gamma"], Names("Work"));
        Assert.Empty(Names("Misc"));
        Assert.Contains(_store.State.Groups, group => group.Name == "Misc"); // the group stays, empty
    }

    [Fact]
    public void Reorder_leaves_other_groups_alone()
    {
        Send(new { type = "reorder", id = "b", group = "Work", index = 2 });

        Assert.Equal(["Other"], Names("Misc"));
        Assert.Equal(["Alpha", "Gamma", "Beta"], Names("Work"));
    }

    [Fact]
    public void The_saved_order_survives_a_reload()
    {
        Send(new { type = "reorder", id = "c", group = "Work", index = 0 });

        var reloaded = new StateStore(_root);
        reloaded.Load();
        Assert.Equal(["Gamma", "Alpha", "Beta", "Other"], reloaded.State.Entries.Select(entry => entry.Name));
    }

    // ---- groups -------------------------------------------------------------------------------------

    [Fact]
    public void ReorderGroup_moves_a_group_to_the_position()
    {
        Assert.Equal(["Work", "Misc"], _store.State.Groups.Select(group => group.Name));

        Send(new { type = "reorderGroup", name = "misc", index = 0 });

        Assert.Equal(["Misc", "Work"], _store.State.Groups.Select(group => group.Name));
        Assert.Equal("list", LastType);
    }

    [Fact]
    public void ReorderGroup_past_the_end_puts_the_group_last_and_the_order_is_saved()
    {
        Send(new { type = "addGroup", name = "Later" });
        Send(new { type = "reorderGroup", name = "Work", index = 99 });

        Assert.Equal(["Misc", "Later", "Work"], _store.State.Groups.Select(group => group.Name));

        var reloaded = new StateStore(_root);
        reloaded.Load();
        Assert.Equal(["Misc", "Later", "Work"], reloaded.State.Groups.Select(group => group.Name));
    }

    [Fact]
    public void A_new_group_goes_last()
    {
        Send(new { type = "moveEntry", id = "a", group = "Aardvark" });

        Assert.Equal(["Work", "Misc", "Aardvark"], _store.State.Groups.Select(group => group.Name));
    }

    [Fact]
    public void SetGroupColor_gives_a_group_a_colour_that_is_saved_and_can_be_taken_away_again()
    {
        Send(new { type = "setGroupColor", name = "work", color = "#7FCFFF" });

        Assert.Equal("list", LastType);
        Assert.Equal("#7fcfff", _posted.Last().RootElement.GetProperty("groups")[0].GetProperty("color").GetString());
        Assert.Null(Grouping.Find(_store.State, "Misc")!.Color);

        var reloaded = new StateStore(_root);
        reloaded.Load();
        Assert.Equal("#7fcfff", Grouping.Find(reloaded.State, "Work")!.Color);

        Send(new { type = "setGroupColor", name = "Work", color = "" });

        Assert.Null(Grouping.Find(_store.State, "Work")!.Color);
    }

    [Fact]
    public void A_groups_colour_survives_renaming_it_and_anything_that_is_not_a_colour_means_none()
    {
        Send(new { type = "setGroupColor", name = "Work", color = "#ff8f88" });
        Send(new { type = "renameGroup", name = "Work", newName = "Job" });

        Assert.Equal("#ff8f88", Grouping.Find(_store.State, "Job")!.Color);

        Send(new { type = "setGroupColor", name = "Job", color = "red; background: url(x)" });

        Assert.Null(Grouping.Find(_store.State, "Job")!.Color);
    }

    // ---- nested groups ------------------------------------------------------------------------------

    private string ParentOf(string name) => Grouping.Find(_store.State, name)!.Parent;

    private IEnumerable<string> ChildrenOf(string parent) => Grouping.Children(_store.State, parent).Select(group => group.Name);

    [Fact]
    public void AddGroup_with_a_parent_nests_the_new_group_last_in_it()
    {
        Send(new { type = "addGroup", name = "Clients", parent = "work" });
        Send(new { type = "addGroup", name = "Tools", parent = "Work" });

        Assert.Equal("list", LastType);
        Assert.Equal(["Clients", "Tools"], ChildrenOf("Work"));
        Assert.Equal(["Work", "Misc"], ChildrenOf(""));
        Assert.Equal("Work", _posted.Last().RootElement.GetProperty("groups")[2].GetProperty("parent").GetString());
    }

    [Fact]
    public void AddGroup_in_a_group_that_is_gone_is_refused()
    {
        Send(new { type = "addGroup", name = "Clients", parent = "Gone" });

        Assert.Equal("listFailed", LastType);
        Assert.Null(Grouping.Find(_store.State, "Clients"));
    }

    [Fact]
    public void ReorderGroup_with_a_parent_puts_the_group_inside_it_at_the_position()
    {
        Send(new { type = "addGroup", name = "Clients", parent = "Work" });
        Send(new { type = "addGroup", name = "Tools", parent = "Work" });

        Send(new { type = "reorderGroup", name = "Misc", parent = "Work", index = 1 });

        Assert.Equal(["Clients", "Misc", "Tools"], ChildrenOf("Work"));
        Assert.Equal(["Work"], ChildrenOf(""));
        Assert.Equal(["Other"], Names("Misc")); // the projects go along

        Send(new { type = "reorderGroup", name = "Misc", parent = "" });

        Assert.Equal(["Work", "Misc"], ChildrenOf(""));
        Assert.Equal(["Clients", "Tools"], ChildrenOf("Work"));
    }

    [Fact]
    public void ReorderGroup_without_a_parent_moves_the_group_among_the_ones_next_to_it()
    {
        Send(new { type = "addGroup", name = "Clients", parent = "Work" });
        Send(new { type = "addGroup", name = "Tools", parent = "Work" });

        Send(new { type = "reorderGroup", name = "Tools", index = 0 });

        Assert.Equal(["Tools", "Clients"], ChildrenOf("Work"));
        Assert.Equal(["Work", "Misc"], ChildrenOf(""));
    }

    [Fact]
    public void A_group_cannot_be_put_inside_itself_or_a_group_nested_in_it()
    {
        Send(new { type = "addGroup", name = "Clients", parent = "Work" });
        Send(new { type = "addGroup", name = "Acme", parent = "Clients" });

        Send(new { type = "reorderGroup", name = "Work", parent = "Acme" });
        Assert.Equal("listFailed", LastType);
        Send(new { type = "reorderGroup", name = "Work", parent = "Work" });
        Assert.Equal("listFailed", LastType);

        Assert.Equal("", ParentOf("Work"));
        Assert.Equal("Work", ParentOf("Clients"));
        Assert.Equal("Clients", ParentOf("Acme"));
    }

    [Fact]
    public void Renaming_a_group_keeps_the_groups_nested_in_it()
    {
        Send(new { type = "addGroup", name = "Clients", parent = "Work" });

        Send(new { type = "renameGroup", name = "Work", newName = "Job" });

        Assert.Equal("Job", ParentOf("Clients"));
    }

    [Fact]
    public void Removing_a_nested_group_moves_its_projects_to_its_parent_and_its_groups_up_into_its_place()
    {
        Send(new { type = "addGroup", name = "Clients", parent = "Work" });
        Send(new { type = "addGroup", name = "Acme", parent = "Clients" });
        Send(new { type = "addGroup", name = "Beta", parent = "Clients" });
        Send(new { type = "addGroup", name = "Tools", parent = "Work" });
        Send(new { type = "reorder", id = "a", group = "Clients", index = 0 });

        Send(new { type = "removeGroup", name = "Clients" });

        Assert.Equal("list", LastType);
        Assert.Null(Grouping.Find(_store.State, "Clients"));
        Assert.Equal(["Acme", "Beta", "Tools"], ChildrenOf("Work"));
        Assert.Equal(["Beta", "Gamma", "Alpha"], Names("Work")); // last in the parent
    }

    [Fact]
    public void Removing_a_top_level_group_moves_its_projects_to_Misc_and_its_groups_to_the_top_level()
    {
        Send(new { type = "addGroup", name = "Clients", parent = "Work" });

        Send(new { type = "removeGroup", name = "Work" });

        Assert.Equal(["Clients", "Misc"], ChildrenOf(""));
        Assert.Equal(["Other", "Alpha", "Beta", "Gamma"], Names("Misc"));
    }

    [Fact]
    public void Nesting_is_saved()
    {
        Send(new { type = "addGroup", name = "Clients", parent = "Work" });

        var reloaded = new StateStore(_root);
        reloaded.Load();
        Assert.Equal("Work", Grouping.Find(reloaded.State, "Clients")!.Parent);
    }

    // ---- names --------------------------------------------------------------------------------------

    [Fact]
    public void Rename_gives_the_entry_a_custom_name()
    {
        Send(new { type = "renameEntry", id = "a", name = "  My   App " });

        Assert.Equal("My App", Entry("a").Name);
        Assert.Equal(@"X:\Alpha", Entry("a").Path);
        Assert.Equal("list", LastType);
    }

    [Fact]
    public void Rename_refuses_a_name_another_entry_carries()
    {
        Send(new { type = "renameEntry", id = "a", name = "beta" });

        Assert.Equal("Alpha", Entry("a").Name);
        Assert.Equal("listFailed", LastType);
        Assert.Contains("Beta", LastMessage);
    }

    [Fact]
    public void Rename_to_the_same_name_in_another_case_is_allowed()
    {
        Send(new { type = "renameEntry", id = "a", name = "ALPHA" });

        Assert.Equal("ALPHA", Entry("a").Name);
        Assert.Equal("list", LastType);
    }

    [Fact]
    public void Clearing_the_name_goes_back_to_the_folder_name()
    {
        Send(new { type = "renameEntry", id = "a", name = "Custom" });
        Send(new { type = "renameEntry", id = "b", name = "Alpha" }); // free now

        Send(new { type = "renameEntry", id = "a", name = "" });

        Assert.Equal("Alpha (2)", Entry("a").Name); // the folder's name, made unique
        Assert.Equal("Alpha", Entry("b").Name);
    }

    [Fact]
    public void Long_names_are_cut()
    {
        Send(new { type = "renameEntry", id = "a", name = new string('n', 100) });

        Assert.Equal(60, Entry("a").Name.Length);
    }

    // ---- adding -------------------------------------------------------------------------------------

    [Fact]
    public void Adding_a_folder_that_is_listed_points_out_its_entry_instead_of_adding_another()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Project")).FullName;
        Send(new { type = "create", path = folder, group = "" });
        var id = _posted.Last().RootElement.GetProperty("entry").GetProperty("id").GetString();

        // Sessions are per tile now, so a folder is listed once however many of them run in it.
        Send(new { type = "create", path = folder.ToUpperInvariant(), group = "Work" });

        Assert.Equal("entryReady", LastType);
        Assert.False(_posted.Last().RootElement.GetProperty("added").GetBoolean());
        Assert.Equal(id, _posted.Last().RootElement.GetProperty("entry").GetProperty("id").GetString());
        Assert.Single(_store.State.Entries, entry => entry.Path == folder);
    }
}
