using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// LAN scanner behind the Devices page and the dashboard "who's home" card. Every
/// few minutes it ping-sweeps the configured /24 to freshen the ARP table, then
/// records every host it finds — IP, MAC, reverse-DNS hostname and MAC vendor.
/// Discovered hosts linger after they go quiet (so you can see what was recently
/// around); "online" means the MAC was seen within <see cref="OnlineGrace"/>.
///
/// Friendly names and the "monitored" flag come from <see cref="DeviceStore"/>
/// (edited on the Devices page), not config. Monitored devices drive presence and
/// get an alert when they go offline or come back.
/// </summary>
public sealed partial class LanScanner(
    IOptions<NetworkOptions> options,
    DeviceStore devices,
    AlertNotifier alerts,
    ILogger<LanScanner> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OnlineGrace = TimeSpan.FromMinutes(10);

    // Keyed by normalized MAC — the stable identity of a device across IP changes.
    private readonly ConcurrentDictionary<string, Tracked> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, DeviceStore.DeviceLabel> _labels =
        new Dictionary<string, DeviceStore.DeviceLabel>();
    // Last known online state of each monitored MAC, so alerts fire on transitions only.
    private readonly ConcurrentDictionary<string, bool> _presenceState = new(StringComparer.OrdinalIgnoreCase);
    private int _scanning; // 0/1 guard so the manual "Scan now" and the timer don't overlap

    public DateTimeOffset? LastScan { get; private set; }
    public bool IsScanning => Volatile.Read(ref _scanning) == 1;

    public sealed record Host(
        string Mac,
        string Ip,
        string? Hostname,
        string? Name,
        bool Monitored,
        string Vendor,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen,
        bool Online);

    public sealed record PresenceEntry(string Mac, string Name, bool Home, DateTimeOffset? LastSeen);

    private sealed class Tracked
    {
        public required string Mac { get; init; }
        public string Ip = "";
        public string? Hostname;
        public DateTimeOffset FirstSeen;
        public DateTimeOffset LastSeen;
    }

    /// <summary>Discovered hosts: monitored first, then online, then by IP.</summary>
    public IReadOnlyList<Host> Hosts
    {
        get
        {
            var now = DateTimeOffset.Now;
            return _hosts.Values
                .Select(h =>
                {
                    _labels.TryGetValue(h.Mac, out var label);
                    return new Host(
                        h.Mac,
                        h.Ip,
                        h.Hostname,
                        label?.Name,
                        label?.Monitored ?? false,
                        OuiLookup.Vendor(h.Mac),
                        h.FirstSeen,
                        h.LastSeen,
                        now - h.LastSeen < OnlineGrace);
                })
                .OrderByDescending(h => h.Monitored)
                .ThenByDescending(h => h.Online)
                .ThenBy(h => IpSortKey(h.Ip))
                .ToList();
        }
    }

    /// <summary>Monitored devices with home/away status, for the dashboard "who's home" card.</summary>
    public IReadOnlyList<PresenceEntry> Presence
    {
        get
        {
            var now = DateTimeOffset.Now;
            return _labels.Values
                .Where(l => l.Monitored)
                .Select(l =>
                {
                    var seen = _hosts.TryGetValue(l.Mac, out var h) ? h.LastSeen : (DateTimeOffset?)null;
                    var name = string.IsNullOrWhiteSpace(l.Name) ? l.Mac : l.Name!;
                    return new PresenceEntry(l.Mac, name, seen is { } s && now - s < OnlineGrace, seen);
                })
                .OrderBy(p => p.Name)
                .ToList();
        }
    }

    public bool AnyMonitored => _labels.Values.Any(l => l.Monitored);

    /// <summary>Reloads labels from the store — call after the page edits a device so the UI updates at once.</summary>
    public async Task RefreshLabelsAsync(CancellationToken ct = default) =>
        _labels = await devices.GetAllAsync(ct);

    /// <summary>Runs a sweep on demand (the page's "Scan now" button). Returns an error message, or null on success.</summary>
    public async Task<string?> TriggerAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) == 1)
            return "A scan is already running.";
        try
        {
            await SweepAsync(ct);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Manual LAN scan failed");
            return ex.Message;
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ScanEnabled)
        {
            logger.LogInformation("LAN scanner disabled (Network:ScanEnabled=false)");
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            if (Interlocked.CompareExchange(ref _scanning, 1, 0) == 1)
                continue; // a manual scan is in flight; skip this tick
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
                logger.LogWarning(ex, "LAN scan failed");
            }
            finally
            {
                Volatile.Write(ref _scanning, 0);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        _labels = await devices.GetAllAsync(ct);

        var subnet = options.Value.PresenceSubnet;
        if (!(subnet.EndsWith("/24") && IPAddress.TryParse(subnet[..^3], out var baseIp)))
        {
            logger.LogWarning("LAN scan skipped: PresenceSubnet '{Subnet}' is not a supported /24", subnet);
            return;
        }

        // Freshen the ARP cache: even sleeping devices usually answer ARP for a ping.
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

        var now = DateTimeOffset.Now;
        foreach (var (ip, mac) in await ReadArpAsync(ct))
        {
            var entry = _hosts.GetOrAdd(mac, m => new Tracked { Mac = m, FirstSeen = now });
            entry.Ip = ip;
            entry.LastSeen = now;
        }

        // Resolve hostnames for freshly-seen hosts we don't have a name for yet.
        await Task.WhenAll(_hosts.Values
            .Where(h => h.Hostname is null && h.LastSeen == now)
            .Select(async h => h.Hostname = await ResolveHostnameAsync(h.Ip, ct)));

        await RaisePresenceAlertsAsync(now, ct);

        LastScan = now;
        logger.LogDebug("LAN scan complete: {Count} hosts known", _hosts.Count);
    }

    /// <summary>Alerts when a monitored device crosses the online/offline line. Transitions only — first sighting stays quiet.</summary>
    private async Task RaisePresenceAlertsAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var label in _labels.Values.Where(l => l.Monitored))
        {
            var online = _hosts.TryGetValue(label.Mac, out var h) && now - h.LastSeen < OnlineGrace;
            var name = string.IsNullOrWhiteSpace(label.Name) ? label.Mac : label.Name!;

            if (_presenceState.TryGetValue(label.Mac, out var was) && was != online)
            {
                var message = online ? $"🏠 {name} is home" : $"🚶 {name} left";
                await alerts.SendAsync(message, ct);
            }
            _presenceState[label.Mac] = online;
        }

        // Forget state for devices that are no longer monitored, so re-adding them starts clean.
        foreach (var mac in _presenceState.Keys)
            if (!(_labels.TryGetValue(mac, out var l) && l.Monitored))
                _presenceState.TryRemove(mac, out _);
    }

    private static async Task<IReadOnlyList<(string Ip, string Mac)>> ReadArpAsync(CancellationToken ct)
    {
        var result = new List<(string, string)>();

        if (File.Exists("/proc/net/arp"))
        {
            foreach (var line in (await File.ReadAllLinesAsync("/proc/net/arp", ct)).Skip(1))
            {
                // ip, hw-type, flags, mac, mask, device — flags 0x2 = complete entry
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4 && p[2] != "0x0" && IsRealDevice(p[3]))
                    result.Add((p[0], Normalize(p[3])));
            }
            return result;
        }

        // Windows dev fallback: parse `arp -a` ("  <ip>   <mac>   <type>").
        using var process = Process.Start(new ProcessStartInfo("arp", "-a") { RedirectStandardOutput = true })!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        foreach (var line in output.Split('\n'))
        {
            var m = ArpLine().Match(line);
            if (m.Success && IsRealDevice(m.Groups[2].Value))
                result.Add((m.Groups[1].Value, Normalize(m.Groups[2].Value)));
        }
        return result;
    }

    private static async Task<string?> ResolveHostnameAsync(string ip, CancellationToken ct)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip, ct);
            var name = entry.HostName;
            if (string.IsNullOrWhiteSpace(name) || name == ip)
                return null;
            // Drop the DNS suffix — "nas.lan" → "nas".
            return name.Split('.')[0];
        }
        catch (Exception)
        {
            return null; // no PTR record is the common case
        }
    }

    /// <summary>Skip broadcast and multicast MACs — they aren't real hosts.</summary>
    private static bool IsRealDevice(string mac)
    {
        var m = Normalize(mac);
        return m != "00:00:00:00:00:00"
            && m != "ff:ff:ff:ff:ff:ff"
            && !m.StartsWith("01:00:5e", StringComparison.Ordinal)  // IPv4 multicast
            && !m.StartsWith("33:33", StringComparison.Ordinal);     // IPv6 multicast
    }

    private static string Normalize(string mac) => mac.Replace('-', ':').ToLowerInvariant();

    /// <summary>Sortable key so ".2" comes before ".10" instead of lexicographically.</summary>
    private static long IpSortKey(string ip)
    {
        long key = 0;
        foreach (var part in ip.Split('.'))
            key = key << 8 | (byte.TryParse(part, out var b) ? b : 0L);
        return key;
    }

    [GeneratedRegex(@"(\d{1,3}(?:\.\d{1,3}){3})\s+(([0-9a-fA-F]{2}[-:]){5}[0-9a-fA-F]{2})")]
    private static partial Regex ArpLine();
}
