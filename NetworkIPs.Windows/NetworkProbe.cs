using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkIPs.Windows;

internal sealed record NetworkSnapshot(
    string PublicIP,
    string PublicStatus,
    string LocalIP,
    string Tailscale,
    bool ShouldRunTraceroute
);

internal sealed record InterfaceAddress(
    string Name,
    string Address,
    AddressFamily Family,
    bool IsUp,
    bool IsLoopback
);

internal sealed record TailscaleInfo(string Status, IReadOnlyList<string> Addresses);

internal static class NetworkProbe
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static async Task<NetworkSnapshot> CreateSnapshotAsync(CancellationToken cancellationToken)
    {
        var interfaces = CollectInterfaceAddresses();
        var localIP = FormatInterfaceAddresses(LocalNetworkAddresses(interfaces));
        var tailscale = await DetectTailscaleAsync(interfaces, cancellationToken);
        var (publicIP, publicStatus) = await FetchPublicIPAsync(cancellationToken);

        return new NetworkSnapshot(
            publicIP,
            publicStatus,
            localIP,
            FormatTailscaleInfo(tailscale),
            publicIP != "n/a"
        );
    }

    private static async Task<(string PublicIP, string PublicStatus)> FetchPublicIPAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync("https://api.ipify.org", cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();

            if (string.IsNullOrWhiteSpace(payload))
            {
                return ("n/a", "Error: unreadable response.");
            }

            return (payload, "Source: api.ipify.org");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ("n/a", $"Error: {ex.Message}");
        }
    }

    private static IReadOnlyList<InterfaceAddress> CollectInterfaceAddresses()
    {
        var results = new List<InterfaceAddress>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            bool isUp = adapter.OperationalStatus == OperationalStatus.Up;
            bool isLoopback = adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback;

            foreach (var unicastAddress in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicastAddress.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                {
                    continue;
                }

                results.Add(new InterfaceAddress(
                    adapter.Name,
                    unicastAddress.Address.ToString(),
                    unicastAddress.Address.AddressFamily,
                    isUp,
                    isLoopback
                ));
            }
        }

        return results
            .GroupBy(address => $"{address.Name}|{address.Address}|{address.Family}")
            .Select(group => group.First())
            .ToList();
    }

    private static IReadOnlyList<InterfaceAddress> LocalNetworkAddresses(IReadOnlyList<InterfaceAddress> allAddresses)
    {
        var privateIPv4 = allAddresses.Where(address =>
            address.Family == AddressFamily.InterNetwork
            && address.IsUp
            && !address.IsLoopback
            && !IsLinkLocalIPv4(address.Address)
            && !IsTailscaleAddress(address.Address)
            && IsPrivateIPv4(address.Address)
        ).ToList();

        if (privateIPv4.Count > 0)
        {
            return privateIPv4;
        }

        var fallbackIPv4 = allAddresses.Where(address =>
            address.Family == AddressFamily.InterNetwork
            && address.IsUp
            && !address.IsLoopback
            && !IsLinkLocalIPv4(address.Address)
            && !IsTailscaleAddress(address.Address)
        ).ToList();

        if (fallbackIPv4.Count > 0)
        {
            return fallbackIPv4;
        }

        return allAddresses.Where(address =>
            address.Family == AddressFamily.InterNetworkV6
            && address.IsUp
            && !address.IsLoopback
            && !IsLinkLocalIPv6(address.Address)
            && !IsTailscaleAddress(address.Address)
        ).ToList();
    }

    private static string FormatInterfaceAddresses(IReadOnlyList<InterfaceAddress> addresses)
    {
        if (addresses.Count == 0)
        {
            return "Not detected";
        }

        return string.Join(Environment.NewLine, addresses.Select(address => $"{address.Name}  {address.Address}"));
    }

    private static async Task<TailscaleInfo> DetectTailscaleAsync(
        IReadOnlyList<InterfaceAddress> allAddresses,
        CancellationToken cancellationToken
    )
    {
        var scannedAddresses = allAddresses
            .Where(address => address.IsUp && !address.IsLoopback && (IsTailscaleAddress(address.Address) || address.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase)))
            .Select(address => address.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cli = await FindTailscaleCLIAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(cli))
        {
            var ipv4 = ParseIPs(await TryRunProcessAsync(cli, "ip -4", cancellationToken));
            var ipv6 = ParseIPs(await TryRunProcessAsync(cli, "ip -6", cancellationToken));
            var merged = ipv4.Concat(ipv6).Concat(scannedAddresses).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (merged.Count > 0)
            {
                return new TailscaleInfo("Detected via CLI", merged);
            }

            return new TailscaleInfo("CLI found, no active Tailscale IP", Array.Empty<string>());
        }

        if (scannedAddresses.Count > 0)
        {
            return new TailscaleInfo("Detected via interface scan", scannedAddresses);
        }

        if (OperatingSystem.IsWindows()
            && (File.Exists(@"C:\Program Files\Tailscale\Tailscale IPN.exe")
                || File.Exists(@"C:\Program Files (x86)\Tailscale\Tailscale IPN.exe")))
        {
            return new TailscaleInfo("Install found, no active Tailscale IP", Array.Empty<string>());
        }

        if (OperatingSystem.IsMacOS() && File.Exists("/Applications/Tailscale.app"))
        {
            return new TailscaleInfo("Install found, no active Tailscale IP", Array.Empty<string>());
        }

        return new TailscaleInfo("Not detected", Array.Empty<string>());
    }

    private static string FormatTailscaleInfo(TailscaleInfo info)
    {
        if (info.Addresses.Count == 0)
        {
            return info.Status;
        }

        return string.Join(Environment.NewLine, new[] { info.Status }.Concat(info.Addresses));
    }

    private static async Task<string?> FindTailscaleCLIAsync(CancellationToken cancellationToken)
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [
                @"C:\Program Files\Tailscale\tailscale.exe",
                @"C:\Program Files (x86)\Tailscale\tailscale.exe"
            ]
            : [
                "/usr/bin/tailscale",
                "/usr/local/bin/tailscale",
                "/opt/homebrew/bin/tailscale",
                "/opt/local/bin/tailscale"
            ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var output = await TryRunProcessAsync(
            OperatingSystem.IsWindows() ? "where" : "which",
            "tailscale",
            cancellationToken
        );
        var firstLine = output?
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
    }

    private static async Task<string?> TryRunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                return string.IsNullOrWhiteSpace(error) ? null : error.Trim();
            }

            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ParseIPs(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<string>();
        }

        return output
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.Contains(' ') && (line.Contains('.') || line.Contains(':')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPrivateIPv4(string address)
    {
        var parts = address.Split('.').Select(part => int.TryParse(part, out var value) ? value : -1).ToArray();
        if (parts.Length != 4)
        {
            return false;
        }

        return parts[0] switch
        {
            10 => true,
            172 => parts[1] is >= 16 and <= 31,
            192 => parts[1] == 168,
            _ => false
        };
    }

    private static bool IsLinkLocalIPv4(string address) => address.StartsWith("169.254.", StringComparison.Ordinal);

    private static bool IsLinkLocalIPv6(string address) => address.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase);

    private static bool IsTailscaleAddress(string address) => IsTailscaleIPv4(address) || IsTailscaleIPv6(address);

    private static bool IsTailscaleIPv4(string address)
    {
        var parts = address.Split('.').Select(part => int.TryParse(part, out var value) ? value : -1).ToArray();
        return parts.Length == 4 && parts[0] == 100 && parts[1] is >= 64 and <= 127;
    }

    private static bool IsTailscaleIPv6(string address)
        => address.StartsWith("fd7a:115c:a1e0:", StringComparison.OrdinalIgnoreCase);
}
