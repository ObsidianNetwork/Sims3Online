using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("SimsOnlineClient.LineResumeKeepsCompletedCursorAndCommandIdentity")]
    private static void ClientWireResume() => ClientWireResumeAsync().GetAwaiter().GetResult();

    private static async Task ClientWireResumeAsync()
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        Type type = Assembly.LoadFrom(Path.Combine(root, "SimsOnline.Client/bin/Debug/net8.0/SimsOnline.Client.dll"))
            .GetType("SimsOnline.Client.PublicWireConnection")!;
        List<string> diagnostics = new();
        object wire = Activator.CreateInstance(type, new object?[] { (Action<string>)diagnostics.Add })!;
        MethodInfo connect = type.GetMethod("ConnectAsync")!, encode = type.GetMethod("EncodeCommand")!, read = type.GetMethod("ReadJsonAsync")!;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        T Property<T>(string name) => (T)type.GetProperty(name)!.GetValue(wire)!;
        async Task<(bool Warm, string Hello)> Connect(string responses)
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            Task<TcpClient> accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            using TcpClient peer = await accepted;
            using StreamReader reader = new(client.GetStream(), Encoding.UTF8);
            using StreamReader peerReader = new(peer.GetStream(), Encoding.UTF8);
            Task<bool> handshake = (Task<bool>)connect.Invoke(wire, [client.GetStream(), reader, "client", deadline.Token])!;
            string hello = (await peerReader.ReadLineAsync(deadline.Token))!;
            await peer.GetStream().WriteAsync(Encoding.UTF8.GetBytes(responses), deadline.Token);
            return (await handshake, hello);
        }
        async Task<ServerEvent> Next(StreamReader source) => PublicProtocolCodec.DecodeEvent(
            await (Task<string>)read.Invoke(wire, [source, deadline.Token])!);
        Assert(!(await Connect("WELCOME|1|9|\n")).Warm && Property<uint?>("Epoch") == 9, "Cold handshake retains epoch.");
        Guid joinId = Guid.NewGuid();
        encode.Invoke(wire, [new ClientCommand.JoinSession(joinId, 7, "token")]);
        string token = "0123456789abcdef0123456789abcdef";
        using StreamReader initial = new(new MemoryStream(Encoding.UTF8.GetBytes(
            $"ACK|1|JoinSession|ok|resume_token={token}|b1\nBULK|1|0|1|b2s=\n"
            + "EV|2|0|0|GameSpeedChanged|Normal\nEV|4|0|0|SimAnimation|b2\n"
            + "EV|5|0|0|GameSpeedChanged|Pause\nBULK|2|0|2|Ww==\nBULK|2|1|2|MzFd\n")));
        Assert(await Next(initial) is ServerEvent.CommandAccepted { CommandId: var command } && command == joinId,
            "Trailing token and ACK bulk preserve acceptance.");
        Assert(Property<string>("ResumeToken") == token && Property<ulong>("LastDeliveredSeq") == 0,
            "ACK never advances EV cursor.");
        Assert(await Next(initial) is ServerEvent.GameSpeedChanged && Property<ulong>("LastDeliveredSeq") == 2,
            "Delivered event advances cursor.");
        Assert(await Next(initial) is ServerEvent.SimAnimation && Property<ulong>("LastDeliveredSeq") == 4,
            "Bulk event advances only on completion; queued seq 5 is not delivered yet.");
        ClientCommand claim = new ClientCommand.ClaimSim(Guid.NewGuid(), 7, 55);
        string before = (string)encode.Invoke(wire, [claim])!;
        var warm = await Connect("WELCOME|1|9|\nRESUME_OK|9|5|1\n");
        Assert(warm.Warm && warm.Hello.Contains($",resume=9.4,resume_token={token}", StringComparison.Ordinal),
            "Warm hello carries exact epoch, delivered cursor and private credential.");
        Assert((string)encode.Invoke(wire, [claim])! == before, "Warm handshake preserves command and correlation identity.");
        using StreamReader replay = new(new MemoryStream(Encoding.UTF8.GetBytes(
            "EV|5|0|0|GameSpeedChanged|Pause\nEV|6|0|0|SimAnimation|b3\nBULK|3|0|1|WzMxXQ==\n")));
        Assert(await Next(replay) is ServerEvent.GameSpeedChanged { Speed: GameSpeed.Pause }, "Queued event is replayed once.");
        Assert(await Next(replay) is ServerEvent.SimAnimation && Property<ulong>("LastDeliveredSeq") == 6,
            "Bulk progression survives warm reconnect.");
        var fresh = await Connect("WELCOME|1|10|\nRESUME_FRESH|10\n");
        Assert(!fresh.Warm && Property<uint?>("Epoch") == 10 && type.GetProperty("ResumeToken")!.GetValue(wire) == null
            && Property<ulong>("LastDeliveredSeq") == 0, "Fresh clears old lease and cursor.");
        Assert((string)encode.Invoke(wire, [claim])! == "CMD|1|ClaimSim|1|7|55", "Fresh resets command identity namespace.");
        Assert(diagnostics.Count == 0, "Valid warm/fresh traffic has no decoding errors.");
    }

    [Test("PublicLineCodec.ResumeFramesAndTokenAreTypedAndBounded")]
    private static void ResumeFramesAndToken()
    {
        Assert(PublicLineCodecV1.DecodeResume("RESUME_OK|9|10|2") is PublicLineCodecV1.ResumeOkFrame(9, 10, 2),
            "Typed warm result.");
        Assert(PublicLineCodecV1.DecodeResume("RESUME_FRESH|9") is PublicLineCodecV1.ResumeFreshFrame(9), "Typed fresh result.");
        foreach (string invalid in new[] { "RESUME_OK|9|0|1", "RESUME_OK|9|1|2049", "RESUME_OK|9|1",
            "RESUME_FRESH|9|extra", "RESUME_FRESH|4294967296", "RESUME_OK|9|1|-1" })
        {
            bool rejected = false;
            try { PublicLineCodecV1.DecodeResume(invalid); }
            catch (Exception error) when (error is FormatException or OverflowException) { rejected = true; }
            Assert(rejected, "Reject malformed resume: " + invalid);
        }
        string token = new('a', 32);
        Assert(PublicLineCodecV1.ResumeToken(PublicLineCodecV1.DecodeAck($"ACK|1|JoinSession|ok|resume_token={token}|b1")) == token,
            "Credential is a tagged ACK field, not a bulk reference.");
        Assert(!PublicLineCodecV1.ValidResumeToken(token.ToUpperInvariant()) && !PublicLineCodecV1.ValidResumeToken("short"),
            "Token grammar is lowercase 128-bit hex.");
    }

    [Test("SimsOnlineClient.LineCodecFailureStillDeliversDrainedEvents")]
    private static void ClientWireDrainedEvents() => ClientWireDrainedEventsAsync().GetAwaiter().GetResult();

    private static async Task ClientWireDrainedEventsAsync()
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        Type type = Assembly.LoadFrom(Path.Combine(root, "SimsOnline.Client/bin/Debug/net8.0/SimsOnline.Client.dll"))
            .GetType("SimsOnline.Client.PublicWireConnection")!;
        List<string> diagnostics = new();
        // Simulate 237c26b7's false+drained-events contract without editing this lane's older codec.
        Func<string, (bool Success, IReadOnlyList<PublicLineCodecV1.EventEnvelope> Events, string? Error)> receive = line =>
            line.StartsWith("BULK|", StringComparison.Ordinal)
                ? (false, new PublicLineCodecV1.EventEnvelope[]
                {
                    new(2, 0, 0, new ServerEvent.ServerError("after bad bulk")),
                    new(3, 0, 0, new ServerEvent.GameSpeedChanged(GameSpeed.Normal)),
                }, "bulk_malformed")
                : (true, Array.Empty<PublicLineCodecV1.EventEnvelope>(), null);
        object wire = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object?[] { (Action<string>)diagnostics.Add, receive }, null)!;
        MethodInfo read = type.GetMethod("ReadJsonAsync")!;
        string frames = "EV|1|0|0|SimAnimation|b1\nEV|2|0|0|ServerError|6166746572206261642062756c6b\n"
            + "EV|3|0|0|GameSpeedChanged|Normal\nBULK|1|1|2|\n";
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(frames)));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        async Task<ServerEvent> Next() => PublicProtocolCodec.DecodeEvent(
            await (Task<string>)read.Invoke(wire, [reader, timeout.Token])!);
        Assert(await Next() is ServerEvent.ServerError { Message: "after bad bulk" }, "First queued valid event survives the codec's false result.");
        Assert(await Next() is ServerEvent.GameSpeedChanged { Speed: GameSpeed.Normal }, "Every drained event is delivered in order.");
        Assert(diagnostics.SequenceEqual(new[] { "bulk_malformed" }), "The failure is also named exactly once.");
    }

    [Test("SimsOnlineClient.LineAckBulkPreservesIntroductionOrderAndNamesDuplicates")]
    private static void ClientWireAckOrder() => ClientWireAckOrderAsync().GetAwaiter().GetResult();

    private static async Task ClientWireAckOrderAsync()
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        Type type = Assembly.LoadFrom(Path.Combine(root, "SimsOnline.Client/bin/Debug/net8.0/SimsOnline.Client.dll"))
            .GetType("SimsOnline.Client.PublicWireConnection")!;
        List<string> diagnostics = new();
        object wire = Activator.CreateInstance(type, new object?[] { (Action<string>)diagnostics.Add })!;
        MethodInfo encode = type.GetMethod("EncodeCommand")!, read = type.GetMethod("ReadJsonAsync")!;
        encode.Invoke(wire, [new ClientCommand.ClaimSim(Guid.NewGuid(), 1, 2)]);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(
            "ACK|1|ClaimSim|b1\nEV|1|0|0|GameSpeedChanged|Normal\nBULK|1|0|1|b2s=\n")));
        async Task<ServerEvent> Next(StreamReader source) => PublicProtocolCodec.DecodeEvent(
            await (Task<string>)read.Invoke(wire, [source, timeout.Token])!);
        Assert(await Next(reader) is ServerEvent.CommandAccepted, "An interleaved event cannot pass the ACK that introduced its bulk first.");
        Assert(await Next(reader) is ServerEvent.GameSpeedChanged, "The interleaved event follows the completed ACK.");
        using StreamReader duplicate = new(new MemoryStream(Encoding.UTF8.GetBytes(
            "ACK|1|ClaimSim|b2|b2\nEV|2|0|0|ServerError|7374696c6c2068657265\n")));
        Assert(await Next(duplicate) is ServerEvent.ServerError { Message: "still here" }, "A malformed ACK must not fabricate acceptance.");
        Assert(diagnostics.SequenceEqual(new[] { "bulk_malformed" }), "Duplicate ACK ids produce a named diagnostic.");
    }

    [Test("SimsOnlineClient.LineHandshakeJoinBulkDeltaAndAnimation")]
    private static void ClientWireLineRoundTrip() => ClientWireLineRoundTripAsync().GetAwaiter().GetResult();

    private static async Task ClientWireLineRoundTripAsync()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        ProcessStartInfo psi = NewClientProcessStartInfo(root,
            $"--wire line --endpoint {listener.LocalEndpoint} --player 73 --json-stdout --enable-mirror --exit-after-role-changed");
        psi.Environment["SIMSONLINE_SIMLINK_CLIENT_RING"] = "1";
        psi.Environment["SIMSONLINE_WORLD_FROM_SERVER"] = "1";
        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(deadline.Token);
            using NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.UTF8);
            using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            string hello = await reader.ReadLineAsync(deadline.Token) ?? "";
            Assert(hello.StartsWith("HELLO|1|ring=1,wfs=1,role=client", StringComparison.Ordinal), "Line client must hello first: " + hello);
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeWelcome(42));
            string command = await reader.ReadLineAsync(deadline.Token) ?? "";
            PublicLineCodecV1.CommandFrame join = PublicLineCodecV1.DecodeCommand(command);
            Assert(join.Verb == "JoinSession" && join.PlayerId == 73, command);
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeAck(new(join.Corr, join.Verb, [])));
            ulong seq = 0;
            async Task Event(ServerEvent value)
            {
                string encoded = PublicLineCodecV1.EncodeEvent(value, ++seq, 5, join.Corr);
                // Force two chunks at this boundary even for a small event.
                PublicLineCodecV1.EventFrame frame = PublicLineCodecV1.DecodeEvent(encoded);
                await writer.WriteLineAsync(PublicLineCodecV1.EncodeEvent(frame with { Fields = ["b" + seq] }));
                byte[] field = Encoding.UTF8.GetBytes(frame.Fields[0]);
                int half = field.Length / 2;
                await writer.WriteLineAsync(PublicLineCodecV1.EncodeBulkChunk(new(seq, 0, 2, Convert.ToBase64String(field[..half]))));
                await writer.WriteLineAsync(PublicLineCodecV1.EncodeBulkChunk(new(seq, 1, 2, Convert.ToBase64String(field[half..]))));
            }
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeEvent(new ServerEvent.SessionJoined(73, "line"), ++seq, 5, join.Corr));
            SimulationSnapshot snapshot = new(5, "World", "Line.sims3", GameSpeed.Normal, [], []);
            await Event(new ServerEvent.FullSnapshot(snapshot));
            PublicLineCodecV1.CommandFrame role = PublicLineCodecV1.DecodeCommand(await reader.ReadLineAsync(deadline.Token) ?? "");
            // A mirror may acknowledge the baseline before setting its role.
            while (role.Verb == "AckSnapshot")
                role = PublicLineCodecV1.DecodeCommand(await reader.ReadLineAsync(deadline.Token) ?? "");
            Assert(role.Verb == "SetClientRole" && role.Corr > join.Corr, "Role follows joined baseline.");
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeAck(new(role.Corr, role.Verb, [])));
            await Event(new ServerEvent.SimAnimation(["1|S|2"]));
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeEvent(new ServerEvent.SnapshotDelta(snapshot with { Tick = 6 }), ++seq, 6, 0));
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeEvent(new ServerEvent.ClientRoleChanged(73, PlayerRole.Client), ++seq, 6, role.Corr));
            await process.WaitForExitAsync(deadline.Token);
            Assert(process.ExitCode == 0, "Line client exit: " + await stderr);
            string output = await stdout;
            Assert(output.Contains("session_joined", StringComparison.Ordinal), "Join must reach dispatch.");
            Assert(output.Contains("sim_animation", StringComparison.Ordinal), "Bulk animation must reach dispatch.");
            Assert(output.Contains("snapshot_delta", StringComparison.Ordinal), "Delta must reach dispatch.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }

    [Test("SimsOnlineClient.LineWorldFetchHandshakesAndReassemblesRawBulk")]
    private static void ClientWireWorldFetch() => ClientWireWorldFetchAsync().GetAwaiter().GetResult();

    private static async Task ClientWireWorldFetchAsync()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        Assembly assembly = Assembly.LoadFrom(Path.Combine(root, "SimsOnline.Client/bin/Debug/net8.0/SimsOnline.Client.dll"));
        MethodInfo method = assembly.GetType("SimsOnline.Client.Program")!.GetMethod("FetchWorldFromServerAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        string folder = Path.Combine(Path.GetTempPath(), "card57-world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Task<string> fetch = (Task<string>)method.Invoke(null,
                ["127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, 74, folder, false, null, deadline.Token, "line"])!;
            using TcpClient client = await listener.AcceptTcpClientAsync(deadline.Token);
            using NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.UTF8);
            using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            string hello = (await reader.ReadLineAsync(deadline.Token))!;
            Assert(hello.StartsWith("HELLO|1|", StringComparison.Ordinal)
                && !hello.Contains("resume", StringComparison.Ordinal), "Unjoined world fetch must never send a player credential.");
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeWelcome(4));
            PublicLineCodecV1.CommandFrame request = PublicLineCodecV1.DecodeCommand(await reader.ReadLineAsync(deadline.Token) ?? "");
            Assert(request.Verb == "RequestWorld", "World request after welcome.");
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeAck(new(request.Corr, request.Verb, [])));
            byte[] data = [0, 255, 17, 42];
            ulong bulk = 0;
            foreach (string line in PublicLineCodecV1.EncodeEventLines(
                new ServerEvent.WorldFileChunk("Line.sims3", 0, 1, "world.nhd", 0, data.Length, data), 1, 0, request.Corr, ref bulk))
                await writer.WriteLineAsync(line);
            Assert(await fetch.WaitAsync(deadline.Token) == "Line.sims3", "World folder returned.");
            Assert(File.ReadAllBytes(Path.Combine(folder, "Line.sims3/world.nhd")).SequenceEqual(data), "Raw bulk must survive the file writer.");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test("SimsOnlineClient.LineRepliesKeepGuidIdentityAndNameBulkErrors")]
    private static void ClientWireLineReplies() => ClientWireLineRepliesAsync().GetAwaiter().GetResult();

    private static async Task ClientWireLineRepliesAsync()
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        Assembly assembly = Assembly.LoadFrom(Path.Combine(root, "SimsOnline.Client/bin/Debug/net8.0/SimsOnline.Client.dll"));
        Type type = assembly.GetType("SimsOnline.Client.PublicWireConnection")!;
        List<string> errors = new();
        object wire = Activator.CreateInstance(type, new object?[] { (Action<string>)errors.Add })!;
        MethodInfo encode = type.GetMethod("EncodeCommand")!;
        MethodInfo read = type.GetMethod("ReadJsonAsync")!;
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        ClientCommand claim = new ClientCommand.ClaimSim(first, 5, ulong.MaxValue - 1);
        string command = (string)encode.Invoke(wire, [claim])!;
        Assert(command == (string)encode.Invoke(wire, [claim])!, "Retry keeps corr and commandId.");
        Assert(command == "CMD|1|ClaimSim|1|5|18446744073709551614", "Public ids are not narrowed.");
        encode.Invoke(wire, [new ClientCommand.QueueInteraction(second, 5, new InteractionRequest(1, "BedSleep", null, null, new Dictionary<string, string>()))]);
        string frames = "ACK|1|ClaimSim|b1\nBULK|1|0|2|bw==\nBULK|1|1|2|aw==\n"
            + "ERR|2|UnauthorizedClientInsertion|QueueInteraction|\n"
            + "EV|1|0|2|PickFailed|RouteFailed|\n"
            + "ERR|0|bad_line|-|\n"
            + "EV|2|0|0|SimAnimation|b2\nBULK|2|1|2|\n"
            + "EV|3|0|0|ServerError|6166746572206572726f72\n";
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(frames)));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        async Task<ServerEvent> Next()
        {
            string json = await (Task<string>)read.Invoke(wire, [reader, timeout.Token])!;
            return PublicProtocolCodec.DecodeEvent(json);
        }
        Assert(await Next() is ServerEvent.CommandAccepted accepted && accepted.CommandId == first, "ACK restores the original GUID after bulk.");
        Assert(await Next() is ServerEvent.CommandRejected rejected && rejected.CommandId == second
            && rejected.Reason == "UnauthorizedClientInsertion", "ERR restores the command and reason.");
        Assert(await Next() is ServerEvent.CommandRejected pick && pick.CommandId == second
            && pick.Reason == "RouteFailed", "PickFailed goes to the command's existing refusal path.");
        Assert(await Next() is ServerEvent.ServerError { Message: "bad_line" }, "Idless errors are named.");
        Assert(await Next() is ServerEvent.ServerError { Message: "after error" }
            && errors.SequenceEqual(new[] { "bulk_malformed" }), "Malformed bulk is named and later valid events still arrive.");
    }

    [Test("SimsOnlineClient.LineTestClientSmoke")]
    private static void ClientWireTestClientSmoke() => ClientWireTestClientSmokeAsync().GetAwaiter().GetResult();

    private static async Task ClientWireTestClientSmokeAsync()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        ProcessStartInfo psi = new("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // The ordinary suite's wrapper builds Client, not TestClient. Run its project so this
        // regression also works from a clean checkout without relying on a developer's artifacts.
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(Path.Combine(root, "SimsOnline.TestClient/SimsOnline.TestClient.csproj"));
        psi.ArgumentList.Add("--no-launch-profile");
        psi.ArgumentList.Add("--");
        foreach (string arg in new[] { "--wire", "line", "--endpoint", listener.LocalEndpoint.ToString()!, "--skip-snapshot", "--read-timeout-seconds", "10" })
            psi.ArgumentList.Add(arg);
        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(deadline.Token);
            using NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.UTF8);
            using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Assert((await reader.ReadLineAsync(deadline.Token))!.StartsWith("HELLO|1|", StringComparison.Ordinal), "TestClient must hello first.");
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeWelcome(1));
            PublicLineCodecV1.CommandFrame join = PublicLineCodecV1.DecodeCommand(await reader.ReadLineAsync(deadline.Token) ?? "");
            Assert(join.Verb == "JoinSession", "TestClient joins.");
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeAck(new(join.Corr, join.Verb, [])));
            PublicLineCodecV1.CommandFrame leave = PublicLineCodecV1.DecodeCommand(await reader.ReadLineAsync(deadline.Token) ?? "");
            Assert(leave.Verb == "LeaveSession" && leave.Corr > join.Corr, "TestClient leaves on the same line session.");
            await writer.WriteLineAsync(PublicLineCodecV1.EncodeAck(new(leave.Corr, leave.Verb, [])));
            await process.WaitForExitAsync(deadline.Token);
            Assert(process.ExitCode == 0, "TestClient line smoke: " + await stdout + await stderr);
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
