<#
.SYNOPSIS
    Removes Wraeclast Conversion and restores the files the installer backed up.

.DESCRIPTION
    Reads _wraeclast_backup/backup.json written by install.ps1. Without a backup
    the script refuses to touch the install unless -Force is given (which only
    deletes the loader files, never save data).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File uninstall.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File uninstall.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Megabonk" -Force
#>
[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$LoaderFiles = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')

function Write-Step($Message) { Write-Host "== $Message" -ForegroundColor Cyan }
function Write-Detail($Message) { Write-Host "   $Message" }
function Write-Good($Message) { Write-Host "   $Message" -ForegroundColor Green }
function Stop-Uninstall($Message) {
    Write-Host "error: $Message" -ForegroundColor Red
    exit 1
}

function Get-SteamLibraries {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        try {
            $steamPath = (Get-ItemProperty -Path $key -ErrorAction Stop).SteamPath
            if ($steamPath) { $roots.Add(($steamPath -replace '/', '\')) }
        } catch {
        }
    }

    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($root in ($roots | Select-Object -Unique)) {
        if (-not (Test-Path $root)) { continue }
        if (-not $libraries.Contains($root)) { $libraries.Add($root) }
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path $vdf)) { continue }
        foreach ($match in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $path = $match.Groups[1].Value -replace '\\\\', '\'
            if ((Test-Path $path) -and -not $libraries.Contains($path)) { $libraries.Add($path) }
        }
    }
    return $libraries
}

function Resolve-GameFolder {
    if ($GamePath) {
        $resolved = Resolve-Path -LiteralPath $GamePath -ErrorAction SilentlyContinue
        if (-not $resolved) { Stop-Uninstall "path not found: $GamePath" }
        return $resolved.Path
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($library in Get-SteamLibraries) {
        $folder = Join-Path $library 'steamapps\common\Megabonk'
        if (Test-Path (Join-Path $folder 'Megabonk.exe')) { $candidates.Add($folder) }
    }
    if ($candidates.Count -ge 1) {
        if ($candidates.Count -gt 1) {
            Write-Detail 'several installs found, using the first (pass -GamePath otherwise):'
            foreach ($candidate in $candidates) { Write-Detail "  $candidate" }
        }
        return $candidates[0]
    }

    Add-Type -AssemblyName System.Windows.Forms | Out-Null
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the Megabonk folder to clean up'
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Stop-Uninstall 'no game folder selected'
    }
    return $dialog.SelectedPath
}

function Remove-Relative($Root, $Relative) {
    $target = Join-Path $Root $Relative
    if (-not (Test-Path -LiteralPath $target)) { return }
    Remove-Item -LiteralPath $target -Recurse -Force
    Write-Detail "removed $Relative"
}

Write-Step 'Wraeclast Conversion uninstaller'

$game = Resolve-GameFolder
Write-Good "game $game"

if (@(Get-Process -Name 'Megabonk' -ErrorAction SilentlyContinue).Count -gt 0) {
    Stop-Uninstall 'Megabonk is running - close it first'
}

$backup = Join-Path $game '_wraeclast_backup'
$record = Join-Path $backup 'backup.json'

if (-not (Test-Path $record)) {
    if (-not $Force) {
        Stop-Uninstall "no backup found at $backup. Use -Force to delete the loader files anyway."
    }
    Write-Step 'no backup, forcing removal'
    Remove-Relative $game 'BepInEx'
    foreach ($file in $LoaderFiles) { Remove-Relative $game $file }
    Write-Good 'loader files removed (saves untouched)'
    exit 0
}

$state = Get-Content $record -Raw | ConvertFrom-Json
Write-Detail "backup from $($state.installedAt), mod $($state.modVersion), build $($state.gameBuild)"

Write-Step 'removing the mod'
Remove-Relative $game 'BepInEx'
foreach ($file in $LoaderFiles) { Remove-Relative $game $file }

Write-Step 'restoring the backup'
foreach ($item in @($state.existingFiles)) {
    $source = Join-Path $backup $item
    if (-not (Test-Path -LiteralPath $source)) { continue }
    if (Test-Path $source -PathType Container) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $game $item) -Recurse -Force
    } else {
        Copy-Item -LiteralPath $source -Destination (Join-Path $game $item) -Force
    }
    Write-Detail "restored $item"
}

Remove-Item -LiteralPath $backup -Recurse -Force
Write-Good 'uninstalled - Megabonk is back to its original files'
