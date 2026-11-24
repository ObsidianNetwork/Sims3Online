using System;
using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    // PURE wire values for the world-wide environment. Object zero is intentional: this state belongs to every
    // presenter, not to a streamed Sim or lot. The weather packet is one committed settings update, not 23 torn setters.
    internal static class EnvironmentState
    {
        internal const int WeatherValueCount = 23;

        internal static string WeatherLine(float[] values)
        {
            StringBuilder line = new StringBuilder("En|0|weather");
            for (int i = 0; i < WeatherValueCount; i++)
            {
                line.Append('|');
                line.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
            }

            return line.ToString();
        }

        internal static bool IsWorldOperation(string operation)
        {
            return operation == "SetLunarCycle" || operation == "SetLunarPhase" ||
                operation == "ForceLunarCycle" || operation == "ForceLunarPhase" ||
                operation == "SetLightingValues" || operation == "RandomizeCloudinessImmediately";
        }

        // Fields include the recorder sequence: seq|En|0|operation|values...
        internal static bool TryParse(string[] fields, out string operation, out float[] values)
        {
            operation = null;
            values = null;
            if (fields == null || fields.Length < 5 || fields[1] != "En" || fields[2] != "0")
            {
                return false;
            }

            string op = fields[3];
            int count = op == "weather" ? WeatherValueCount : 1;
            if ((op != "weather" && !IsWorldOperation(op)) || fields.Length != count + 4)
            {
                return false;
            }

            float[] parsed = new float[count];
            for (int i = 0; i < count; i++)
            {
                float value;
                if (!float.TryParse(fields[i + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                    float.IsNaN(value) || float.IsInfinity(value))
                {
                    return false;
                }

                parsed[i] = value;
            }

            // Enum, audio-index and boolean slots are discrete; reject packets that would silently truncate.
            if (op == "weather")
            {
                int[] discrete = new int[] { 0, 2, 5, 6, 7, 13, 19 };
                for (int i = 0; i < discrete.Length; i++)
                {
                    float value = parsed[discrete[i]];
                    if (value < int.MinValue || (double)value > int.MaxValue || value != (float)(int)value)
                    {
                        return false;
                    }
                }

                if ((parsed[13] != 0f && parsed[13] != 1f) || (parsed[19] != 0f && parsed[19] != 1f))
                {
                    return false;
                }
            }
            else if (op != "RandomizeCloudinessImmediately" &&
                (parsed[0] < int.MinValue || (double)parsed[0] > int.MaxValue || parsed[0] != (float)(int)parsed[0]))
            {
                return false;
            }

            operation = op;
            values = parsed;
            return true;
        }
    }
}
