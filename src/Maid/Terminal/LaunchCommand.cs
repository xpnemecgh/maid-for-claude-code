using System.Collections;
using System.IO;

namespace Maid.Terminal;

/// <summary>Turns the configured command (e.g. <c>claude --dangerously-skip-permissions</c>) into something CreateProcess can run.</summary>
internal static class LaunchCommand
{
    public const string Default = "claude --dangerously-skip-permissions";
    public const string ResumeArgument = "--continue";

    /// <summary>The command that picks up the last conversation in the folder instead of starting a new one.</summary>
    public static string WithResume(string command) => command.Trim() + " " + ResumeArgument;

    /// <summary>
    /// Variables Claude Code sets to describe *its own* session. When Maid itself is started from inside
    /// a Claude Code session they must not leak into the sessions it hosts, or those would consider
    /// themselves children of that outer session.
    /// </summary>
    private static readonly string[] SessionScopedVariables =
    [
        "CLAUDECODE",
        "AI_AGENT",
        "CLAUDE_PID",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN",
        "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_SSE_PORT",
    ];

    public static string BuildCommandLine(string command)
    {
        command = command.Trim();
        var (program, arguments) = SplitProgram(command);
        var resolved = FindExecutable(program);

        if (resolved is not null && Path.GetExtension(resolved).ToLowerInvariant() is ".exe" or ".com")
            return $"\"{resolved}\"{arguments}";

        // Script shims (claude.cmd from an npm install) need the command interpreter. Unresolvable
        // commands go the same way so that the "not recognized" error shows up inside the terminal.
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{commandInterpreter}\" /d /s /c \"{command}\"";
    }

    /// <summary>Where the command's program is (searched on the PATH like CreateProcess would), or null when it is nowhere.</summary>
    public static string? Locate(string command) => FindExecutable(SplitProgram(command.Trim()).Program);

    /// <summary>Whether the command's program is started directly (an .exe), not through cmd.exe (a script shim).</summary>
    public static bool RunsDirectly(string command)
    {
        var resolved = FindExecutable(SplitProgram(command.Trim()).Program);
        return resolved is not null && Path.GetExtension(resolved).ToLowerInvariant() is ".exe" or ".com";
    }

    public static Dictionary<string, string> BuildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && name.Length > 0 && !name.StartsWith('='))
                environment[name] = variable.Value as string ?? "";
        }

        // Claude Code also turns colour off for the non-interactive tools it runs. Inherited from such a
        // shell that is an artifact, not the user's preference, and it would make every hosted TUI monochrome.
        if (environment.ContainsKey("CLAUDECODE"))
            environment.Remove("NO_COLOR");

        foreach (var name in SessionScopedVariables)
            environment.Remove(name);

        // xterm.js is a full xterm-compatible, 24-bit colour terminal.
        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        return environment;
    }

    /// <summary>Splits a command into its program token (unquoted) and the remainder (with leading whitespace).</summary>
    private static (string Program, string Arguments) SplitProgram(string command)
    {
        if (command.StartsWith('"'))
        {
            int closing = command.IndexOf('"', 1);
            return closing < 0
                ? (command[1..], "")
                : (command[1..closing], command[(closing + 1)..]);
        }

        int space = command.IndexOfAny([' ', '\t']);
        return space < 0 ? (command, "") : (command[..space], command[space..]);
    }

    private static string? FindExecutable(string program)
    {
        if (program.Length == 0) return null;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A name that already carries an extension is tried verbatim first ("claude.exe", "tool.cmd").
        var suffixes = Path.HasExtension(program) ? extensions.Prepend("") : extensions;

        IEnumerable<string> directories = program.IndexOfAny(['\\', '/', ':']) >= 0
            ? [""]
            : (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => directory.Trim('"'));

        foreach (var directory in directories)
        {
            foreach (var suffix in suffixes)
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory, program + suffix));
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Malformed PATH entry; skip it.
                }
            }
        }
        return null;
    }
}
