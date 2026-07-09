using System.Text.Json;
using Labby.Models;

namespace Labby.Services;

/// <summary>
/// QNAP File Station API (cgi-bin/filemanager/utilRequest.cgi). Reuses the
/// management session from <see cref="QnapClient"/>.
/// </summary>
public sealed class QnapFileStation(IHttpClientFactory httpFactory, QnapClient qnap)
{
    public const string UploadHttpClientName = "qnap-upload";

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

    /// <summary>Uploads a file into <paramref name="folderPath"/>, overwriting an existing one of the same name.</summary>
    public async Task UploadAsync(string folderPath, string fileName, Stream content, CancellationToken ct = default)
    {
        var sid = await qnap.GetSidAsync(ct);
        var url = $"cgi-bin/filemanager/utilRequest.cgi?func=upload&type=standard&overwrite=1" +
                  $"&dest_path={Uri.EscapeDataString(folderPath)}&progress={Uri.EscapeDataString(fileName)}&sid={sid}";

        // QTS's CGI multipart parser is fussy: it needs a Content-Length (no chunked
        // encoding, so no non-seekable browser streams), quoted disposition values
        // without RFC 5987 filename*, and Content-Disposition as the part's first
        // header — .NET's MultipartFormDataContent violates all three, and QTS then
        // reports success while writing nothing. So spool the whole request body,
        // envelope included, to a temp file and send it verbatim.
        var boundary = "----labby" + Guid.NewGuid().ToString("N");
        var safeName = fileName.Replace("\"", "_");
        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var spool = File.Create(tempPath))
            {
                var prologue = System.Text.Encoding.UTF8.GetBytes(
                    $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{safeName}\"\r\n" +
                    "Content-Type: application/octet-stream\r\n\r\n");
                await spool.WriteAsync(prologue, ct);
                await content.CopyToAsync(spool, ct);
                await spool.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n"), ct);
            }

            await using var body = File.OpenRead(tempPath);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StreamContent(body) };
            request.Content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

            var http = httpFactory.CreateClient(UploadHttpClientName);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            EnsureFileStationSuccess(await response.Content.ReadAsStringAsync(ct), "upload");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    public async Task CreateFolderAsync(string parentPath, string name, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(sid =>
            $"cgi-bin/filemanager/utilRequest.cgi?func=createdir&dest_path={Uri.EscapeDataString(parentPath)}" +
            $"&dest_folder={Uri.EscapeDataString(name)}&sid={sid}", ct);
        EnsureFileStationSuccess(doc.RootElement, "createdir");
    }

    private static void EnsureFileStationSuccess(string body, string operation)
    {
        using var doc = JsonDocument.Parse(body);
        EnsureFileStationSuccess(doc.RootElement, operation);
    }

    // File Station signals success as {"status": 1}; anything else is an error code
    // (2/3 auth, 4 permission, 33 name exists, ...).
    private static void EnsureFileStationSuccess(JsonElement root, string operation)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Number
            && status.GetInt32() is var code && code != 1)
        {
            throw new InvalidOperationException(code switch
            {
                4 => $"File Station refused the {operation} (permission denied — does the Labby account have write access here?)",
                33 => "That name already exists here.",
                _ => $"File Station {operation} failed with status {code}.",
            });
        }
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
