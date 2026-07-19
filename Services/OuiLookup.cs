using System.Globalization;
using System.Reflection;

namespace Labby.Services;

/// <summary>
/// Maps a MAC address to its manufacturer using the IEEE OUI registry (the MA-L
/// block), bundled as an embedded resource (<c>Resources/oui.tsv</c>) so the lookup
/// works fully offline. ~40k vendors, loaded into memory once on first use.
/// Randomized/private MACs (modern phones) are detected from the
/// locally-administered bit rather than a lookup. Regenerate the data file with
/// <c>tools/update-oui.ps1</c>.
/// </summary>
public static class OuiLookup
{
    // A few friendly names that read better than the raw registry entry. Checked
    // before the registry (and before the randomized-MAC test, so locally-
    // administered virtualization prefixes like 52:54:00 still resolve).
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.Ordinal)
    {
        ["080027"] = "VirtualBox",
        ["525400"] = "QEMU/KVM",
        ["000c29"] = "VMware",
        ["005056"] = "VMware",
        ["001c42"] = "Parallels",
    };

    // 6-hex prefix ("aabbcc", lower-case) → vendor. Lazily loaded, thread-safe.
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Registry = new(Load);

    /// <summary>Returns the manufacturer for a MAC, "Randomized" for a private/random MAC, or "Unknown".</summary>
    public static string Vendor(string mac)
    {
        if (mac.Length < 8)
            return "Unknown";

        // First three octets, colons/dashes stripped → "aabbcc".
        var prefix = mac[..8].Replace(":", "").Replace("-", "").ToLowerInvariant();
        if (prefix.Length != 6)
            return "Unknown";

        if (Overrides.TryGetValue(prefix, out var friendly))
            return friendly;
        if (Registry.Value.TryGetValue(prefix, out var vendor))
            return vendor;

        // The locally-administered bit (0x02 in the first octet) marks MACs that
        // aren't burned-in — i.e. the randomized addresses modern phones rotate.
        if (int.TryParse(prefix.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first)
            && (first & 0x02) != 0)
            return "Randomized";

        return "Unknown";
    }

    /// <summary>How many vendor prefixes are loaded (diagnostics / sanity checks).</summary>
    public static int Count => Registry.Value.Count;

    private static IReadOnlyDictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("oui.tsv", StringComparison.OrdinalIgnoreCase));
            if (name is null)
                return map;

            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var tab = line.IndexOf('\t');
                if (tab != 6) // every valid row is "<6 hex>\t<name>"
                    continue;
                map[line[..tab]] = line[(tab + 1)..];
            }
        }
        catch
        {
            // A missing or malformed resource just means every MAC reads as "Unknown".
        }
        return map;
    }
}
