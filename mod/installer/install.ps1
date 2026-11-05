<#
.SYNOPSIS
    Installs the Wraeclast Conversion mod (BepInEx 6 IL2CPP + plugin) into a
    Megabonk install.

.DESCRIPTION
    Finds the game (Steam libraries, -GamePath, or a folder picker), checks the
    build against mod/manifest.json, backs up any existing loader files, then
    copies the payload. The backup is never overwritten: if one exists, run
    uninstall.ps1 first.

    Private prototype only. The payload contains Path of Exile and Megabonk
    derived assets and must not be redistributed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Megabonk" -DryRun
#>
[CmdletBinding()]
param(
    [string]$GamePath,
    [string]$Payload,
    [switch]$DryRun,
    [switch]$IgnoreBuildCheck
)

$ErrorActionPreference = 'Stop'

$ModRoot = Split-Path $PSScriptRoot -Parent
if (-not $Payload) { $Payload = Join-Path $ModRoot 'payload' }
$ManifestPath = Join-Path $ModRoot 'manifest.json'
$LoaderFiles = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')

function Write-Step($Message) { Write-Host "== $Message" -ForegroundColor Cyan }
function Write-Detail($Message) { Write-Host "   $Message" }
function Write-Good($Message) { Write-Host "   $Message" -ForegroundColor Green }
function Stop-Install($Message) {
    Write-Host "error: $Message" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- payload ----

function Test-Payload {
    $required = @(
        'BepInEx\core\BepInEx.Unity.IL2CPP.dll',
        'BepInEx\plugins\Wraeclast\Wraeclast.dll',
        'dotnet\coreclr.dll',
        'winhttp.dll',
        'doorstop_config.ini',
        '.doorstop_version'
    )
    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $Payload $_)) })
    if ($missing.Count -gt 0) {
        Stop-Install ("payload incomplete in $Payload - missing: " + ($missing -join ', ') +
            ". See mod/installer/README.md.")
    }
}

# ------------------------------------------------------------------- game ----

function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        try {
            $steamPath = (Get-ItemProperty -Path $key -ErrorAction Stop).SteamPath
            if ($steamPath) {
                $path = ($steamPath -replace '/', '\').TrimEnd('\')
                if ($seen.Add($path)) { $roots.Add($path) }
            }
        } catch {
            # Steam not installed for this user/registry view.
        }
    }
    return $roots
}

function Get-SteamLibraries {
    $libraries = New-Object System.Collections.Generic.List[string]
    # The same library arrives from HKCU and HKLM with different casing
    # (c:\program files vs C:\Program Files); compare case-insensitively.
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($root in Get-SteamRoots) {
        if (-not (Test-Path $root)) { continue }
        if ($seen.Add($root)) { $libraries.Add($root) }

        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path $vdf)) { continue }
        foreach ($match in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $path = ($match.Groups[1].Value -replace '\\\\', '\').TrimEnd('\')
            if ((Test-Path $path) -and $seen.Add($path)) { $libraries.Add($path) }
        }
    }
    return $libraries
}

function Get-GameCandidates {
    $found = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($library in Get-SteamLibraries) {
        $exe = Join-Path $library 'steamapps\common\Megabonk\Megabonk.exe'
        if (Test-Path $exe) {
            $folder = (Split-Path $exe -Parent).TrimEnd('\')
            if ($seen.Add($folder)) { $found.Add($folder) }
        }
    }
    return $found
}

function Select-GameFolder {
    Add-Type -AssemblyName System.Windows.Forms | Out-Null
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the Megabonk folder (the one containing Megabonk.exe)'
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Stop-Install 'no game folder selected'
    }
    return $dialog.SelectedPath
}

function Resolve-GameFolder {
    if ($GamePath) {
        $resolved = (Resolve-Path -LiteralPath $GamePath -ErrorAction SilentlyContinue)
        if (-not $resolved) { Stop-Install "path not found: $GamePath" }
        $folder = $resolved.Path
        if (Test-Path (Join-Path $folder 'Megabonk.exe')) { return $folder }
        Stop-Install "Megabonk.exe not found in $folder"
    }

    $candidates = @(Get-GameCandidates)
    if ($candidates.Count -eq 1) {
        Write-Detail "found $($candidates[0])"
        return $candidates[0]
    }
    if ($candidates.Count -gt 1) {
        Write-Detail 'several Megabonk installs found:'
        for ($i = 0; $i -lt $candidates.Count; $i++) { Write-Detail "  [$i] $($candidates[$i])" }
        $answer = Read-Host 'Which one? (number)'
        $index = 0
        if (-not [int]::TryParse($answer, [ref]$index) -or $index -lt 0 -or $index -ge $candidates.Count) {
            Stop-Install 'invalid selection'
        }
        return $candidates[$index]
    }

    Write-Detail 'game not found in Steam libraries, asking for the folder'
    return Select-GameFolder
}

function Assert-GameFolder($Folder) {
    if (-not (Test-Path (Join-Path $Folder 'Megabonk.exe'))) {
        Stop-Install "Megabonk.exe missing in $Folder"
    }
    if (-not (Test-Path (Join-Path $Folder 'GameAssembly.dll'))) {
        Stop-Install "GameAssembly.dll missing in $Folder - this is not an IL2CPP Megabonk install"
    }
    if (-not (Test-Path (Join-Path $Folder 'Megabonk_Data\globalgamemanagers'))) {
        Stop-Install "Megabonk_Data\globalgamemanagers missing in $Folder"
    }
}

function Get-GameBuild($Folder) {
    $file = Join-Path $Folder 'Megabonk_Data\globalgamemanagers'
    $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($file))
    $match = [regex]::Match($text, '\b\d{4}\.\d+\.\d+[abfp]\d+\b')
    if ($match.Success) { return $match.Value }
    return $null
}

function Assert-NotRunning {
    $running = @(Get-Process -Name 'Megabonk' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) { Stop-Install 'Megabonk is running - close it first' }
}

# ----------------------------------------------------------------- install ----

Write-Step 'Wraeclast Conversion installer'

Test-Payload
Write-Good "payload $Payload"

$game = Resolve-GameFolder
Assert-GameFolder $game
Write-Good "game $game"

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$expected = $manifest.megabonkBuild
$actual = Get-GameBuild $game
if (-not $actual) {
    Write-Host "   warning: could not read the Unity version from globalgamemanagers" -ForegroundColor Yellow
} elseif ($actual -ne $expected) {
    if (-not $IgnoreBuildCheck) {
        Stop-Install "unsupported game build: found $actual, expected $expected. " +
            'The mod is version-locked; use -IgnoreBuildCheck only to experiment.'
    }
    Write-Host "   warning: build mismatch ($actual != $expected), continuing because -IgnoreBuildCheck" -ForegroundColor Yellow
} else {
    Write-Good "game build $actual matches the manifest"
}

Assert-NotRunning

$backup = Join-Path $game '_wraeclast_backup'
if (Test-Path $backup) {
    Stop-Install "a backup already exists at $backup - the mod is installed. Run uninstall.ps1 first."
}

$existing = New-Object System.Collections.Generic.List[string]
if (Test-Path (Join-Path $game 'BepInEx')) { $existing.Add('BepInEx') }
foreach ($file in $LoaderFiles) {
    if (Test-Path (Join-Path $game $file)) { $existing.Add($file) }
}

if ($DryRun) {
    Write-Step 'dry run'
    Write-Detail "would back up: $(if ($existing.Count) { $existing -join ', ' } else { 'nothing' })"
    Write-Detail "would copy payload to $game"
    Write-Detail "would write $backup\backup.json"
    Write-Good 'no changes made'
    exit 0
}

Write-Step 'backing up'
New-Item -ItemType Directory -Path $backup -Force | Out-Null
foreach ($item in $existing) {
    $source = Join-Path $game $item
    $target = Join-Path $backup $item
    if (Test-Path $source -PathType Container) {
        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force
    } else {
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
}
Write-Good "backup in $backup ($($existing.Count) item(s))"

$record = [pscustomobject]@{
    game          = $game
    installedAt   = (Get-Date).ToString('o')
    modVersion    = $manifest.version
    gameBuild     = $actual
    existingFiles = @($existing)
    installedFiles = @('BepInEx') + $LoaderFiles
}
$record | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $backup 'backup.json') -Encoding UTF8

Write-Step 'installing'
Copy-Item -Path (Join-Path $Payload '*') -Destination $game -Recurse -Force
Write-Good "plugin at BepInEx\plugins\Wraeclast\Wraeclast.dll"
Write-Good 'done - launch Megabonk normally'

Write-Host ''
Write-Host 'First run: check BepInEx\LogOutput.log for "Wraeclast Conversion".' -ForegroundColor DarkGray
Write-Host 'To remove: powershell -ExecutionPolicy Bypass -File uninstall.ps1' -ForegroundColor DarkGray
