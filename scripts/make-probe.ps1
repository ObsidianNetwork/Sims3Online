# make-probe.ps1 — compile a scratch-probe .cs into a .NET 2 assembly for hot-injection ( T5).
#
# Mirrors build-server-host-mod.ps1's toolchain exactly: csc v2.0.50727 + the same
# refs/sims3 reference set (reference-only stubs), so a probe can call any engine type the mod can.
# Each build gets a UNIQUE assembly name (there is no assembly unload in .NET 2 — every uploaded
# probe stays resident in TS3's AppDomain; a colliding name would shadow or fail to load).
#
#   .\scripts\make-probe.ps1 path\to\Probe.cs
#   .\scripts\make-probe.ps1 path\to\Probe.cs -Name MyProbe
#
# Probe convention: a public type with `public static string Run()`, invoked by DIAG_RUN on the
# simulator pump. C#2 / .NET 2 only (no var/LINQ/lambdas/async; anonymous delegates OK).
#
# STDOUT's final line is the built DLL path (bridgectl `probe` captures it). Diagnostics go to the
# information stream so they never pollute that contract.
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Source,
    [string]$Name,
    [string]$Stamp
)

$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptRoot "..")
$Assemblies = Join-Path $RepoRoot "refs/sims3"
$OutputDir = Join-Path $RepoRoot ".omo/cache/probes"

if (-not (Test-Path $Source)) {
    throw "Probe source not found: $Source"
}

. (Join-Path $ScriptRoot "find-csc.ps1")
$csc = Find-Csc20
$cscExe = $csc.Exe
$cscPreArgs = $csc.Args

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# Unique assembly name per build: <base>_<stamp>. Stamp is caller-supplied (bridgectl passes a
# blob id so the DLL name, assembly name, and upload id all line up) or derived from the clock.
if (-not $Name) {
    $Name = [System.IO.Path]::GetFileNameWithoutExtension($Source)
}
if (-not $Stamp) {
    $Stamp = (Get-Date -Format "yyyyMMddHHmmss")
}
$AssemblyName = ($Name -replace "[^A-Za-z0-9_]", "_") + "_" + $Stamp
$OutputDll = Join-Path $OutputDir ($AssemblyName + ".dll")

$References = @(
    "scripts_00.dll",
    "scripts_01.dll",
    "scripts_02.dll",
    "gameplay_00.dll",
    "gameplay_01.dll",
    "gameplay_02.dll",
    "gameplay_03.dll"
) | ForEach-Object { Join-Path $Assemblies $_ }

foreach ($Reference in $References) {
    if (-not (Test-Path $Reference)) {
        throw "Missing Sims 3 reference assembly: $Reference"
    }
}

$CompilerArgs = @(
    "/nologo",
    "/target:library",
    "/platform:x86",
    "/optimize+",
    "/out:$OutputDll"
)
foreach ($Reference in $References) {
    $CompilerArgs += "/reference:$Reference"
}
$CompilerArgs += (Resolve-Path $Source).Path

# All diagnostics go to STDERR so STDOUT carries only the DLL path (the bridgectl contract).
# NB: never pipe csc into Write-Information — with /nologo csc emits nothing, the pipeline fails
# to bind -MessageData, and a non-NonInteractive host then PROMPTS on stdin and hangs forever.
[Console]::Error.WriteLine("Compiling $Source -> $AssemblyName.dll")
$cscOutput = & $cscExe @cscPreArgs @CompilerArgs 2>&1
foreach ($line in $cscOutput) { [Console]::Error.WriteLine($line) }
if ($LASTEXITCODE -ne 0) {
    throw "csc failed (exit $LASTEXITCODE) compiling $Source"
}

# Contract: final stdout line is the DLL path.
Write-Output $OutputDll
