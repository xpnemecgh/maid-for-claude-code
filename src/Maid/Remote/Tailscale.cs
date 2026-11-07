using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Maid.Remote;

/// <summary>A device in the tailnet, as Tailscale identifies it.</summary>
internal sealed record TailscalePeer(string NodeId, string Name, string HostName, string Os);

/// <summary>This machine in the tailnet: the addresses to listen on and the name to give the phone.</summary>
internal sealed record TailscaleSelf(IReadOnlyList<IPAddress> Addresses, string DnsName, string HostName);

/// <summary>Who is on the other end of a connection, according to Tailscale.</summary>
internal interface IPeerDirectory
{
    /// <summary>This machine's place in the tailnet; null when Tailscale is not running or not logged in.</summary>
    TailscaleSelf? Self(out string? error);
    /// <summary>The tailnet device that has <paramref name="address"/>; null for anything else.</summary>
    TailscalePeer? WhoIs(IPAddress address);
}

/// <summary>Asks the local Tailscale service, through its command line (tailscale.exe), with a timeout.</summary>
internal sealed class TailscaleCli(string configuredPath) : IPeerDirectory
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    public TailscaleSelf? Self(out string? error)
    {
        var json = Run(["status", "--json"], out error);
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var backend = Text(root, "BackendState");
            if (backend != "Running")
            {
                error = backend.Length > 0 ? $"Tailscale is {backend}" : "Tailscale is not running";
                return null;
            }
            var addresses = new List<IPAddress>();
            if (root.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
            {
                foreach (var ip in ips.EnumerateArray())
                {
                    if (IPAddress.TryParse(ip.GetString(), out var address)) addresses.Add(address);
                }
            }
            var self = root.TryGetProperty("Self", out var node) ? node : default;
            return new TailscaleSelf(addresses,
                self.ValueKind == JsonValueKind.Object ? Text(self, "DNSName").TrimEnd('.') : "",
                self.ValueKind == JsonValueKind.Object ? Text(self, "HostName") : "");
        }
        catch (JsonException e)
        {
            error = $"Unexpected answer from tailscale status: {e.Message}";
            return null;
        }
    }

    public TailscalePeer? WhoIs(IPAddress address)
    {
        if (!IsTailnetAddress(address)) return null;
        var json = Run(["whois", "--json", address.ToString()], out _);
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Node", out var node) || node.ValueKind != JsonValueKind.Object) return null;
            var id = Text(node, "StableID");
            if (id.Length == 0) return null;
            var info = node.TryGetProperty("Hostinfo", out var hostinfo) && hostinfo.ValueKind == JsonValueKind.Object ? hostinfo : default;
            return new TailscalePeer(id, Text(node, "Name").TrimEnd('.'),
                info.ValueKind == JsonValueKind.Object ? Text(info, "Hostname") : "",
                info.ValueKind == JsonValueKind.Object ? Text(info, "OS") : "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Tailscale hands out addresses from 100.64.0.0/10 and fd7a:115c:a1e0::/48.</summary>
    public static bool IsTailnetAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] == 100 && (bytes[1] & 0xC0) == 64,
            AddressFamily.InterNetworkV6 => bytes[0] == 0xfd && bytes[1] == 0x7a && bytes[2] == 0x11 && bytes[3] == 0x5c && bytes[4] == 0xa1 && bytes[5] == 0xe0,
            _ => false,
        };
    }

    private string? FindExecutable()
    {
        if (configuredPath.Length > 0) return File.Exists(configuredPath) ? configuredPath : null;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "tailscale.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(installed) ? installed : null;
    }

    private string? Run(string[] arguments, out string? error)
    {
        error = null;
        var executable = FindExecutable();
        if (executable is null)
        {
            error = "Tailscale is not installed (tailscale.exe not found)";
            return null;
        }
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                error = "tailscale.exe did not start";
                return null;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                error = "tailscale.exe did not answer";
                return null;
            }
            if (process.ExitCode != 0)
            {
                error = errors.Result.Trim() is { Length: > 0 } message ? message : $"tailscale.exe failed ({process.ExitCode})";
                return null;
            }
            return output.Result;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = $"tailscale.exe could not be run: {e.Message}";
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
