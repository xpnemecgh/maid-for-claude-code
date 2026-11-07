using System.Net;

namespace Maid.Tests;

public sealed class ElevenLabsTests
{
    [Fact]
    public void The_key_comes_from_the_settings_else_from_the_environment()
    {
        var settings = new Settings { ElevenLabsApiKey = " sk-settings " };
        Assert.Equal("sk-settings", ElevenLabs.ApiKey(settings, out var source, _ => "sk-env"));
        Assert.Equal("settings", source);

        settings.ElevenLabsApiKey = "";
        Assert.Equal("sk-env", ElevenLabs.ApiKey(settings, out source, name => name == ElevenLabs.EnvironmentVariable ? " sk-env\n" : null));
        Assert.Equal("environment", source);

        Assert.Null(ElevenLabs.ApiKey(settings, out source, _ => ""));
        Assert.Equal("none", source);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"detail":{"status":"invalid_api_key","message":"Invalid API key."}}""", "does not accept the API key (Invalid API key)")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"detail":"Too many requests."}""", "refused for now: Too many requests.")]
    [InlineData(HttpStatusCode.PaymentRequired, "", "refused for now: the quota or rate limit is reached.")]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"detail":{"status":"invalid_model"}}""", "answered 422: invalid_model.")]
    [InlineData(HttpStatusCode.InternalServerError, "<html>boom</html>", "answered 500: InternalServerError.")]
    public void A_refusal_is_explained_in_words(HttpStatusCode status, string body, string expected) =>
        Assert.Contains(expected, ElevenLabs.Explain(status, body));

    [Fact]
    public void The_words_are_taken_from_the_answer()
    {
        Assert.Equal("Hello there.", ElevenLabs.TextOf("""{"language_code":"eng","text":" Hello there. ","words":[]}"""));
        Assert.Equal("", ElevenLabs.TextOf("""{"text":""}"""));
        Assert.Equal("", ElevenLabs.TextOf("[]"));
        Assert.Throws<TranscriptionException>(() => ElevenLabs.TextOf("not json"));
    }

    [Theory]
    [InlineData("audio/mp4", ".m4a")]
    [InlineData("Audio/OGG", ".ogg")]
    [InlineData("audio/wav", ".wav")]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("text/plain", ".bin")]
    [InlineData("", ".bin")]
    public void Recordings_are_named_after_their_kind(string mime, string extension) => Assert.Equal(extension, ElevenLabs.ExtensionFor(mime));

    /// <summary>
    /// The real service, only when asked for: MAID_TEST_AUDIO names a recording of speech and the key is set.
    /// Otherwise there is nothing to check here.
    /// </summary>
    [Fact]
    public async Task The_real_service_transcribes_a_recording_when_asked_to()
    {
        var sample = Environment.GetEnvironmentVariable("MAID_TEST_AUDIO");
        var key = ElevenLabs.ApiKey(new Settings(), out _);
        if (sample is null || !File.Exists(sample) || key is null) return;
        var mime = Path.GetExtension(sample).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            _ => "audio/mp4",
        };
        var text = await ElevenLabs.TranscribeAsync(key, await File.ReadAllBytesAsync(sample), mime, ElevenLabs.DefaultModel, "", CancellationToken.None);
        Assert.NotEmpty(text);
    }
}
