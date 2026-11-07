using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;

namespace Maid;

/// <summary>
/// Runs Maid as administrator, so that every console it hosts is elevated too: a process attached to a
/// pseudo console gets the token of the process that starts it, so an elevated session inside the window
/// takes an elevated Maid. A Maid started without admin rights starts an elevated copy of itself
/// (<see cref="TryRestartElevated"/>) and exits.
/// </summary>
/// <remarks>
/// Windows starts an elevated process with a fresh environment and in System32, not with the starter's,
/// so the starter hands both over in a file (<see cref="TakeHandOver"/>): MAID_DATA_DIR, a PATH set up in
/// the launching shell and the like reach the sessions just as they would without elevation.
/// </remarks>
internal static class Elevation
{
    /// <summary>First argument of the elevated copy, followed by the hand-over file.</summary>
    public const string HandOverArgument = "--elevated-from";

    private const int ErrorCancelled = 1223;

    private sealed record HandOver(string Directory, Dictionary<string, string> Environment);

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Why Maid runs without admin rights although it was meant to have them; null otherwise.</summary>
    public static string? Failure { get; private set; }

    /// <summary>
    /// In the elevated copy: takes over the starter's environment and working directory and deletes the
    /// hand-over file. Returns the remaining arguments, and whether this process is such a copy.
    /// </summary>
    public static (string[] Args, bool HandedOver) TakeHandOver(string[] args)
    {
        if (args.Length < 2 || args[0] != HandOverArgument) return (args, false);

        var file = args[1];
        try
        {
            var handOver = JsonSerializer.Deserialize<HandOver>(File.ReadAllText(file));
            if (handOver is not null) Apply(handOver);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Start with the environment Windows gave us rather than not at all.
        }
        try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }

        if (!IsElevated) Failure ??= "the elevated copy of Maid started without admin rights";
        return (args[2..], true);
    }

    /// <summary>Starts an elevated copy of Maid with the same arguments; false (see <see cref="Failure"/>) when that did not work.</summary>
    public static bool TryRestartElevated(string[] args)
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            Failure = "Maid could not find its own executable to restart it elevated";
            return false;
        }

        var file = Path.Combine(Path.GetTempPath(), $"maid-elevate-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(Capture()));
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add(HandOverArgument);
            start.ArgumentList.Add(file);
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var _ = Process.Start(start);
            return true;
        }
        catch (Win32Exception e)
        {
            Failure = e.NativeErrorCode == ErrorCancelled ? "the request for admin rights was declined" : e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failure = e.Message;
        }
        try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>
    /// Opens a web address in the user's browser. From an elevated Maid it goes through Explorer, which
    /// opens it without admin rights: a browser has no business running elevated.
    /// </summary>
    public static void OpenUrl(string url)
    {
        var start = IsElevated
            ? new ProcessStartInfo("explorer.exe", $"\"{url}\"")
            : new ProcessStartInfo(url) { UseShellExecute = true };
        using var _ = Process.Start(start);
    }

    private static HandOver Capture()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && name.Length > 0 && !name.StartsWith('='))
                environment[name] = variable.Value as string ?? "";
        }
        return new HandOver(Environment.CurrentDirectory, environment);
    }

    private static void Apply(HandOver handOver)
    {
        var wanted = new HashSet<string>(handOver.Environment.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && name.Length > 0 && !name.StartsWith('=') && !wanted.Contains(name))
                Environment.SetEnvironmentVariable(name, null);
        }
        foreach (var (name, value) in handOver.Environment)
        {
            if (name.Length > 0 && !name.Contains('='))
                Environment.SetEnvironmentVariable(name, value);
        }
        if (System.IO.Directory.Exists(handOver.Directory))
            Environment.CurrentDirectory = handOver.Directory;
    }
}
