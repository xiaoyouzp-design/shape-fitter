[CmdletBinding(SupportsShouldProcess=$true)]
param([Parameter(Mandatory=$true)][string]$ProjectPath)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Verify-Package.ps1')
$projectRoot = (Resolve-Path -LiteralPath $ProjectPath).Path
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'Assets') -PathType Container) -or -not (Test-Path -LiteralPath (Join-Path $projectRoot 'ProjectSettings') -PathType Container)) { throw 'ProjectPath must be an existing Unity project.' }
$assetsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'Assets'))
$target = [IO.Path]::GetFullPath((Join-Path $assetsRoot '形态建拟合器'))
$legacy = [IO.Path]::GetFullPath((Join-Path $assetsRoot 'ModelCard'))
foreach ($path in @($target, $legacy)) {
    if (-not $path.StartsWith($assetsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Plugin path escaped Assets.' }
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Plugin directory must not be a junction or symlink.' }
}
if ((Test-Path -LiteralPath $legacy) -and (Test-Path -LiteralPath $target)) { throw 'Both legacy and new plugin folders exist. Move the redundant version outside Assets before installing; neither folder was modified.' }
if ((Test-Path -LiteralPath $legacy) -and (Test-Path -LiteralPath ($target + '.meta'))) { throw 'New plugin folder has an orphan meta file. Resolve it before migration; nothing was modified.' }
if ($PSCmdlet.ShouldProcess($target, 'Back up, migrate legacy directory if present, and install packaged files')) {
    $existing = if (Test-Path -LiteralPath $legacy) { $legacy } else { $target }
    if (Test-Path -LiteralPath $existing) {
        $backup = Join-Path $projectRoot ('形态建拟合器备份/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '/Assets')
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        Copy-Item -LiteralPath $existing -Destination $backup -Recurse
        if (Test-Path -LiteralPath ($existing + '.meta')) { Copy-Item -LiteralPath ($existing + '.meta') -Destination $backup }
        Write-Output "Previous plugin backup: $backup"
        if ($existing -eq $legacy) {
            Move-Item -LiteralPath $legacy -Destination $target
            if (Test-Path -LiteralPath ($legacy + '.meta')) { Move-Item -LiteralPath ($legacy + '.meta') -Destination ($target + '.meta') }
        }
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $source = Join-Path $PSScriptRoot 'Assets/形态建拟合器'
    foreach ($entry in Get-ChildItem -LiteralPath $source -Force) { Copy-Item -LiteralPath $entry.FullName -Destination $target -Recurse -Force }
    Copy-Item -LiteralPath ($source + '.meta') -Destination ($target + '.meta') -Force
    Write-Output "Installed to $target. Existing unrelated files were retained; open Unity and wait for compilation."
}
