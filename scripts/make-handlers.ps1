# make-handlers.ps1 — / T5b: build the SWAPPABLE handler assembly and embed it into the
# shell as a generated EmbeddedHandlerAssembly.cs.
#
#   .\scripts\make-handlers.ps1
#
# The handler assembly (SimsOnlineServerHostMod/Handlers/*.cs) references the 7 engine DLLs ONLY —
# NOT the shell, NOT a contract assembly. The reflection-only topology (proven by the DIAG_HANDLERTEST
# spike) crosses only mscorlib (System.Converter<object[],object>) + engine-ref types, so cross-
# assembly type identity is guaranteed and there is no build cycle.
#
# Runs as make-mod step 0 (before build-server-host-mod.ps1): build handler -> hex-embed -> the shell
# build then globs the generated EmbeddedHandlerAssembly.cs in. Cold-boot loads the embed on the pump.
#
#   .\scripts\make-handlers.ps1 -DllOnly   # T5d: build ONLY the handler DLL (no embed
#                                          # regeneration — keeps the shell source clean) and print
#                                          # its path. Used by `bridgectl reload` for live hot-swap.

param([switch]$DllOnly)

$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptRoot "..")
$ModRoot = Join-Path $RepoRoot "SimsOnlineServerHostMod"
$Assemblies = Join-Path $RepoRoot "refs\sims3"
$HandlersDir = Join-Path $ModRoot "Handlers"
$OutputDir = Join-Path $ModRoot "bin\Release"
$EmbedPath = Join-Path $ModRoot "EmbeddedHandlerAssembly.cs"

# Unique assembly name per build (no .NET2 unload — a colliding name shadows/aliases the resident one;
# unique names keep every reload distinct, matching the make-probe.ps1 rule).
$gitSha = "unknown"
Push-Location $RepoRoot
try {
    $out = git rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $out) { $gitSha = $out.Trim() }
}
finally { Pop-Location }
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddHHmmss")
$AssemblyName = "SimsOnlineHandlers_" + ($gitSha -replace "[^A-Za-z0-9_]", "_") + "_" + $stamp
$EntryTypeName = "SimsOnline.Handlers.HandlerSet"
$OutputDll = Join-Path $OutputDir ($AssemblyName + ".dll")

. (Join-Path $ScriptRoot "..\scripts\find-csc.ps1")
$csc = Find-Csc20
$cscExe = $csc.Exe
$cscPreArgs = $csc.Args

if (-not (Test-Path $HandlersDir)) {
    throw "Handler source folder missing: $HandlersDir"
}

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

$Sources = Get-ChildItem $HandlersDir -Filter "*.cs" -File | ForEach-Object { $_.FullName }
if (-not $Sources) {
    throw "No handler sources in $HandlersDir"
}

$CompilerArgs = @(
    "/nologo",
    "/target:library",
    "/platform:x86",
    "/optimize+",
    "/out:$OutputDll"
)
foreach ($Reference in $References) { $CompilerArgs += "/reference:$Reference" }
$CompilerArgs += $Sources

[Console]::Error.WriteLine("Compiling handler assembly $AssemblyName.dll from $($Sources.Count) source(s)")
$cscOutput = & $cscExe @cscPreArgs @CompilerArgs 2>&1
foreach ($line in $cscOutput) { [Console]::Error.WriteLine($line) }
if ($LASTEXITCODE -ne 0) {
    throw "csc failed (exit $LASTEXITCODE) building the handler assembly"
}

# T5d: -DllOnly builds just the handler DLL for live RELOAD_HANDLERS (bridgectl reload). It
# does NOT regenerate EmbeddedHandlerAssembly.cs (a live reload must not dirty the shell source — the
# embed only matters for the NEXT cold boot, which a full make-mod handles). Print the DLL path on
# stdout (diagnostics already went to stderr) so the caller can read + upload it.
if ($DllOnly) {
    Write-Output $OutputDll
    return
}

# Hex-encode the DLL bytes into chunked C# string literals (csc v2 has a string-literal length cap;
# mirror EmbeddedLoadTestAssembly.cs — 120 hex chars (60 bytes) per "" + "..." continuation line).
$bytes = [System.IO.File]::ReadAllBytes($OutputDll)
$hex = -join ($bytes | ForEach-Object { $_.ToString("x2") })

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("namespace SimsOnlineServerHost")
[void]$sb.AppendLine("{")
[void]$sb.AppendLine("    // DEV-120 / T5b: hex of the SWAPPABLE handler assembly ($EntryTypeName),")
[void]$sb.AppendLine("    // built engine-only by scripts/make-handlers.ps1 and loaded on the pump by")
[void]$sb.AppendLine("    // BuildDispatch via Assembly.Load(byte[]). GENERATED -- do not hand-edit.")
[void]$sb.AppendLine("    internal static class EmbeddedHandlerAssembly")
[void]$sb.AppendLine("    {")
[void]$sb.AppendLine("        public const string AsmName = `"$AssemblyName`";")
[void]$sb.AppendLine("        public const string EntryTypeName = `"$EntryTypeName`";")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("        public const string Hex = `"`"")
$chunkLen = 120
for ($i = 0; $i -lt $hex.Length; $i += $chunkLen) {
    $len = [Math]::Min($chunkLen, $hex.Length - $i)
    $chunk = $hex.Substring($i, $len)
    [void]$sb.AppendLine("            + `"$chunk`"")
}
[void]$sb.AppendLine("            ;")
[void]$sb.AppendLine("    }")
[void]$sb.AppendLine("}")

# UTF-8 no BOM, to match the existing generated files (csc v2 tolerates BOM but keep it clean).
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($EmbedPath, $sb.ToString(), $utf8NoBom)

Write-Host "make-handlers OK: $AssemblyName.dll ($($bytes.Length) bytes) -> $EmbedPath"
