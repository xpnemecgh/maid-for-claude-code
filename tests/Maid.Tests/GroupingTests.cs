using System.Text.Json;

namespace Maid.Tests;

public sealed class GroupingTests
{
    private static Entry Entry(string name, string group = "") => new() { Id = name.ToLowerInvariant(), Path = @"X:\" + name, Name = name, Group = group };

    [Fact]
    public void NormalizeName_trims_collapses_whitespace_and_bounds_length()
    {
        Assert.Equal("My  Work".Replace("  ", " "), Grouping.NormalizeName("  My \t Work \n"));
        Assert.Null(Grouping.NormalizeName(""));
        Assert.Null(Grouping.NormalizeName("   "));
        Assert.Null(Grouping.NormalizeName(null));
        Assert.Equal(Grouping.MaxNameLength, Grouping.NormalizeName(new string('x', 100))!.Length);
    }

    [Fact]
    public void Entries_without_a_group_go_to_Misc()
    {
        var state = new AppState { Entries = [Entry("Maid"), Entry("Notes", "   ")] };

        Grouping.Reconcile(state);

        Assert.All(state.Entries, entry => Assert.Equal("Misc", entry.Group));
        var group = Assert.Single(state.Groups);
        Assert.Equal("Misc", group.Name);
        Assert.False(group.Collapsed);
    }

    [Fact]
    public void Groups_referenced_by_entries_are_created_and_entries_take_the_groups_spelling()
    {
        var state = new AppState
        {
            Entries = [Entry("Maid", "work"), Entry("Notes", "Writing"), Entry("Shop", "WORK")],
            Groups = [new Group { Name = "Work", Collapsed = true }],
        };

        Grouping.Reconcile(state);

        Assert.Equal(["Work", "Writing"], state.Groups.Select(group => group.Name));
        Assert.True(state.Groups[0].Collapsed);
        Assert.Equal("Work", state.Entries[0].Group);
        Assert.Equal("Work", state.Entries[2].Group);
    }

    [Fact]
    public void Groups_keep_their_order_and_duplicates_and_blanks_are_dropped()
    {
        var state = new AppState
        {
            Entries = [Entry("Maid"), Entry("Notes", "Writing")],
            Groups = [new Group { Name = "Misc" }, new Group { Name = "zeta" }, new Group { Name = "Alpha" }, new Group { Name = "alpha" }, new Group { Name = " " }],
        };

        Grouping.Reconcile(state);

        // The order is the user's; a group that only an entry knows about is created at the end.
        Assert.Equal(["Misc", "zeta", "Alpha", "Writing"], state.Groups.Select(group => group.Name));
    }

    [Fact]
    public void Empty_groups_survive()
    {
        var state = new AppState { Groups = [new Group { Name = "Later" }] };

        Grouping.Reconcile(state);

        Assert.Equal(["Later"], state.Groups.Select(group => group.Name));
    }

    // ---- nesting ------------------------------------------------------------------------------------

    private static string ParentOf(AppState state, string name) => Grouping.Find(state, name)!.Parent;

    [Fact]
    public void Nested_groups_keep_their_parent_in_its_spelling()
    {
        var state = new AppState
        {
            Groups = [new Group { Name = "Work" }, new Group { Name = "Clients", Parent = "  work " }, new Group { Name = "Acme", Parent = "CLIENTS" }],
        };

        Grouping.Reconcile(state);

        Assert.Equal("", ParentOf(state, "Work"));
        Assert.Equal("Work", ParentOf(state, "Clients"));
        Assert.Equal("Clients", ParentOf(state, "Acme"));
        Assert.Equal("Work / Clients / Acme", Grouping.PathOf(state, Grouping.Find(state, "Acme")!));
    }

    [Fact]
    public void A_group_nested_in_one_that_is_not_there_or_in_itself_goes_to_the_top_level()
    {
        var state = new AppState
        {
            Groups = [new Group { Name = "Orphan", Parent = "Gone" }, new Group { Name = "Narcissus", Parent = "narcissus" }],
        };

        Grouping.Reconcile(state);

        Assert.Equal("", ParentOf(state, "Orphan"));
        Assert.Equal("", ParentOf(state, "Narcissus"));
    }

    [Fact]
    public void A_loop_is_cut_at_the_first_of_its_groups_in_the_list()
    {
        var state = new AppState
        {
            Groups = [new Group { Name = "Top" }, new Group { Name = "A", Parent = "C" }, new Group { Name = "B", Parent = "A" }, new Group { Name = "C", Parent = "B" }, new Group { Name = "Below", Parent = "B" }],
        };

        Grouping.Reconcile(state);

        Assert.Equal("", ParentOf(state, "A"));
        Assert.Equal("A", ParentOf(state, "B"));
        Assert.Equal("B", ParentOf(state, "C"));
        Assert.Equal("B", ParentOf(state, "Below"));
    }

    [Fact]
    public void The_tree_lists_each_group_followed_by_the_ones_nested_in_it_in_their_order()
    {
        var state = new AppState
        {
            Groups =
            [
                new Group { Name = "Acme", Parent = "Clients" }, new Group { Name = "Work" }, new Group { Name = "Home" },
                new Group { Name = "Clients", Parent = "Work" }, new Group { Name = "Beta", Parent = "Clients" }, new Group { Name = "Tools", Parent = "Work" },
            ],
        };

        Grouping.Reconcile(state);

        Assert.Equal(["Work:0", "Clients:1", "Acme:2", "Beta:2", "Tools:1", "Home:0"],
            Grouping.Tree(state).Select(node => $"{node.Group.Name}:{node.Depth}"));
        Assert.True(Grouping.IsWithin(state, Grouping.Find(state, "Acme")!, Grouping.Find(state, "Work")!));
        Assert.False(Grouping.IsWithin(state, Grouping.Find(state, "Work")!, Grouping.Find(state, "Acme")!));
    }

    [Fact]
    public void A_group_only_an_entry_knows_about_is_created_at_the_top_level()
    {
        var state = new AppState { Entries = [Entry("Maid", "New")], Groups = [new Group { Name = "Work" }] };

        Grouping.Reconcile(state);

        Assert.Equal("", ParentOf(state, "New"));
    }
}

public sealed class StateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));

    public StateStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_state_file_saved_before_groups_existed_loads_with_its_entries_under_Misc()
    {
        File.WriteAllText(Path.Combine(_root, "state.json"), """
            {
              "entries": [
                { "id": "e8a07b781aab", "path": "X:\\Maid", "name": "Maid" },
                { "id": "e5eeb76aa399", "path": "X:\\Beta", "name": "Beta" }
              ],
              "settings": { "command": "claude --dangerously-skip-permissions", "fontSize": 21 }
            }
            """);
        var store = new StateStore(_root);

        store.Load();

        Assert.Equal(2, store.State.Entries.Count);
        Assert.All(store.State.Entries, entry => Assert.Equal("Misc", entry.Group));
        Assert.Equal(["Misc"], store.State.Groups.Select(group => group.Name));
        Assert.Equal(21, store.State.Settings.FontSize);

        store.Save();
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "state.json")));
        Assert.Equal("Misc", saved.RootElement.GetProperty("entries")[0].GetProperty("group").GetString());
        Assert.Equal("Misc", saved.RootElement.GetProperty("groups")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Groups_and_their_collapsed_state_round_trip()
    {
        var store = new StateStore(_root);
        store.Load();
        store.State.Entries.Add(new Entry { Id = "a", Path = @"X:\A", Name = "A", Group = "Work" });
        store.State.Groups.Add(new Group { Name = "Work", Collapsed = true });
        Grouping.Reconcile(store.State);
        store.Save();

        var reloaded = new StateStore(_root);
        reloaded.Load();

        var group = Assert.Single(reloaded.State.Groups);
        Assert.Equal("Work", group.Name);
        Assert.True(group.Collapsed);
        Assert.Equal("Work", Assert.Single(reloaded.State.Entries).Group);
    }

    [Fact]
    public void Nesting_round_trips_and_groups_saved_before_it_existed_are_at_the_top_level()
    {
        File.WriteAllText(Path.Combine(_root, "state.json"), """
            {
              "entries": [ { "id": "a", "path": "X:\\A", "name": "A", "group": "Clients" } ],
              "groups": [ { "name": "Work", "collapsed": false }, { "name": "Clients", "parent": "Work", "color": "#7fcfff" } ]
            }
            """);
        var store = new StateStore(_root);
        store.Load();
        store.Save();

        var reloaded = new StateStore(_root);
        reloaded.Load();

        Assert.Equal(["Work", "Clients"], reloaded.State.Groups.Select(group => group.Name));
        Assert.Equal("", reloaded.State.Groups[0].Parent);
        Assert.Equal("Work", reloaded.State.Groups[1].Parent);
        Assert.Equal("#7fcfff", reloaded.State.Groups[1].Color);
    }

    [Theory]
    [InlineData("\"voiceArguments\": \"--model sonnet --effort medium\",", "--model opus --effort xhigh")] // the old default, as saved
    [InlineData("", "--model opus --effort xhigh")]                                                   // saved before the setting existed
    [InlineData("\"voiceArguments\": null,", "--model opus --effort xhigh")]
    [InlineData("\"voiceArguments\": \"--model fable\",", "--model fable")]                             // the user's own choice stays
    [InlineData("\"voiceArguments\": \"\",", "")]                                                     // nothing added, on purpose
    public void The_voice_tile_starts_with_Opus_at_xhigh_unless_the_user_chose_otherwise(string saved, string expected)
    {
        File.WriteAllText(Path.Combine(_root, "state.json"), $$"""
            { "settings": { {{saved}} "fontSize": 21 } }
            """);
        var store = new StateStore(_root);

        store.Load();

        Assert.Equal(expected, store.State.Settings.VoiceArguments);
        Assert.Equal(21, store.State.Settings.FontSize);
    }
}
