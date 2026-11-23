# SimsOnline tester kit: install.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-Server <ip:port>] [-Sim <first name>] [-Player <n>]
#
# Checks the game by hash, backs up every file it replaces, copies SimLink and the ASI loader into Game\Bin, the mod
# packages and Resource.cfg into each Sims 3 profile, the client controller into %LOCALAPPDATA%\SimsOnline, writes
# config.json and makes the desktop shortcut "SimsOnline: join". Run it from the unpacked kit folder. Runs on Windows
# PowerShell 5.1 (built into Windows) and PowerShell 7.
#
# Developer options (tester_kit.py install-box): -GameDir, -ProfileDir, -InstallDir, -NoShortcut, -SkipGameBin,
# -AllowRunningGame (a box's profile while the host's own games run; never with Game\Bin).
param(
    [string]$Server = "",
    [string]$Sim = "",
    [int]$Player = 2,
    [string]$GameDir = "",
    [string[]]$ProfileDir = @(),
    [string]$InstallDir = "",
    [switch]$NoShortcut,
    [switch]$SkipGameBin,
    [switch]$SkipVersionCheck,
    [switch]$AllowRunningGame
)
if ($AllowRunningGame -and -not $SkipGameBin) { Write-Host "[install] STOPPED: -AllowRunningGame needs -SkipGameBin" -ForegroundColor Red; exit 1 }

$ErrorActionPreference = "Stop"
$Kit = $PSScriptRoot
$Ts3Sha256 = "10b2423e2368e161774462b0ef1960231ad0af298d564a7bb6a6e78c5e880609"
$BackupSuffix = ".simsonline-backup"

function Say([string]$Text) { Write-Host "[install] $Text" }
function Fail([string]$Text) { Write-Host "[install] STOPPED: $Text" -ForegroundColor Red; exit 1 }
# .NET's SHA256, not Get-FileHash: that cmdlet is missing when Windows PowerShell inherits PowerShell 7's module path.
function Sha256([string]$Path) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA "SimsOnline" }
$Manifest = Get-Content (Join-Path $Kit "manifest.json") -Raw | ConvertFrom-Json

# 1. The kit is whole.
$bad = @()
foreach ($file in $Manifest.files) {
    $path = Join-Path $Kit $file.path
    if (-not (Test-Path -LiteralPath $path)) { $bad += "missing $($file.path)" }
    elseif ((Get-Item -LiteralPath $path).Length -ne $file.size -or (Sha256 $path) -ne $file.sha256) { $bad += "changed $($file.path)" }
}
if ($bad.Count -gt 0) { Fail ("the kit is not whole (unpack the zip again): " + (($bad | Select-Object -First 5) -join "; ")) }
Say "kit $($Manifest.kit) is whole ($($Manifest.files.Count) files, feed $($Manifest.feed_commit))"

# 2. The game: The Sims 3 at patch 1.67 (Steam), found by the registry or the usual folders.
function Find-Game {
    $candidates = @()
    foreach ($key in @("HKLM:\SOFTWARE\WOW6432Node\Sims\The Sims 3", "HKLM:\SOFTWARE\Sims\The Sims 3")) {
        $value = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue)
        if ($value -and $value."Install Dir") { $candidates += $value."Install Dir" }
    }
    $steam = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 47890" -ErrorAction SilentlyContinue
    if ($steam -and $steam.InstallLocation) { $candidates += $steam.InstallLocation }
    $candidates += "${env:ProgramFiles(x86)}\Steam\steamapps\common\The Sims 3"
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem | ForEach-Object { $_.Root })) {
        $candidates += (Join-Path $drive "SteamLibrary\steamapps\common\The Sims 3")
        $candidates += (Join-Path $drive "Games\The Sims 3")
    }
    foreach ($dir in $candidates) {
        if ($dir -and (Test-Path -LiteralPath (Join-Path $dir "Game\Bin\TS3W.exe"))) { return $dir }
    }
    return $null
}
if (-not $GameDir) { $GameDir = Find-Game }
if (-not $GameDir) { Fail "The Sims 3 was not found. Run again with -GameDir '<folder that holds Game\Bin\TS3W.exe>'." }
$Ts3 = Join-Path $GameDir "Game\Bin\TS3W.exe"
if (-not (Test-Path -LiteralPath $Ts3)) { Fail "no $Ts3" }
$ts3Hash = Sha256 $Ts3
if ($ts3Hash -ne $Ts3Sha256) {
    if (-not $SkipVersionCheck) { Fail "$Ts3 is not The Sims 3 1.67 (Steam): sha256 $ts3Hash. SimsOnline needs that exact game build." }
    Say "WARNING: $Ts3 is not the 1.67 Steam build; continuing because of -SkipVersionCheck"
} else {
    Say "game: $GameDir (TS3W.exe 1.67, hash ok)"
}
if (-not $AllowRunningGame -and (Get-Process -Name TS3W -ErrorAction SilentlyContinue)) { Fail "The Sims 3 is running: close it first." }

# 3. The profiles: every Documents\Electronic Arts\The Sims 3 TS3 may use (the Personal shell folder, OneDrive).
function Find-Profiles {
    $roots = @()
    $shell = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders" -ErrorAction SilentlyContinue
    if ($shell -and $shell.Personal) { $roots += $shell.Personal }
    $roots += [Environment]::GetFolderPath("MyDocuments")
    $roots += (Join-Path $env:USERPROFILE "Documents")
    $found = @()
    foreach ($root in $roots) {
        $profileDir = Join-Path $root "Electronic Arts\The Sims 3"
        if ((Test-Path -LiteralPath $profileDir) -and ($found -notcontains $profileDir)) { $found += $profileDir }
    }
    return $found
}
if ($ProfileDir.Count -eq 0) { $ProfileDir = Find-Profiles }
if ($ProfileDir.Count -eq 0) { Fail "no 'Documents\Electronic Arts\The Sims 3' folder: start The Sims 3 once (to the main menu), quit, then run this again." }

$Backup = Join-Path $InstallDir ("backup\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
# installed.json: each target and what the first install found there ("added": nothing, "replaced": a backup was
# kept, "kept": the same file was already there). uninstall.ps1 undoes exactly that; a later install keeps the first.
$RecordPath = Join-Path $InstallDir "installed.json"
$Record = @{}
if (Test-Path -LiteralPath $RecordPath) {
    foreach ($row in (Get-Content $RecordPath -Raw | ConvertFrom-Json)) { $Record[$row.target] = $row.how }
}
function Put-File([string]$Source, [string]$Target) {
    $dir = Split-Path -Parent $Target
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $how = "added"
    if (Test-Path -LiteralPath $Target) {
        if ((Sha256 $Target) -eq (Sha256 $Source)) {
            if (-not $Record.ContainsKey($Target)) { $Record[$Target] = "kept" }
            return "already in place"
        }
        $keep = Join-Path $Backup ($Target -replace '[:\\]+', '_')
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $keep) | Out-Null
        Copy-Item -LiteralPath $Target -Destination $keep -Force
        if (-not $Record.ContainsKey($Target) -and -not (Test-Path -LiteralPath ($Target + $BackupSuffix))) {
            Copy-Item -LiteralPath $Target -Destination ($Target + $BackupSuffix)
        }
        $how = "replaced"
    }
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    if (-not $Record.ContainsKey($Target)) { $Record[$Target] = $how }
    if ($how -eq "replaced") { return "replaced (backup kept)" }
    return "added"
}

# The gameplay fork package (EA-derived, so a kit sent to a tester leaves it out): which one a profile already has.
# The manifest's host_fork is the host's installed attempt and its package's sha256; build-fork.ps1 records what it
# installs in installed.json, and its package holds the host's assembly but is packed with other bytes.
$ForkPackage = "SimsOnlineGameplay02Cold.package"
function Describe-Fork([string]$Path) {
    $hash = Sha256 $Path
    $hostFork = $Manifest.host_fork
    if ($hostFork -and $hostFork.package_sha256 -eq $hash) { if ($hostFork.attempt -eq "unreadable") { return "the host's fork (its attempt receipt was unreadable)" }; return "the host's fork attempt $($hostFork.attempt)" }
    $made = (Get-Item -LiteralPath $Path).LastWriteTime.ToString("yyyy-MM-dd HH:mm")
    if ($Record.ContainsKey($Path)) { return "the fork SimsOnline installed here earlier (build-fork.ps1 or a kit; $made, sha256 $($hash.Substring(0, 12)))" }
    $named = ""
    if ($hostFork -and $hostFork.attempt -and $hostFork.attempt -ne "unreadable") { $named = " $($hostFork.attempt)" }
    return "a SimsOnline fork, not the host's attempt$named ($made, sha256 $($hash.Substring(0, 12))): if the host says the fork changed, run build-fork.ps1"
}

# 4. Game\Bin: SimLink and the ASI loader.
if (-not $SkipGameBin) {
    foreach ($name in @("SimLink.asi", "wininet.dll")) {
        $source = Join-Path $Kit "game-bin\$name"
        if (Test-Path -LiteralPath $source) {
            try { $how = Put-File $source (Join-Path $GameDir "Game\Bin\$name") }
            catch { Fail "could not write $GameDir\Game\Bin\$name ($($_.Exception.Message)). Run this from a PowerShell opened as administrator." }
            Say "Game\Bin\$name $how"
        }
    }
}

# 5. Each profile: Resource.cfg and the packages; the script cache aside.
$packages = Get-ChildItem -LiteralPath (Join-Path $Kit "mods\Packages") -Filter *.package
foreach ($profileDir in $ProfileDir) {
    Say "profile: $profileDir"
    Say ("  Mods\Resource.cfg " + (Put-File (Join-Path $Kit "mods\Resource.cfg") (Join-Path $profileDir "Mods\Resource.cfg")))
    foreach ($package in $packages) {
        Say ("  Mods\Packages\$($package.Name) " + (Put-File $package.FullName (Join-Path $profileDir "Mods\Packages\$($package.Name)")))
    }
    $ours = @($packages | ForEach-Object { $_.Name })
    # The gameplay fork is ours too when this kit leaves it out (build-fork.ps1 makes it, or an earlier install left
    # it): keep it, and say which one it is.
    $forkTarget = Join-Path $profileDir "Mods\Packages\$ForkPackage"
    if ($ours -notcontains $ForkPackage -and (Test-Path -LiteralPath $forkTarget)) {
        $ours += $ForkPackage
        Say "  Mods\Packages\$ForkPackage left in place: $(Describe-Fork $forkTarget)"
    }
    $others = Get-ChildItem -LiteralPath (Join-Path $profileDir "Mods\Packages") -Recurse -Include *.package -ErrorAction SilentlyContinue |
        Where-Object { $ours -notcontains $_.Name }
    if ($others) { Say ("  NOTE: other mods in Mods\Packages (move them out while testing): " + (($others | Select-Object -First 8 | ForEach-Object { $_.Name }) -join ", ")) }
    $cache = Join-Path $profileDir "scriptCache.package"
    if (Test-Path -LiteralPath $cache) {
        Move-Item -LiteralPath $cache -Destination (Join-Path $profileDir ("scriptCache.simsonline-aside-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".package")) -Force
        Say "  scriptCache.package moved aside (TS3 rebuilds it)"
    }
    # The client writes the host's world here; a profile the game has never run in has no Saves folder yet.
    New-Item -ItemType Directory -Force -Path (Join-Path $profileDir "Saves") | Out-Null
}

# 6. The controller and the scripts into the install folder.
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$rows = @($Record.Keys | Sort-Object | ForEach-Object { New-Object PSObject -Property ([ordered]@{ target = $_; how = $Record[$_] }) })
ConvertTo-Json -InputObject $rows | Set-Content -Path $RecordPath -Encoding UTF8
$client = Join-Path $InstallDir "client"
if (Test-Path -LiteralPath $client) { Remove-Item -LiteralPath $client -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $Kit "client") -Destination $client -Recurse
foreach ($name in @("README.md", "join.ps1", "collect-logs.ps1", "uninstall.ps1", "join.cmd", "collect-logs.cmd", "uninstall.cmd", "manifest.json")) {
    if (Test-Path -LiteralPath (Join-Path $Kit $name)) { Copy-Item -LiteralPath (Join-Path $Kit $name) -Destination (Join-Path $InstallDir $name) -Force }
}
New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir "logs") | Out-Null

$configPath = Join-Path $InstallDir "config.json"
$config = [ordered]@{ server = $Server; sim = $Sim; player = $Player; gameDir = $GameDir; ts3 = $Ts3; profiles = @($ProfileDir) }
if (Test-Path -LiteralPath $configPath) {
    $old = Get-Content $configPath -Raw | ConvertFrom-Json
    if (-not $Server -and $old.server) { $config.server = $old.server }
    if (-not $Sim -and $old.sim) { $config.sim = $old.sim }
    if (-not $PSBoundParameters.ContainsKey("Player") -and $old.player) { $config.player = $old.player }
}
$config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
Say "controller and scripts: $InstallDir"

# 7. The desktop shortcut.
if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath("Desktop")) "SimsOnline - join.lnk"))
    $link.TargetPath = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    $link.Arguments = "-NoExit -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'join.ps1')`""
    $link.WorkingDirectory = $InstallDir
    $link.Description = "SimsOnline: join"
    $link.Save()
    Say "desktop shortcut: SimsOnline - join"
}

if ((Test-Path -LiteralPath (Join-Path $Kit "build-fork.ps1")) -and -not (Test-Path -LiteralPath (Join-Path $Kit "mods\Packages\SimsOnlineGameplay02Cold.package"))) {
    Say "done. Next: double-click build-fork.cmd in this kit folder (it builds the gameplay fork from your own game, 2 to 5 minutes), then double-click 'SimsOnline - join' on the desktop."
} else {
    Say "done. Next: double-click 'SimsOnline - join' on the desktop (or run join.ps1 -Server <host ip:port> -Sim <first name>)."
}
