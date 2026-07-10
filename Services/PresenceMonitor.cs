using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// "Who's home": every 2 minutes, ping-sweeps the LAN to freshen the ARP table,
/// then matches configured MACs (Network:KnownDevices) against it. A device
/// counts as home if its MAC was seen in the last 10 minutes — phones nap, so
/// the grace period matters.
/// </summary>
public sealed class PresenceMonitor(IOptions<NetworkOptions> options, ILogger<PresenceMonitor> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HomeGrace = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Device(string Name, string Mac, bool IsHome, DateTimeOffset? LastSeen);

    public bool AnyConfigured => options.Value.KnownDevices.Count > 0;

    public IReadOnlyList<Device> Devices =>
        options.Value.KnownDevices.Select(d =>
        {
            var seen = _lastSeen.TryGetValue(Normalize(d.Mac), out var at) ? at : (DateTimeOffset?)null;
            return new Device(d.Name, d.Mac, seen is { } s && DateTimeOffset.Now - s < HomeGrace, seen);
        }).ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!AnyConfigured)
        {
            logger.LogInformation("Presence idle (no Network:KnownDevices configured)");
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Presence sweep failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var subnet = options.Value.PresenceSubnet;
        if (subnet.EndsWith("/24") && IPAddress.TryParse(subnet[..^3], out var baseIp))
        {
            // Freshen the ARP cache: sleeping devices usually still answer ARP for a ping.
            var prefix = string.Join('.', baseIp.ToString().Split('.').Take(3));
            var pings = Enumerable.Range(1, 254).Select(async i =>
            {
                try
                {
                    using var ping = new Ping();
                    await ping.SendPingAsync($"{prefix}.{i}", TimeSpan.FromMilliseconds(800), cancellationToken: ct);
                }
                catch (Exception) { /* unreachable hosts are the norm */ }
            });
            await Task.WhenAll(pings);
        }

        var now = DateTimeOffset.Now;
        foreach (var mac in await ReadArpMacsAsync(ct))
            _lastSeen[mac] = now;
    }

    private static async Task<IEnumerable<string>> ReadArpMacsAsync(CancellationToken ct)
    {
        var macs = new List<string>();
        if (File.Exists("/proc/net/arp"))
        {
            foreach (var line in (await File.ReadAllLinesAsync("/proc/net/arp", ct)).Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // ip, hw-type, flags, mac, mask, device — flags 0x2 = complete entry
                if (parts.Length >= 4 && parts[2] != "0x0" && parts[3] != "00:00:00:00:00:00")
                    macs.Add(Normalize(parts[3]));
            }
            return macs;
        }

        // Windows dev fallback: parse `arp -a`.
        using var process = Process.Start(new ProcessStartInfo("arp", "-a") { RedirectStandardOutput = true })!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(output, @"\b([0-9a-fA-F]{2}[-:]){5}[0-9a-fA-F]{2}\b"))
        {
            macs.Add(Normalize(m.Value));
        }
        return macs;
    }

    private static string Normalize(string mac) => mac.Replace('-', ':').ToLowerInvariant();
}
