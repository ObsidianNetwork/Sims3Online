"""PowerShell tester-kit v1 decoder tests; no game, installation or network required."""
import shutil
import subprocess
import unittest
from pathlib import Path


@unittest.skipUnless(shutil.which("pwsh"), "PowerShell 7 is not installed")
class KitWireTests(unittest.TestCase):
    def run_decoder_script(self, command):
        setup = r"""
$ErrorActionPreference = 'Stop'
$ast = [Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$null, [ref]$null)
$ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $false) | ForEach-Object {
    . ([scriptblock]::Create($_.Extent.Text))
}
"""
        result = subprocess.run(["pwsh", "-NoProfile", "-CommandWithArgs", setup + command,
                                 str(Path(__file__).with_name("join.ps1"))],
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_bulk_byte_budget_is_checked_before_append(self):
        self.run_decoder_script(r"""
$reader = [IO.StringReader]::new("WELCOME|1|4|`nEV|1|7|1|FullSnapshot|b1`nBULK|1|0|2|MTIz`nBULK|1|1|2|NDU=`n")
try { Read-LineSims $reader ([IO.StringWriter]::new()) 9 -MaxBulkBytes 4; throw 'accepted excessive bulk' }
catch { if (-not $_.Exception.Message.Contains('bulk_malformed')) { throw } }
exit 0
""")

    def test_second_pending_snapshot_bulk_cannot_replace_the_first(self):
        self.run_decoder_script(r"""
$field = '(7;57;53;Normal;[];[])'
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($field))
$reader = [IO.StringReader]::new("WELCOME|1|4|`nEV|1|7|1|FullSnapshot|b1`nEV|2|7|1|FullSnapshot|b2`nBULK|2|0|1|$b64`n")
try { Read-LineSims $reader ([IO.StringWriter]::new()) 9 -MaxInFlightBulks 1; throw 'replaced pending bulk' }
catch { if (-not $_.Exception.Message.Contains('bulk_malformed')) { throw } }
exit 0
""")

    def test_line_reader_stops_buffering_at_the_limit(self):
        self.run_decoder_script(r"""
$reader = [IO.StringReader]::new(('x' * 100) + "`n")
try { Read-WireLine $reader -MaxLineBytes 8; throw 'accepted oversized line' }
catch { if (-not $_.Exception.Message.Contains('line_too_long')) { throw } }
if ($reader.Peek() -eq -1) { throw 'buffered the entire oversized line' }
exit 0
""")

    def test_bulk_snapshot_preserves_u64_name_and_owner(self):
        helper = Path(__file__).with_name("join.ps1")
        command = r"""
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $false) | ForEach-Object {
    . ([scriptblock]::Create($_.Extent.Text))
}
$field = '(7;576f726c64;53617665;Normal;[(18446744073709551614;416c696365;1;(1;2;3);~;9;~;~;~;~;~;~)];[])'
$half = [int]($field.Length / 2)
$a = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($field.Substring(0, $half)))
$b = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($field.Substring($half)))
$text = "WELCOME|1|4|`nACK|1|RequestSnapshot`nEV|1|7|1|FullSnapshot|b1`nBULK|1|0|2|$a`nBULK|1|1|2|$b`n"
$reader = [IO.StringReader]::new($text)
$writer = [IO.StringWriter]::new()
$sims = @(Read-LineSims $reader $writer 9)
if ($sims.Count -ne 1 -or $sims[0].Id -ne '18446744073709551614' -or $sims[0].Name -ne 'Alice' -or $sims[0].Owner -ne '9') { throw 'decoded Sim differs' }
if (-not $writer.ToString().StartsWith("HELLO|1|ring=1,wfs=1,role=client`nCMD|1|RequestSnapshot|1|9|~`n")) { throw 'wire requests differ' }
"""
        # -CommandWithArgs passes the helper path without interpolating a machine path into code.
        result = subprocess.run(["pwsh", "-NoProfile", "-CommandWithArgs", command, str(helper)],
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_malformed_bulk_is_refused(self):
        helper = Path(__file__).with_name("join.ps1")
        command = r"""
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $false) | ForEach-Object {
    . ([scriptblock]::Create($_.Extent.Text))
}
$reader = [IO.StringReader]::new("WELCOME|1|4|`nEV|1|7|1|FullSnapshot|b1`nBULK|1|1|2|`n")
$writer = [IO.StringWriter]::new()
try { Read-LineSims $reader $writer 9; throw 'accepted malformed bulk' }
catch { if (-not $_.Exception.Message.Contains('bulk_malformed')) { throw } }
exit 0
"""
        result = subprocess.run(["pwsh", "-NoProfile", "-CommandWithArgs", command, str(helper)],
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
