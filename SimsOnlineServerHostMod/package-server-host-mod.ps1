$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptRoot "..")
$OutputDir = Join-Path $ScriptRoot "bin\Release"
$DllPath = Join-Path $OutputDir "SimsOnlineServerHostMod.dll"
$PackagePath = Join-Path $OutputDir "SimsOnlineServerHostMod.package"
$BuilderSource = Join-Path $ScriptRoot "Sims3PackageBuilder.cs"
$BuilderExe = Join-Path $OutputDir "Sims3PackageBuilder.exe"

# Cross-platform s3pe path. On Windows s3pe lives at C:\Program Files\s3pe (real install).
# On Linux the s3pe DLLs are copied into the wine prefix at $WINEPREFIX/drive_c/Program Files/s3pe
# so the path "C:\Program Files\s3pe" resolves correctly inside wine for both the reference
# lookup and the builder exe's runtime s3pi resolution. WINEPREFIX defaults to ~/.wine.
$IsLinuxHost = -not $env:WINDIR -and -not $IsWindows
$S3PePath = "C:\Program Files\s3pe"
if ($IsLinuxHost) {
    $wineprefix = $env:WINEPREFIX
    if (-not $wineprefix) { $wineprefix = Join-Path $HOME ".wine" }
    $hostS3Pe = Join-Path $wineprefix "drive_c/Program Files/s3pe"
    if (-not (Test-Path $hostS3Pe)) {
        throw "s3pe not found in the wine prefix at $hostS3Pe. Copy the s3pi.*.dll from an existing s3pe install into $hostS3Pe (it is a Windows .NET app that runs under wine)."
    }
}

if (-not (Test-Path $DllPath)) {
    & (Join-Path $ScriptRoot "build-server-host-mod.ps1")
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

. (Join-Path $ScriptRoot "..\scripts\find-csc.ps1")
$csc = Find-Csc40
$cscExe = $csc.Exe
$cscPreArgs = $csc.Args

$ReferenceNames = @(
    "s3pi.Settings.dll",
    "s3pi.Interfaces.dll",
    "s3pi.Package.dll",
    "s3pi.ScriptResource.dll",
    "s3pi.NameMapResource.dll"
)

# Resolve references on the host filesystem so Test-Path works; pass them to csc as Unix paths
# (wine maps the Unix root natively, so the s3pi DLLs under $WINEPREFIX/drive_c/Program Files/s3pe
# are reachable both at compile time and at builder-exe runtime via C:\Program Files\s3pe).
$References = @()
foreach ($ReferenceName in $ReferenceNames) {
    if ($IsLinuxHost) {
        $ReferencePath = Join-Path $hostS3Pe $ReferenceName
    } else {
        $ReferencePath = Join-Path $S3PePath $ReferenceName
    }
    if (-not (Test-Path $ReferencePath)) {
        throw "Missing s3pi assembly: $ReferencePath. Install s3pe before packaging."
    }
    $References += $ReferencePath
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$CompilerArgs = @(
    "/target:exe",
    "/platform:x86",
    "/optimize+",
    "/out:$BuilderExe"
)

foreach ($Reference in $References) {
    $CompilerArgs += "/reference:$Reference"
}

$CompilerArgs += $BuilderSource

& $cscExe @cscPreArgs @CompilerArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

# The builder exe is a CLR4 app that calls s3pi.Package to write the .package. Run it via wine
# on Linux (the C:\Program Files\s3pe path it receives resolves inside the wine prefix).
$BuilderRunner = if ($IsLinuxHost) { "wine" } else { $BuilderExe }
$BuilderRunnerArgs = if ($IsLinuxHost) { @($BuilderExe, $DllPath, $PackagePath, $S3PePath) } else { @($DllPath, $PackagePath, $S3PePath) }
# The client presentation module (scripts/build-presentation-module.ps1), packaged as a second script assembly.
$ModuleDll = Join-Path $OutputDir "SimsOnlinePresentation.dll"
if (Test-Path $ModuleDll) { $BuilderRunnerArgs += $ModuleDll }
& $BuilderRunner @BuilderRunnerArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}