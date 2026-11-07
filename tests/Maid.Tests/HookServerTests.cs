using System.Net.Http;
using System.Text;
using System.Text.Json;
using Maid.Terminal;

namespace Maid.Tests;

public sealed class HookServerTests
{
    [Fact]
    public async Task A_hook_on_the_session_path_is_heard_and_anything_else_is_turned_away()
    {
        var heard = new List<(string Token, string Event, string Body)>();
        using var server = new HookServer((token, name, body) => { lock (heard) heard.Add((token, name, body.GetRawText())); });
        var token = HookServer.NewToken();
        using var http = new HttpClient();

        var ok = await http.PostAsync($"http://127.0.0.1:{server.Port}/hook/{token}/Stop",
            new StringContent("{\"last_assistant_message\":\"Done.\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)ok.StatusCode);
        Assert.Empty(await ok.Content.ReadAsByteArrayAsync()); // an empty body: Claude Code carries on as it would

        foreach (var path in new[] { $"/hook/{token}/Unknown", "/hook/short/Stop", $"/other/{token}/Stop", $"/hook/{token.ToUpperInvariant()}/Stop" })
            Assert.Equal(404, (int)(await http.PostAsync($"http://127.0.0.1:{server.Port}{path}", new StringContent("{}"))).StatusCode);
        Assert.Equal(404, (int)(await http.GetAsync($"http://127.0.0.1:{server.Port}/hook/{token}/Stop")).StatusCode);

        for (int i = 0; i < 100 && heard.Count == 0; i++) await Task.Delay(20);
        var only = Assert.Single(heard);
        Assert.Equal((token, "Stop"), (only.Token, only.Event));
        Assert.Contains("Done.", only.Body);
    }

    [Fact]
    public void The_settings_ask_for_the_events_Maid_reads_on_this_server_only()
    {
        var token = HookServer.NewToken();
        var hooks = JsonDocument.Parse(HookServer.SettingsJson(4711, token)).RootElement.GetProperty("hooks");
        foreach (var name in HookServer.Events)
        {
            var group = hooks.GetProperty(name)[0];
            var hook = group.GetProperty("hooks")[0];
            Assert.Equal("http", hook.GetProperty("type").GetString());
            Assert.Equal($"http://127.0.0.1:4711/hook/{token}/{name}", hook.GetProperty("url").GetString());
            // Asking the user is all Maid wants to know about tools: no hook on every tool call.
            Assert.Equal(name is "PreToolUse" or "PostToolUse", group.TryGetProperty("matcher", out var matcher));
            if (name is "PreToolUse") Assert.Equal("AskUserQuestion|ExitPlanMode", matcher.GetString());
        }
        Assert.Matches("^[0-9a-f]{32}$", token);
        Assert.Equal((token, "Notification"), HookServer.Route($"/hook/{token}/Notification"));
    }

    [Theory]
    [InlineData("open Spotify", "\"open Spotify\"")]
    [InlineData("say \"hi\" to 100% of C:\\Users ^now", "\"say hi to 100 of C: Users now\"")]
    [InlineData("--dangerous flag", "\"dangerous flag\"")]
    [InlineData("two\r\nlines", "\"two lines\"")]
    [InlineData("  ", "")]
    public void A_spoken_request_becomes_one_safe_quoted_argument(string spoken, string argument)
    {
        Assert.Equal(argument, Dashboard.PromptArgument(spoken));
    }
}
