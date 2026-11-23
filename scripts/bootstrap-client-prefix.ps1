# bootstrap-client-prefix.ps1 -- 2026-07-03 two-client Linux: set up an isolated Wine prefix
# for the second (mirror client) TS3 instance. Sandboxie's role on Windows; on Linux a second
# WINEPREFIX gives the second Sims its own C-drive, registry, and Documents dir so two TS3
# instances can run side by side without single-instance redirection or save conflicts.
#
#   .\scripts\bootstrap-client-prefix.ps1                      # default ~/.sims3-client from ~/.sims3
#   .\scripts\bootstrap-client-prefix.ps1 -Source ~/.sims3 -Dest ~/.sims3-client
#   .\scripts\bootstrap-client-prefix.ps1 -SkipGameInstall     # Documents + registry only (game already copied)
#
# What gets copied:
#   1. The registry hives (system.reg + user.reg) -- the Sims(Steam) + Electronic Arts keys that
#      gate the game launch (without these TS3W.exe exits silently with "Installation Verification
#      Failure"). Copied verbatim from the server prefix; they are install-scoped, not player-scoped.
#   2. The Documents\Electronic Arts\The Sims 3 tree (Mods, saves, Resource.cfg) -- isolated so
#      the client's scriptCache flushes don't race the server's, and so the client can load a
#      different save than the server. Copied fresh each bootstrap so the client starts clean.
#   3. The game install (drive_c/Program Files (x86)/The Sims 3) -- a FULL copy (not a symlink)
#      because TS3 writes to its install dir (scriptCache, Sims3Scripts) and a shared read-only
#      install would break. ~22G; one-time cost. Skip with -SkipGameInstall if already present.
#
# The script is idempotent: re-running refreshes Documents + registry, leaves the game install
# in place if it already exists.

param(
    [string]$Source = (Join-Path $HOME ".sims3"),
    [string]$Dest = (Join-Path $HOME ".sims3-client"),
    [switch]$SkipGameInstall
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Source)) {
    throw "Source Wine prefix not found: $Source. The server prefix (~/.sims3 by default) must exist before bootstrapping the client prefix."
}

$sourceDriveC = Join-Path $Source "drive_c"
$destDriveC = Join-Path $Dest "drive_c"
$gameInstallRel = "Program Files (x86)/The Sims 3"

Write-Host "bootstrap-client-prefix: $Source -> $Dest"

# Wine names the profile dir drive_c/users/<unix-username>; detect it from the source prefix
# instead of hardcoding a name, preferring the current user's dir if present.
$currentUser = [Environment]::UserName
$sourceUsersDir = Join-Path $sourceDriveC "users"
$wineUser = $null
if (Test-Path $sourceUsersDir) {
    $profileDirs = @(Get-ChildItem $sourceUsersDir -Directory | Where-Object { $_.Name -ne "Public" })
    if ($profileDirs.Name -contains $currentUser) {
        $wineUser = $currentUser
    }
    elseif ($profileDirs.Count -eq 1) {
        $wineUser = $profileDirs[0].Name
    }
    elseif ($profileDirs.Count -gt 1) {
        $wineUser = $profileDirs[0].Name
        Write-Host "  multiple Wine user profiles in ${sourceUsersDir}: $($profileDirs.Name -join ', '); using '$wineUser'" -ForegroundColor Yellow
    }
}
if (-not $wineUser) {
    $wineUser = $currentUser
}
$documentsRel = "users/$wineUser/Documents/Electronic Arts/The Sims 3"

# 1. Prefix skeleton + registry hives.
New-Item -ItemType Directory -Force -Path $Dest | Out-Null
foreach ($hive in @("system.reg", "user.reg", "userdef.reg")) {
    $src = Join-Path $Source $hive
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $Dest $hive) -Force
        Write-Host "  copied registry hive: $hive"
    }
}

# 2. Game install (full copy, ~22G, one-time).
$destGameInstall = Join-Path $destDriveC $gameInstallRel
if ($SkipGameInstall) {
    Write-Host "  game install: SKIPPED (-SkipGameInstall)"
}
elseif (Test-Path $destGameInstall) {
    Write-Host "  game install: already present at $destGameInstall (skipped)"
}
else {
    $srcGameInstall = Join-Path $sourceDriveC $gameInstallRel
    if (-not (Test-Path $srcGameInstall)) {
        throw "Source game install not found: $srcGameInstall. Copy or install The Sims 3 into the source prefix first."
    }
    Write-Host "  copying game install (~22G, one-time -- this takes a while)..."
    New-Item -ItemType Directory -Force -Path (Split-Path $destGameInstall) | Out-Null
    Copy-Item $srcGameInstall $destGameInstall -Recurse -Force
    Write-Host "  game install: copied"
}

# 3. Isolated Documents tree (Mods, saves, Resource.cfg). Refreshed each run so the client
# starts with the current mod package + a clean scriptCache.
$destDocs = Join-Path $destDriveC $documentsRel
$srcDocs = Join-Path $sourceDriveC $documentsRel
if (Test-Path $srcDocs) {
    if (Test-Path $destDocs) {
        Remove-Item $destDocs -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $destDocs) | Out-Null
    Copy-Item $srcDocs $destDocs -Recurse -Force
    # Wipe the client's scriptCache so the new mod package reloads cleanly on first boot.
    $clientScriptCache = Join-Path $destDocs "scriptCache.package"
    if (Test-Path $clientScriptCache) {
        Remove-Item $clientScriptCache -Force
    }
    Write-Host "  Documents: copied (scriptCache cleared)"
}
else {
    Write-Host "  Documents: source not found at $srcDocs (the client will start without Mods/saves)" -ForegroundColor Yellow
}

# 4. Symlink the wine system tree (windows/, dosdevices/) so wine itself boots. These are
# shared across prefixes -- only the C-drive contents differ.
foreach ($sys in @("dosdevices", "drive_c/windows")) {
    $srcSys = Join-Path $Source $sys
    $destSys = Join-Path $Dest $sys
    if ((Test-Path $srcSys) -and -not (Test-Path $destSys)) {
        New-Item -ItemType SymbolicLink -Path $destSys -Target $srcSys | Out-Null
        Write-Host "  symlinked: $sys"
    }
}

Write-Host ""
Write-Host "bootstrap-client-prefix OK: $Dest ready" -ForegroundColor Green
Write-Host "  the client controller launches with: --client-ts3-wineprefix $Dest"