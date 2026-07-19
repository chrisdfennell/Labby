# Regenerates Data/oui.tsv from the live IEEE OUI (MA-L) registry.
# The app loads this file as an embedded resource for offline MAC-vendor lookups
# (see Services/OuiLookup.cs). Run occasionally to pick up newly-assigned vendors:
#
#   pwsh tools/update-oui.ps1
#
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'Resources/oui.tsv'
$url  = 'https://standards-oui.ieee.org/oui/oui.csv'

Write-Host "Downloading $url ..."
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::All
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(120)
try {
    $bytes = $client.GetByteArrayAsync($url).GetAwaiter().GetResult()
} finally {
    $client.Dispose()
}

$tmp = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllBytes($tmp, $bytes)
try {
    $rows = Import-Csv -Path $tmp
    $sb = [System.Text.StringBuilder]::new()
    $count = 0
    foreach ($r in $rows) {
        $a = $r.Assignment
        if ([string]::IsNullOrWhiteSpace($a)) { continue }
        $a = $a.Trim().ToLower()
        if ($a.Length -ne 6) { continue }
        $name = $r.'Organization Name'
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $name = ($name -replace "[`t`r`n]", ' ').Trim()
        [void]$sb.Append($a).Append("`t").Append($name).Append("`n")
        $count++
    }
    [System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
} finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}

Write-Host "Wrote $count vendors to $out"
