using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Labby.Models;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Client for the QNAP QTS management API. Handles authLogin.cgi session (sid)
/// acquisition and re-login when the session expires. QTS XML responses vary a
/// fair bit between firmware versions, so all parsing is by descendant element
/// name and tolerant of missing fields.
/// </summary>
public sealed class QnapClient(IHttpClientFactory httpFactory, IOptions<QnapOptions> options, ILogger<QnapClient> logger)
{
    public const string HttpClientName = "qnap";

    private readonly QnapOptions _options = options.Value;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _sid;

    public bool IsConfigured => _options.IsConfigured;
    public string BaseUrl => _options.BaseUrl;

    public async Task<string> GetSidAsync(CancellationToken ct = default)
    {
        if (_sid is { } cached)
            return cached;

        await _loginLock.WaitAsync(ct);
        try
        {
            if (_sid is { } raced)
                return raced;

            if (!IsConfigured)
                throw new InvalidOperationException("QNAP is not configured. Set Qnap:Host, Qnap:Username and Qnap:Password in appsettings.json or user secrets.");

            var http = httpFactory.CreateClient(HttpClientName);
            var encodedPwd = Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.Password));
            var url = $"cgi-bin/authLogin.cgi?user={Uri.EscapeDataString(_options.Username)}&pwd={Uri.EscapeDataString(encodedPwd)}";
            var doc = XDocument.Parse(await http.GetStringAsync(url, ct));

            var authPassed = doc.Descendants("authPassed").FirstOrDefault()?.Value.Trim();
            var sid = doc.Descendants("authSid").FirstOrDefault()?.Value.Trim();
            if (authPassed != "1" || string.IsNullOrEmpty(sid))
                throw new InvalidOperationException("QNAP login was rejected — check the username/password (note: accounts with 2FA enabled cannot log in via this API).");

            logger.LogInformation("Logged in to QNAP at {Host}", _options.Host);
            _sid = sid;
            return sid;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public void InvalidateSession() => _sid = null;

    /// <summary>Runs a GET that needs a sid, retrying once with a fresh login if the session expired.</summary>
    private async Task<XDocument> GetXmlAsync(Func<string, string> buildUrl, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        var doc = XDocument.Parse(await http.GetStringAsync(buildUrl(await GetSidAsync(ct)), ct));

        if (doc.Descendants("authPassed").FirstOrDefault()?.Value.Trim() == "0")
        {
            InvalidateSession();
            doc = XDocument.Parse(await http.GetStringAsync(buildUrl(await GetSidAsync(ct)), ct));
        }
        return doc;
    }

    public async Task<NasSystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var doc = await GetXmlAsync(sid => $"cgi-bin/management/manaRequest.cgi?subfunc=sysinfo&sid={sid}", ct);

        TimeSpan? uptime = null;
        var days = Num(doc, "uptime_day");
        var hours = Num(doc, "uptime_hour");
        var mins = Num(doc, "uptime_min");
        if (days is not null || hours is not null || mins is not null)
            uptime = new TimeSpan((int)(days ?? 0), (int)(hours ?? 0), (int)(mins ?? 0), 0);

        return new NasSystemInfo
        {
            ModelName = Str(doc, "displayModelName") ?? Str(doc, "modelName"),
            FirmwareVersion = Str(doc, "version"),
            ServerName = Str(doc, "hostname") ?? Str(doc, "server_name"),
            CpuUsagePercent = Num(doc, "cpu_usage"),
            TotalMemoryMb = Num(doc, "total_memory"),
            FreeMemoryMb = Num(doc, "free_memory"),
            Uptime = uptime,
            CpuTempC = Num(doc, "cpu_tempc"),
            SystemTempC = Num(doc, "sys_tempc"),
        };
    }

    public async Task<IReadOnlyList<NasVolume>> GetVolumesAsync(CancellationToken ct = default)
    {
        var doc = await GetXmlAsync(sid => $"cgi-bin/management/chartReq.cgi?chart_func=disk_usage&disk_select=all&include=all&sid={sid}", ct);

        var volumes = new List<NasVolume>();
        foreach (var vol in doc.Descendants("volume"))
        {
            var label = Str(vol, "volumeLabel") ?? Str(vol, "volumeValue") ?? $"Volume {volumes.Count + 1}";
            var total = (long)(Num(vol, "total_size") ?? 0);
            var free = (long)(Num(vol, "free_size") ?? 0);
            if (total > 0)
                volumes.Add(new NasVolume { Label = label, TotalBytes = total, FreeBytes = free });
        }
        return volumes;
    }

    public async Task<IReadOnlyList<NasDisk>> GetDisksAsync(CancellationToken ct = default)
    {
        try
        {
            var doc = await GetXmlAsync(sid => $"cgi-bin/disk/qsmart.cgi?func=all_hd_data&sid={sid}", ct);
            var disks = new List<NasDisk>();
            foreach (var entry in doc.Descendants("entry"))
            {
                var model = Str(entry, "Model");
                if (string.IsNullOrWhiteSpace(model))
                    continue;
                disks.Add(new NasDisk
                {
                    Slot = Str(entry, "HDNo") ?? Str(entry, "Disk_Alias") ?? "",
                    Model = model.Trim(),
                    Capacity = Str(entry, "Capacity")?.Trim() ?? "",
                    TempC = Num(entry, "oC"),
                    Health = Str(entry, "Health")?.Trim() ?? "",
                });
            }
            return disks;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // qsmart.cgi is not present/permitted on every QTS version; the storage page works without it.
            logger.LogDebug(ex, "Disk SMART query failed; continuing without disk data");
            return [];
        }
    }

    private static string? Str(XContainer scope, string name)
    {
        var value = scope.Descendants(name).FirstOrDefault()?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>QTS mixes plain numbers with strings like "5 %" or "3,894 MB"; strip to the numeric part.</summary>
    private static double? Num(XContainer scope, string name)
    {
        var raw = Str(scope, name);
        if (raw is null)
            return null;
        var cleaned = new string(raw.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
