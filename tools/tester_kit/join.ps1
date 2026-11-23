# SimsOnline tester kit: join a host's game.
#
#   powershell -ExecutionPolicy Bypass -File join.ps1 [-Server <ip:port>] [-Sim <first name or id>] [-Player <n>]
#
# Starts the client controller against the host, the way stackctl.py does for a client box, and the controller starts
# The Sims 3. Before that it reads the server's Sims once to turn the Sim's name into its id (about 15 s). Prints each
# step it waits for: the world download, the game start, the join and the claim. Leave the window open while playing;
# closing the game ends the session. The last values are kept in config.json, so later joins need no arguments.
#
# Developer options (the kit's proof on the host): -SandboxieBox, -SandboxieStart, -SavesDir, -BridgectlListen,
# -StateFile.
param(
    [string]$Server = "",
    [string]$Sim = "",
    [int]$Player = 0,
    [string]$Display = "",
    [ValidateSet("json", "line")]
    [string]$Wire = "json",
    [int]$TimeoutMinutes = 20,
    [string]$SandboxieBox = "",
    [string]$SandboxieStart = "C:\Program Files\Sandboxie-Plus\Start.exe",
    [string]$SavesDir = "",
    [string]$BridgectlListen = "",
    [string]$StateFile = ""
)

# Card 57 read-only lookup. Kept in join.ps1 so older kit packagers include it.
function Split-WireComposite([string]$Text, [char]$Open = '(', [char]$Close = ')', [char]$Separator = ';') {
    if ($Text.Length -lt 2 -or $Text[0] -ne $Open -or $Text[$Text.Length - 1] -ne $Close) { throw "bad composite" }
    $inner = $Text.Substring(1, $Text.Length - 2)
    if ($inner.Length -eq 0) { return }
    $closers = New-Object 'System.Collections.Generic.Stack[char]'
    $start = 0
    for ($i = 0; $i -lt $inner.Length; $i++) {
        $c = $inner[$i]
        if ($c -eq '(') { $closers.Push(')') }
        elseif ($c -eq '[') { $closers.Push(']') }
        elseif ($c -eq ')' -or $c -eq ']') {
            if ($closers.Count -eq 0 -or $closers.Pop() -ne $c) { throw "bad composite" }
        }
        elseif ($c -eq $Separator -and $closers.Count -eq 0) {
            $inner.Substring($start, $i - $start)
            $start = $i + 1
        }
    }
    if ($closers.Count -ne 0) { throw "bad composite" }
    $inner.Substring($start)
}

function ConvertFrom-WireHex([string]$Text) {
    if ($Text.Length % 2 -ne 0 -or $Text -cmatch '[^0-9a-f]') { throw "bad hex" }
    $bytes = New-Object byte[] ($Text.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($Text.Substring(2 * $i, 2), 16) }
    (New-Object System.Text.UTF8Encoding($false, $true)).GetString($bytes)
}

function Read-WireLine($Reader, [int]$MaxLineBytes = 4194304) {
    $line = New-Object System.Text.StringBuilder
    while ($true) {
        $next = $Reader.Read()
        if ($next -eq -1) {
            if ($line.Length -eq 0) { return $null }
            throw "unterminated line"
        }
        if ($next -eq 10) { break }
        if ($line.Length -ge $MaxLineBytes + 1) { throw "line_too_long" }
        $null = $line.Append([char]$next)
    }
    if ($line.Length -gt 0 -and $line[$line.Length - 1] -eq [char]13) { $line.Length-- }
    $text = $line.ToString()
    if ([Text.Encoding]::UTF8.GetByteCount($text) -gt $MaxLineBytes) { throw "line_too_long" }
    return $text
}

function Read-LineSims($Reader, $Writer, [int]$Player, [int]$MaxBulkBytes = 268435456,
                       [int]$MaxInFlightBulks = 16, [int]$MaxLineBytes = 4194304) {
    $Writer.Write("HELLO|1|ring=1,wfs=1,role=client`n")
    $Writer.Flush()
    $hello = Read-WireLine $Reader $MaxLineBytes
    if ($hello -notmatch '^WELCOME\|1\|\d+\|') { throw "the host refused the line hello: $hello" }
    $Writer.Write("CMD|1|RequestSnapshot|1|$Player|~`n")
    $Writer.Flush()
    $snapshot = $null
    $bulkId = $null
    $next = 0
    $count = $null
    $bulk = New-Object System.IO.MemoryStream
    try {
        while ($null -eq $snapshot) {
            $line = Read-WireLine $Reader $MaxLineBytes
            if ($null -eq $line) { throw "the host closed before the Sims list arrived" }
            if ([Text.Encoding]::UTF8.GetByteCount($line) -gt 4194304) { throw "line_too_long" }
            $fields = $line.Split('|')
            if ($fields[0] -eq 'ERR') {
                if ($fields.Length -ne 5) { throw "bad ERR frame" }
                throw ("the host refused the Sims list: " + $fields[2] + " " + (ConvertFrom-WireHex $fields[4]))
            }
            if (($fields[0] -eq 'EV' -or $fields[0] -eq 'EVT') -and $fields.Length -eq 6 -and $fields[4] -eq 'FullSnapshot') {
                # One RequestSnapshot has one snapshot field. Do not replace an unfinished one.
                if ($null -ne $bulkId) { throw "bulk_malformed" }
                if ($fields[5] -match '^b(\d+)$') {
                    if ($MaxInFlightBulks -lt 1) { throw "bulk_malformed" }
                    $bulkId = $Matches[1]
                }
                else { $snapshot = $fields[5] }
            }
            elseif ($fields[0] -eq 'BULK') {
                if ($null -eq $bulkId -or $fields.Length -ne 5 -or $fields[1] -ne $bulkId) { throw "bulk_malformed" }
                $index = [uint64]$fields[2]
                $declaredCount = [uint64]$fields[3]
                if ($declaredCount -eq 0 -or $index -ne $next -or $index -ge $declaredCount -or
                    ($null -ne $count -and $count -ne $declaredCount)) { throw "bulk_malformed" }
                $chunk = [Convert]::FromBase64String($fields[4])
                if ($chunk.Length -gt 3145632 -or $chunk.Length -gt $MaxBulkBytes - $bulk.Length) { throw "bulk_malformed" }
                $bulk.Write($chunk, 0, $chunk.Length)
                $count = $declaredCount
                $next++
                if ($next -eq $count) { $snapshot = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($bulk.ToArray()) }
            }
        }
    } finally { $bulk.Dispose() }
    $world = @(Split-WireComposite $snapshot)
    if ($world.Count -ne 6) { throw "bad snapshot" }
    foreach ($sim in @(Split-WireComposite $world[4] '[' ']' ',')) {
        $values = @(Split-WireComposite $sim)
        if ($values.Count -ne 12) { throw "bad Sim snapshot" }
        New-Object PSObject -Property @{
            Id = $values[0]; Name = (ConvertFrom-WireHex $values[1])
            Owner = $(if ($values[5] -eq '~') { 'null' } else { $values[5] })
        }
    }
}

$ErrorActionPreference = "Stop"
$Here = $PSScriptRoot
function Say([string]$Text) { Write-Host ("[join " + (Get-Date -Format "HH:mm:ss") + "] $Text") }
function Fail([string]$Text) { Write-Host "[join] STOPPED: $Text" -ForegroundColor Red; exit 1 }

$configPath = Join-Path $Here "config.json"
if (-not (Test-Path -LiteralPath $configPath)) { Fail "no config.json next to join.ps1: run install.ps1 from the kit first." }
$config = Get-Content $configPath -Raw | ConvertFrom-Json
if (-not $Server) { $Server = [string]$config.server }
if (-not $Sim) { $Sim = [string]$config.sim }
if ($Player -le 0) { $Player = [int]$config.player }
if ($Player -le 0) { $Player = 2 }
if (-not $Server) { $Server = Read-Host "Host address (ip:port, from the host, e.g. 100.101.102.103:34019)" }
if (-not $Sim) { $Sim = Read-Host "Your Sim's id or first name (from the host)" }
if (-not $Display) { $Display = "player-$Player" }
if ($Server -notmatch '^[^:]+:\d+$') { Fail "-Server must be <ip>:<port>, got '$Server'" }
$hostName, $port = $Server.Split(':')

$Exe = Join-Path $Here "client\SimsOnline.Client.exe"
if (-not (Test-Path -LiteralPath $Exe)) { Fail "no ${Exe}: run install.ps1 from the kit again." }
$Ts3 = [string]$config.ts3
if (-not (Test-Path -LiteralPath $Ts3)) { Fail "no game at ${Ts3}: run install.ps1 again (or with -GameDir)." }
if (-not $SavesDir) { $SavesDir = Join-Path ([string]@($config.profiles)[0]) "Saves" }
# The client writes the host's world here and refuses a folder that does not exist (a profile never played in).
New-Item -ItemType Directory -Force -Path $SavesDir | Out-Null
if (Get-Process -Name TS3W -ErrorAction SilentlyContinue | Where-Object { -not $SandboxieBox }) {
    Fail "The Sims 3 is already running: close it; join starts the game itself."
}
if (-not $SandboxieBox -and -not (Get-Process -Name steam -ErrorAction SilentlyContinue)) {
    Say "NOTE: Steam is not running. If the game does not start, start Steam, sign in and join again."
}

# 1. Can the host be reached?
Say "connecting to the host $Server ..."
$tcp = New-Object System.Net.Sockets.TcpClient
try {
    $wait = $tcp.BeginConnect($hostName, [int]$port, $null, $null)
    if (-not $wait.AsyncWaitHandle.WaitOne(10000) -or -not $tcp.Connected) { throw "no answer in 10 s" }
    $tcp.EndConnect($wait)
} catch {
    Fail "the host $Server does not answer ($($_.Exception.Message)). Is the host's server up, and is the tunnel (Tailscale) connected on both PCs?"
}

# 2. The Sim's id, from one read of the server's Sims (a read-only request the server allows before joining).
$SimId = $null
$SimName = $Sim
if ($Sim -match '^\d+$') {
    $SimId = $Sim
    $tcp.Close()
} else {
    Say "asking the host for its Sims to find '$Sim' (about 15 s) ..."
    $stream = $tcp.GetStream()
    $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)))
    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
    $commandId = [guid]::NewGuid().ToString()
    $tcp.ReceiveTimeout = 90000
    $sims = @()
    if ($Wire -eq "line") {
        try { $sims = @(Read-LineSims $reader $writer $Player) }
        catch { Fail "no Sims list from the line host ($($_.Exception.Message))." }
        finally { $tcp.Close() }
    } else {
    $writer.Write('{"$type":"request_snapshot","commandId":"' + $commandId + '","playerId":' + $Player + ',"sinceTick":null}' + "`n")
    $writer.Flush()
    $tcp.ReceiveTimeout = 90000
    $snapshot = $null
    try {
        while ($true) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            if ($line.Contains('"command_rejected"') -and $line.Contains($commandId)) { Fail "the host refused the Sims list: $line" }
            if ($line.Contains('"$type":"full_snapshot"')) { $snapshot = $line; break }
        }
    } catch {
        Fail "no Sims list from the host in 90 s ($($_.Exception.Message))."
    } finally {
        $tcp.Close()
    }
    if (-not $snapshot) { Fail "the host closed the connection before sending its Sims." }
    # Each Sim starts {"objectId":<id>,"name":"<name>" (SimSnapshot's first two fields; objects have no name). Ids
    # exceed 2^63, which Windows PowerShell's JSON reader does not keep exactly, so the line is read with regexes.
    $sims = @()
    foreach ($chunk in ($snapshot -split '\{"objectId":')) {
        $m = [regex]::Match($chunk, '^(\d+),"name":"((?:[^"\\]|\\.)*)"')
        if (-not $m.Success) { continue }
        $owner = [regex]::Match($chunk, '"owningPlayerId":(null|\d+)')
        $sims += New-Object PSObject -Property @{ Id = $m.Groups[1].Value; Name = $m.Groups[2].Value; Owner = $owner.Groups[1].Value }
    }
    }
    $hits = @($sims | Where-Object { $_.Name -like "*$Sim*" })
    $first = @($hits | Where-Object { ($_.Name -split ' ')[0] -eq $Sim })
    if ($first.Count -eq 1) { $hits = $first }
    # Once any player has claimed a Sim, the server lists only the Sims on the lots players are on (interest scope).
    if ($hits.Count -eq 0) {
        Fail ("the host's Sims list ($($sims.Count) Sims) has no '$Sim'. It only shows Sims where a player already is, " +
            "so ask the host for your Sim's id (on the host: python scripts/household_read.py --sim $Sim) and join with -Sim <id>.")
    }
    if ($hits.Count -gt 1) { Fail ("'$Sim' matches several Sims: " + (($hits | Select-Object -First 8 | ForEach-Object { $_.Name }) -join ", ") + ". Use the full name or the id.") }
    $SimId = $hits[0].Id
    $SimName = $hits[0].Name
    if ($hits[0].Owner -and $hits[0].Owner -ne "null" -and $hits[0].Owner -ne [string]$Player) {
        Fail "$SimName is already played by player $($hits[0].Owner). Ask the host which Sim is yours."
    }
    Say "your Sim: $SimName ($SimId)"
}

$config.server = $Server
$config.sim = $Sim
$config.player = $Player
$config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8

# 3. The controller, with the environment quick_loop gives a client (CLIENT_ENV / CLIENT_UNSET).
$logs = Join-Path $Here "logs"
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$log = Join-Path $logs "client-$stamp.log"
if (-not $StateFile) { $StateFile = Join-Path $logs "client.state.json" }
$arguments = @("--endpoint", $Server, "--player", "$Player", "--display", $Display, "--role", "client", "--enable-mirror",
    "--claim-sim", "$SimId", "--client-save", "Sunset Valley.sims3", "--client-host-timeout", "900",
    "--client-ts3", $Ts3, "--client-world-from-server", "--client-saves-dir", $SavesDir,
    "--state-file", $StateFile, "--snapshot-log", (Join-Path $logs "snapshot-$stamp.log"), "--connect-timeout", "60")
if ($Wire -eq "line") { $arguments += @("--wire", "line") }
if ($SandboxieBox) { $arguments += @("--client-ts3-sandboxie-box", $SandboxieBox, "--client-ts3-sandboxie-start", $SandboxieStart) }
if ($BridgectlListen) { $arguments += @("--client-bridgectl-listen", $BridgectlListen) }
$argumentLine = ($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '

foreach ($name in @("SIMSONLINE_SIMLINK_CLIENT_RING", "SIMSONLINE_WORLD_FROM_SERVER")) { [Environment]::SetEnvironmentVariable($name, "1", "Process") }
foreach ($name in @("SIMSONLINE_AUTHORITY_GATE", "SIMSONLINE_MIRROR_INTERACTIONS", "SIMSONLINE_SIMLINK_SHADOW", "SIMSONLINE_SIMLINK_SERVER_RING")) {
    [Environment]::SetEnvironmentVariable($name, $null, "Process")
}

Say "starting the SimsOnline client (log: $log)"
$proc = Start-Process -FilePath $Exe -ArgumentList $argumentLine -WorkingDirectory $Here -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $log -RedirectStandardError ($log + ".err")
$null = $proc.Handle  # keeps the process handle so ExitCode is readable after it exits
Set-Content -Path (Join-Path $logs "client.pid") -Value $proc.Id

# 4. Follow the log: one line per step, until the claim, the controller's exit or the timeout.
$steps = [ordered]@{
    "client_ts3_starting" = "starting The Sims 3 ...";
    "world_requested" = "downloading the host's world ...";
    "world_received" = "world received; the game is loading it (1 to 3 minutes) ...";
    "client_ts3_session_ready" = "the game is in the world; joining the session ...";
    '"session_joined"' = "joined; claiming $SimName ...";
    '"sim_claimed"' = "claimed $SimName. Play from this game: your picks run on the host."
}
$seen = @{}
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$claimed = $false
while ((Get-Date) -lt $deadline -and -not $proc.HasExited -and -not $claimed) {
    Start-Sleep -Seconds 2
    if (-not (Test-Path -LiteralPath $log)) { continue }
    # The controller holds the log open for writing: read it with ReadWrite sharing.
    $fs = New-Object System.IO.FileStream($log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try { $text = (New-Object System.IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
    foreach ($key in $steps.Keys) {
        if (-not $seen.ContainsKey($key) -and $text.Contains($key)) { $seen[$key] = $true; Say $steps[$key] }
    }
    if ($seen.ContainsKey('"sim_claimed"')) { $claimed = $true }
}
if ($proc.HasExited) {
    Fail "the client stopped (exit $($proc.ExitCode)) before the claim. Send the logs: run collect-logs.ps1."
}
if (-not $claimed) { Say "WARNING: no claim within $TimeoutMinutes minutes; the client keeps running. Send the logs if the game does not join." }

Say "leave this window open while you play. Closing the game ends the session."
$proc.WaitForExit()
Say "the client ended (exit $($proc.ExitCode)). Something wrong? Run collect-logs.ps1 and send the zip to the host."
