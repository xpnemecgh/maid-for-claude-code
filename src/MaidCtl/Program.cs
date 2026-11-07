using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaidCtl;

/// <summary>
/// maidctl: controls Maid through its API pipe (\\.\pipe\Maid.Api, or MAID_API_PIPE). One request per run: the
/// arguments become a JSON request, Maid's answer is printed (as text, or as it came with --json).
/// </summary>
internal static class Program
{
    private const int ExitError = 1;
    private const int ExitUsage = 2;
    /// <summary>ask / wait: the session still works when the time is up.</summary>
    private const int ExitStillWorking = 3;
    private const int ExitNoMaid = 4;
    /// <summary>Fits Claude Code's default Bash timeout (2 minutes); a longer wait needs a longer tool timeout.</summary>
    private const int DefaultWaitSeconds = 110;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string Usage = """
        maidctl: control Maid, the dashboard of Claude Code sessions, from a shell.

        Reading
          maidctl status                       Maid, its windows (how many work, ask), the phone
          maidctl projects                     the listed projects by group, with their folders
          maidctl windows                      the open windows: name, state, id, last answer
          maidctl screen <window> [--lines N]  the text a window shows (or its last N lines, scrollback too)
          maidctl answer <window>              a window's last answer (what Claude said when its turn ended)

        Sessions
          maidctl launch <project> [--resume] [--prompt <text>|-]
                                               start a session (in an ended window of the project, or a new one)
          maidctl send <window|project> <text>|-   a prompt: typed into the window (then Enter); for a project, into one
                                               of its sessions that waits for a prompt, else a new session starts with it
          maidctl ask <window|project> <text>|- [--timeout S]
                                               send, then wait for the answer and print it
          maidctl wait <window> [--timeout S]  wait until the window's turn is over, print its answer
          maidctl type <window> <text>|- [--enter]   type text as is (several lines go in as one paste)
          maidctl keys <window> <key>...       press keys: enter esc tab shift+tab up down left right home end
                                               pageup pagedown backspace delete space ctrl+a…ctrl+z, or one character
          maidctl stop <window>                stop the session (the window stays, with its last screen)
          maidctl relaunch <window> [--resume] start the window's session again
          maidctl close <window>               close the window (stops its session)
          maidctl show <window>                bring the window to the front in Maid on the PC

        The list
          maidctl add <folder> [--group <name>]    list a project folder (not from the phone)

        Other
          maidctl raw '<json>'                 send a request as it is, e.g. '{"op":"windows"}'

        <window>: its name as Maid shows it ("MyApp", "MyApp #2"), its id, "self", "jarvis", or a
        project's name when that project has one window. <project>: its name (or a unique part of it), its folder,
        or "hub" for All projects. A text of "-" is read from standard input (use it for long or multi-line text).

        --timeout S   ask/wait: seconds to wait (default 110, at most 3600; 0 = just look). With a longer timeout give
                      the shell command a longer timeout too. Still working when the time is up: exit code 3, and
                      "maidctl wait <window>" carries on waiting.
        --json        print Maid's answer as JSON
        --pipe NAME   another API pipe than MAID_API_PIPE or "Maid.Api"

        Exit codes: 0 done, 1 Maid refused or failed, 2 wrong arguments, 3 still working, 4 Maid not reachable.
        Maid runs as administrator, so only elevated programs can use its API (its own sessions are).
        """;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            return Run(args);
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine($"maidctl: {e.Message}");
            Console.Error.WriteLine("Run \"maidctl help\" for the commands.");
            return ExitUsage;
        }
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class Arguments
    {
        public List<string> Words { get; } = [];
        public Dictionary<string, string?> Options { get; } = [];
        public bool Has(string name) => Options.ContainsKey(name);
        public string? Get(string name) => Options.GetValueOrDefault(name);
    }

    /// <summary>Options that take a value; the rest are switches.</summary>
    private static readonly HashSet<string> Valued = ["timeout", "prompt", "lines", "group", "pipe"];

    private static Arguments Parse(string[] args)
    {
        var parsed = new Arguments();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                parsed.Words.AddRange(args[(i + 1)..]);
                break;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var name = arg[2..];
                string? value = null;
                var equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    value = name[(equals + 1)..];
                    name = name[..equals];
                }
                else if (Valued.Contains(name))
                {
                    if (i + 1 >= args.Length) throw new UsageException($"--{name} needs a value.");
                    value = args[++i];
                }
                parsed.Options[name] = value;
                continue;
            }
            if (arg == "-p")
            {
                if (i + 1 >= args.Length) throw new UsageException("-p needs a value.");
                parsed.Options["prompt"] = args[++i];
                continue;
            }
            parsed.Words.Add(arg);
        }
        return parsed;
    }

    private static int Run(string[] raw)
    {
        var args = Parse(raw);
        var command = args.Words.Count > 0 ? args.Words[0].ToLowerInvariant() : "help";
        var words = args.Words.Skip(1).ToList();
        if (command is "help" or "-h" or "/?" || args.Has("help"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        string Word(int index, string what) => index < words.Count ? words[index] : throw new UsageException($"{command} needs {what}.");
        string Text(int from, string what)
        {
            if (from >= words.Count) throw new UsageException($"{command} needs {what}.");
            var text = string.Join(' ', words.Skip(from));
            return text == "-" ? ReadStdin() : text;
        }
        int? Number(string name)
        {
            if (args.Get(name) is not { } value) return null;
            return int.TryParse(value, out var number) && number >= 0 ? number : throw new UsageException($"--{name} takes a number.");
        }
        int timeout = Number("timeout") ?? DefaultWaitSeconds;

        var request = new JsonObject();
        switch (command)
        {
            case "status" or "projects" or "windows":
                request["op"] = command;
                break;
            case "ls":
                request["op"] = "windows";
                break;
            case "launch" or "start":
                request["op"] = "launch";
                request["project"] = Word(0, "a project");
                request["resume"] = args.Has("resume");
                if (args.Get("prompt") is { } prompt) request["prompt"] = prompt == "-" ? ReadStdin() : prompt;
                break;
            case "send" or "ask":
                request["op"] = command;
                request["to"] = Word(0, "a window or a project");
                request["text"] = Text(1, "the text to send");
                if (command == "ask") request["timeout"] = timeout;
                break;
            case "wait":
                request["op"] = "wait";
                request["window"] = Word(0, "a window");
                request["timeout"] = timeout;
                break;
            case "answer":
                request["op"] = "wait";
                request["window"] = Word(0, "a window");
                request["timeout"] = 0;
                break;
            case "screen":
                request["op"] = "screen";
                request["window"] = Word(0, "a window");
                if (Number("lines") is { } lines) request["lines"] = lines;
                break;
            case "type":
                request["op"] = "type";
                request["window"] = Word(0, "a window");
                request["text"] = words.Count > 1 ? Text(1, "the text") : "";
                request["enter"] = args.Has("enter");
                break;
            case "keys" or "key" or "press":
                request["op"] = "keys";
                request["window"] = Word(0, "a window");
                if (words.Count < 2) throw new UsageException("keys needs the keys to press, e.g. maidctl keys MyApp down enter");
                request["keys"] = new JsonArray(words.Skip(1).Select(key => (JsonNode)JsonValue.Create(key)!).ToArray());
                break;
            case "stop" or "close" or "show":
                request["op"] = command;
                request["window"] = Word(0, "a window");
                break;
            case "relaunch" or "restart":
                request["op"] = "relaunch";
                request["window"] = Word(0, "a window");
                request["resume"] = args.Has("resume");
                break;
            case "add":
                request["op"] = "addProject";
                request["path"] = Word(0, "a folder");
                if (args.Get("group") is { } group) request["group"] = group;
                break;
            case "raw":
                try
                {
                    request = JsonNode.Parse(Text(0, "a JSON request")) as JsonObject ?? throw new UsageException("raw takes a JSON object.");
                }
                catch (JsonException e)
                {
                    throw new UsageException($"That is not JSON: {e.Message}");
                }
                break;
            default:
                throw new UsageException($"There is no command \"{command}\".");
        }

        request["id"] ??= "1";
        if (Environment.GetEnvironmentVariable("MAID_WINDOW") is { Length: > 0 } self) request["caller"] ??= self;
        var op = request["op"]?.GetValue<string>() ?? "";
        var waits = op is "ask" or "wait";
        var seconds = request["timeout"] is JsonValue value && value.TryGetValue<int>(out var given) ? given : timeout;
        var pipe = args.Get("pipe") ?? (Environment.GetEnvironmentVariable("MAID_API_PIPE") is { Length: > 0 } named ? named : "Maid.Api");

        JsonObject answer;
        try
        {
            answer = Exchange(pipe, request, TimeSpan.FromSeconds(waits ? Math.Min(seconds, 3600) + 30 : 30));
        }
        catch (MaidUnreachableException e)
        {
            Console.Error.WriteLine($"maidctl: {e.Message}");
            return ExitNoMaid;
        }

        if (answer["ok"]?.GetValue<bool>() != true)
        {
            Console.Error.WriteLine($"maidctl: {answer["error"]?.GetValue<string>() ?? "Maid refused."}");
            return ExitError;
        }
        var result = answer["result"] as JsonObject ?? [];
        var stillWorking = waits && result["timedOut"]?.GetValue<bool>() == true;
        if (args.Has("json"))
        {
            Console.WriteLine(result.ToJsonString(Indented));
            return stillWorking ? ExitStillWorking : 0;
        }
        Print(command, op, result);
        return stillWorking ? ExitStillWorking : 0;
    }

    private static string ReadStdin()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        return Console.In.ReadToEnd();
    }

    private sealed class MaidUnreachableException(string message) : Exception(message);

    /// <summary>Sends the request and reads lines until the answer to it comes.</summary>
    private static JsonObject Exchange(string pipe, JsonObject request, TimeSpan limit)
    {
        using var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            stream.Connect(3000);
        }
        catch (TimeoutException)
        {
            throw new MaidUnreachableException($"Maid isn't answering on \\\\.\\pipe\\{pipe}: it isn't running, it is older than 1.3, or its API is switched off (settings.apiPipe).");
        }
        catch (UnauthorizedAccessException)
        {
            throw new MaidUnreachableException("Access denied: Maid runs as administrator, so only programs running as administrator can use its API. Run this from an elevated shell (Maid's own sessions are).");
        }
        catch (IOException e)
        {
            throw new MaidUnreachableException($"Could not connect to Maid: {e.Message}");
        }

        var line = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
        stream.Write(line);
        stream.Flush();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var id = request["id"]?.ToString();
        var deadline = DateTime.UtcNow + limit;
        while (true)
        {
            var read = reader.ReadLineAsync();
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !read.Wait(left))
                throw new MaidUnreachableException("Maid did not answer in time.");
            var text = read.Result ?? throw new MaidUnreachableException("Maid closed the connection without answering.");
            if (JsonNode.Parse(text) is JsonObject message && (message["id"]?.ToString() == id || message["id"]?.ToString() is null or ""))
                return message;
        }
    }

    // ---- output -----------------------------------------------------------------------------------------------

    private static string? Str(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static int Int(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
    private static bool Bool(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static void Print(string command, string op, JsonObject result)
    {
        switch (op)
        {
            case "status":
            {
                var windows = result["windows"];
                var phone = result["phone"];
                Console.WriteLine($"Maid {Str(result, "version")} (API {Int(result, "api")}){(Bool(result, "elevated") ? ", running as administrator" : "")}");
                Console.WriteLine($"Projects: {Int(result, "projects")}");
                Console.WriteLine($"Windows: {Int(windows, "open")} open, {Int(windows, "running")} running, {Int(windows, "working")} working, {Int(windows, "asking")} asking the user");
                var phoneText = !Bool(phone, "enabled") ? "remote access off"
                    : Bool(phone, "connected") ? $"connected{(Str(phone, "viewing") is { } viewing ? $", showing {viewing}" : "")}"
                    : Str(phone, "paired") is { } paired ? $"{paired}, not connected" : "not paired";
                Console.WriteLine($"Phone: {phoneText}");
                if (Str(result, "window") is { Length: > 0 } self) Console.WriteLine($"This shell runs in window {self}.");
                break;
            }
            case "projects":
            {
                string? group = null;
                foreach (var project in result["projects"]?.AsArray() ?? [])
                {
                    var name = Str(project, "group");
                    if (name != group)
                    {
                        group = name;
                        Console.WriteLine(group);
                    }
                    var running = Int(project, "running");
                    Console.WriteLine($"  {Str(project, "name")} — {Str(project, "path")}{(running > 0 ? $"  [{running} running]" : "")}{(Bool(project, "moving") ? "  [being moved]" : "")}");
                }
                var hub = result["hub"];
                Console.WriteLine($"All projects (\"hub\") — {Str(hub, "path")}{(Int(hub, "running") > 0 ? $"  [{Int(hub, "running")} running]" : "")}");
                break;
            }
            case "windows":
            {
                var windows = result["windows"]?.AsArray() ?? [];
                if (windows.Count == 0) Console.WriteLine("No windows are open.");
                foreach (var window in windows)
                {
                    var extra = Bool(window, "jarvis") ? "  Jarvis's window" : "";
                    Console.WriteLine($"{Str(window, "name")}  [{Str(window, "state")}]{extra}  id {Str(window, "id")}");
                    if (Str(window, "path") is { } path) Console.WriteLine($"    {path}");
                    if (window?["question"] is JsonObject question) Console.WriteLine($"    asks: {QuestionText(question)}");
                    else if (Str(window, "answer") is { Length: > 0 } answer) Console.WriteLine($"    last answer: {OneLine(answer, 160)}");
                }
                break;
            }
            case "launch":
                Console.WriteLine($"Started Claude Code in {Str(result, "project")}: window {Str(result, "window")}.");
                break;
            case "send":
                Console.WriteLine(Bool(result, "launched")
                    ? $"Started a new session with it: window {Str(result, "window")}."
                    : $"Sent to window {Str(result, "window")}.");
                break;
            case "ask" or "wait":
                PrintTurn(command, result);
                break;
            case "screen":
                Console.WriteLine(Str(result, "text"));
                break;
            case "type":
                Console.WriteLine($"Typed into window {Str(result, "window")}.");
                break;
            case "keys":
                Console.WriteLine($"Pressed {Int(result, "pressed")} key(s) in window {Str(result, "window")}.");
                break;
            case "stop":
                Console.WriteLine($"Stopped {Str(result, "name")}.");
                break;
            case "close":
                Console.WriteLine($"Closed {Str(result, "name")}.");
                break;
            case "show":
                Console.WriteLine($"Showing {Str(result, "name")}.");
                break;
            case "relaunch":
                Console.WriteLine($"Started {Str(result, "name")} again.");
                break;
            case "addProject":
            {
                var project = result["project"];
                Console.WriteLine(Bool(result, "added")
                    ? $"Added {Str(project, "name")} ({Str(project, "path")}) to {Str(project, "group")}."
                    : $"{Str(project, "name")} ({Str(project, "path")}) is listed already.");
                break;
            }
            default:
                Console.WriteLine(result.ToJsonString(Indented));
                break;
        }
    }

    /// <summary>ask / wait / answer: the answer itself on standard output, how the turn ended on standard error.</summary>
    private static void PrintTurn(string command, JsonObject result)
    {
        var window = Str(result, "window");
        var state = Str(result, "state");
        if (Bool(result, "timedOut"))
            Console.Error.WriteLine($"[{Str(result, "name")} is still working; run \"maidctl wait {window}\" to keep waiting.]");
        else if (state == "asking" && result["question"] is JsonObject question)
            Console.Error.WriteLine($"[{Str(result, "name")} asks the user: {QuestionText(question)}. Answer with \"maidctl keys {window} …\" or \"maidctl type {window} … --enter\".]");
        else if (state == "ended")
            Console.Error.WriteLine($"[The session in {Str(result, "name")} has ended.]");
        else if (Bool(result, "interrupted"))
            Console.Error.WriteLine($"[The turn in {Str(result, "name")} was interrupted.]");
        if (Str(result, "failure") is { Length: > 0 } failure) Console.Error.WriteLine($"[The turn failed: {failure}]");
        if (Str(result, "answer") is { Length: > 0 } answer)
        {
            // An answer from before this ask is not this ask's answer.
            if (command == "ask" && Bool(result, "timedOut")) Console.Error.WriteLine("[Its previous answer:]");
            Console.WriteLine(answer);
        }
        else if (!Bool(result, "hooks"))
        {
            Console.Error.WriteLine($"[This session reports no answers: read it with \"maidctl screen {window}\".]");
        }
        else if (!Bool(result, "timedOut") && state != "asking")
        {
            Console.Error.WriteLine("[No answer yet.]");
        }
    }

    private static string QuestionText(JsonObject question)
    {
        if (Str(question, "message") is { Length: > 0 } message) return message;
        if (question["input"] is JsonObject input)
        {
            if (input["questions"] is JsonArray questions)
            {
                return string.Join(" / ", questions.Select(item =>
                {
                    var options = item?["options"] is JsonArray choices
                        ? " (" + string.Join(", ", choices.Select((choice, index) => $"{index + 1}. {Str(choice, "label")}")) + ")"
                        : "";
                    return $"{Str(item, "question")}{options}";
                }));
            }
            if (Str(input, "plan") is { Length: > 0 } plan) return "approve this plan: " + OneLine(plan, 300);
        }
        return Str(question, "kind") ?? "a question";
    }

    private static string OneLine(string text, int max)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > max ? line[..max] + "…" : line;
    }
}
