using System.Net;
using System.Text;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>Public-protocol hardening: line length, who may speak for which player, listen address, lot exports.</summary>
internal static partial class Program
{
    [Test("BoundedLineReader.ReadsLinesLikeReadLine")]
    private static void BoundedLineReaderReadsLinesLikeReadLine() => RunBoundedLineReaderReadsLinesAsync().GetAwaiter().GetResult();

    private static async Task RunBoundedLineReaderReadsLinesAsync()
    {
        BoundedLineReader reader = new(new StringReader("first\r\nsecond\n\nlast"), maxLineChars: 16);
        Assert(await reader.ReadLineAsync(CancellationToken.None) == "first", "A CRLF line should drop its carriage return.");
        Assert(await reader.ReadLineAsync(CancellationToken.None) == "second", "An LF line should read whole.");
        Assert(await reader.ReadLineAsync(CancellationToken.None) == "", "An empty line should read as empty.");
        Assert(await reader.ReadLineAsync(CancellationToken.None) == "last", "An unterminated last line should still be returned.");
        Assert(await reader.ReadLineAsync(CancellationToken.None) == null, "End of stream should return null.");

        string longLine = new('x', 10_000);
        BoundedLineReader spanning = new(new StringReader(longLine + "\nnext\n"), maxLineChars: 10_000);
        Assert(await spanning.ReadLineAsync(CancellationToken.None) == longLine, "A line exactly at the limit, longer than one buffer, should be accepted.");
        Assert(await spanning.ReadLineAsync(CancellationToken.None) == "next", "The line after a long one should read normally.");
    }

    [Test("BoundedLineReader.RejectsLineOverLimit")]
    private static void BoundedLineReaderRejectsLineOverLimit() => RunBoundedLineReaderRejectsLineOverLimitAsync().GetAwaiter().GetResult();

    private static async Task RunBoundedLineReaderRejectsLineOverLimitAsync()
    {
        foreach (string input in new[] { new string('x', 17) + "\n", new string('x', 50_000) })
        {
            BoundedLineReader reader = new(new StringReader(input), maxLineChars: 16);
            bool threw = false;
            try
            {
                await reader.ReadLineAsync(CancellationToken.None);
            }
            catch (LineTooLongException ex)
            {
                threw = ex.MaxLineChars == 16;
            }
            Assert(threw, $"A {input.TrimEnd('\n').Length}-character line must exceed a 16-character limit, newline or not.");
        }
    }

    [Test("ClientConnection.CommandAuthorityRules")]
    private static void ClientConnectionCommandAuthorityRules()
    {
        Guid id = Guid.NewGuid();
        ClientCommand claimFor7 = new ClientCommand.ClaimSim(id, 7, 100uL);
        ClientCommand claimFor8 = new ClientCommand.ClaimSim(id, 8, 100uL);

        Assert(ClientConnection.CheckCommandAuthority(claimFor7, joinedPlayerId: 7, isLoopbackPeer: false) == null,
            "A joined connection may command its own player.");
        string? impersonation = ClientConnection.CheckCommandAuthority(claimFor8, joinedPlayerId: 7, isLoopbackPeer: true);
        Assert(impersonation != null && impersonation.Contains("joined as player 7", StringComparison.Ordinal),
            "A joined connection must not command another player, even from loopback. Reason: " + impersonation);

        Assert(ClientConnection.CheckCommandAuthority(claimFor8, joinedPlayerId: null, isLoopbackPeer: true) == null,
            "An unjoined loopback connection is the admin path (scripts/simctl.py) and may command any player.");
        Assert(ClientConnection.CheckCommandAuthority(claimFor8, joinedPlayerId: null, isLoopbackPeer: false) != null,
            "An unjoined remote connection must not change state.");
        Assert(ClientConnection.CheckCommandAuthority(new ClientCommand.SaveGame(id, 1, "x.sims3"), null, false) != null,
            "An unjoined remote connection must not save the game.");

        foreach (ClientCommand allowed in new ClientCommand[]
        {
            new ClientCommand.JoinSession(id, 9, "remote"),
            new ClientCommand.RequestWorld(id, 9),
            new ClientCommand.RequestSnapshot(id, 9, null),
            new ClientCommand.RequestSimDescription(id, 9, 5uL),
        })
        {
            Assert(ClientConnection.CheckCommandAuthority(allowed, joinedPlayerId: null, isLoopbackPeer: false) == null,
                $"{allowed.GetType().Name} must be accepted from a remote peer before it joins.");
        }
    }

    [Test("ClientListenerServer.RejectsNonIpHostAndWarnsWhenExposed")]
    private static void ClientListenerServerRejectsNonIpHostAndWarnsWhenExposed()
    {
        bool threw = false;
        try
        {
            ClientListenerServer.ParseEndpoint("example.invalid:34019");
        }
        catch (FormatException)
        {
            threw = true;
        }
        Assert(threw, "A host that is not an IP address must fail instead of silently binding loopback.");

        IPEndPoint v6 = ClientListenerServer.ParseEndpoint("[::1]:34019");
        Assert(v6.Address.Equals(IPAddress.IPv6Loopback) && v6.Port == 34019, "A bracketed IPv6 host should parse.");

        AuthoritativeSession session = new(new InMemoryAuthoritativeHost());
        Assert(new ClientListenerServer(session, "127.0.0.1:0").ExposureWarning == null, "A loopback listener needs no warning.");
        string? warning = new ClientListenerServer(session, "0.0.0.0:0").ExposureWarning;
        Assert(warning != null && warning.Contains("no authentication", StringComparison.Ordinal),
            "A listener open to other machines must warn that the protocol has no authentication.");
    }

    [Test("ClientListenerServer.ClosesConnectionOnOversizedLine")]
    private static void ClientListenerServerClosesConnectionOnOversizedLine() =>
        RunClientListenerServerClosesConnectionOnOversizedLineAsync().GetAwaiter().GetResult();

    private static async Task RunClientListenerServerClosesConnectionOnOversizedLineAsync()
    {
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Hardening.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0", maxCommandLineChars: 256);
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);

        byte[] flood = Encoding.UTF8.GetBytes(new string('x', 4096)); // no newline: would buffer forever unbounded
        await peer.Client.GetStream().WriteAsync(flood);
        ServerEvent error = await peer.ReadEventAsync();
        Assert(error is ServerEvent.ServerError serverError && serverError.Message.Contains("exceeds 256", StringComparison.Ordinal),
            "An oversized line should get a server_error naming the limit.");
        Assert(await peer.TryReadLineAsync(TimeSpan.FromSeconds(3)) == null, "The server should then close the connection.");
    }

    [Test("ClientListenerServer.JoinedConnectionCannotCommandAnotherPlayer")]
    private static void ClientListenerServerJoinedConnectionCannotCommandAnotherPlayer() =>
        RunJoinedConnectionCannotCommandAnotherPlayerAsync().GetAwaiter().GetResult();

    private static async Task RunJoinedConnectionCannotCommandAnotherPlayerAsync()
    {
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Hardening.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await peer.JoinAndDrainAsync(7, "seven");

        Guid claimId = Guid.NewGuid();
        await peer.SendCommandAsync(new ClientCommand.ClaimSim(claimId, 8, 100uL));
        ServerEvent reply = await peer.ReadEventAsync();
        Assert(reply is ServerEvent.CommandRejected rejected && rejected.CommandId == claimId
                && rejected.Reason.Contains("joined as player 7", StringComparison.Ordinal),
            "A claim for another player on a joined connection must be rejected before it reaches the session.");
    }

    [Test("AuthoritativeSession.RejectsLotExportWithoutClientNativeModes")]
    private static void AuthoritativeSessionRejectsLotExportWithoutClientNativeModes()
    {
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = NewSoloNativeSession(host);
        session.ConfigureClientNativeModes(false);
        CommandResult result = session.HandleCommandAsync(
            new ClientCommand.SubmitLotExport(Guid.NewGuid(), 1, NewOwnedLotExportBundle()), CancellationToken.None).GetAwaiter().GetResult();
        Assert(!result.IsAccepted && (result.RejectionReason?.Contains("client-native", StringComparison.Ordinal) ?? false),
            "With one simulator (client-native modes off), a lot export must be rejected. Reason: " + result.RejectionReason);
        Assert(host.MergeLotStateCalls.Count == 0, "A rejected lot export must not merge into the server's game.");
    }
}
