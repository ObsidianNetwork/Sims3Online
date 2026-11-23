# SimsOnline tester kit: collect what the host needs after a problem.
#
#   powershell -ExecutionPolicy Bypass -File collect-logs.ps1 [-Newest 3]
#
# Zips the controller's newest logs (the mod's own log lines are in them: the mod writes through the bridge), the
# state file, the kit's manifest and config, and the newest crash reports TS3 wrote into each Sims 3 profile
# (xcpt*.txt, and the newest .mdmp), into a zip on the desktop. Nothing else from the PC is read.
param(
    [int]$Newest = 3,
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$Here = $PSScriptRoot
function Say([string]$Text) { Write-Host "[collect-logs] $Text" }

if (-not $OutDir) { $OutDir = [Environment]::GetFolderPath("Desktop") }
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$work = Join-Path $env:TEMP "simsonline-logs-$stamp"
New-Item -ItemType Directory -Force -Path $work | Out-Null

foreach ($name in @("manifest.json", "config.json")) {
    if (Test-Path -LiteralPath (Join-Path $Here $name)) { Copy-Item -LiteralPath (Join-Path $Here $name) -Destination $work }
}
$logs = Join-Path $Here "logs"
if (Test-Path -LiteralPath $logs) {
    New-Item -ItemType Directory -Force -Path (Join-Path $work "logs") | Out-Null
    $runs = Get-ChildItem -LiteralPath $logs -Filter "client-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First $Newest
    foreach ($run in $runs) {
        # Copy with ReadWrite sharing: a running controller still holds its log open.
        foreach ($path in @($run.FullName, ($run.FullName + ".err"))) {
            if (-not (Test-Path -LiteralPath $path)) { continue }
            $source = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            $target = [System.IO.File]::Create((Join-Path $work ("logs\" + (Split-Path -Leaf $path))))
            try { $source.CopyTo($target) } finally { $source.Dispose(); $target.Dispose() }
        }
    }
    foreach ($name in @("client.state.json")) {
        if (Test-Path -LiteralPath (Join-Path $logs $name)) { Copy-Item -LiteralPath (Join-Path $logs $name) -Destination (Join-Path $work "logs") }
    }
    Say "controller logs: $(@($runs).Count)"
}

$config = $null
if (Test-Path -LiteralPath (Join-Path $Here "config.json")) { $config = Get-Content (Join-Path $Here "config.json") -Raw | ConvertFrom-Json }
$profiles = @()
if ($config -and $config.profiles) { $profiles = @($config.profiles) }
$i = 0
foreach ($profileDir in $profiles) {
    $i += 1
    if (-not (Test-Path -LiteralPath $profileDir)) { continue }
    $target = Join-Path $work "crash-reports-$i"
    $reports = Get-ChildItem -LiteralPath $profileDir -Filter "xcpt*.txt" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First $Newest
    $dump = Get-ChildItem -LiteralPath $profileDir -Filter "xcpt*.mdmp" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $all = @($reports) + @($dump) | Where-Object { $_ }
    if ($all.Count -gt 0) {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        foreach ($file in $all) { Copy-Item -LiteralPath $file.FullName -Destination $target }
    }
    Say "profile ${i}: $(@($reports).Count) crash report(s)"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path $OutDir "SimsOnline-logs-$stamp.zip"
# .NET's zip, not Compress-Archive: a module cmdlet, missing when Windows PowerShell inherits another module path.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($work, $zip)
Remove-Item -LiteralPath $work -Recurse -Force
Say "wrote $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB). Send this file to the host."
