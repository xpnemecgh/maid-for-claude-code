using System.IO;
using Xunit;

namespace Maid.Tests;

public class DiagnosticsTests
{
    private static readonly DateTime T0 = new(2026, 11, 25, 0, 0, 0);

    /// <summary>Samples every 30 s of a GPU process using <paramref name="gpuCores"/> and Maid 0.01 cores.</summary>
    private sealed class Run
    {
        private readonly LoadWatch _watch = new();
        private TimeSpan _gpu, _maid;
        public DateTime Now = T0;
        public int ContextCalls;

        public string? Step(double gpuCores, double seconds = 30)
        {
            Now += TimeSpan.FromSeconds(seconds);
            _gpu += TimeSpan.FromSeconds(gpuCores * seconds);
            _maid += TimeSpan.FromSeconds(0.01 * seconds);
            return _watch.Sample(Now, [new(10, "GPU process", _gpu), new(1, "Maid", _maid)], () => { ContextCalls++; return "the PC at 87%"; });
        }

        public string? First() => _watch.Sample(Now, [new(10, "GPU process", _gpu), new(1, "Maid", _maid)], () => "");
    }

    [Fact]
    public void LightLoadIsNeverNoted()
    {
        var run = new Run();
        Assert.Null(run.First());
        for (int i = 0; i < 40; i++) Assert.Null(run.Step(0.3));
        Assert.Equal(0, run.ContextCalls);
    }

    [Fact]
    public void HeavyLoadIsNotedAfterAMinuteThenEveryTenMinutesThenWhenOver()
    {
        var run = new Run();
        run.First();
        Assert.Null(run.Step(0.98)); // 30 s: not yet
        var first = run.Step(0.98);  // a minute
        Assert.NotNull(first);
        Assert.StartsWith("Maid is busy: GPU process 0.98, Maid 0.01 cores over the last 30 s; the PC at 87%", first);

        for (int i = 0; i < 19; i++) Assert.Null(run.Step(1.0)); // the next 9.5 minutes
        var again = run.Step(1.0);
        Assert.NotNull(again);
        Assert.StartsWith("Maid is still busy: GPU process 1.00", again);
        Assert.Contains("(11 minutes so far)", again);

        var over = run.Step(0.05);
        Assert.NotNull(over);
        Assert.StartsWith("Maid is back to normal after", over);
        Assert.Null(run.Step(0.05));
    }

    [Fact]
    public void SamplesThatComeSlightlyEarlyStillCountAsAMinute()
    {
        var run = new Run();
        run.First();
        Assert.Null(run.Step(0.9, seconds: 29.99));
        Assert.NotNull(run.Step(0.9, seconds: 29.99)); // 59.98 s
    }

    [Fact]
    public void ABriefSpikeIsNotNoted()
    {
        var run = new Run();
        run.First();
        Assert.Null(run.Step(2.0));
        Assert.Null(run.Step(0.1));
        Assert.Null(run.Step(2.0));
        Assert.Null(run.Step(0.1));
        Assert.Equal(0, run.ContextCalls);
    }

    [Fact]
    public void ProcessesWithoutAnEarlierSampleAreLeftOut()
    {
        var watch = new LoadWatch();
        Assert.Null(watch.Sample(T0, [new(1, "Maid", TimeSpan.Zero)], () => ""));
        // A GPU process that appeared meanwhile, with an hour of CPU time behind it, is not counted as this interval's.
        Assert.Null(watch.Sample(T0.AddSeconds(30), [new(1, "Maid", TimeSpan.Zero), new(7, "GPU process", TimeSpan.FromHours(1))], () => ""));
        Assert.Null(watch.Sample(T0.AddSeconds(60), [new(1, "Maid", TimeSpan.Zero), new(7, "GPU process", TimeSpan.FromHours(1))], () => ""));
    }

    [Fact]
    public void TheLogKeepsItsNewerHalfOnceTooLong()
    {
        var file = Path.Combine(Path.GetTempPath(), "maid-diagnostics-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            for (int i = 0; i < 100; i++) DiagnosticsLog.Append(file, $"line {i:000} " + new string('x', 40), T0.AddMinutes(i), maxBytes: 2000);
            var lines = File.ReadAllLines(file);
            Assert.True(new FileInfo(file).Length <= 2000);
            Assert.EndsWith("line 099 " + new string('x', 40), lines[^1]);
            Assert.StartsWith("2026-11-25 01:39:00  line 099", lines[^1]);
            Assert.DoesNotContain(lines, line => line.Contains("line 000"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AppendingWhereNothingCanBeWrittenDoesNotThrow()
    {
        DiagnosticsLog.Append(Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"), "x.log"), "text", T0);
    }

    [Fact]
    public void SystemLoadIsAShareOnceThereIsAnEarlierSample()
    {
        var load = new SystemLoad();
        Assert.Null(load.Sample());
        Thread.Sleep(50);
        if (load.Sample() is { } share) Assert.InRange(share, 0, 1);
    }
}
