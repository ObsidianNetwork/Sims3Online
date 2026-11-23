using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// a join compares the joiner's on-screen walk with the server's game (OnScreenWalk). Games that differ walk
/// routes two ways with no error, so the server logs a warning and the joiner's SessionJoined carries a notice.
/// </summary>
internal static partial class Program
{
    [Test("OnScreenWalk.ClassifiesTheEntryBytes")]
    private static void OnScreenWalkClassifiesTheEntryBytes()
    {
        Assert(OnScreenWalk.Classify(new byte[] { 0xB0, 0x01, 0xC2, 0x04, 0x00, 0x90 }) == OnScreenWalk.On,
            "SimLink's rewritten entry (mov al, 1 / ret 4 / nop) is on.");
        Assert(OnScreenWalk.Classify(new byte[] { 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0 }) == OnScreenWalk.Off,
            "The stock entry (push ebp / mov ebp, esp / and esp, 0xfffffff0) is off.");
        Assert(OnScreenWalk.Classify(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }) == OnScreenWalk.Unknown,
            "Other bytes (another TS3 build) are unknown.");
        Assert(OnScreenWalk.Classify(new byte[] { 0xB0, 0x01 }) == OnScreenWalk.Unknown, "A short read is unknown.");
        Assert(OnScreenWalk.ReadProcess(null) == OnScreenWalk.Unknown, "No game process is unknown.");
    }

    [Test("OnScreenWalk.WarnsOnlyWhenBothGamesAreKnownAndDiffer")]
    private static void OnScreenWalkWarnsOnlyWhenBothGamesAreKnownAndDiffer()
    {
        string? serverOnClientOff = OnScreenWalk.Mismatch(OnScreenWalk.On, OnScreenWalk.Off);
        Assert(serverOnClientOff != null && serverOnClientOff.Contains("Your game's on-screen walk is off and the server's is on", StringComparison.Ordinal),
            "A client without the walk (older SimLink, SIMLINK_ONSCREEN=0) is told. Got: " + serverOnClientOff);
        string? serverOffClientOn = OnScreenWalk.Mismatch(OnScreenWalk.Off, OnScreenWalk.On);
        Assert(serverOffClientOn != null && serverOffClientOn.Contains("is on and the server's is off", StringComparison.Ordinal),
            "A server without the walk is a difference too. Got: " + serverOffClientOn);
        Assert(OnScreenWalk.Mismatch(OnScreenWalk.On, OnScreenWalk.On) == null, "Both on: no notice.");
        Assert(OnScreenWalk.Mismatch(OnScreenWalk.Off, OnScreenWalk.Off) == null, "Both off (an A/B's base arm): no notice.");
        Assert(OnScreenWalk.Mismatch(OnScreenWalk.On, null) == null, "A client that does not report is not compared.");
        Assert(OnScreenWalk.Mismatch(OnScreenWalk.On, OnScreenWalk.Unknown) == null, "An unknown client is not compared.");
        Assert(OnScreenWalk.Mismatch(OnScreenWalk.Unknown, OnScreenWalk.Off) == null, "An unknown server is not compared.");
        Assert(OnScreenWalk.Mismatch(OnScreenWalk.On, "off\n[onscreen] forged") == null,
            "A reported value other than on or off counts as unknown, so it never reaches a notice.");
        Assert(OnScreenWalk.Normalize("garbage") == OnScreenWalk.Unknown && OnScreenWalk.Normalize(null) == null,
            "Normalize keeps null and maps anything but on and off to unknown.");
    }

    [Test("PublicProtocolCodec.CarriesTheOnScreenWalk")]
    private static void PublicProtocolCodecCarriesTheOnScreenWalk()
    {
        Guid id = Guid.Parse("11111111222233334444555566667777");
        string withState = PublicProtocolCodec.EncodeCommand(new ClientCommand.JoinSession(id, 42, "alice", OnScreenWalk.Off));
        Assert(withState.Contains("\"onScreen\":\"off\"", StringComparison.Ordinal), "JoinSession should carry onScreen. Got: " + withState);
        Assert(PublicProtocolCodec.DecodeCommand(withState) is ClientCommand.JoinSession { OnScreen: "off" }, "onScreen should round-trip.");
        string without = PublicProtocolCodec.EncodeCommand(new ClientCommand.JoinSession(id, 42, "alice"));
        Assert(!without.Contains("onScreen", StringComparison.Ordinal), "An unreported onScreen is left out. Got: " + without);
        // An older client's join, as it writes it.
        ClientCommand older = PublicProtocolCodec.DecodeCommand(
            "{\"$type\":\"join_session\",\"commandId\":\"11111111-2222-3333-4444-555566667777\",\"playerId\":42,\"displayName\":\"alice\"}");
        Assert(older is ClientCommand.JoinSession { OnScreen: null, PlayerId: 42, DisplayName: "alice" },
            "A join without onScreen decodes with OnScreen null. Got: " + older);

        string notice = PublicProtocolCodec.EncodeEvent(new ServerEvent.SessionJoined(42, "alice", "walks differ"));
        Assert(PublicProtocolCodec.DecodeEvent(notice) is ServerEvent.SessionJoined { Notice: "walks differ" }, "SessionJoined's notice should round-trip. Got: " + notice);
        string plain = PublicProtocolCodec.EncodeEvent(new ServerEvent.SessionJoined(42, "alice"));
        Assert(!plain.Contains("notice", StringComparison.Ordinal), "No notice is left out. Got: " + plain);
    }

    [Test("AuthoritativeSession.JoinWarnsWhenTheOnScreenWalksDiffer")]
    private static void AuthoritativeSessionJoinWarnsWhenTheOnScreenWalksDiffer()
    {
        AuthoritativeSession session = MakeSession();
        session.OnScreenOverride = OnScreenWalk.On;
        TextWriter original = Console.Out;
        StringWriter log = new();
        Console.SetOut(log);
        try
        {
            Assert(JoinNotice(session, 1, OnScreenWalk.On) == null, "The same walk: no notice.");
            string? notice = JoinNotice(session, 2, OnScreenWalk.Off);
            Assert(notice != null && notice.Contains("is off and the server's is on", StringComparison.Ordinal),
                "A joiner whose walk differs gets the notice in its SessionJoined. Got: " + notice);
            Assert(JoinNotice(session, 3, null) == null, "An older client's join (no onScreen) is not compared.");
            session.OnScreenOverride = null; // the in-memory host has no TS3 to read
            Assert(JoinNotice(session, 4, OnScreenWalk.Off) == null, "A server game that cannot be read is not compared.");
        }
        finally
        {
            Console.SetOut(original);
        }

        string text = log.ToString();
        Assert(text.Contains("[onscreen] player 1 joined: server=on client=on", StringComparison.Ordinal), "Each join logs both states: " + text);
        Assert(text.Contains("[onscreen] WARNING: player 2 (p2) joined with a different on-screen walk: server=on client=off", StringComparison.Ordinal),
            "A difference logs a WARNING: " + text);
        Assert(text.Contains("[onscreen] player 3 joined: server=on client=unreported", StringComparison.Ordinal), "An unreported walk says so: " + text);
        Assert(text.Contains("[onscreen] player 4 joined: server=unknown client=off", StringComparison.Ordinal), "An unread server says unknown: " + text);
    }

    private static string? JoinNotice(AuthoritativeSession session, int playerId, string? onScreen)
    {
        CommandResult result = session.HandleCommandAsync(
            new ClientCommand.JoinSession(Guid.NewGuid(), playerId, "p" + playerId, onScreen), CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.IsAccepted && result.Events.Count > 0 && result.Events[0] is ServerEvent.SessionJoined,
            "The join should be accepted with SessionJoined first. Got: " + result.RejectionReason);
        return ((ServerEvent.SessionJoined)result.Events[0]).Notice;
    }
}
