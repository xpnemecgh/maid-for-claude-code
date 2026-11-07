using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Maid;

/// <summary>Why a recording could not be put into words, in words for the phone's screen.</summary>
internal sealed class TranscriptionException(string message) : Exception(message);

/// <summary>
/// Speech to text for the phone's dictation, through ElevenLabs' speech-to-text API (Scribe). The phone
/// records and sends the recording to Maid, which sends it on from here with the PC's API key: the key
/// never goes to the phone, and the phone keeps talking to nothing but Maid.
/// </summary>
internal static class ElevenLabs
{
    public const string EnvironmentVariable = "ELEVENLABS_API_KEY";
    public const string DefaultModel = "scribe_v2";
    private const string Endpoint = "https://api.elevenlabs.io/v1/speech-to-text";
    /// <summary>Longer than that and the phone has given up on the answer anyway.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    /// <summary>
    /// The key from the settings, else from the environment; null when there is none. <paramref name="source"/>
    /// says which ("settings", "environment" or "none").
    /// </summary>
    public static string? ApiKey(Settings settings, out string source, Func<string, string?>? environment = null)
    {
        var fromSettings = settings.ElevenLabsApiKey.Trim();
        if (fromSettings.Length > 0)
        {
            source = "settings";
            return fromSettings;
        }
        var fromEnvironment = ((environment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable) ?? "").Trim();
        if (fromEnvironment.Length > 0)
        {
            source = "environment";
            return fromEnvironment;
        }
        source = "none";
        return null;
    }

    /// <summary>The words in <paramref name="audio"/> (a recording of type <paramref name="mime"/>); empty when nothing was said.</summary>
    /// <exception cref="TranscriptionException">The service refused or could not be reached; the message is for the user.</exception>
    public static async Task<string> TranscribeAsync(string apiKey, byte[] audio, string mime, string model, string language, CancellationToken token)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(model.Trim().Length > 0 ? model.Trim() : DefaultModel), "model_id");
        // Words only: no "(laughter)" and no speaker labels in a dictation.
        form.Add(new StringContent("false"), "tag_audio_events");
        form.Add(new StringContent("false"), "diarize");
        if (language.Trim().Length > 0) form.Add(new StringContent(language.Trim()), "language_code");
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", "dictation" + ExtensionFor(mime));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        request.Headers.Add("xi-api-key", apiKey);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, token);
        }
        catch (HttpRequestException e)
        {
            throw new TranscriptionException($"ElevenLabs could not be reached: {e.Message}");
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TranscriptionException("ElevenLabs did not answer in time.");
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode) throw new TranscriptionException(Explain(response.StatusCode, body));
            return TextOf(body);
        }
    }

    /// <summary>The <c>text</c> of a transcript answer.</summary>
    internal static string TextOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? (text.GetString() ?? "").Trim()
                : "";
        }
        catch (JsonException)
        {
            throw new TranscriptionException("ElevenLabs answered with something other than a transcript.");
        }
    }

    /// <summary>A refusal, in words: the status and what the service said about it.</summary>
    internal static string Explain(HttpStatusCode status, string body)
    {
        var detail = Detail(body);
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"ElevenLabs does not accept the API key{(detail.Length > 0 ? $" ({detail})" : "")}. Check {EnvironmentVariable} on the PC.",
            HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests =>
                $"ElevenLabs refused for now: {(detail.Length > 0 ? detail : "the quota or rate limit is reached")}.",
            _ => $"ElevenLabs answered {(int)status}: {(detail.Length > 0 ? detail : status.ToString())}.",
        };
    }

    /// <summary>What the service says went wrong: <c>{"detail":{"status":…,"message":…}}</c> or <c>{"detail":"…"}</c>; empty otherwise.</summary>
    private static string Detail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("detail", out var detail)) return "";
            var text = detail.ValueKind switch
            {
                JsonValueKind.String => detail.GetString() ?? "",
                JsonValueKind.Object when detail.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String => message.GetString() ?? "",
                JsonValueKind.Object when detail.TryGetProperty("status", out var code) && code.ValueKind == JsonValueKind.String => code.GetString() ?? "",
                _ => "",
            };
            text = text.Trim().TrimEnd('.');
            return text.Length > 200 ? text[..200] + "…" : text;
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>The file name extension for a recording's media type; ".bin" for one that is not a known kind of audio.</summary>
    internal static string ExtensionFor(string mime) => mime.Trim().ToLowerInvariant() switch
    {
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
        "audio/aac" => ".aac",
        "audio/ogg" => ".ogg",
        "audio/opus" => ".opus",
        "audio/webm" => ".webm",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/3gpp" => ".3gp",
        "audio/flac" => ".flac",
        _ => ".bin",
    };
}
