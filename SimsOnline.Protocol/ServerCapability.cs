namespace SimsOnline.DedicatedServer.Protocol;

/// <summary>
/// the names a server lists in <c>session_joined</c>'s <c>capabilities</c>. Each says the server does something
/// its clients mirror differently. A client ignores a name it does not know, and a missing list is an older server
/// (no capabilities). A new one goes here, in internal documentation and where a client reads it.
/// </summary>
public static class ServerCapability
{
    /// <summary>The server streams presentation (<c>sim_animation</c>), so a client never starts legacy interaction replay.</summary>
    public const string PresentationStream = "presentation_stream";

    public static bool Has(IReadOnlyList<string>? capabilities, string name) =>
        capabilities != null && capabilities.Contains(name, StringComparer.Ordinal);
}
