param(
    [string]$Sims3UserFolder = (Join-Path $(if ($env:USERPROFILE) { $env:USERPROFILE } else { $HOME }) "Documents\Electronic Arts\The Sims 3"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if (-not $Force -and (Get-Process TS3W, TS3W.exe -ErrorAction SilentlyContinue)) {
    throw "TS3W.exe is running - stop the dev stack before installing the mod (scriptCache flush), or pass -Force after stopping the dev-stack TS3 instances (.\scripts\make-mod.ps1 -Reload)."
}

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackagePath = Join-Path $ScriptRoot "bin\Release\SimsOnlineServerHostMod.package"

if (-not (Test-Path $PackagePath)) {
    & (Join-Path $ScriptRoot "package-server-host-mod.ps1")
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$ModsFolder = Join-Path $Sims3UserFolder "Mods"
$PackagesFolder = Join-Path $ModsFolder "Packages"
$ResourceCfg = Join-Path $ModsFolder "Resource.cfg"

New-Item -ItemType Directory -Force -Path $PackagesFolder | Out-Null

if (-not (Test-Path $ResourceCfg)) {
    @(
        "Priority 500",
        "PackedFile Packages/*.package",
        "PackedFile Packages/*/*.package",
        "PackedFile Packages/*/*/*.package"
    ) | Set-Content -Path $ResourceCfg -Encoding ASCII
}

Copy-Item -Path $PackagePath -Destination (Join-Path $PackagesFolder "SimsOnlineServerHostMod.package") -Force

$ScriptCache = Join-Path $Sims3UserFolder "scriptCache.package"
if (Test-Path $ScriptCache) {
    Remove-Item -Path $ScriptCache -Force
}

Write-Host "Installed SimsOnlineServerHostMod.package into $PackagesFolder"
