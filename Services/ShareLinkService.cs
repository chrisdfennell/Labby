using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Labby.Options;
using Microsoft.Extensions.Options;

namespace Labby.Services;

/// <summary>
/// Creates signed, expiring download links for NAS files. The link points at
/// Labby itself (/share/{token}) and works without logging in — the HMAC
/// signature is the authorization. The signing key persists in the data
/// directory so links survive restarts and rebuilds.
/// </summary>
public sealed class ShareLinkService
{
    private readonly byte[] _key;

    public ShareLinkService(IOptions<HistoryOptions> options, IHostEnvironment env, ILogger<ShareLinkService> logger)
    {
        var keyPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(options.Value.DatabasePath, env.ContentRootPath))!,
            "sharelink.key");
        try
        {
            if (File.Exists(keyPath))
            {
                _key = File.ReadAllBytes(keyPath);
            }
            else
            {
                _key = RandomNumberGenerator.GetBytes(32);
                Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
                File.WriteAllBytes(keyPath, _key);
            }
        }
        catch (Exception ex)
        {
            // No persistent key -> links die with the process; still functional.
            logger.LogWarning(ex, "Could not persist share-link key; links will expire on restart");
            _key = RandomNumberGenerator.GetBytes(32);
        }
    }

    private sealed record Payload(string P, string N, long E);

    public string CreateToken(string folderPath, string fileName, TimeSpan validity)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(folderPath, fileName, DateTimeOffset.UtcNow.Add(validity).ToUnixTimeSeconds()));
        return $"{Base64Url(payload)}.{Base64Url(HMACSHA256.HashData(_key, payload))}";
    }

    /// <summary>Returns the folder/file for a valid unexpired token, else null.</summary>
    public (string FolderPath, string FileName)? Validate(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2)
                return null;
            var payloadBytes = FromBase64Url(parts[0]);
            var expected = HMACSHA256.HashData(_key, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[1])))
                return null;
            var payload = JsonSerializer.Deserialize<Payload>(payloadBytes);
            if (payload is null || DateTimeOffset.FromUnixTimeSeconds(payload.E) < DateTimeOffset.UtcNow)
                return null;
            return (payload.P, payload.N);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
