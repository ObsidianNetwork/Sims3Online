# SimsOnline tester kit: build the gameplay fork on this PC.
#
#   powershell -ExecutionPolicy Bypass -File build-fork.ps1
#
# Makes SimsOnlineGameplay02Cold.package from this PC's own copy of The Sims 3, so the kit never carries the game's
# code: takes Sims3GameplaySystems out of Game\Bin\gameplay.package, decompiles it (ILSpy, the version the host used),
# checks every decompiled file against the host's, applies SimsOnline's changes and hooks, compiles it (Roslyn, against
# the game's own assemblies), checks the result is the host's fork byte for byte, packs it and installs it into each
# profile's Mods\Packages. Everything it runs is in the kit's fork-builder folder (a private .NET runtime, ILSpy,
# the C# compiler, our ForkTool); nothing is installed on the PC. About 2 to 5 minutes; needs about 1 GB free.
# Runs on Windows PowerShell 5.1 (built into Windows) and PowerShell 7. Run install.ps1 first.
#
# Developer options (tester_kit.py / the host's proof): -GameDir, -ProfileDir, -InstallDir, -WorkDir, -KeepWork,
# -NoInstall (build and check only), -AllowRunningGame (a box's profile while the host's own games run).
param(
    [string]$GameDir = "",
    [string[]]$ProfileDir = @(),
    [string]$InstallDir = "",
    [string]$WorkDir = "",
    [switch]$KeepWork,
    [switch]$NoInstall,
    [switch]$SkipVersionCheck,
    [switch]$AllowRunningGame
)

$ErrorActionPreference = "Stop"
$Kit = $PSScriptRoot
$Builder = Join-Path $Kit "fork-builder"
$Ts3Sha256 = "10b2423e2368e161774462b0ef1960231ad0af298d564a7bb6a6e78c5e880609"
$BackupSuffix = ".simsonline-backup"
$PackageName = "SimsOnlineGameplay02Cold.package"
$Started = Get-Date

function Say([string]$Text) { Write-Host ("[build-fork {0,4:N0}s] {1}" -f ((Get-Date) - $Started).TotalSeconds, $Text) }
function Fail([string]$Text) { Write-Host "[build-fork] STOPPED: $Text" -ForegroundColor Red; exit 1 }
function Sha256([string]$Path) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA "SimsOnline" }
if (-not $WorkDir) { $WorkDir = Join-Path $InstallDir "fork-build" }
if (-not (Test-Path -LiteralPath (Join-Path $Builder "fork-patch.json"))) { Fail "no fork-builder folder next to this script: unpack the whole kit and run it from there." }
$Expect = Get-Content (Join-Path $Builder "fork-expect.json") -Raw | ConvertFrom-Json

# 1. The builder is whole (the kit's manifest lists every file of it).
$Manifest = Get-Content (Join-Path $Kit "manifest.json") -Raw | ConvertFrom-Json
$bad = @()
foreach ($file in @($Manifest.files | Where-Object { $_.path -like "fork-builder/*" -or $_.path -eq "build-fork.ps1" })) {
    $path = Join-Path $Kit $file.path
    if (-not (Test-Path -LiteralPath $path)) { $bad += "missing $($file.path)" }
    elseif ((Get-Item -LiteralPath $path).Length -ne $file.size -or (Sha256 $path) -ne $file.sha256) { $bad += "changed $($file.path)" }
}
if ($bad.Count -gt 0) { Fail ("the kit is not whole (unpack the zip again): " + (($bad | Select-Object -First 5) -join "; ")) }
Say "builder is whole (ILSpy $($Expect.ilspycmd), .NET $($Expect.dotnet_runtime), Roslyn of SDK $($Expect.roslyn_sdk))"

# 2. The game (install.ps1's search: the registry, Steam, the usual folders).
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
$GameBin = Join-Path $GameDir "Game\Bin"
$ts3Hash = Sha256 (Join-Path $GameBin "TS3W.exe")
if ($ts3Hash -ne $Ts3Sha256) {
    if (-not $SkipVersionCheck) { Fail "$GameBin\TS3W.exe is not The Sims 3 1.67 (Steam): sha256 $ts3Hash. The fork is built for that exact game build." }
    Say "WARNING: TS3W.exe is not the 1.67 Steam build; continuing because of -SkipVersionCheck"
}
Say "game: $GameDir"

# 3. The tools run on the kit's own .NET runtime, never one installed on this PC.
$Dotnet = Join-Path $Builder "runtime\dotnet.exe"
Get-ChildItem Env: | Where-Object { $_.Name -like "DOTNET_*" } | ForEach-Object { Remove-Item -LiteralPath ("Env:" + $_.Name) }
$env:DOTNET_ROOT = Join-Path $Builder "runtime"
$env:DOTNET_MULTILEVEL_LOOKUP = "0"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$ForkTool = Join-Path $Builder "forktool\ForkTool.dll"
function Invoke-Tool([string]$Step, [string[]]$Arguments) {
    $log = Join-Path $WorkDir ($Step + ".log")
    $errors = Join-Path $WorkDir ($Step + ".err.log")
    # One quoted string: Windows PowerShell 5.1 does not quote an argument list's items for a native program.
    $argumentLine = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $Dotnet -ArgumentList $argumentLine -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError $errors
    if ($process.ExitCode -ne 0) {
        $tail = ((Get-Content -LiteralPath $log -Tail 12 -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath $errors -Tail 6 -ErrorAction SilentlyContinue)) -join "`n"
        Fail "$Step failed (exit $($process.ExitCode)); logs: $log`n$tail"
    }
    return (Get-Content -LiteralPath $log)
}

if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$Assemblies = Join-Path $WorkDir "assemblies"
$Decompile = Join-Path $WorkDir "decompile"
$Dll = Join-Path $WorkDir "Sims3GameplaySystems.dll"
$Package = Join-Path $WorkDir $PackageName

# 4. The game's script assemblies, out of this PC's own packages; each must be the host's.
Say "extracting the game's script assemblies from Game\Bin ..."
Invoke-Tool "extract" @($ForkTool, "extract", $GameBin, $Assemblies) | Out-Null
foreach ($row in $Expect.assemblies.PSObject.Properties) {
    $hash = Sha256 (Join-Path $Assemblies $row.Name)
    if ($hash -ne $row.Value) { Fail "this game's $($row.Name) is not the host's (sha256 $hash): the fork needs The Sims 3 1.67 Steam, unmodified (Steam: Properties, Installed Files, Verify integrity)." }
}
Say "assemblies match the host's (10 files)"

# 5. Decompile with the pinned ILSpy; the patch checks every file against the host's decompile.
Say "decompiling Sims3GameplaySystems (ILSpy $($Expect.ilspycmd), C# 7.3; about a minute) ..."
Invoke-Tool "decompile" @((Join-Path $Builder "ilspy\ilspycmd.dll"), "-p", "-lv", "CSharp7_3", "-r", $Assemblies, "-o", $Decompile, (Join-Path $Assemblies "gameplay_02.dll")) | Out-Null
Remove-Item -LiteralPath (Join-Path $Decompile "gameplay_02.csproj") -ErrorAction SilentlyContinue
Say "applying SimsOnline's changes ($($Expect.changed_files) files, $($Expect.edits) edits) and hooks ..."
$out = Invoke-Tool "patch" @($ForkTool, "patch", $Decompile, (Join-Path $Builder "fork-patch.json"), (Join-Path $Builder "hooks"))
Say ($out | Select-Object -Last 1)

# 6. Compile against the game's own assemblies, restore the metadata C# cannot say, check it is the host's fork.
Say "compiling (Roslyn; a few minutes on a slow PC) ..."
$rsp = Join-Path $WorkDir "csc.rsp"
Invoke-Tool "rsp" @($ForkTool, "rsp", $Decompile, $Assemblies, $Dll, $rsp) | Out-Null
Invoke-Tool "compile" @("exec", (Join-Path $Builder "roslyn\csc.dll"), "@$rsp") | Out-Null
$out = Invoke-Tool "restore" @($ForkTool, "restore", (Join-Path $Assemblies "gameplay_02.dll"), $Dll)
$forkHash = Sha256 $Dll
if ($forkHash -ne $Expect.fork_sha256) { Fail "the built fork ($forkHash) is not the host's ($($Expect.fork_sha256)); nothing installed. Send $WorkDir\*.log to the host." }
Say "fork built: sha256 $forkHash, the host's byte for byte ($($out | Select-Object -Last 1))"
Invoke-Tool "pack" @($ForkTool, "pack", $Dll, (Join-Path $Assemblies "s3sa-header.json"), $Package) | Out-Null
Say "packed: $PackageName ($([math]::Round((Get-Item -LiteralPath $Package).Length / 1MB, 1)) MB)"

if ($NoInstall) {
    Say "-NoInstall: the package is $Package"
    Say "done in $([math]::Round(((Get-Date) - $Started).TotalSeconds))s"
    exit 0
}

# 7. Each profile's Mods\Packages (install.ps1's profile search), the replaced file kept, recorded for uninstall.ps1.
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
if (-not $AllowRunningGame -and (Get-Process -Name TS3W -ErrorAction SilentlyContinue)) { Fail "The Sims 3 is running: close it first (the fork is built; run this again to install it)." }
if ($ProfileDir.Count -eq 0) { $ProfileDir = Find-Profiles }
if ($ProfileDir.Count -eq 0) { Fail "no 'Documents\Electronic Arts\The Sims 3' folder: start The Sims 3 once (to the main menu), quit, then run install.ps1 and this again." }

$RecordPath = Join-Path $InstallDir "installed.json"
$Record = [ordered]@{}
if (Test-Path -LiteralPath $RecordPath) {
    foreach ($row in (Get-Content $RecordPath -Raw | ConvertFrom-Json)) { $Record[$row.target] = $row.how }
}
foreach ($profileDir in $ProfileDir) {
    $target = Join-Path $profileDir "Mods\Packages\$PackageName"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    $how = "added"
    if (Test-Path -LiteralPath $target) {
        if ((Sha256 $target) -eq $forkHash) { $how = "kept" }
        else {
            if (-not $Record.Contains($target) -and -not (Test-Path -LiteralPath ($target + $BackupSuffix))) { Copy-Item -LiteralPath $target -Destination ($target + $BackupSuffix) }
            $how = "replaced"
        }
    }
    if ($how -ne "kept") { Copy-Item -LiteralPath $Package -Destination $target -Force }
    if (-not $Record.Contains($target)) { $Record[$target] = $how }
    $cache = Join-Path $profileDir "scriptCache.package"
    if (Test-Path -LiteralPath $cache) {
        Move-Item -LiteralPath $cache -Destination (Join-Path $profileDir ("scriptCache.simsonline-aside-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".package")) -Force
    }
    Say "profile: $profileDir  Mods\Packages\$PackageName $how; scriptCache.package moved aside"
}
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$rows = @($Record.Keys | Sort-Object | ForEach-Object { New-Object PSObject -Property ([ordered]@{ target = $_; how = $Record[$_] }) })
ConvertTo-Json -InputObject $rows | Set-Content -Path $RecordPath -Encoding UTF8

if (-not $KeepWork) { Remove-Item -LiteralPath $WorkDir -Recurse -Force; Say "work folder removed (it held the decompiled game code)" }
Say "done in $([math]::Round(((Get-Date) - $Started).TotalSeconds))s. Next: join as usual."
