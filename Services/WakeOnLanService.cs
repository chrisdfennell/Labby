using System.Net;
using System.Net.Sockets;

namespace Labby.Services;

/// <summary>Sends Wake-on-LAN magic packets (UDP broadcast, port 9).</summary>
public sealed class WakeOnLanService(ILogger<WakeOnLanService> logger)
{
    public async Task<bool> WakeAsync(string mac, CancellationToken ct = default)
    {
        try
        {
            var macBytes = ParseMac(mac);
            var packet = new byte[102];
            Array.Fill(packet, (byte)0xFF, 0, 6);
            for (var i = 0; i < 16; i++)
                macBytes.CopyTo(packet, 6 + i * 6);

            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            await udp.SendAsync(packet, new IPEndPoint(IPAddress.Broadcast, 9), ct);
            logger.LogInformation("Sent Wake-on-LAN packet to {Mac}", mac);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Wake-on-LAN failed for {Mac}", mac);
            return false;
        }
    }

    private static byte[] ParseMac(string mac)
    {
        var parts = mac.Split([':', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6)
            throw new FormatException($"MAC address must have six octets: '{mac}'");
        return [.. parts.Select(p => Convert.ToByte(p, 16))];
    }
}
