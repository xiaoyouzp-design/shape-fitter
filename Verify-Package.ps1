[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$manifest = Get-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$mismatches = @()
foreach ($entry in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $packageRoot $entry.path))
    if (-not $path.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Manifest path escapes package: $($entry.path)" }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $mismatches += "Missing: $($entry.path)"; continue }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $mismatches += "Hash mismatch: $($entry.path)" }
}
if ($mismatches.Count -gt 0) { throw ($mismatches -join [Environment]::NewLine) }
Write-Output "Verified $($manifest.files.Count) listed files for $($manifest.version). Manifest itself is excluded; use the release archive SHA256 for whole-package integrity."
