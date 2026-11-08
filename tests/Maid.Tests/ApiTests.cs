using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Maid.Tests;

/// <summary>
/// Maid's API (Dashboard.Api.cs) over its pipe, as maidctl uses it. The test plays the page: it carries out what the
/// API asks of the tiles (remote → remoteResult), publishes the tiles, and sends the sessions' hooks.
/// </summary>
public sealed class ApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "Maid.Api.Test." + Guid.NewGuid().ToString("N");
    private readonly List<JsonElement> _posted = [];
    private readonly List<object> _tiles = [];
    private readonly StateStore _store;
    private readonly Dashboard _dashboard;
    private readonly HttpClient _http = new();

    public ApiTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "project"));
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        Directory.CreateDirectory(Path.Combine(_root, "new one"));
        _store = new StateStore(Path.Combine(_root, "data"));
        _store.Load();
        // The session shows the window it was told it runs in.
        _store.State.Settings.Command = "cmd.exe /k echo window=%MAID_WINDOW%";
        _store.State.Settings.SaveHistory = false;
        _store.State.Settings.ApiPipe = _pipe;
        _store.State.Settings.VoicePipe = "";
        _store.State.Entries.Add(new Entry { Id = "e1", Path = Path.Combine(_root, "project"), Name = "Project" });
        _store.State.Entries.Add(new Entry { Id = "e2", Path = Path.Combine(_root, "other"), Name = "Other Thing", Group = "Work" });
        Grouping.Reconcile(_store.State);
        _dashboard = new Dashboard(_store, Dispatcher.CurrentDispatcher, json => _posted.Add(JsonDocument.Parse(json).RootElement.Clone()));
        _dashboard.StartApiPipe();
    }

    public void Dispose()
    {
        _dashboard.Shutdown();
        _http.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- the test as the page ----------------------------------------------------------------------------------

    private void Send(object message) => _dashboard.HandleMessage(JsonSerializer.Serialize(message));

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static T Await<T>(Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            Pump();
            Thread.Sleep(5);
        }
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Keeps the dispatcher going for a while (to show that something does not happen).</summary>
    private static void Idle(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Pump();
            Thread.Sleep(5);
        }
    }

    private JsonElement WaitForPage(string type, Func<JsonElement, bool>? where = null, int after = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var found = _posted.Skip(after).FirstOrDefault(message => message.GetProperty("type").GetString() == type && (where is null || where(message)));
            if (found.ValueKind != JsonValueKind.Undefined) return found;
            Pump();
            Thread.Sleep(5);
        }
        throw new TimeoutException(type);
    }

    private JsonElement PageAsked(string action, int after = 0) =>
        WaitForPage("remote", message => message.GetProperty("action").GetString() == action, after);

    private void PublishTiles() => Send(new { type = "tiles", tiles = _tiles });

    private static string Text(JsonElement message, string name) => message.GetProperty(name).GetString()!;

    /// <summary>The page starts a tile's session as asked (launchEntry) and says which tile; returns the command it runs.</summary>
    private string PageLaunches(JsonElement asked, string id, string name, string entry = "e1")
    {
        int mark = _posted.Count;
        Send(new
        {
            type = "launch", id, entry = Text(asked, "entry"), cols = 100, rows = 30,
            prompt = asked.TryGetProperty("prompt", out var prompt) ? prompt.GetString() : "",
        });
        var command = Text(WaitForPage("started", message => Text(message, "id") == id, mark), "command");
        _tiles.Add(new { id, entry, name, project = name.Split(" #")[0], path = Path.Combine(_root, "project"), status = "running", busy = false, jarvis = false });
        PublishTiles();
        Send(new { type = "remoteResult", @ref = Text(asked, "ref"), id, error = "" });
        return command;
    }

    /// <summary>Starts a tile's session straight away, as a click on its row would.</summary>
    private string Started(string id, string name, string entry = "e1")
    {
        int mark = _posted.Count;
        Send(new { type = "launch", id, entry, cols = 100, rows = 30 });
        var command = Text(WaitForPage("started", message => Text(message, "id") == id, mark), "command");
        _tiles.Add(new { id, entry, name, project = name.Split(" #")[0], path = Path.Combine(_root, "project"), status = "running", busy = false, jarvis = false });
        PublishTiles();
        return command;
    }

    /// <summary>Claude Code's hook of the session that runs <paramref name="command"/>.</summary>
    private void Hook(string command, string name, object body)
    {
        var settings = Regex.Match(command, "--settings \"([^\"]+)\"").Groups[1].Value;
        var stop = JsonDocument.Parse(File.ReadAllText(settings)).RootElement
            .GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("url").GetString()!;
        var url = stop[..stop.LastIndexOf('/')] + "/" + name;
        int mark = _posted.Count;
        Await(_http.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")));
        WaitForPage("hook", message => Text(message, "event") == name, mark);
    }

    // ---- the test as maidctl -------------------------------------------------------------------------------------

    /// <summary>A program on the API pipe; answers are matched to requests by id.</summary>
    private sealed class Client : IDisposable
    {
        private readonly NamedPipeClientStream _stream;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _waiting = new();
        private int _last;

        public Client(string pipe, string caller = "")
        {
            Caller = caller;
            _stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            _stream.Connect(5000);
            var reader = new StreamReader(_stream, Encoding.UTF8);
            _ = Task.Run(async () =>
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    var message = JsonDocument.Parse(line).RootElement.Clone();
                    if (_waiting.TryRemove(message.GetProperty("id").GetString() ?? "", out var waiting)) waiting.SetResult(message);
                }
            });
        }

        public string Caller { get; }

        /// <summary>Sends a request (an anonymous object, plus id and caller); the task gets Maid's answer.</summary>
        public Task<JsonElement> Request(object request)
        {
            var id = $"r{Interlocked.Increment(ref _last)}";
            var node = JsonSerializer.SerializeToNode(request)!.AsObject();
            node["id"] = id;
            if (Caller.Length > 0) node["caller"] = Caller;
            var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[id] = waiting;
            var bytes = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n");
            lock (_stream)
            {
                _stream.Write(bytes);
                _stream.Flush();
            }
            return waiting.Task;
        }

        public void WriteLine(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (_stream) _stream.Write(bytes);
        }

        public void Dispose() => _stream.Dispose();
    }

    private static JsonElement Ok(JsonElement answer)
    {
        Assert.True(answer.GetProperty("ok").GetBoolean(), answer.TryGetProperty("error", out var error) ? error.ToString() : "");
        return answer.GetProperty("result");
    }

    private static string Error(JsonElement answer)
    {
        Assert.False(answer.GetProperty("ok").GetBoolean());
        return answer.GetProperty("error").GetString()!;
    }

    // ---- tests ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_session_launched_through_the_API_gets_its_prompt_and_its_window_and_answers_through_its_hooks()
    {
        using var client = new Client(_pipe);
        var hello = Ok(Await(client.Request(new { op = "hello" })));
        Assert.Equal("Maid", Text(hello, "app"));
        Assert.Equal(Dashboard.ApiVersion, hello.GetProperty("api").GetInt32());

        // The page is asked to start the project (quietly: it must not take the keyboard), with the prompt.
        var launched = client.Request(new { op = "launch", project = "project", prompt = "look at C:\\Temp\\x \"now\"" });
        var asked = PageAsked("launchEntry");
        Assert.Equal("e1", Text(asked, "entry"));
        Assert.True(asked.GetProperty("quiet").GetBoolean());
        var command = PageLaunches(asked, "t1", "Project");
        Assert.Equal("t1", Text(Ok(Await(launched)), "window"));
        // The prompt comes last, quoted for the program; the session knows its window.
        Assert.EndsWith(" \"look at C:\\Temp\\x \\\"now\\\"\"", command);
        WaitForPage("output", message => Text(message, "data").Contains("window=t1"));

        // Its first turn is on its way: it counts as working until its hooks say it's done.
        var windows = Ok(Await(client.Request(new { op = "windows" }))).GetProperty("windows");
        Assert.Equal("working", Text(windows[0], "state"));
        var waited = client.Request(new { op = "wait", window = "Project", timeout = 30 });
        Hook(command, "UserPromptSubmit", new { prompt = "look" });
        Idle(200);
        Assert.False(waited.IsCompleted);
        Hook(command, "Stop", new { last_assistant_message = "It is a folder.", background_tasks = Array.Empty<object>() });
        var turn = Ok(Await(waited));
        Assert.Equal("idle", Text(turn, "state"));
        Assert.Equal("It is a folder.", Text(turn, "answer"));
        Assert.False(turn.GetProperty("timedOut").GetBoolean());

        // Its last answer can be looked up any time.
        Assert.Equal("It is a folder.", Text(Ok(Await(client.Request(new { op = "wait", window = "t1", timeout = 0 }))), "answer"));
        var status = Ok(Await(client.Request(new { op = "status" })));
        Assert.Equal(1, status.GetProperty("windows").GetProperty("running").GetInt32());
    }

    [Fact]
    public void Ask_types_into_a_session_that_waits_and_waits_for_the_turn_its_prompt_starts()
    {
        var command = Started("t1", "Project");
        using var client = new Client(_pipe);

        // The project's session waits for a prompt: it is typed in there (no new session).
        int mark = _posted.Count;
        var asking = client.Request(new { op = "ask", to = "project", text = "what now?", timeout = 30 });
        WaitForPage("output", message => Text(message, "data").Contains("what now?"), mark);
        Hook(command, "UserPromptSubmit", new { prompt = "what now?" });
        Hook(command, "Stop", new { last_assistant_message = "Answer 1" });
        var first = Ok(Await(asking));
        Assert.Equal("Answer 1", Text(first, "answer"));
        Assert.False(first.GetProperty("launched").GetBoolean());
        Assert.DoesNotContain(_posted.Skip(mark), message => Text(message, "type") == "remote");

        // A prompt sent while a turn runs waits in the queue: the answer is that of its own turn, not the running one's.
        Hook(command, "UserPromptSubmit", new { prompt = "the user's" });
        var queued = client.Request(new { op = "ask", window = "t1", text = "second", timeout = 30 });
        Idle(200);
        Hook(command, "Stop", new { last_assistant_message = "for the user" });
        Idle(300);
        Assert.False(queued.IsCompleted);
        Hook(command, "UserPromptSubmit", new { prompt = "second" });
        Hook(command, "Stop", new { last_assistant_message = "Answer 2" });
        Assert.Equal("Answer 2", Text(Ok(Await(queued)), "answer"));

        // A question to the user ends the wait: the caller sees it and can answer with keys.
        var third = client.Request(new { op = "ask", window = "t1", text = "third", timeout = 30 });
        Hook(command, "UserPromptSubmit", new { prompt = "third" });
        Hook(command, "PreToolUse", new { tool_name = "AskUserQuestion", tool_input = new { questions = new[] { new { question = "Which?", options = new[] { new { label = "A" }, new { label = "B" } } } } } });
        var question = Ok(Await(third));
        Assert.Equal("asking", Text(question, "state"));
        Assert.Equal("AskUserQuestion", Text(question.GetProperty("question"), "kind"));
        Assert.Equal("Which?", Text(question.GetProperty("question").GetProperty("input").GetProperty("questions")[0], "question"));
        Assert.Equal(2, Ok(Await(client.Request(new { op = "keys", window = "t1", keys = new[] { "down", "enter" } }))).GetProperty("pressed").GetInt32());
        Assert.Equal("working", Text(Ok(Await(client.Request(new { op = "windows" }))).GetProperty("windows")[0], "state"));
        Assert.Contains("not a key", Error(Await(client.Request(new { op = "keys", window = "t1", keys = "down sideways" }))));
        Hook(command, "Stop", new { last_assistant_message = "Answer 3" });

        // When the time is up the caller hears it still works (and can wait again).
        var slow = Ok(Await(client.Request(new { op = "ask", window = "t1", text = "fourth", timeout = 1 })));
        Assert.True(slow.GetProperty("timedOut").GetBoolean());
        Assert.Equal("working", Text(slow, "state"));

        // A session that ends ends the wait.
        var last = client.Request(new { op = "wait", window = "t1", timeout = 30 });
        Idle(200);
        Send(new { type = "kill", id = "t1" });
        Assert.Equal("ended", Text(Ok(Await(last)), "state"));
    }

    [Fact]
    public void Sending_to_a_project_without_a_waiting_session_starts_one_with_the_prompt()
    {
        using var client = new Client(_pipe);
        var sent = client.Request(new { op = "send", to = "other thing", text = "line one\nline two" });
        var asked = PageAsked("launchEntry");
        Assert.Equal("e2", Text(asked, "entry"));
        Assert.Equal("line one\nline two", Text(asked, "prompt"));
        var command = PageLaunches(asked, "t2", "Other Thing", entry: "e2");
        var result = Ok(Await(sent));
        Assert.True(result.GetProperty("launched").GetBoolean());
        Assert.Equal("t2", Text(result, "window"));
        // cmd.exe runs directly, so the prompt keeps its lines.
        Assert.EndsWith(" \"line one\nline two\"", command);
    }

    [Fact]
    public void Windows_and_projects_are_found_by_name_and_a_name_that_fits_several_is_explained()
    {
        Started("t1", "Project #1");
        Started("t2", "Project #2");
        using var client = new Client(_pipe, caller: "t1");

        Assert.Contains("has 2 windows", Error(Await(client.Request(new { op = "wait", window = "Project", timeout = 0 }))));
        Assert.Equal("t2", Text(Ok(Await(client.Request(new { op = "wait", window = "project #2", timeout = 0 }))), "window"));
        Assert.Equal("t2", Text(Ok(Await(client.Request(new { op = "window", window = "Project #2" }))), "id"));
        // A session can't wait for itself (it would wait for ever).
        Assert.Contains("own", Error(Await(client.Request(new { op = "wait", window = "self", timeout = 0 }))));
        Assert.Contains("itself", Error(Await(client.Request(new { op = "ask", window = "t1", text = "hi" }))));
        Assert.Contains("There is no project", Error(Await(client.Request(new { op = "launch", project = "nothing like it" }))));
        Assert.Contains("ops are", Error(Await(client.Request(new { op = "dance" }))));

        // Spoken names come out spelled in all sorts of ways.
        var launched = client.Request(new { op = "launch", project = "OTHER" });
        var asked = PageAsked("launchEntry");
        Assert.Equal("e2", Text(asked, "entry"));
        Send(new { type = "remoteResult", @ref = Text(asked, "ref"), id = "", error = "Could not start it." });
        Assert.Equal("Could not start it.", Error(Await(launched)));

        var projects = Ok(Await(client.Request(new { op = "projects" })));
        Assert.Equal("Work", Text(projects.GetProperty("projects")[1], "group"));
        Assert.Equal(2, projects.GetProperty("projects")[0].GetProperty("running").GetInt32());

        client.WriteLine("this is not json");
        Thread.Sleep(100);
        Pump();
    }

    [Fact]
    public void Screens_and_the_tiles_buttons_go_through_the_page()
    {
        Started("t1", "Project");
        using var client = new Client(_pipe);

        var screen = client.Request(new { op = "screen", window = "Project", lines = 50 });
        var asked = PageAsked("screen");
        Assert.Equal(50, asked.GetProperty("lines").GetInt32());
        Send(new { type = "remoteResult", @ref = Text(asked, "ref"), id = "t1", error = "", text = "C:\\>dir\nVolume in drive C", cols = 100, rows = 30 });
        Assert.Equal("C:\\>dir\nVolume in drive C", Text(Ok(Await(screen)), "text"));

        var shown = client.Request(new { op = "show", window = "t1" });
        Send(new { type = "remoteResult", @ref = Text(PageAsked("show"), "ref"), id = "t1", error = "" });
        Assert.Equal("Project", Text(Ok(Await(shown)), "name"));

        var stopped = client.Request(new { op = "stop", window = "t1" });
        Send(new { type = "remoteResult", @ref = Text(PageAsked("stop"), "ref"), id = "t1", error = "" });
        Ok(Await(stopped));

        // Typing reaches the console, and the page hears about it (as good as typing at the tile).
        int mark = _posted.Count;
        Ok(Await(client.Request(new { op = "type", window = "t1", text = "echo typed through the API", enter = true })));
        WaitForPage("output", message => Text(message, "data").Contains("typed through the API"), mark);
        WaitForPage("remoteInput", after: mark);
    }

    [Fact]
    public void Projects_can_be_added_through_the_pipe()
    {
        using var client = new Client(_pipe);
        var added = Ok(Await(client.Request(new { op = "addProject", path = Path.Combine(_root, "new one"), group = "Fresh" })));
        Assert.True(added.GetProperty("added").GetBoolean());
        Assert.Equal("new one", Text(added.GetProperty("project"), "name"));
        Assert.Contains(_store.State.Entries, entry => entry.Name == "new one" && entry.Group == "Fresh");
        Assert.False(Ok(Await(client.Request(new { op = "addProject", path = Path.Combine(_root, "new one") }))).GetProperty("added").GetBoolean());
        Assert.Contains("not found", Error(Await(client.Request(new { op = "addProject", path = Path.Combine(_root, "missing") }))));
    }

    [Theory]
    [InlineData("run the tests", true, "\"run the tests\"")]
    [InlineData("look in C:\\Temp\\", true, "\"look in C:\\Temp\\\\\"")]
    [InlineData("say \"hi\"", true, "\"say \\\"hi\\\"\"")]
    [InlineData("--help me", true, "\"help me\"")]
    [InlineData("two\nlines", true, "\"two\nlines\"")]
    [InlineData("two\nlines with %PATH% and \"quotes\"", false, "\"two lines with PATH and quotes\"")]
    [InlineData("   ", true, "")]
    public void A_first_prompt_is_quoted_for_the_program_that_reads_it(string prompt, bool direct, string argument) =>
        Assert.Equal(argument, Dashboard.FirstPromptArgument(prompt, direct));

    [Theory]
    [InlineData("Enter", "\r")]
    [InlineData("esc", "\u001b")]
    [InlineData("shift+tab", "\u001b[Z")]
    [InlineData("down", "\u001b[B")]
    [InlineData("ctrl+c", "\u0003")]
    [InlineData("Y", "Y")]
    [InlineData("2", "2")]
    [InlineData("sideways", null)]
    public void Keys_are_what_a_terminal_sends_for_them(string key, string? sequence) =>
        Assert.Equal(sequence, Dashboard.KeySequence(key));
}
