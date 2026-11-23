# probe-session.ps1 -- SHIM to stackctl.py (isolated probe stack)
#
# The original probe-session.ps1 (209 LOC) ran a parallel stack on alternate ports
# (34020/34031/34032) using its own Sandboxie boxes. Now delegates to stackctl.py:
# SIMSONLINE_RUN_DIR isolates the state dir (.omo/probe-run/) and the
# SIMSONLINE_*_BRIDGECTL env vars move the bridgectl listeners off the main
# dev-stack ports.
#
# Windows-only limitation: stackctl never sandboxes the server host, so
# $ServerSandboxBox has no effect on the server process (the probe client still
# runs in its own box).
#
# Usage unchanged:
#   pwsh -NoProfile -File scripts/probe-session.ps1 up
#   pwsh -NoProfile -File scripts/probe-session.ps1 status
#   pwsh -NoProfile -File scripts/probe-session.ps1 down

param(
    [Parameter(Position = 0)]
    [ValidateSet("up", "server", "client", "status", "logs", "down")]
    [string]$Verb = "status",
    [string]$Save = "Sunset Valley.sims3",
    [string]$Endpoint = "127.0.0.1:34020",
    [int]$ServerBridgectlPort = 34031,
    [int]$ClientBridgectlPort = 34032,
    [UInt64]$ClaimSim = 18031287978496800304,
    [string]$Ts3Path = "C:\Program Files (x86)\The Sims 3\Game\Bin\TS3W.exe",
    [string]$ServerSandboxBox = "ProbeHost",
    [string]$ClientSandboxBox = "ProbeClient",
    [string]$SandboxieStart = "C:\Program Files\Sandboxie-Plus\Start.exe",
    [int]$HostTimeoutSeconds = 600,
    [int]$ClientLaunchDelaySeconds = 8,
    [switch]$Build,
    [switch]$Quick
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

$ctlArgs = @($Verb)
if ($Build) { $ctlArgs += "--build" }
if ($Quick) { $ctlArgs += "--quick" }
$ctlArgs += @("--endpoint", $Endpoint)
$ctlArgs += @("--save", $Save)
$ctlArgs += @("--host-timeout", $HostTimeoutSeconds)
$ctlArgs += @("--claim-sim", $ClaimSim)
$ctlArgs += @("--ts3-path", $Ts3Path)
$ctlArgs += @("--client-launch-delay", $ClientLaunchDelaySeconds)
# The probe's client is player 1, so its box goes to --sandboxie-box (not -box2).
$ctlArgs += @("--sandboxie-box", $ClientSandboxBox)
$ctlArgs += @("--sandboxie-start", $SandboxieStart)

# Isolate from the main dev stack: own run dir + own bridgectl ports.
$env:SIMSONLINE_RUN_DIR = Join-Path $RepoRoot ".omo" "probe-run"
$env:SIMSONLINE_SERVER_BRIDGECTL = "127.0.0.1:$ServerBridgectlPort"
$env:SIMSONLINE_CLIENT_BRIDGECTL = "127.0.0.1:$ClientBridgectlPort"

$python = if ($IsLinux -or -not $IsWindows) { "python3" } else { "python" }
& $python (Join-Path $PSScriptRoot "stackctl.py") @ctlArgs
exit $LASTEXITCODE
