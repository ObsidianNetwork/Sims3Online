using System.Globalization;

namespace SimsOnline.DedicatedServer;

/// <summary>
/// M4 P4.3 — typed parser for the script DLL's <c>SIM_AUTONOMY_PROBE</c> response.
///
/// Wire payload (semicolon-delimited key=value pairs, mirrors <see cref="SimSkillProbeReport"/>):
/// <code>sim=&lt;objectId&gt;;enabled=&lt;true|false&gt;;disabledCount=&lt;int&gt;</code>
///
/// The fields:
/// <list type="bullet">
///   <item><see cref="SimObjectId"/>: the Sim's <c>ObjectGuid</c></item>
///   <item><see cref="Enabled"/>: <c>AutonomyRestrictions.IsAnyAutonomyEnabled(sim)</c> at read time</item>
///   <item><see cref="DisabledCount"/>: best-effort indicator that any disabler is currently in effect.
///   Sims 3's <c>Sim.Autonomy</c> exposes the disable state via <c>IsAnyAutonomyEnabled</c>; the
///   underlying disable counter is private. We surface it as a 0/1 derived from Enabled to keep
///   the wire surface stable for callers that want a numeric channel.</item>
/// </list>
/// </summary>
public sealed record SimAutonomyProbeReport(ulong SimObjectId, bool Enabled, int DisabledCount)
{
    public static SimAutonomyProbeReport Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new SimAutonomyProbeReport(0UL, false, 0);
        }

        ulong simObjectId = 0UL;
        bool enabled = false;
        int disabledCount = 0;

        string[] parts = raw.Split(';');
        foreach (string p in parts)
        {
            int eq = p.IndexOf('=');
            if (eq <= 0) continue;
            string key = p.Substring(0, eq);
            string value = p.Substring(eq + 1);
            switch (key)
            {
                case "sim":
                    ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out simObjectId);
                    break;
                case "enabled":
                    bool.TryParse(value, out enabled);
                    break;
                case "disabledCount":
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out disabledCount);
                    break;
            }
        }

        return new SimAutonomyProbeReport(simObjectId, enabled, disabledCount);
    }
}
