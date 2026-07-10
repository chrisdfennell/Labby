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

public sealed class TerminalOptions
{
    public const string SectionName = "Terminal";

    /// <summary>Browser-reachable URL of a web terminal (iframe src, so it must resolve from the client).</summary>
    public string Url { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);
}

public sealed class OsintOptions
{
    public const string SectionName = "Osint";

    /// <summary>Browser-reachable URL of the OSINT Hub instance (iframe src, so it must resolve from the client).</summary>
    public string Url { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);
}

/// <summary>MyPersonalGit (github.com/chrisdfennell/MyPersonalGit) integration for the Git page and dashboard card.</summary>
public sealed class GitOptions
{
    public const string SectionName = "Git";

    /// <summary>Base URL of the MyPersonalGit instance. Reached from the Labby server for the API and by the browser for repo links.</summary>
    public string Url { get; set; } = "";

    /// <summary>A personal access token ("mypg_…"). Kept in the environment, never in appsettings — the repo is public.</summary>
    public string Token { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Token);
}

/// <summary>Media-stack integrations shown on the Media page. Each source is optional.</summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    public ApiEndpoint Tautulli { get; set; } = new();
    public ApiEndpoint Sonarr { get; set; } = new();
    public ApiEndpoint Radarr { get; set; } = new();
    public ApiEndpoint Overseerr { get; set; } = new();
    /// <summary>Direct Plex access for "recently added"; ApiKey holds the X-Plex-Token.</summary>
    public ApiEndpoint Plex { get; set; } = new();
    public ApiEndpoint Prowlarr { get; set; } = new();
    public CredentialEndpoint Qbittorrent { get; set; } = new();
    public CredentialEndpoint Nzbget { get; set; } = new();

    public bool AnyConfigured =>
        Tautulli.IsConfigured || Sonarr.IsConfigured || Radarr.IsConfigured
        || Overseerr.IsConfigured || Plex.IsConfigured || Qbittorrent.IsConfigured || Nzbget.IsConfigured;

    public sealed class ApiEndpoint
    {
        public string Url { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
    }

    public sealed class CredentialEndpoint
    {
        public string Url { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);
    }
}

/// <summary>Network page: ping targets and scheduled speed tests.</summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    public List<PingHost> PingHosts { get; set; } = [];

    /// <summary>Hours between speed tests; 0 disables them (they use real bandwidth).</summary>
    public double SpeedtestHours { get; set; }

    /// <summary>Alert when a speedtest download lands below this (Mbps); 0 disables.</summary>
    public double MinDownloadMbps { get; set; }

    /// <summary>Track the WAN IP (api.ipify.org, every 15 min) and alert when it changes.</summary>
    public bool WatchPublicIp { get; set; } = true;

    /// <summary>Devices for the "who's home" card, matched by MAC from the ARP table.</summary>
    public List<KnownDevice> KnownDevices { get; set; } = [];

    /// <summary>Subnet ping-swept to freshen ARP before matching (only /24 supported).</summary>
    public string PresenceSubnet { get; set; } = "192.168.1.0/24";

    public bool AnyConfigured => PingHosts.Count > 0 || SpeedtestHours > 0 || WatchPublicIp;
}

public sealed class PingHost
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
}

public sealed class KnownDevice
{
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
}

/// <summary>Scheduled database backups to a NAS share (opt-in via SharePath).</summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>File Station destination, e.g. "/Public/labby-backups". Empty disables.</summary>
    public string SharePath { get; set; } = "";

    /// <summary>Days between scheduled backups.</summary>
    public double Days { get; set; } = 7;
}

public sealed class HistoryOptions
{
    public const string SectionName = "History";

    /// <summary>SQLite file for logged weather readings. Relative paths resolve against the content root.</summary>
    public string DatabasePath { get; set; } = "data/labby.db";
}

public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>
    /// Webhook that receives service down/recovery messages. Discord and Slack URLs get
    /// their JSON shape; anything else (e.g. an ntfy topic URL) gets a plain-text POST.
    /// </summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>Pushover application token — set together with <see cref="PushoverUser"/> for push notifications.</summary>
    public string PushoverToken { get; set; } = "";

    /// <summary>Pushover user (or group) key.</summary>
    public string PushoverUser { get; set; } = "";

    /// <summary>Local hour (0-23) for the good-morning digest; -1 disables it.</summary>
    public int DigestHour { get; set; } = -1;

    /// <summary>NAS health alert when a volume passes this used-% (0 disables).</summary>
    public double VolumeFullPercent { get; set; } = 90;

    /// <summary>NAS health alert when the CPU passes this temperature (0 disables).</summary>
    public double CpuTempC { get; set; } = 85;

    public bool PushoverEnabled => !string.IsNullOrWhiteSpace(PushoverToken) && !string.IsNullOrWhiteSpace(PushoverUser);

    public bool IsEnabled => !string.IsNullOrWhiteSpace(WebhookUrl) || PushoverEnabled;
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Username { get; set; } = "labby";
    public string Password { get; set; } = "";

    /// <summary>Login is only enforced when a password is set; with none Labby stays open (trusted LAN).</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Password);
}

public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    public List<MonitoredService> Services { get; set; } = [];

    /// <summary>NMMiner-style devices shown in the dashboard's Miners section.</summary>
    public List<MinerEndpoint> Miners { get; set; } = [];

    /// <summary>Plain bookmarks shown as a strip on the dashboard — no health checks.</summary>
    public List<QuickLink> Links { get; set; } = [];
}

public sealed class QuickLink
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Icon { get; set; } = "🔗";
}

public sealed class MinerEndpoint
{
    public string Name { get; set; } = "";
    /// <summary>Base URL of the miner's web UI, e.g. "http://192.168.1.34".</summary>
    public string Url { get; set; } = "";
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

    /// <summary>Optional MAC address; when set, a Wake-on-LAN button appears while the service is down.</summary>
    public string? Mac { get; set; }
}
