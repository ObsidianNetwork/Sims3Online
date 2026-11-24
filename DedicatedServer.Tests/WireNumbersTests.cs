using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// WireNumbers.Clean: a number that needs more than 22 decimal places leaves the exact path of the game's number
/// parser, which then returns NaN for some of them (float.Parse("2.775558E-17") in the game, 2026-11-15). Such a
/// number is replaced with 0 before a client reads the line; everything else in the line is left as it is.
/// </summary>
internal static partial class Program
{
    [Test("WireNumbers.TheRecordedTransformRowIsCleaned")]
    private static void WireNumbersTheRecordedTransformRowIsCleaned()
    {
        // The taxi's pose the transform guard repaired (two-lots-travel-stability, motion 072009).
        string line = "1794687631673|821328|124150|Ob|18031288061866091488|SetTransform:m|1~2.775558E-17~-4.865233E-05~0~4.438218E-10~1~9.122312E-06~0~4.865233E-05~-9.122312E-06~1~0~1192.99988~45.8831406~1087.48901~1";
        string want = "1794687631673|821328|124150|Ob|18031288061866091488|SetTransform:m|1~0~-4.865233E-05~0~4.438218E-10~1~9.122312E-06~0~4.865233E-05~-9.122312E-06~1~0~1192.99988~45.8831406~1087.48901~1";
        Assert(WireNumbers.Clean(line) == want, "Only the number past 22 decimal places becomes 0.");
        Assert(WireNumbers.Clean(want) == want, "A cleaned line is left alone.");
    }

    [Test("WireNumbers.OnlyNumbersPastTheExactPathChange")]
    private static void WireNumbersOnlyNumbersPastTheExactPathChange()
    {
        Assert(WireNumbers.Clean("-2.775558E-17") == "0", "A negative one, alone in the text.");
        Assert(WireNumbers.Clean("a|1.387779E-17|6.938894E-18|b") == "a|0|0|b", "Two in one line.");
        Assert(WireNumbers.Clean("5.551115E-17") == "0", "23 places: past the exact path, replaced whether or not the game parses it.");
        Assert(WireNumbers.Clean("1E-23~x") == "0~x" && WireNumbers.Clean("x~1.5E-22") == "x~0", "No decimal point, and one.");
        string[] kept = new string[] {
            "1.110223E-16", "4.440892E-16", "2.664535E-15", "8.13673E-10", "1E-17", "2.8E-17", "1E-22", "-4.865233E-05", "0.000439391268", "1192.99988",
        };
        for (int i = 0; i < kept.Length; i++)
        {
            Assert(WireNumbers.Clean("a~" + kept[i] + "~b") == "a~" + kept[i] + "~b", "22 places or fewer is left: " + kept[i]);
        }
    }

    [Test("WireNumbers.NamesAndIdsAreNotNumbers")]
    private static void WireNumbersNamesAndIdsAreNotNumbers()
    {
        string[] kept = new string[] {
            "", "Ob|1|SetPosition:v|1~2~3", "f974c01809e2e249:577793763", "nameE-30|x", "E-30", "x.E-30", "1.2.3E-30", "1.5E-30x", "1.5E-30.2",
            "abc1.5E-30", "a_1.5E-30", "1.5E-|x", "Wd|ObjectSetOpacity:ff/0/0",
        };
        for (int i = 0; i < kept.Length; i++)
        {
            Assert(WireNumbers.Clean(kept[i]) == kept[i], "Not a number, left as it is: " + kept[i]);
        }

        Assert(WireNumbers.Clean(null) == null, "No line.");
        Assert(WireNumbers.Clean("v/1.5E-30/2") == "v/0/2" && WireNumbers.Clean("a,-1.5E-30;b") == "a,0;b", "Any separator bounds a number.");
    }
}
