using System.IO;
using System.Text.Json;
using Maid.Remote;

namespace Maid;

/// <summary>
/// The voice pipe's side of the Dashboard: a push-to-talk program at this PC hands its spoken requests to the voice
/// tile through a local named pipe (<see cref="LocalPipeServer"/>), and hears the answer.
/// In: <c>hello</c>, <c>voice { ref, text }</c>. Out: <c>hello { app, version }</c>, <c>voiceSent { ref, id }</c>,
/// <c>voiceReply { ref, text }</c>, <c>voiceFailed { ref, message }</c>.
/// </summary>
internal sealed partial class Dashboard
{
    private LocalPipeServer? _voicePipe;

    /// <summary>The pipe's name while Maid listens on it; null otherwise (tests, or a name another program holds).</summary>
    internal string? VoicePipeName => _voicePipe?.Name;

    /// <summary>Starts listening on the voice pipe (<see cref="Settings.VoicePipe"/>; empty: never).</summary>
    public void StartVoicePipe()
    {
        var name = _store.State.Settings.VoicePipe.Trim();
        if (name.Length == 0 || _voicePipe is not null || _shutDown) return;
        try
        {
            _voicePipe = new LocalPipeServer(name, Elevation.IsElevated)
            {
                Message = (client, json) => _dispatcher.BeginInvoke(() => OnVoiceClientMessage(client, json)),
                Closed = client => _dispatcher.BeginInvoke(() => OnVoiceClientClosed(client)),
                Log = text => _dispatcher.BeginInvoke(() => RemoteLog("Voice pipe: " + text)),
            };
            _voicePipe.Start();
        }
        catch (IOException e)
        {
            RemoteLog("Voice pipe: " + e.Message);
        }
    }

    private void OnVoiceClientMessage(LocalPipeClient client, string json)
    {
        if (_shutDown || client.IsClosed) return;
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(json);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        if (message.ValueKind != JsonValueKind.Object) return;
        switch (GetString(message, "type"))
        {
            case "hello":
                client.Send(Serialize(new { type = "hello", app = "Maid", version = MainWindow.AppVersion }));
                break;
            case "voice":
                var reference = Bounded(GetString(message, "ref"));
                // Refs are the client's own: the page gets one that can't clash with another client's.
                PlaceVoice(new VoiceAsker(reference, client), $"pc{client.Number}:{reference}", GetString(message, "text"));
                break;
        }
    }

    /// <summary>
    /// The push-to-talk program went away: nobody is left to hear the answers to its requests (those still being placed are
    /// dropped when the page reports on them).
    /// </summary>
    private void OnVoiceClientClosed(LocalPipeClient client)
    {
        foreach (var session in _sessions.Values)
            session.VoiceRefs.RemoveAll(asker => asker.Local == client);
    }
}
