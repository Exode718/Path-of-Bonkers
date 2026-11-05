<#
.SYNOPSIS
    Run the full offline check battery for the Wraeclast mod.

.DESCRIPTION
    Content/data audits, generated-table checks, asset checks, a Release build
    and the installed-plugin consistency check. No game is launched and nothing
    outside the repo is touched.

    Every step runs; the script reports each result and exits 1 if any failed.

.EXAMPLE
    powershell -File mod/tools/check-all.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

$ModRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Split-Path -Parent $ModRoot
$GameDir = Join-Path $RepoRoot 'lab\megabonk-mod'
$Python = 'python'

$failures = New-Object System.Collections.Generic.List[string]

function Step([string]$name, [scriptblock]$body) {
    Write-Host ""
    Write-Host "== $name ==" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $body
    if ($LASTEXITCODE -ne 0) {
        $failures.Add($name)
        Write-Host "FAIL: $name (exit $LASTEXITCODE)" -ForegroundColor Red
    }
    else {
        Write-Host "ok: $name" -ForegroundColor Green
    }
}

Push-Location $RepoRoot
try {
    Step 'content' { & $Python 'mod\tools\data\audit-content.py' }
    Step 'class kit' { & $Python 'mod\tools\data\audit-class-kit.py' }
    Step 'tomes' { & $Python 'mod\tools\data\audit-tomes.py' }
    Step 'relics' { & $Python 'mod\tools\data\audit-relics.py' }
    Step 'rewards table' { & $Python 'mod\tools\data\build-rewards.py' '--check' }
    Step 'item donors doc' { & $Python 'mod\tools\data\dump-item-donors.py' '--check' }
    Step 'props' { & $Python 'mod\tools\data\check-props.py' }
    Step 'skill sounds' { & node 'mod\tools\audio\export-skill-sounds.mjs' '--check' }
    Step 'balance floor' { & $Python 'mod\tools\data\sim-balance.py' '--builds' '5000' '--strict' }
    Step 'item balance' { & $Python 'mod\tools\data\sim-items.py' '--strict' }

    Step 'staged assets' {
        & powershell -NoProfile -ExecutionPolicy Bypass -File 'mod\tools\prepare-data.ps1'
        if (-not $?) { $global:LASTEXITCODE = 1 }
    }

    Step 'build' {
        & dotnet build 'mod\Wraeclast' -c Release -p:GameDir=$GameDir --nologo -v q
    }

    Step 'installed plugin' {
        $manifest = Get-Content (Join-Path $ModRoot 'manifest.json') -Raw | ConvertFrom-Json
        $dll = Join-Path $GameDir 'BepInEx\plugins\Wraeclast\Wraeclast.dll'
        $text = [System.IO.File]::ReadAllText($dll, [System.Text.Encoding]::GetEncoding(28591))
        if ($text -notmatch [regex]::Escape($manifest.version)) {
            Write-Host "installed DLL does not carry version $($manifest.version)"
            $global:LASTEXITCODE = 1
            return
        }

        $assets = Join-Path $GameDir 'BepInEx\plugins\Wraeclast\assets'
        foreach ($name in @('classes.json', 'skills.json', 'tomes.json', 'items.json', 'rewards.json')) {
            if (-not (Test-Path (Join-Path $assets $name))) {
                Write-Host "missing staged asset: $name"
                $global:LASTEXITCODE = 1
            }
        }

        $sounds = Join-Path $assets 'sounds'
        $clips = if (Test-Path $sounds) { @(Get-ChildItem $sounds -Filter *.wav).Count } else { 0 }
        if ($clips -ne 7) {
            Write-Host "installed skill sounds: $clips/7"
            $global:LASTEXITCODE = 1
        }
        Write-Host "installed: Wraeclast $($manifest.version)"
    }
}
finally {
    Pop-Location
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "CHECK BATTERY FAILED: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "CHECK BATTERY CLEAN" -ForegroundColor Green
exit 0
