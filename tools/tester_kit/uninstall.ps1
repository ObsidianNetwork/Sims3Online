# SimsOnline tester kit: uninstall.
#
#   powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-KeepInstall]
#
# Puts back every file install.ps1 replaced (from its "<file>.simsonline-backup" copies) and removes the files it
# added: SimLink.asi and wininet.dll in Game\Bin, Resource.cfg and the kit's packages in each profile, the desktop
# shortcut, and (unless -KeepInstall) the install folder with the controller and logs. Saves are left alone.
param(
    [switch]$KeepInstall
)

$ErrorActionPreference = "Stop"
$Here = $PSScriptRoot
$BackupSuffix = ".simsonline-backup"
function Say([string]$Text) { Write-Host "[uninstall] $Text" }

if (Get-Process -Name TS3W -ErrorAction SilentlyContinue) { Write-Host "[uninstall] STOPPED: close The Sims 3 first." -ForegroundColor Red; exit 1 }
# No @(...) around ConvertFrom-Json: Windows PowerShell emits a JSON array as one object, so @() would make it one row.
$rows = Get-Content (Join-Path $Here "installed.json") -Raw | ConvertFrom-Json

# installed.json (install.ps1) says what the first install found at each target: put that back.
foreach ($row in $rows) {
    $target = $row.target
    $backup = $target + $BackupSuffix
    if ($row.how -eq "replaced" -and (Test-Path -LiteralPath $backup)) {
        Move-Item -LiteralPath $backup -Destination $target -Force
        Say "$target restored"
    } elseif ($row.how -eq "added" -and (Test-Path -LiteralPath $target)) {
        Remove-Item -LiteralPath $target -Force
        Say "$target removed"
    } else {
        Say "$target left as it is ($($row.how))"
    }
}

$link = Join-Path ([Environment]::GetFolderPath("Desktop")) "SimsOnline - join.lnk"
if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force; Say "desktop shortcut removed" }
Say "TS3 rebuilds scriptCache.package at its next start; the set-aside copies (scriptCache.simsonline-aside-*) can be deleted."

if (-not $KeepInstall) {
    Say "removing $Here (controller, logs, backups) ..."
    Set-Location $env:TEMP
    Remove-Item -LiteralPath $Here -Recurse -Force
}
Say "done."
