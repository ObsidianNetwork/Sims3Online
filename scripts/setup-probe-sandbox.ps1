# setup-probe-sandbox.ps1 -- Create ProbeHost + ProbeClient Sandboxie boxes (one-time).
# Uses SbieIni.exe (works when Sandboxie.ini is not in ProgramData).
#   pwsh -NoProfile -File .\scripts\setup-probe-sandbox.ps1

param(
    [string[]]$BoxNames = @("ProbeHost", "ProbeClient"),
    [string]$SbieIni = "C:\Program Files\Sandboxie-Plus\SbieIni.exe"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SbieIni)) {
    Write-Host "SbieIni.exe not found: $SbieIni" -ForegroundColor Red
    Write-Host "Install Sandboxie-Plus or duplicate DefaultBox manually as ProbeHost and ProbeClient."
    exit 1
}

$added = @()
foreach ($box in $BoxNames) {
    $existing = & $SbieIni query $box 2>&1
    if ($LASTEXITCODE -eq 0 -and $existing -match "Enabled") {
        Write-Host "[$box] already exists -- skip"
        continue
    }
    & $SbieIni set $box Enabled y | Out-Null
    & $SbieIni set $box ConfigLevel 7 | Out-Null
    & $SbieIni set $box BorderColor "#00ffff,ttl" | Out-Null
    $added += $box
}

if ($added.Count -eq 0) {
    Write-Host "All probe boxes present."
} else {
    Write-Host "Created boxes: $($added -join ', ')" -ForegroundColor Green
}
