using System.IO;
using System.Text.Json;
using Maid.Terminal;

namespace Maid;

/// <summary>
/// Claude Code's hooks (what a session is doing, for certain) and the voice tile ("Jarvis") in All projects:
/// spoken requests go there (from a push-to-talk program at this PC, through the voice pipe: Dashboard.Voice.cs),
/// and Claude's answer goes back to whoever asked.
/// </summary>
internal sealed partial class Dashboard
{
    /// <summary>What the voice tile's Claude Code is told on top of its usual instructions.</summary>
    internal const string VoiceInstructions =
        "Requests in this session are spoken by the user and transcribed, so expect recognition errors and interpret them " +
        "sensibly. They come from push-to-talk at this PC, where the user saw the transcript and could cancel it. Carry " +
        "them out on this Windows PC.\n\n" +
        "Maid, the dashboard this session runs in, is controlled with `maidctl` (`maidctl help` lists the commands). When the " +
        "user asks to start, stop or check on a session, or to have one of their projects do something, use it: for example " +
        "`maidctl windows`, `maidctl ask <project> \"<task>\"` (prints the session's answer, which you then sum up), " +
        "`maidctl stop <window>`. Never type into your own window.\n\n" +
        "Your final message is read aloud to the user, who may be away from this screen. Answer in one or two short, plain " +
        "sentences in the language of the request, without Markdown, lists, tables, code or file paths. When something is " +
        "done, a short confirmation such as \"Done\" is enough. If a request is unclear, or would delete, send, buy or " +
        "install something the user didn't clearly ask for, don't guess: ask one short question. Their spoken answer " +
        "arrives as the next request.";

    /// <summary>The longest request a voice client may send; spoken requests are far shorter.</summary>
    internal const int MaxVoiceChars = 2000;

    private HookServer? _hookServer;
    /// <summary>Who asked for a voice request (a program on the voice pipe) and under what reference.</summary>
    private sealed record VoiceAsker(string Ref, Remote.LocalPipeClient Local);

    /// <summary>Voice requests the page is still placing, by the key the page was given.</summary>
    private readonly Dictionary<string, VoiceAsker> _voicePlacing = [];

    private string HooksDirectory => Path.Combine(_store.DataDirectory, "hooks");

    /// <summary>Starts the hook server at the first Claude Code launch; null when it can't listen.</summary>
    private HookServer? HookServerOrNull()
    {
        if (_hookServer is not null || _shutDown) return _hookServer;
        try
        {
            // Settings files of sessions from an earlier run point at a server that is gone.
            if (Directory.Exists(HooksDirectory))
                foreach (var stale in Directory.EnumerateFiles(HooksDirectory, "*.json")) TryDelete(stale);
            _hookServer = new HookServer((token, name, body) => _dispatcher.BeginInvoke(() => OnHook(token, name, body)));
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or UnauthorizedAccessException)
        {
            _hookServer = null;
        }
        return _hookServer;
    }

    /// <summary>
    /// The arguments that give a Claude Code session its hooks (and, in Jarvis's tile, its instructions and
    /// first request). Without a hook server the session runs without hooks and the page guesses from output.
    /// </summary>
    private string HookArguments(RunningSession session, bool jarvis, string prompt)
    {
        var arguments = new System.Text.StringBuilder();
        if (HookServerOrNull() is { } server)
        {
            try
            {
                Directory.CreateDirectory(HooksDirectory);
                var token = HookServer.NewToken();
                var settingsFile = Path.Combine(HooksDirectory, token + ".json");
                File.WriteAllText(settingsFile, HookServer.SettingsJson(server.Port, token));
                session.HookToken = token;
                session.HookSettingsFile = settingsFile;
                arguments.Append($" --settings \"{settingsFile}\"");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                session.HookToken = null;
                session.HookSettingsFile = null;
            }
        }
        if (jarvis)
        {
            try
            {
                var instructions = Path.Combine(_store.DataDirectory, "jarvis-instructions.md");
                File.WriteAllText(instructions, VoiceInstructions);
                arguments.Append($" --append-system-prompt-file \"{instructions}\"");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (PromptArgument(prompt) is { Length: > 0 } quoted) arguments.Append(' ').Append(quoted);
        }
        return arguments.ToString();
    }

    /// <summary>
    /// A spoken request as Claude Code's first prompt on its command line: one line, without the characters
    /// that quoting (or cmd.exe, for a script shim) would trip over, and never mistaken for an option.
    /// </summary>
    internal static string PromptArgument(string prompt)
    {
        var clean = new string(prompt.Select(c => char.IsControl(c) || c is '"' or '%' or '^' or '\\' ? ' ' : c).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimStart('-', ' ');
        if (clean.Length > MaxVoiceChars) clean = clean[..MaxVoiceChars];
        return clean.Length == 0 ? "" : $"\"{clean}\"";
    }

    /// <summary>A hook arrived (dispatcher thread): the page learns what the session does, Jarvis gets its answer.</summary>
    private void OnHook(string token, string name, JsonElement body)
    {
        var session = _sessions.Values.FirstOrDefault(candidate => candidate.HookToken == token);
        if (session is null) return;
        var background = name == "Stop" && body.TryGetProperty("background_tasks", out var tasks)
            && tasks.ValueKind == JsonValueKind.Array && tasks.GetArrayLength() > 0;
        var kind = name switch
        {
            "Notification" => GetString(body, "notification_type"),
            "PreToolUse" or "PostToolUse" => GetString(body, "tool_name"),
            _ => "",
        };
        Post(new { type = "hook", id = session.Id, run = session.Run, @event = name, background, kind });
        TrackTurn(session, name, body, background, kind);

        if (session.VoiceRefs.Count == 0) return;
        if (name == "Stop")
        {
            var answer = GetString(body, "last_assistant_message").Trim();
            foreach (var asker in session.VoiceRefs)
                SendVoice(asker, new { type = "voiceReply", @ref = asker.Ref, text = answer });
            session.VoiceRefs.Clear();
        }
        else if (name == "StopFailure")
        {
            FailVoice(session, "Claude on the computer ran into an error.");
        }
    }

    private void FailVoice(RunningSession session, string message)
    {
        foreach (var asker in session.VoiceRefs)
            SendVoice(asker, new { type = "voiceFailed", @ref = asker.Ref, message });
        session.VoiceRefs.Clear();
    }

    /// <summary>To the program that asked.</summary>
    private void SendVoice(VoiceAsker asker, object message)
    {
        asker.Local.Send(Serialize(message));
    }

    /// <summary>
    /// A spoken request for the voice tile: the page puts it there under <paramref name="key"/> (unique among the
    /// requests being placed) and answers with <c>remoteResult</c>.
    /// </summary>
    private void PlaceVoice(VoiceAsker asker, string key, string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (asker.Ref.Length == 0) return;
        if (text.Length == 0 || text.Length > MaxVoiceChars || _voicePlacing.Count >= 16)
        {
            SendVoice(asker, new { type = "voiceFailed", @ref = asker.Ref, message = "The request was empty or too long." });
            return;
        }
        _voicePlacing[key] = asker;
        Post(new { type = "remote", action = "voice", @ref = key, text });
    }

    /// <summary>The page placed a voice request (or couldn't): the asker hears it was sent, the answer follows.</summary>
    private bool OnVoicePlaced(string key, string id, string error)
    {
        if (!_voicePlacing.Remove(key, out var asker)) return false;
        if (asker.Local is { IsClosed: true }) return true; // nobody left to tell
        if (error.Length == 0 && _sessions.TryGetValue(id, out var session))
        {
            session.VoiceRefs.Add(asker);
            SendVoice(asker, new { type = "voiceSent", @ref = asker.Ref, id });
        }
        else
        {
            SendVoice(asker, new { type = "voiceFailed", @ref = asker.Ref, message = error.Length > 0 ? error : "Jarvis's window on the computer didn't start." });
        }
        return true;
    }

    /// <summary>A session ended: its hook settings go, and a request still waiting for its answer won't get one.</summary>
    private void ForgetHooks(RunningSession session)
    {
        if (session.HookSettingsFile is { } file) TryDelete(file);
        FailVoice(session, "Jarvis's window on the computer closed before it answered.");
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
