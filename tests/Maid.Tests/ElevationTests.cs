using System.Collections;
using System.Text.Json;
using Maid;

namespace Maid.Tests;

public sealed class ElevationTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"maid-elevation-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAID_TEST_HANDED_OVER", null);
        Environment.SetEnvironmentVariable("MAID_TEST_NOT_HANDED_OVER", null);
        File.Delete(_file);
    }

    [Fact]
    public void Arguments_without_a_hand_over_are_left_alone()
    {
        var (args, handedOver) = Elevation.TakeHandOver(["--something", "else"]);

        Assert.False(handedOver);
        Assert.Equal(["--something", "else"], args);
    }

    [Fact]
    public void The_elevated_copy_takes_over_the_starters_environment_and_deletes_the_file()
    {
        // What the starter had: everything this process has, plus one variable, minus another.
        Environment.SetEnvironmentVariable("MAID_TEST_NOT_HANDED_OVER", "stale");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && !name.StartsWith('=') && name != "MAID_TEST_NOT_HANDED_OVER")
                environment[name] = variable.Value as string ?? "";
        }
        environment["MAID_TEST_HANDED_OVER"] = "from the starter";
        File.WriteAllText(_file, JsonSerializer.Serialize(new { Directory = Environment.CurrentDirectory, Environment = environment }));

        var (args, handedOver) = Elevation.TakeHandOver([Elevation.HandOverArgument, _file, "--rest"]);

        Assert.True(handedOver);
        Assert.Equal(["--rest"], args);
        Assert.Equal("from the starter", Environment.GetEnvironmentVariable("MAID_TEST_HANDED_OVER"));
        Assert.Null(Environment.GetEnvironmentVariable("MAID_TEST_NOT_HANDED_OVER"));
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void A_missing_hand_over_file_still_starts()
    {
        var (args, handedOver) = Elevation.TakeHandOver([Elevation.HandOverArgument, _file]);

        Assert.True(handedOver);
        Assert.Empty(args);
    }
}
