# build-presentation-module.ps1 — the client presentation module (SimsOnlineServerHostMod/Presentation/*.cs), a script
# assembly beside the shell (SimsOnlineServerHostMod/PresentationModules.cs).
#
#   .\scripts\build-presentation-module.ps1                       # SimsOnlinePresentation.dll: packaged with the shell
#   .\scripts\build-presentation-module.ps1 -AssemblyName X       # a reload build: its own name (.NET 2 cannot unload,
#                                                                 # and a repeated name aliases the resident assembly)
#
# Compiled with the shell's C# 2.0 compiler against the game's own runtime (simcore_00..02 with /nostdlib: an API that
# desktop .NET has and TS3 lacks fails here, not in the game), the engine assemblies and the built shell
# (bin\Release\SimsOnlineServerHostMod.dll: build the shell first). Prints the DLL path last.

param([string]$AssemblyName = "SimsOnlinePresentation")

$ErrorActionPreference = "Stop"
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptRoot "..")
$ModRoot = Join-Path $RepoRoot "SimsOnlineServerHostMod"
$Assemblies = Join-Path $RepoRoot "refs\sims3"
$OutputDir = Join-Path $ModRoot "bin\Release"
$Shell = Join-Path $OutputDir "SimsOnlineServerHostMod.dll"
$Sources = Get-ChildItem (Join-Path $ModRoot "Presentation") -Filter "*.cs" -File | ForEach-Object { $_.FullName }
$OutputDll = Join-Path $OutputDir ($AssemblyName + ".dll")

if (-not (Test-Path $Shell)) { throw "build the shell first: $Shell is missing" }
if (-not $Sources) { throw "no module sources in $ModRoot\Presentation" }

. (Join-Path $ScriptRoot "find-csc.ps1")
$csc = Find-Csc20
$References = @("simcore_00.dll", "simcore_01.dll", "simcore_02.dll", "scripts_00.dll", "scripts_01.dll", "scripts_02.dll",
    "gameplay_00.dll", "gameplay_01.dll", "gameplay_02.dll", "gameplay_03.dll") |
    ForEach-Object { Join-Path $Assemblies $_ }
$CompilerArgs = @("/nostdlib+", "/noconfig", "/target:library", "/platform:x86", "/optimize+", "/out:$OutputDll", "/nologo")
foreach ($Reference in $References) { $CompilerArgs += "/reference:$Reference" }
$CompilerArgs += "/reference:$Shell"
$CompilerArgs += $Sources

& $csc.Exe @($csc.Args) @CompilerArgs
if ($LASTEXITCODE -ne 0) { throw "presentation module compile failed (exit $LASTEXITCODE)" }
Write-Host $OutputDll
