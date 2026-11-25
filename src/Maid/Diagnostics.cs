using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Maid;

/// <summary>
/// Maid's notes on its own load and on its browser's troubles, in &lt;data dir&gt;\diagnostics.log (MainWindow writes
/// them): when Maid's processes keep a CPU core busy (<see cref="LoadWatch"/>), with how busy the whole PC was and what
/// the windows were doing, and every WebView2 process failure and page reload. Without them a slow desktop cannot be
/// traced to Maid, or Maid cleared of it, afterwards.
/// </summary>
internal static class DiagnosticsLog
{
    public const string FileName = "diagnostics.log";
    public const long MaxBytes = 1_000_000;

    /// <summary>Adds a line. Once the file grows over <paramref name="maxBytes"/> its older half goes. Never throws.</summary>
    public static void Append(string file, string text, DateTime now, long maxBytes = MaxBytes)
    {
        try
        {
            File.AppendAllText(file, $"{now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}", Encoding.UTF8);
            if (new FileInfo(file).Length <= maxBytes) return;
            var lines = File.ReadAllLines(file, Encoding.UTF8);
            File.WriteAllLines(file, lines[(lines.Length / 2)..], Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A note that cannot be written is not worth disturbing anything for.
        }
    }
}

/// <summary>
/// Decides from samples of the CPU time Maid's processes have used (taken every half minute) when that is worth a
/// note: one kind of process (the GPU process, the page, ...) at half a core or more for a minute, again every ten
/// minutes while it lasts, and once more when it is over.
/// </summary>
internal sealed class LoadWatch
{
    public const double HeavyCores = 0.5;
    public static readonly TimeSpan Sustained = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Repeat = TimeSpan.FromMinutes(10);
    /// <summary>Samples do not come exactly on time: two half minutes may add up to 59.99 s.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    /// <summary>A process to watch: its id, what it is ("GPU process", "page", "Maid", ...) and its CPU time so far.</summary>
    public readonly record struct Sampled(int Id, string Kind, TimeSpan Cpu);

    private Dictionary<int, TimeSpan> _previous = [];
    private DateTime? _previousAt;
    private DateTime? _heavySince;
    private DateTime? _notedAt;

    /// <summary>
    /// Takes a sample. Returns a line for the log, or null. <paramref name="context"/> describes the moment (how busy
    /// the PC is, what the windows do) and is only asked for when there is something to note.
    /// </summary>
    public string? Sample(DateTime now, IReadOnlyCollection<Sampled> processes, Func<string> context)
    {
        var since = _previousAt;
        var previous = _previous;
        _previous = processes.GroupBy(process => process.Id).ToDictionary(group => group.Key, group => group.First().Cpu);
        _previousAt = now;
        if (since is not { } start || now <= start) return null;

        // Processes that were not there at the last sample have no measure yet; ones that went are gone from it.
        var seconds = (now - start).TotalSeconds;
        var cores = new Dictionary<string, double>();
        foreach (var process in processes)
        {
            if (previous.TryGetValue(process.Id, out var before) && process.Cpu >= before)
                cores[process.Kind] = cores.GetValueOrDefault(process.Kind) + (process.Cpu - before).TotalSeconds / seconds;
        }

        if (cores.Values.Any(value => value >= HeavyCores))
        {
            _heavySince ??= start;
            if (now - _heavySince < Sustained - Slack || (_notedAt is { } noted && now - noted < Repeat - Slack)) return null;
            var again = _notedAt is not null;
            _notedAt = now;
            return $"Maid is {(again ? "still " : "")}busy: {Format(cores)} over the last {seconds:0} s"
                + $"{(again ? $" ({Minutes(now - _heavySince.Value)} so far)" : "")}; {context()}.";
        }

        var wasNoted = _notedAt is not null;
        var heavyFor = now - (_heavySince ?? now);
        _heavySince = null;
        _notedAt = null;
        return wasNoted ? $"Maid is back to normal after {Minutes(heavyFor)}: {Format(cores)}." : null;
    }

    /// <summary>"GPU process 0.98, page 0.12, Maid 0.01 cores": busiest first, the idle ones left out.</summary>
    private static string Format(Dictionary<string, double> cores)
    {
        var busy = cores.Where(pair => pair.Value >= 0.005).OrderByDescending(pair => pair.Value).ToList();
        return busy.Count == 0 ? "next to no CPU" : string.Join(", ", busy.Select(pair => $"{pair.Key} {pair.Value:0.00}")) + " cores";
    }

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes < 1.5 ? "a minute" : $"{Math.Round(span.TotalMinutes):0} minutes";
}

/// <summary>How busy all of the PC's processors were between two samples.</summary>
internal sealed class SystemLoad
{
    private ulong _idle, _total;

    /// <summary>The share of processor time in use (0 to 1) since the last call; null at the first, or when unknown.</summary>
    public double? Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var total = kernel + user; // kernel time includes the idle time
        double? load = _total == 0 || total <= _total ? null : 1 - (double)(idle - _idle) / (total - _total);
        (_idle, _total) = (idle, total);
        return load is { } value ? Math.Clamp(value, 0, 1) : null;
    }

    // FILETIMEs, read as the 64-bit counts of 100 ns they are.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}
