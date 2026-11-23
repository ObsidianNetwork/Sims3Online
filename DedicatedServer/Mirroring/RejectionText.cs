namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// what a player sees when the server refuses a pick they made. The server's reasons are written for
/// logs (for example "InteractionQueue.Add refused Sims3...Sit+Definition 'Sit'."); this turns them into a short line
/// for an in-game notification.
/// </summary>
public static class RejectionText
{
    public const int MaxReasonLength = 120;

    public static string ForPick(string? pickName, string? reason)
    {
        string what = string.IsNullOrWhiteSpace(pickName) ? "that" : "\"" + pickName.Trim() + "\"";
        string text = reason?.Trim() ?? string.Empty;

        // A pick the server's queue refused on its test, with the server game's own greyed-out tooltip: show that.
        string? tooltip = SimsOnlineServerHost.UserIntentWire.TooltipOf(text);
        if (tooltip != null)
        {
            return $"Can't do {what}: {Cut(tooltip)}";
        }

        // the reasons of a pick the server's queue accepted and then dropped (EVENT|PickFailed).
        switch (text)
        {
            case "ObjectInUse":
                return $"Can't do {what}: someone else is using it.";
            case "RouteFailed":
                return $"Can't do {what}: can't get there.";
            case "FailedToStart":
            case "NotRun":
                return $"Can't do {what} right now.";
        }

        if (text.Contains("claimed by player", StringComparison.OrdinalIgnoreCase))
        {
            return $"Can't do {what}: that Sim belongs to another player.";
        }

        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return $"Can't do {what}: it isn't there any more.";
        }

        if (text.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no matching interaction", StringComparison.OrdinalIgnoreCase)
            || text.Length == 0)
        {
            return $"Can't do {what} right now.";
        }

        return $"Can't do {what}: {Cut(text)}";
    }

    private static string Cut(string text) =>
        text.Length > MaxReasonLength ? text[..MaxReasonLength].TrimEnd() + "..." : text;
}
