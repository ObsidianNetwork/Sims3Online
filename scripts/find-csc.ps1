# find-csc.ps1 -- cross-platform .NET Framework 2.0 csc resolver (2026-07-03 mod-build port).
#
# The mod (shell + swappable handler) targets C# 2 / CLR 2.0 because it runs INSIDE Sims 3's
# thin mono runtime -- not a modern .NET target. On Windows the compiler lives at
# $env:WINDIR\Microsoft.NET\Framework\v2.0.50727\csc.exe. On Linux there is no .NET 2 runtime,
# but Wine ships its own .NET 2 stack inside the prefix at
# drive_c/windows/Microsoft.NET/Framework/v2.0.50727/csc.exe, and wine executes it natively.
#
# Unix paths are passed to csc UNTRANSLATED -- wine's filesystem maps the Unix root at /, so
# /run/media/.../SimsOnlineServerHostMod/Foo.cs is readable by the Windows process. DO NOT
# translate to Z:\ paths; that triggers System.ArgumentException "Illegal characters in path"
# inside csc's response-file parser.
#
# Returns a hashtable with the invocation prefix:
#   $csc = Find-Csc20
#   & $csc.Exe $csc.Args[0] $csc.Args[1] ... /out:foo.dll source.cs
# On Linux $csc.Exe is "wine" and $csc.Args is @("C:\...\csc.exe"); on Windows $csc.Exe is the
# csc path itself and $csc.Args is @().

function Find-Csc20 {
    # Windows: $env:WINDIR is set and the framework path exists.
    if ($env:WINDIR) {
        $frameworkPath = Join-Path $env:WINDIR "Microsoft.NET\Framework\v2.0.50727"
        $cscExe = Join-Path $frameworkPath "csc.exe"
        if (Test-Path $cscExe) {
            return @{ Exe = $cscExe; Args = @() }
        }
    }

    # Linux: use the wine-shipped csc. WINEPREFIX defaults to ~/.wine; the SimsOnline dev
    # prefix is ~/.sims3 (scripts/stackctl.py sets it). Resolve from $WINEPREFIX or the default.
    $wineprefix = $env:WINEPREFIX
    if (-not $wineprefix) {
        $wineprefix = Join-Path $HOME ".wine"
    }
    $wineCsc = Join-Path $wineprefix "drive_c/windows/Microsoft.NET/Framework/v2.0.50727/csc.exe"
    if (-not (Test-Path $wineCsc)) {
        $windowsCsc = if ($env:WINDIR) { Join-Path $env:WINDIR 'Microsoft.NET\Framework\v2.0.50727\csc.exe' } else { "(WINDIR not set)" }
        throw "Could not find the .NET Framework 2.0 compiler. Looked at: $windowsCsc (WINDIR), $wineCsc (WINEPREFIX). On Linux install Wine (the prefix ships csc.exe) or run on Windows."
    }
    return @{ Exe = "wine"; Args = @($wineCsc) }
}

function Find-Csc40 {
    # The package builder targets CLR 4 (it runs on the host, not in TS3). Same wine trick.
    if ($env:WINDIR) {
        $frameworkPath = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319"
        $cscExe = Join-Path $frameworkPath "csc.exe"
        if (Test-Path $cscExe) {
            return @{ Exe = $cscExe; Args = @() }
        }
    }
    $wineprefix = $env:WINEPREFIX
    if (-not $wineprefix) {
        $wineprefix = Join-Path $HOME ".wine"
    }
    $wineCsc = Join-Path $wineprefix "drive_c/windows/Microsoft.NET/Framework/v4.0.30319/csc.exe"
    if (-not (Test-Path $wineCsc)) {
        $windowsCsc = if ($env:WINDIR) { Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' } else { "(WINDIR not set)" }
        throw "Could not find the .NET Framework 4 compiler. Looked at: $windowsCsc (WINDIR), $wineCsc (WINEPREFIX)."
    }
    return @{ Exe = "wine"; Args = @($wineCsc) }
}