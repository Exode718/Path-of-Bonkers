<#
.SYNOPSIS
    Build the Wraeclast release payload from the installed plugin and zip it.

.DESCRIPTION
    Reads mod/manifest.json, copies the loader and the built plugin out of the
    test install (lab/megabonk-mod) into mod/payload, then packs
    installer/, manifest.json, README.md and payload/ into
    lab/dist/WraeclastConversion-v<version>.zip.

    Build the plugin and prepare the runtime assets first:
        dotnet build mod/Wraeclast -c Release
        powershell -File mod/tools/prepare-data.ps1

.EXAMPLE
    powershell -File mod/tools/make-release.ps1
    powershell -File mod/tools/make-release.ps1 -SkipZip
    powershell -File mod/tools/make-release.ps1 -Out D:\builds\wraeclast.zip
#>
[CmdletBinding()]
param(
    [string]$Out,
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'

$ModRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Split-Path -Parent $ModRoot
$ManifestPath = Join-Path $ModRoot 'manifest.json'
$GameCopy = Join-Path $RepoRoot 'lab\megabonk-mod'
$PluginSource = Join-Path $GameCopy 'BepInEx\plugins\Wraeclast'
$Payload = Join-Path $ModRoot 'payload'

if (-not (Test-Path $ManifestPath)) { throw "Missing manifest: $ManifestPath" }
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$version = $manifest.version

$required = @(
    (Join-Path $PluginSource 'Wraeclast.dll'),
    (Join-Path $PluginSource 'assets'),
    (Join-Path $GameCopy 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'),
    (Join-Path $GameCopy 'dotnet\coreclr.dll'),
    (Join-Path $GameCopy 'winhttp.dll'),
    (Join-Path $GameCopy 'doorstop_config.ini'),
    (Join-Path $GameCopy '.doorstop_version')
)
foreach ($path in $required) {
    if (-not (Test-Path $path)) {
        throw "Missing build input: $path (build the plugin and run prepare-data first)"
    }
}

# Rebuild the payload from scratch: the installer expects payload/ and
# manifest.json beside it, exactly this layout.
if (Test-Path $Payload) { Remove-Item $Payload -Recurse -Force }
$payloadPlugin = Join-Path $Payload 'BepInEx\plugins\Wraeclast'
New-Item -ItemType Directory -Force -Path (Join-Path $Payload 'BepInEx\core'), $payloadPlugin, (Join-Path $Payload 'dotnet') | Out-Null
Copy-Item (Join-Path $GameCopy 'BepInEx\core\*') (Join-Path $Payload 'BepInEx\core') -Recurse -Force
Copy-Item (Join-Path $GameCopy 'dotnet\*') (Join-Path $Payload 'dotnet') -Recurse -Force
Copy-Item (Join-Path $GameCopy 'winhttp.dll') $Payload -Force
Copy-Item (Join-Path $GameCopy 'doorstop_config.ini') $Payload -Force
Copy-Item (Join-Path $GameCopy '.doorstop_version') $Payload -Force
Copy-Item (Join-Path $PluginSource '*') $payloadPlugin -Recurse -Force

$dll = Get-Item (Join-Path $payloadPlugin 'Wraeclast.dll')
$assets = Join-Path $payloadPlugin 'assets'
$assetCount = (Get-ChildItem $assets -Recurse -File | Measure-Object).Count
Write-Host "payload: $Payload (Wraeclast.dll $($dll.Length) bytes, $assetCount asset file(s))"

if ($SkipZip) {
    Write-Host 'zip skipped (-SkipZip)'
    return
}

$dist = Join-Path $RepoRoot 'lab\dist'
$folder = "WraeclastConversion-v$version"
# Stage in a disposable folder and write the clean folder name into the archive
# below, so a previous stage kept open by an explorer/cmd window cannot force a
# suffixed root inside the zip.
$stageFolder = Join-Path $dist (".stage-" + (Get-Date -Format 'HHmmss'))
if (Test-Path $stageFolder) { Remove-Item $stageFolder -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageFolder | Out-Null
Copy-Item (Join-Path $ModRoot 'installer') $stageFolder -Recurse -Force
Copy-Item $ManifestPath $stageFolder -Force
# The repository no longer carries any README (the public docs were removed).
# The archive still needs a short install note, generated only in the stage.
$releaseReadme = Join-Path $stageFolder 'README.md'
if (Test-Path (Join-Path $ModRoot 'README.md')) {
    Copy-Item (Join-Path $ModRoot 'README.md') $releaseReadme -Force
}
else {
    @"
Wraeclast Conversion $version - private build

Unzip anywhere outside the game folder, run install.cmd, launch Megabonk.
Update: uninstall.cmd then install.cmd.
Requires Megabonk 2023.2.22f1 on Windows x64.
Private: ships Path of Exile-derived art. Do not redistribute.
"@ | Set-Content -LiteralPath $releaseReadme -Encoding UTF8
}
Copy-Item $Payload (Join-Path $stageFolder 'payload') -Recurse -Force

if (-not $Out) { $Out = Join-Path $dist "$folder.zip" }
if (Test-Path $Out) { Remove-Item $Out -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($Out, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $rootLength = $stageFolder.Length + 1
    foreach ($file in Get-ChildItem $stageFolder -Recurse -File) {
        $relative = $file.FullName.Substring($rootLength).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $file.FullName, "$folder/$relative",
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zip.Dispose()
    Remove-Item $stageFolder -Recurse -Force -ErrorAction SilentlyContinue
}

$size = [math]::Round((Get-Item $Out).Length / 1MB, 1)
Write-Host "release: $Out ($size MB, version $version)"
