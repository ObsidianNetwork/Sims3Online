using System.Globalization;

namespace SimsOnline.DedicatedServer;

/// <summary>
/// M4 P4.1.1.3: parsed view of the SIM_SKILL_PROBE response report.
/// Wire format: "sim=&lt;id&gt;;skill=&lt;name&gt;;level=&lt;int&gt;;points=&lt;float&gt;"
/// </summary>
public sealed record SimSkillProbeReport(ulong SimObjectId, string SkillName, int Level, float Points)
{
    public static SimSkillProbeReport Parse(string report)
    {
        ulong sim = 0UL;
        string name = string.Empty;
        int level = 0;
        float points = 0f;

        foreach (string field in report.Split(';'))
        {
            int eq = field.IndexOf('=');
            if (eq < 0) continue;
            string key = field.Substring(0, eq);
            string val = field.Substring(eq + 1);
            if (key == "sim")
            {
                if (ulong.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v)) sim = v;
            }
            else if (key == "skill")
            {
                name = val;
            }
            else if (key == "level")
            {
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) level = v;
            }
            else if (key == "points")
            {
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) points = v;
            }
        }

        return new SimSkillProbeReport(sim, name, level, points);
    }
}