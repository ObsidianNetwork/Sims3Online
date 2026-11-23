namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>
/// The on-screen walk policy (SimLink native version 12; see
/// <c>docs/native-and-reverse-engineering/locomotion.md</c>): whether a game's
/// route follower on-screen test answers true, and what a joining player is told when their game and the server's
/// differ. Games that differ walk the same route two ways (animated with clips, or at a steady speed), with no error.
///
/// Read from the game's code, not from SACS_RECORDER status: the mod's onScreen field reads off,ready=0 until its
/// controller's SimLink handshake, which the server runs on the first presentation tick after a player joins and a
/// client on its first animation. So at a join it is wrong on both sides. The six bytes at the test's entry are what
/// SimLink writes (onscreen_logic.h), the same check as offline analyzer sites.
/// </summary>
public static class OnScreenWalk
{
    public const string On = "on";
    public const string Off = "off";
    public const string Unknown = "unknown";

    /// <summary>TestRouteFollowerOnScreen's entry (RVA 0x554240) in TS3W.exe 1.67.2.024037, the build SimLink matches.</summary>
    public const long EntryAddress = 0x954240;

    // push ebp / mov ebp, esp / and esp, 0xfffffff0: the stock test, which asks the cameras.
    private static readonly byte[] StockEntry = { 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0 };

    // mov al, 1 / ret 4 / nop: SimLink's rewrite, which answers true.
    private static readonly byte[] AlwaysEntry = { 0xB0, 0x01, 0xC2, 0x04, 0x00, 0x90 };

    /// <summary>On for SimLink's rewritten entry, off for the stock one, unknown for anything else (another build).</summary>
    public static string Classify(ReadOnlySpan<byte> entry) =>
        entry.SequenceEqual(AlwaysEntry) ? On
        : entry.SequenceEqual(StockEntry) ? Off
        : Unknown;

    /// <summary>The on-screen walk of the TS3 with this process id: unknown when there is none or the read fails.</summary>
    public static string ReadProcess(int? processId)
    {
        if (processId is not int pid)
        {
            return Unknown;
        }

        INativeProcessMemory native = NativeProcessMemory.Instance;
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = native.Open(pid);
            if (handle == IntPtr.Zero)
            {
                return Unknown;
            }

            byte[] entry = new byte[AlwaysEntry.Length];
            return native.Read(handle, new IntPtr(EntryAddress), entry, entry.Length, out long read) && read == entry.Length
                ? Classify(entry)
                : Unknown;
        }
        catch (Exception)
        {
            return Unknown;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                native.Close(handle);
            }
        }
    }

    /// <summary>A reported state as on, off or unknown; null stays null (not reported). A client's state comes off the
    /// wire, so nothing else reaches a log line or a notice.</summary>
    public static string? Normalize(string? reported) => reported is null or On or Off ? reported : Unknown;

    /// <summary>
    /// The notice for a joining player whose game's on-screen walk differs from the server's, or null. Null too when
    /// either is unknown, or the client did not report one (null: an older client, or a client with no game of its
    /// own). A difference only warns: the server's state stays authoritative, only the copies' walks differ.
    /// </summary>
    public static string? Mismatch(string server, string? client)
    {
        server = Normalize(server) ?? Unknown;
        client = Normalize(client);
        if (client == null || server == Unknown || client == Unknown || server == client)
        {
            return null;
        }

        return $"Your game's on-screen walk is {client} and the server's is {server}, so Sims here will not walk as they "
            + "do on the server. Both games need SimLink native version 12, without SIMLINK_ONSCREEN=0.";
    }
}
