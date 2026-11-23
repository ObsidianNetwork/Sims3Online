# check-mod-compile.ps1 -- compile-only gate for the FULL SimsOnlineServerHostMod sources.
#
#   pwsh -NoProfile -File scripts/check-mod-compile.ps1
#
# Compiles the .NET 2 host mod (shell + swappable handler) exactly the way
# make-mod.ps1 steps 0-1 do, but WITHOUT packaging, installing, BuildStamp
# regeneration, or EmbeddedHandlerAssembly.cs regeneration -- it never dirties
# tracked sources and needs no live game. Exit 0 = compiles clean, exit 1 = fail.
#
# Feasibility (verified 2026-08-17): needs only the .NET Framework 2.0 csc.exe
# (Windows: %WINDIR%\Microsoft.NET\Framework\v2.0.50727; Linux: wine prefix) and
# the reference-only engine assemblies in refs/sims3 (API only, no game code). CI runs
# this via the mod-compile-gate job in .github/workflows/build-and-test.yml with
# -Strict; local pre-commit runs it non-strict so hosts without the toolchain
# skip cleanly instead of blocking.

param([switch]$Strict)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$ModRoot = Join-Path $RepoRoot "SimsOnlineServerHostMod"
$Assemblies = Join-Path $RepoRoot "refs\sims3"
$OutDir = Join-Path $RepoRoot ".omo\check-mod-compile"   # scratch only, never bin\Release

. (Join-Path $PSScriptRoot "find-csc.ps1")

function Stop-Gate([string]$Message) {
    if ($Strict) {
        Write-Host "MOD COMPILE GATE FAILED (strict): $Message" -ForegroundColor Red
        exit 1
    }
    Write-Host "SKIP: $Message" -ForegroundColor Yellow
    exit 0
}

if (-not (Test-Path $Assemblies)) {
    Stop-Gate "reference assemblies missing ($Assemblies)."
}

try { $csc = Find-Csc20 } catch {
    Stop-Gate ".NET Framework 2.0 csc.exe not found on this host. $($_.Exception.Message)"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$References = @(
    "scripts_00.dll", "scripts_01.dll", "scripts_02.dll",
    "gameplay_00.dll", "gameplay_01.dll", "gameplay_02.dll", "gameplay_03.dll"
) | ForEach-Object { Join-Path $Assemblies $_ }
foreach ($r in $References) {
    if (-not (Test-Path $r)) {
        Stop-Gate "missing reference assembly $r"
    }
}

$refArgs = $References | ForEach-Object { "/reference:$_" }
$failed = $false

# Step 1: swappable handler assembly (Handlers/*.cs, engine refs only) -- same as
# make-handlers.ps1 -DllOnly but into scratch output.
$handlerSources = Get-ChildItem (Join-Path $ModRoot "Handlers") -Filter "*.cs" -File |
    ForEach-Object { $_.FullName }
if ($handlerSources) {
    Write-Host "gate: compiling $($handlerSources.Count) handler source(s)..."
    $cscArgs = @("/nologo", "/target:library", "/platform:x86", "/optimize+",
               "/out:$(Join-Path $OutDir 'gate-handlers.dll')") + $refArgs + $handlerSources
    & $csc.Exe @($csc.Args) @cscArgs
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}

# Step 2: full shell (every top-level *.cs except the packaging-only builder) --
# same glob as build-server-host-mod.ps1, scratch output, tracked BuildStamp.cs as-is.
$shellSources = Get-ChildItem $ModRoot -Filter "*.cs" -File |
    Where-Object { $_.Name -ne "Sims3PackageBuilder.cs" } |
    ForEach-Object { $_.FullName }
Write-Host "gate: compiling $($shellSources.Count) shell source(s)..."
$cscArgs = @("/nologo", "/target:library", "/platform:x86", "/optimize+",
           "/out:$(Join-Path $OutDir 'gate-shell.dll')") + $refArgs + $shellSources
& $csc.Exe @($csc.Args) @cscArgs
if ($LASTEXITCODE -ne 0) { $failed = $true }

if ($failed) {
    Write-Host "MOD COMPILE GATE FAILED -- fix the errors above before committing." -ForegroundColor Red
    exit 1
}
Write-Host "mod compile gate OK (handler + shell, full mod sources)" -ForegroundColor Green
exit 0
