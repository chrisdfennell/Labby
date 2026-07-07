namespace Labby.Options;

public sealed class QnapOptions
{
    public const string SectionName = "Qnap";

    /// <summary>Hostname or IP of the NAS, e.g. "192.168.1.50" or "nas.local".</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8080;
    public bool UseHttps { get; set; }

    /// <summary>QTS self-signed certs are the norm on a LAN, so accept them by default.</summary>
    public bool IgnoreCertificateErrors { get; set; } = true;

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Username);

    public string BaseUrl => $"{(UseHttps ? "https" : "http")}://{Host}:{Port}/";
}

public sealed class AmbientWeatherOptions
{
    public const string SectionName = "AmbientWeather";

    public string ApiKey { get; set; } = "";
    public string ApplicationKey { get; set; } = "";

    /// <summary>Optional. When set, picks this station if the account has more than one.</summary>
    public string? DeviceMac { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApplicationKey);
}

public sealed class KontainrOptions
{
    public const string SectionName = "Kontainr";

    /// <summary>Browser-reachable URL of the Kontainr instance (it is embedded in an iframe, so this must resolve from the client, not the server).</summary>
    public string Url { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);
}

public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    public List<MonitoredService> Services { get; set; } = [];
}

public sealed class MonitoredService
{
    public string Name { get; set; } = "";

    /// <summary>URL to open when the tile is clicked.</summary>
    public string Url { get; set; } = "";

    /// <summary>Optional URL to probe for health; defaults to <see cref="Url"/>.</summary>
    public string? HealthUrl { get; set; }

    /// <summary>Emoji or short text shown on the tile.</summary>
    public string Icon { get; set; } = "🧩";

    public string? Description { get; set; }
}
