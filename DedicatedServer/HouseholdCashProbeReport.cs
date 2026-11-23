using System.Globalization;

namespace SimsOnline.DedicatedServer;

/// <summary>
/// M4 P4.1.1.2: parsed view of the HOUSEHOLD_CASH_PROBE response report.
/// Wire report format: "household=&lt;id&gt;;funds=&lt;int&gt;"
/// </summary>
public sealed record HouseholdCashProbeReport(ulong HouseholdId, int Funds)
{
    public static HouseholdCashProbeReport Parse(string report)
    {
        ulong household = 0UL;
        int funds = 0;

        foreach (string field in report.Split(';'))
        {
            int eq = field.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            string key = field.Substring(0, eq);
            string val = field.Substring(eq + 1);

            if (key == "household")
            {
                if (ulong.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed))
                {
                    household = parsed;
                }
            }
            else if (key == "funds")
            {
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedFunds))
                {
                    funds = parsedFunds;
                }
            }
        }

        return new HouseholdCashProbeReport(household, funds);
    }
}