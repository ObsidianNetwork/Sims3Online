$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptRoot "..")
$Assemblies = Join-Path $RepoRoot "refs\sims3"
$OutputDir = Join-Path $ScriptRoot "bin\Release"
$OutputDll = Join-Path $OutputDir "SimsOnlineServerHostMod.dll"
$StampPath = Join-Path $ScriptRoot "BuildStamp.cs"

function Write-BuildStampIfMissing {
    if (Test-Path $StampPath) { return }
    $gitSha = "unknown"
    Push-Location $RepoRoot
    try {
        $out = git rev-parse --short HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $out) { $gitSha = $out.Trim() }
    }
    finally { Pop-Location }
    $builtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    @"
namespace SimsOnlineServerHost
{
    internal static class BuildStamp
    {
        public static readonly string GitSha = "$gitSha";
        public static readonly string BuiltUtc = "$builtUtc";
        public static readonly string Sources = "unknown";  // make-mod.ps1 writes the hash

        public static string WireForm()
        {
            return GitSha + "@" + BuiltUtc;
        }
    }
}
"@ | Set-Content -Path $StampPath -Encoding UTF8
}

Write-BuildStampIfMissing

. (Join-Path $ScriptRoot "..\scripts\find-csc.ps1")
$csc = Find-Csc20
$cscExe = $csc.Exe
$cscPreArgs = $csc.Args

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

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
    "/target:library",
    "/platform:x86",
    "/optimize+",
    "/out:$OutputDll"
)

foreach ($Reference in $References) {
    $CompilerArgs += "/reference:$Reference"
}

$CompilerArgs += Get-ChildItem $ScriptRoot -Filter "*.cs" -File |
    Where-Object { $_.Name -ne "Sims3PackageBuilder.cs" } |
    ForEach-Object { $_.FullName }

# On Linux $cscExe is "wine" and $cscPreArgs is the csc.exe path; Unix paths are passed
# unmodified (wine maps the Unix root natively -- DO NOT translate to Z:\).
& $cscExe @cscPreArgs @CompilerArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built $OutputDll"
