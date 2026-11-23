using System.Reflection;
using System.Text;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("CheckpointSave.ConfigDefaultEnvironmentAndCli")]
    private static void CheckpointSaveConfigDefaultEnvironmentAndCli()
    {
        string? previous = Environment.GetEnvironmentVariable("SIMSONLINE_CHECKPOINT_SAVE");
        try
        {
            Environment.SetEnvironmentVariable("SIMSONLINE_CHECKPOINT_SAVE", null);
            Assert(SessionConfig.FromArgs([]).CheckpointSaveName == null, "Unset must preserve the boot-save default.");
            Environment.SetEnvironmentVariable("SIMSONLINE_CHECKPOINT_SAVE", "Environment.sims3");
            Assert(SessionConfig.FromArgs([]).CheckpointSaveName == "Environment.sims3", "Read the checkpoint environment variable.");
            SessionConfig config = SessionConfig.FromArgs(["--save", "Boot.sims3", "--checkpoint-save", "Cli.sims3"]);
            Assert(config.SaveName == "Boot.sims3", "The checkpoint flag must not change the boot slot.");
            Assert(config.CheckpointSaveName == "Cli.sims3", "CLI must override the environment.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMSONLINE_CHECKPOINT_SAVE", previous);
        }
    }

    [Test("CheckpointSave.DisabledReuseStillUsesCheckpointSlot")]
    private static void CheckpointSaveDisabledReuseStillUsesCheckpointSlot()
    {
        using CheckpointSaveHost fixture = new("Join.sims3", reuse: false);
        Assert(fixture.Checkpoint()?.FolderName == "Join.sims3", "Disabling reuse must not change the checkpoint slot.");
        fixture.Fill = 9;
        WorldSave? world = fixture.Checkpoint();
        Assert(world != null && world.Files.Single(f => f.Name == "Meta.data").Data.SequenceEqual(FakeDbpf(1, 64, 9)),
            "Without reuse, every join must read the newly saved bytes.");
        Assert(fixture.Saves.SequenceEqual(["Join.sims3", "Join.sims3"]), "Both fresh checkpoints must use the separate slot.");
    }

    [Test("CheckpointSave.JoinWritesSeparateSlotAndLastGood")]
    private static void CheckpointSaveJoinWritesSeparateSlotAndLastGood()
    {
        foreach (string? slot in new string?[] { "Join", "Join.sims3", null, "", " " })
        {
            using CheckpointSaveHost fixture = new(slot);
            WorldSave? world = fixture.Checkpoint();
            string expected = string.IsNullOrWhiteSpace(slot) ? "Boot.sims3" : "Join.sims3";
            Assert(world?.FolderName == expected, $"Join must return {expected}, got {world?.FolderName}.");
            Assert(fixture.Saves.SequenceEqual([expected]), "SAVE_GAME must target the checkpoint slot with its extension.");
            Assert(!Directory.Exists(Path.Combine(fixture.Root, Path.GetFileNameWithoutExtension(expected))),
                "A checkpoint must never create an extensionless save folder.");
            Assert(LastGoodSave.Verify(Path.Combine(fixture.Root, expected) + LastGoodSave.Suffix) == null,
                "The checkpoint's last-good copy must be complete.");
            if (!string.IsNullOrWhiteSpace(slot))
            {
                Assert(File.ReadAllBytes(Path.Combine(fixture.Root, "Boot.sims3", "Meta.data")).SequenceEqual(FakeDbpf(1, 64, 3)),
                    "A join must leave the boot save's bytes untouched.");
                Assert(!Directory.Exists(Path.Combine(fixture.Root, "Boot.sims3.lastgood")), "A join must not rotate the boot's last-good copy.");
            }
        }
    }

    [Test("CheckpointSave.PublicSavesRefreshOrInvalidateJoinCache")]
    private static void CheckpointSavePublicSavesRefreshOrInvalidateJoinCache()
    {
        foreach (string target in new[] { "Boot", "Boot.sims3", "Join", "Join.sims3", "Manual.sims3" })
        {
            using CheckpointSaveHost fixture = new("Join.sims3");
            fixture.Checkpoint();
            fixture.Fill = 9;
            fixture.Host.SaveGameAsync(target, fixture.Token).GetAwaiter().GetResult();
            string expected = Path.GetFileNameWithoutExtension(target) + ".sims3";
            Assert(File.ReadAllBytes(Path.Combine(fixture.Root, expected, "Meta.data")).SequenceEqual(FakeDbpf(1, 64, 9)),
                "A public save after a checkpoint must update the requested .sims3 folder, not an extensionless sibling.");
            Assert(fixture.Saves.Last() == expected, "Public saves must send the requested slot with its extension.");
            Assert(LastGoodSave.Verify(Path.Combine(fixture.Root, expected) + LastGoodSave.Suffix) == null,
                "Public saves must keep their own last-good copy.");
            WorldSave? joined = fixture.Checkpoint();
            Assert(joined != null && joined.FolderName == "Join.sims3", "A public save must not redirect the next client's load slot.");
            Assert(joined.Files.Single(f => f.Name == "Meta.data").Data.SequenceEqual(FakeDbpf(1, 64, 9)),
                "A public save must not leave an older reusable world, even when its fingerprint is unchanged.");
            Assert(fixture.Saves.Count == (expected == "Join.sims3" ? 2 : 3),
                "Only a public save to the checkpoint slot may seed join reuse.");
        }
    }

    [Test("CheckpointSave.UnsetPublicBootSaveStillSeedsJoinReuse")]
    private static void CheckpointSaveUnsetPublicBootSaveStillSeedsJoinReuse()
    {
        using CheckpointSaveHost fixture = new(null);
        fixture.Host.SaveGameAsync("Boot.sims3", fixture.Token).GetAwaiter().GetResult();
        Assert(fixture.Checkpoint()?.FolderName == "Boot.sims3", "Without a checkpoint slot, clients still load the server save.");
        Assert(fixture.Saves.SequenceEqual(["Boot.sims3"]), "The public boot save must still be reusable when unset.");
    }

    /// <summary>The real host and bridge, with only TS3's SAVE_GAME and fingerprint replies simulated.</summary>
    private sealed class CheckpointSaveHost : IDisposable
    {
        private readonly string? _previousRoot = Environment.GetEnvironmentVariable("SIMSONLINE_SERVER_SAVES_DIR");
        private readonly string? _previousReuse = Environment.GetEnvironmentVariable("SIMSONLINE_JOIN_REUSE");
        private readonly string? _previousSlot = Environment.GetEnvironmentVariable("SIMSONLINE_CHECKPOINT_SAVE");
        private readonly PinnedHostBridge _bridge;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(5));
        private string _currentSaveId = "Boot.sims3";
        public string Root { get; } = NewSavesDirectory();
        public HeadlessSims3Host Host { get; }
        public CancellationToken Token => _timeout.Token;
        public List<string> Saves { get; } = new();
        public byte Fill { get; set; } = 7;

        public CheckpointSaveHost(string? slot, bool reuse = true)
        {
            Environment.SetEnvironmentVariable("SIMSONLINE_SERVER_SAVES_DIR", Root);
            Environment.SetEnvironmentVariable("SIMSONLINE_JOIN_REUSE", reuse ? "1" : "0");
            Environment.SetEnvironmentVariable("SIMSONLINE_CHECKPOINT_SAVE", null);
            WriteSaveFolder(Path.Combine(Root, "Boot.sims3"), FakeSaveFiles(3));
            List<string> args = ["--save", "Boot.sims3"];
            if (slot != null) args.AddRange(["--checkpoint-save", slot]);
            Host = new HeadlessSims3Host(SessionConfig.FromArgs(args));
            typeof(HeadlessSims3Host).GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Host, true);
            _bridge = new PinnedHostBridge(Host.Bridge);
            // Registered before each action: no reader thread, polling or timing-dependent response.
            Host.Bridge.FrameRecord += Reply;
        }

        private void Reply(string frame)
        {
            if (!frame.StartsWith("ctl->host", StringComparison.Ordinal)) return;
            string[] command = frame[(frame.IndexOf(" slot=", StringComparison.Ordinal) + 1)..].Split(' ', 2)[1].Split('|');
            Guid correlation = Guid.Parse(command[1]);
            string? report = null;
            if (command[0] == "SAVE_GAME")
            {
                string name = Encoding.UTF8.GetString(Convert.FromHexString(command[2]));
                Saves.Add(name);
                // CurrentSaveIdFor preserves the current id only for the same slot; a save-as uses the wire name.
                string id = name.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
                string currentId = _currentSaveId.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase) ? _currentSaveId[..^6] : _currentSaveId;
                string target = string.Equals(id, currentId, StringComparison.OrdinalIgnoreCase) ? _currentSaveId : name;
                WriteSaveFolder(Path.Combine(Root, target), FakeSaveFiles(Fill));
                _currentSaveId = target;
            }
            else
            {
                Assert(command[0] == "SACS_RECORDER", "Unexpected bridge command: " + command[0]);
                report = command[2] == "fingerprint-scope" ? "fingerprint-scope=ok" : FingerprintReply();
            }

            // Consume the outgoing frame and answer its pending request just as the bridge reader does.
            Array.Clear(_bridge.Region, BridgeSharedRegionLayout.SlotByteOffset(0), BridgeSharedRegionLayout.SlotSize);
            var pending = (System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<BridgeResponse>>)
                HostBridgeField("_pending").GetValue(Host.Bridge)!;
            Assert(pending.TryRemove(correlation, out var completion), "The outgoing command must already have a pending response.");
            completion.SetResult(new BridgeResponse(correlation, true, null, null, report));
        }

        public WorldSave? Checkpoint()
        {
            WorldSave? world = Host.CheckpointWorldAsync(Token).GetAwaiter().GetResult();
            DrainLastGood();
            return world;
        }

        private void DrainLastGood()
        {
            var gate = (SemaphoreSlim)typeof(HeadlessSims3Host).GetField("_lastGoodGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;
            gate.Wait(Token);
            gate.Release();
        }

        public void Dispose()
        {
            DrainLastGood();
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _bridge.Dispose();
            _timeout.Dispose();
            Environment.SetEnvironmentVariable("SIMSONLINE_SERVER_SAVES_DIR", _previousRoot);
            Environment.SetEnvironmentVariable("SIMSONLINE_JOIN_REUSE", _previousReuse);
            Environment.SetEnvironmentVariable("SIMSONLINE_CHECKPOINT_SAVE", _previousSlot);
            Directory.Delete(Root, recursive: true);
        }
    }
}
