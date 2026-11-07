using System.IO.Pipes;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Maid.Remote;

namespace Maid.Tests;

/// <summary>A push-to-talk program at the PC hands spoken requests to the voice tile through Maid's local pipe.</summary>
public sealed class VoicePipeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maid-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "Maid.Voice.Test." + Guid.NewGuid().ToString("N");
    private readonly List<JsonElement> _posted = [];
    private readonly StateStore _store;
    private readonly Dashboard _dashboard;

    public VoicePipeTests()
    {
        _store = new StateStore(Path.Combine(_root, "data"));
        _store.Load();
        _store.State.Settings.Command = "cmd.exe /k echo hello from the session";
        _store.State.Settings.SaveHistory = false;
        _store.State.Settings.VoicePipe = _pipe;
        _dashboard = new Dashboard(_store, Dispatcher.CurrentDispatcher, json => _posted.Add(JsonDocument.Parse(json).RootElement.Clone()));
    }

    public void Dispose()
    {
        _dashboard.Shutdown();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

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

    private static void Await(Task task) => Await(task.ContinueWith(done => { done.GetAwaiter().GetResult(); return 0; }));

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

    private sealed class Client : IDisposable
    {
        private readonly NamedPipeClientStream _stream;
        private readonly StreamReader _reader;

        public Client(string name)
        {
            _stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            _stream.Connect(5000);
            _reader = new StreamReader(_stream, Encoding.UTF8);
        }

        public NamedPipeClientStream Stream => _stream;

        public void Write(object message)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
            _stream.Write(bytes);
            _stream.Flush();
        }

        public JsonElement ReadUntil(string type)
        {
            while (true)
            {
                var line = Await(_reader.ReadLineAsync()) ?? throw new EndOfStreamException();
                var message = JsonDocument.Parse(line).RootElement.Clone();
                if (message.GetProperty("type").GetString() == type) return message;
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    [Fact]
    public void A_request_from_the_PC_goes_to_the_voice_tile_and_the_answer_comes_back_to_the_PC()
    {
        _dashboard.StartVoicePipe();
        Assert.Equal(_pipe, _dashboard.VoicePipeName);
        using var client = new Client(_pipe);
        client.Write(new { type = "hello" });
        Assert.Equal("Maid", client.ReadUntil("hello").GetProperty("app").GetString());

        // The request reaches the page under a key of its own (refs are the client's).
        client.Write(new { type = "voice", @ref = "r1", text = "what time\r\n is it" });
        var asked = WaitForPage("remote", message => message.GetProperty("action").GetString() == "voice");
        Assert.Equal("pc1:r1", asked.GetProperty("ref").GetString());
        Assert.Equal("what time is it", asked.GetProperty("text").GetString());

        // The page starts the voice tile: its model options (Opus, xhigh) come before the hooks and the first prompt.
        int mark = _posted.Count;
        Send(new { type = "launch", id = "j1", entry = Hub.EntryId, cols = 100, rows = 30, jarvis = true, prompt = "what time is it" });
        var command = WaitForPage("started", after: mark).GetProperty("command").GetString()!;
        Assert.Contains(" --model opus --effort xhigh --settings \"", command);
        Assert.EndsWith(" \"what time is it\"", command);
        // Its instructions say how spoken requests are to be handled.
        Assert.Equal(Dashboard.VoiceInstructions, File.ReadAllText(Path.Combine(_root, "data", "jarvis-instructions.md")));
        Assert.Contains("maidctl", Dashboard.VoiceInstructions);
        Send(new { type = "remoteResult", @ref = "pc1:r1", id = "j1" });
        var sent = client.ReadUntil("voiceSent");
        Assert.Equal("r1", sent.GetProperty("ref").GetString());
        Assert.Equal("j1", sent.GetProperty("id").GetString());

        // The end of the turn brings the answer to the PC.
        var settings = Directory.GetFiles(Path.Combine(_root, "data", "hooks"), "*.json").Single();
        var stop = JsonDocument.Parse(File.ReadAllText(settings)).RootElement
            .GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("url").GetString()!;
        using var http = new HttpClient();
        Await(http.PostAsync(stop, new StringContent("{\"last_assistant_message\":\"It's ten past seven.\"}", Encoding.UTF8, "application/json")));
        var reply = client.ReadUntil("voiceReply");
        Assert.Equal("r1", reply.GetProperty("ref").GetString());
        Assert.Equal("It's ten past seven.", reply.GetProperty("text").GetString());

        // A second client's refs can't clash with the first one's; a refusal by the page reaches the right client.
        using var other = new Client(_pipe);
        other.Write(new { type = "voice", @ref = "r1", text = "again" });
        WaitForPage("remote", message => message.GetProperty("ref").GetString() == "pc2:r1");
        Send(new { type = "remoteResult", @ref = "pc2:r1", id = "", error = "Maid has no All projects folder." });
        Assert.Equal("Maid has no All projects folder.", other.ReadUntil("voiceFailed").GetProperty("message").GetString());

        // Empty requests are refused at once; a request whose window closes before answering fails.
        client.Write(new { type = "voice", @ref = "r2", text = "   " });
        Assert.Equal("r2", client.ReadUntil("voiceFailed").GetProperty("ref").GetString());
        client.Write(new { type = "voice", @ref = "r3", text = "and now?" });
        WaitForPage("remote", message => message.GetProperty("ref").GetString() == "pc1:r3");
        Send(new { type = "remoteResult", @ref = "pc1:r3", id = "j1" });
        client.ReadUntil("voiceSent");
        Send(new { type = "kill", id = "j1" });
        var failed = client.ReadUntil("voiceFailed");
        Assert.Equal("r3", failed.GetProperty("ref").GetString());
        Assert.Contains("closed", failed.GetProperty("message").GetString());
    }

    [Fact]
    public void The_pipe_lets_in_only_the_user_and_nobody_from_the_network()
    {
        _dashboard.StartVoicePipe();
        using var client = new Client(_pipe);
        var rules = client.Stream.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var user = WindowsIdentity.GetCurrent().User!;
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, rule => rule.AccessControlType == AccessControlType.Deny
            && rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)));
        Assert.Contains(rules, rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Equals(user));
        Assert.Equal("D:P(D;;GA;;;NU)(A;;GA;;;S-1-5-21-1)S:(ML;;NW;;;HI)", LocalPipeServer.SecurityDescriptor("S-1-5-21-1", elevated: true));
        Assert.Equal("D:P(D;;GA;;;NU)(A;;GA;;;S-1-5-21-1)", LocalPipeServer.SecurityDescriptor("S-1-5-21-1", elevated: false));
    }

    [Fact]
    public void A_second_Maid_does_not_share_the_pipe_name()
    {
        _dashboard.StartVoicePipe();
        Assert.Throws<IOException>(() => new LocalPipeServer(_pipe, elevated: false));
    }
}
