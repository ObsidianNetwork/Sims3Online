// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System;
using System.Text;

namespace SimsOnlineServerHost
{
    // Client side: the numbers in a server line that this game's own number parser cannot be trusted with.
    //
    // float.Parse and double.Parse in The Sims 3's script runtime return NaN for some well-formed numbers: in the game,
    // float.Parse("2.775558E-17") is NaN, and so are 1.387779E-17 and 6.938894E-18, while 5.551115E-17 and
    // 1.110223E-16 parse (probe FloatParseProbe on a client, 2026-11-15). Every number that failed needs more than
    // 22 decimal places (its digits times ten to a power below -22); none of the numbers with 22 or fewer did. That
    // is the limit of the parser's exact path (a power of ten up to 10^22 is exact in a double); past it the parser
    // takes its long path, which is the one that fails here.
    //
    // Such numbers reach a client as rounding residue in the rotation of an object transform: a car on a straight
    // road sends rows like 1~2.775558E-17~-4.865233E-05. Parsed as NaN and handed to SetTransform, the car's copy
    // and every Sim seated in it read an invalid facing and position (two-lots-travel-stability: the invalid-position
    // bursts of runs 14 to 21, and with the playout buffer on, the copy jumping 4 to 7 m back and forth in runs c3 and
    // c4, because TransformPlayout sends a transform it cannot parse past the buffer). 306 of 120621 captured lines
    // carried one: 304 object transforms and 2 Sim positions.
    //
    // Clean replaces each such number with 0 before the line is split. All of them are below 1e-14 in size, which
    // is nothing in a position, a direction or a time. A number with 22 decimal places or fewer is left as it is.
    internal static class WireNumbers
    {
        private const int ExactPlaces = 22;

        internal static long Cleaned;       // numbers replaced
        internal static long LinesCleaned;  // lines that carried one
        internal static string LastCleaned = "";

        internal static string Report()
        {
            return "cleaned=" + Cleaned.ToString() + ";lines=" + LinesCleaned.ToString() + ";last=" + LastCleaned;
        }

        internal static string Clean(string line)
        {
            if (line == null)
            {
                return line;
            }

            int e = line.IndexOf("E-", StringComparison.Ordinal);
            if (e < 0)
            {
                return line;
            }

            StringBuilder sb = null;
            int copied = 0;
            while (e >= 0)
            {
                int end = e + 2;
                int exponent = 0;
                while (end < line.Length && line[end] >= '0' && line[end] <= '9')
                {
                    exponent = exponent < 100000 ? exponent * 10 + (line[end] - '0') : exponent;
                    end++;
                }

                int start = e;
                int digits = 0;
                int places = 0;
                bool dot = false;
                bool number = end > e + 2 && (end == line.Length || !IsWord(line[end]));
                while (number && start > copied)
                {
                    char ch = line[start - 1];
                    if (ch >= '0' && ch <= '9')
                    {
                        digits++;
                        if (!dot)
                        {
                            places++;
                        }
                    }
                    else if (ch == '.' && !dot)
                    {
                        dot = true;
                    }
                    else
                    {
                        break;
                    }

                    start--;
                }

                if (!dot)
                {
                    places = 0; // no decimal point: the digits counted were the whole part
                }

                if (number && start > copied && line[start - 1] == '-')
                {
                    start--;
                }

                number = number && digits > 0 && (start == 0 || !IsWord(line[start - 1]));
                if (number && exponent + places > ExactPlaces)
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder(line.Length);
                        LinesCleaned++;
                    }

                    LastCleaned = line.Substring(start, end - start);
                    sb.Append(line, copied, start - copied).Append('0');
                    copied = end;
                    Cleaned++;
                }

                e = end < line.Length ? line.IndexOf("E-", end, StringComparison.Ordinal) : -1;
            }

            if (sb == null)
            {
                return line;
            }

            sb.Append(line, copied, line.Length - copied);
            return sb.ToString();
        }

        // A letter, a digit, a dot or an underscore next to a number makes it part of a name or of another number.
        private static bool IsWord(char ch)
        {
            return (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '.' || ch == '_';
        }
    }
}
