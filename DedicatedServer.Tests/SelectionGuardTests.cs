using System.Text.RegularExpressions;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// cause 1: on the client role, replay never changes the player's selected Sim. Every select the mod makes goes
/// through one choke point, ServerHostMain.SelectSim, which asks SelectionGuard first.
/// </summary>
internal static partial class Program
{
    private const string SelectChokePoint = "private static bool SelectSim(Sim sim, SelectCause cause)";

    [Test("SelectionGuard.ModSelectsOnlyThroughTheChokePoint")]
    private static void SelectionGuardModSelectsOnlyThroughTheChokePoint()
    {
        string mod = Path.Combine(SolutionRoot(), "SimsOnlineServerHostMod");
        Regex select = new Regex(@"PlumbBob\.(Force)?SelectActor\s*\(", RegexOptions.CultureInvariant);
        List<string> sites = new List<string>();
        foreach (string file in Directory.GetFiles(mod, "*.cs").Concat(Directory.GetFiles(Path.Combine(mod, "Presentation"), "*.cs")))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i].TrimStart();
                if (!code.StartsWith("//", StringComparison.Ordinal) && select.IsMatch(code))
                {
                    sites.Add(Path.GetFileName(file) + ":" + (i + 1));
                }
            }
        }

        Assert(sites.Count == 1, "The mod selects a Sim in one place only. Found: " + string.Join(", ", sites));
        string main = File.ReadAllText(Path.Combine(mod, "ServerHostMain.cs"));
        int start = main.IndexOf(SelectChokePoint, StringComparison.Ordinal);
        Assert(start >= 0, "ServerHostMain has the choke point " + SelectChokePoint);
        int end = main.IndexOf("\n        }", start, StringComparison.Ordinal);
        string body = main.Substring(start, end - start);
        Assert(select.IsMatch(body) && body.Contains("SelectionGuard.KeepsSelection(", StringComparison.Ordinal),
            "The one select is inside SelectSim, behind SelectionGuard. Body: " + body);
    }

    [Test("SelectionGuard.PlayerSelectsGoThroughReplayKeepsTheSelection")]
    private static void SelectionGuardPlayerSelectsGoThroughReplayKeepsTheSelection()
    {
        Assert(!SimsOnlineServerHost.SelectionGuard.KeepsSelection(SimsOnlineServerHost.SelectCause.Player),
            "The player's own select (the claim's SET_ACTIVE_SIM, its re-assert, INJECT_CLICK) goes through.");
        Assert(SimsOnlineServerHost.SelectionGuard.KeepsSelection(SimsOnlineServerHost.SelectCause.Replay),
            "A replay never moves the selection.");

        // Each caller names the cause the guard decides on: the claim and the click are the player's, the replays are not.
        string main = File.ReadAllText(Path.Combine(SolutionRoot(), "SimsOnlineServerHostMod", "ServerHostMain.cs"));
        Regex method = new Regex(@"private static \w+ (\w+)\(", RegexOptions.CultureInvariant);
        Dictionary<string, string> causes = new Dictionary<string, string>();
        foreach (Match call in Regex.Matches(main, @"SelectSim\(\w+, SelectCause\.(\w+)\)"))
        {
            Match caller = method.Matches(main.Substring(0, call.Index)).Last();
            causes[caller.Groups[1].Value] = call.Groups[1].Value;
        }

        string got = string.Join(", ", causes.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + "=" + c.Value));
        Assert(got == "HandleInjectClick=Player, HandleInjectReadIntent=Replay, HandleSetActiveSim=Player, TryApplyInteractionFields=Replay",
            "The select callers and their causes. Got: " + got);
    }

    private static string SolutionRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SimsOnline.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("SimsOnline.sln not found above the test assembly.");
    }
}
