using System.Globalization;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    private static float[] EnvironmentWeatherValues()
    {
        float[] values = Enumerable.Range(0, EnvironmentState.WeatherValueCount).Select(i => i + 0.125f).ToArray();
        foreach (int i in new[] { 0, 2, 5, 6, 7 })
            values[i] = i;
        values[13] = 1;
        values[19] = 0;
        return values;
    }

    [Test("Environment.CompleteCommittedWeatherRoundTrip")]
    private static void EnvironmentCompleteCommittedWeatherRoundTrip()
    {
        float[] expected = EnvironmentWeatherValues();
        string[] packet = ("12|" + EnvironmentState.WeatherLine(expected)).Split('|');
        Assert(EnvironmentState.TryParse(packet, out string operation, out float[] actual), "Parse the committed weather.");
        Assert(operation == "weather", "Identify the weather operation.");
        Assert(actual.SequenceEqual(expected), "Keep every weather setting, including tree cover, offsets, fog and audio indices.");
        Assert(packet[2] == "0", "The environment is world-wide, not tied to a streamed object or lot.");
    }

    [Test("Environment.WeatherUsesInvariantRoundTripNumbers")]
    private static void EnvironmentWeatherUsesInvariantRoundTripNumbers()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            float[] expected = EnvironmentWeatherValues();
            expected[1] = float.Epsilon;
            expected[3] = -1.2345678f;
            expected[22] = float.MaxValue;
            string line = EnvironmentState.WeatherLine(expected);
            Assert(!line.Contains(','), "Never use the local decimal separator.");
            Assert(EnvironmentState.TryParse(("99|" + line).Split('|'), out _, out float[] actual), "Read invariant numbers.");
            Assert(actual.SequenceEqual(expected), "Keep float precision, signs and exponents.");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Test("Environment.RejectsPartialOrAppendedWeather")]
    private static void EnvironmentRejectsPartialOrAppendedWeather()
    {
        string line = "1|" + EnvironmentState.WeatherLine(EnvironmentWeatherValues());
        Assert(!EnvironmentState.TryParse(line.Split('|')[..^1], out _, out _), "Do not apply a partial settings update.");
        Assert(!EnvironmentState.TryParse((line + "|0").Split('|'), out _, out _), "Reject unexpected fields.");
    }

    [Test("Environment.RejectsNonFiniteOrFractionalDiscreteValues")]
    private static void EnvironmentRejectsNonFiniteOrFractionalDiscreteValues()
    {
        string[] valid = ("1|" + EnvironmentState.WeatherLine(EnvironmentWeatherValues())).Split('|');
        foreach (string invalid in new[] { "NaN", "Infinity", "-Infinity", "1e100", "not-a-number" })
        {
            string[] packet = (string[])valid.Clone();
            packet[5] = invalid;
            Assert(!EnvironmentState.TryParse(packet, out _, out _), "Reject invalid weather float: " + invalid);
        }

        foreach (int index in new[] { 0, 2, 5, 6, 7, 13, 19 })
        {
            string[] packet = (string[])valid.Clone();
            packet[4 + index] = "1.5";
            Assert(!EnvironmentState.TryParse(packet, out _, out _), "Never truncate a discrete weather setting.");
        }

        valid[17] = "2"; // Tree fall-set flag.
        Assert(!EnvironmentState.TryParse(valid, out _, out _), "Boolean settings must be zero or one.");
    }

    [Test("Environment.LunarLightingAndCloudinessOperations")]
    private static void EnvironmentLunarLightingAndCloudinessOperations()
    {
        foreach (string operation in new[] { "SetLunarCycle", "SetLunarPhase", "ForceLunarCycle", "ForceLunarPhase", "SetLightingValues" })
        {
            Assert(EnvironmentState.TryParse(("1|En|0|" + operation + "|7").Split('|'), out string actual, out float[] values),
                "Accept world operation " + operation);
            Assert(actual == operation && values[0] == 7, "Keep operation and argument.");
            Assert(!EnvironmentState.TryParse(("1|En|0|" + operation + "|2.25").Split('|'), out _, out _), "Do not truncate world enums.");
        }

        Assert(EnvironmentState.TryParse("1|En|0|RandomizeCloudinessImmediately|0.375".Split('|'), out _, out float[] cloudiness),
            "Cloudiness keeps its fractional argument.");
        Assert(cloudiness[0] == 0.375f, "Cloudiness value.");
    }

    [Test("Environment.RejectsWrongKindObjectOrUnknownOperation")]
    private static void EnvironmentRejectsWrongKindObjectOrUnknownOperation()
    {
        foreach (string line in new[] {
            "1|Wd|0|SetLunarPhase|7", "1|En|12|SetLunarPhase|7", "1|En|0|DeleteObject|7",
            "1|En|0|SetLunarPhase", "1|En|0|SetLunarPhase|7|8", "1|En|0|SetLunarPhase|2147483648"
        })
            Assert(!EnvironmentState.TryParse(line.Split('|'), out _, out _), "Reject malformed world packet " + line);
        Assert(!EnvironmentState.TryParse(null!, out _, out _), "Reject absent fields.");
    }
}
