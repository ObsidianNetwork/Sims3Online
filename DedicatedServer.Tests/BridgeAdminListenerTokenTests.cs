using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Network;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// The admin listener's argument tokens (card 56 gate 6, Main's Q2 ruling of 2026-11-01): a bare <c>-</c> is an
/// empty wire part, <c>\-</c> is the escape for a literal <c>-</c> part, and no other token changes. The listener
/// splits request lines on spaces, so a wire line whose argument is a null or an empty list — the codec writes
/// those as <c>-</c>, as in <c>SET_SIM_SCOPE|&lt;id&gt;|-|-</c> — could not cross it until the escape existed.
/// </summary>
internal static partial class Program
{
    private static readonly TimeSpan AdminTokenTestWait = TimeSpan.FromSeconds(10);

    [Test("BridgeAdminListener.ADashIsAnEmptyPartAndAnEscapedDashIsLiteral")]
    private static void BridgeAdminListenerDashTokens() => RunAdminListenerDashTokensAsync().GetAwaiter().GetResult();

    private static async Task RunAdminListenerDashTokensAsync()
    {
        // No game behind the listener: the local callback answers every name, so the arguments it captured are
        // exactly what the request tokenizer made of the tokens on the line (HandleRequestAsync).
        List<string[]> captured = new();
        await using BridgeAdminListener listener = new(
            new HostBridgeServer("memory:TS3W"),
            "127.0.0.1:0",
            (string name, string[] args) =>
            {
                captured.Add(args);
                return name + "=ok";
            });
        await listener.StartAsync(CancellationToken.None);
        string[] endpoint = listener.BoundEndpoint!.Split(':');
        using TcpClient client = new();
        await client.ConnectAsync(endpoint[0], int.Parse(endpoint[1], System.Globalization.CultureInfo.InvariantCulture));
        using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8);

        string[] requests =
        {
            "TOKEN_PROBE - 2",
            @"TOKEN_PROBE \- \-",
            @"TOKEN_PROBE \-x a\b \\",
            "TOKEN_PROBE",
            @"TOKEN_PROBE observe \-",
        };
        string[][] want =
        {
            // A bare "-" still means an empty part: DIAG_WALK's hexPath for "walk from the root", and the empty
            // argument bridgectl.py sends for a wire line with an empty part between two "|".
            new[] { "", "2" },
            // The escape: card 56 gate 6's literal arguments reach the mod as "-" fields
            // (DIALOG_ANSWER|<id>|0|-, SET_SIM_SCOPE|<id>|-|-, SET_AUTHORITY|<id>|observe|-).
            new[] { "-", "-" },
            // Only the whole token "\-" is the escape: any other token with a backslash passes through unchanged.
            new[] { @"\-x", @"a\b", @"\\" },
            Array.Empty<string>(),
            new[] { "observe", "-" },
        };

        for (int i = 0; i < requests.Length; i++)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(requests[i] + "\n"));
            string? reply = await reader.ReadLineAsync().WaitAsync(AdminTokenTestWait);
            using JsonDocument document = JsonDocument.Parse(reply ?? "{}");
            JsonElement root = document.RootElement;
            Assert(root.GetProperty("ok").GetBoolean() && root.GetProperty("accepted").GetBoolean(),
                $"{requests[i]}: the local callback should answer it: {reply}");
            Assert(root.GetProperty("report").GetString() == "TOKEN_PROBE=ok",
                $"{requests[i]}: the name should reach the callback upper-cased: {reply}");
            Assert(captured.Count == i + 1, $"{requests[i]}: one callback per request, got {captured.Count}.");
            Assert(captured[i].SequenceEqual(want[i]),
                $"{requests[i]}: args should be [{string.Join("][", want[i])}], got [{string.Join("][", captured[i])}].");
        }

        // And the line the mod is handed: EncodeRawCommand joins the parts verbatim, so the escaped token lands as a
        // `-` field (the Rust half's own line, which gate 6 pairs against) and a bare `-` still lands as an empty one.
        string zeros = new('0', 32);
        string dashLine = BridgeLineCodec.EncodeCommand(new BridgeCommand.Raw(Guid.Empty, "SET_SIM_SCOPE", captured[1]));
        Assert(dashLine == "SET_SIM_SCOPE|" + zeros + "|-|-", $"the escape records the wire line as the codec wrote it: {dashLine}");
        string emptyLine = BridgeLineCodec.EncodeCommand(new BridgeCommand.Raw(Guid.Empty, "DIAG_WALK", captured[0]));
        Assert(emptyLine == "DIAG_WALK|" + zeros + "||2", $"a bare - still records an empty part: {emptyLine}");
    }
}
