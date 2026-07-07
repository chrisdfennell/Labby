using System.Text.Json;
using Labby.Models;

namespace Labby.Services;

/// <summary>
/// QNAP File Station API (cgi-bin/filemanager/utilRequest.cgi). Reuses the
/// management session from <see cref="QnapClient"/>.
/// </summary>
public sealed class QnapFileStation(IHttpClientFactory httpFactory, QnapClient qnap)
{
    public bool IsConfigured => qnap.IsConfigured;

    /// <summary>Lists the top-level shared folders.</summary>
    public async Task<IReadOnlyList<FileEntry>> GetSharesAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(sid =>
            $"cgi-bin/filemanager/utilRequest.cgi?func=get_tree&is_iso=0&node=share_root&sid={sid}", ct);

        var shares = new List<FileEntry>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in doc.RootElement.EnumerateArray())
            {
                var name = node.TryGetProperty("text", out var text) ? text.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                    shares.Add(new FileEntry { Name = name, IsFolder = true });
            }
        }
        return shares;
    }

    public async Task<IReadOnlyList<FileEntry>> ListFolderAsync(string path, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(sid =>
            $"cgi-bin/filemanager/utilRequest.cgi?func=get_list&is_iso=0&list_mode=all&limit=1000&start=0&sort=filename&dir=ASC" +
            $"&path={Uri.EscapeDataString(path)}&sid={sid}", ct);

        var entries = new List<FileEntry>();
        if (doc.RootElement.TryGetProperty("datas", out var datas) && datas.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in datas.EnumerateArray())
            {
                var name = item.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                if (string.IsNullOrEmpty(name) || name is "." or "..")
                    continue;
                entries.Add(new FileEntry
                {
                    Name = name,
                    IsFolder = GetNumber(item, "isfolder") == 1,
                    SizeBytes = (long)GetNumber(item, "filesize"),
                    Modified = GetNumber(item, "epochmt") is > 0 and var epoch
                        ? DateTimeOffset.FromUnixTimeSeconds((long)epoch).ToLocalTime()
                        : null,
                });
            }
        }
        return entries
            .OrderByDescending(e => e.IsFolder)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Streams a file from the NAS; used by the download endpoint.</summary>
    public async Task<HttpResponseMessage> OpenDownloadAsync(string folderPath, string fileName, CancellationToken ct = default)
    {
        var sid = await qnap.GetSidAsync(ct);
        var url = $"cgi-bin/filemanager/utilRequest.cgi?func=download&isfolder=0&source_total=1" +
                  $"&source_path={Uri.EscapeDataString(folderPath)}&source_file={Uri.EscapeDataString(fileName)}&sid={sid}";
        var http = httpFactory.CreateClient(QnapClient.HttpClientName);
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task<JsonDocument> GetJsonAsync(Func<string, string> buildUrl, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(QnapClient.HttpClientName);
        var body = await http.GetStringAsync(buildUrl(await qnap.GetSidAsync(ct)), ct);

        // An expired sid comes back as {"status": 3} (or an XML login page) instead of the listing.
        if (!LooksAuthorized(body))
        {
            qnap.InvalidateSession();
            body = await http.GetStringAsync(buildUrl(await qnap.GetSidAsync(ct)), ct);
        }
        return JsonDocument.Parse(body);
    }

    private static bool LooksAuthorized(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('<'))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return !(doc.RootElement.ValueKind == JsonValueKind.Object
                     && doc.RootElement.TryGetProperty("status", out var status)
                     && status.ValueKind == JsonValueKind.Number
                     && status.GetInt32() is 3 or 2);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>File Station returns numbers as either JSON numbers or strings depending on QTS version.</summary>
    private static double GetNumber(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }
}
