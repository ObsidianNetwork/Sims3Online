namespace SimsOnline.DedicatedServer.Observability;

/// <summary>
/// Release/Distribution metadata computed once at startup for Sentry tagging.
/// </summary>
internal sealed record SentryReleaseInfo(string Release, string Distribution, string CommitSha, bool Dirty);
