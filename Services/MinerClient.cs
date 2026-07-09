using System.Text.Json;
using Labby.Options;
using Labby.Models;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Polls NMMiner-style devices (GET /api/system/info) for the dashboard's Miners
/// section. Devices are tiny ESP32s, so results are cached briefly and each one
/// fails independently.
/// </summary>
public sealed class MinerClient(IHttpClientFactory httpFactory, IOptions<DashboardOptions> options, ILogger<MinerClient> logger)
{
    public const string HttpClientName = "miners";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<MinerStatus>? _cached;
    private DateTimeOffset _cachedAt;

    public bool AnyConfigured => options.Value.Miners.Count > 0;

    public async Task<IReadOnlyList<MinerStatus>> GetStatusAsync(CancellationToken ct = default)
    {
        if (_cached is { } fresh && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return fresh;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is { } stillFresh && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return stillFresh;
            _cached = await Task.WhenAll(options.Value.Miners.Select(m => FetchAsync(m, ct)));
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<MinerStatus> FetchAsync(MinerEndpoint miner, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            using var doc = JsonDocument.Parse(
                await http.GetStringAsync($"{miner.Url.TrimEnd('/')}/api/system/info", ct));
            var root = doc.RootElement;
            var identity = root.TryGetProperty("identity", out var i) ? i : default;
            var stats = root.TryGetProperty("miner", out var m) ? m : default;
            var stratum = root.TryGetProperty("stratum", out var s) ? s : default;

            return new MinerStatus
            {
                Name = miner.Name,
                Url = miner.Url,
                Host = Str(identity, "hostName"),
                HashRateMhs = Num(stats, "hashRate"),
                Accepted = (long)(Num(stats, "sAccepted") ?? 0),
                Rejected = (long)(Num(stats, "sRejected") ?? 0),
                BestDiffSession = Str(stats, "bestDiffSession")?.Trim(),
                BestDiffEver = Str(stats, "bestDiffEver")?.Trim(),
                Uptime = Num(stats, "uptimeSeconds") is { } up ? TimeSpan.FromSeconds(up) : null,
                RssiDbm = Num(identity, "rssi"),
                Pool = Str(stratum, "url"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Miner {Name} fetch failed", miner.Name);
            return new MinerStatus
            {
                Name = miner.Name,
                Url = miner.Url,
                Error = ex is TaskCanceledException ? "timed out" : ex.GetBaseException().Message,
            };
        }
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? Num(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;
}
