using System.Reflection;
using System.Text;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("CheckpointSave.OriginalSelectorFindsExtensionlessFolder")]
    private static void CheckpointSaveOriginalSelectorFindsExtensionlessFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "simsonline-selector-" + Guid.NewGuid().ToString("N"));
        string extensionless = Path.Combine(root, "Town");
        string suffixed = extensionless + ".sims3";
        try
        {
            // Bind the original production entry, not the candidate-only roots overload.
            MethodInfo selector = typeof(HeadlessSims3Host).GetMethod("FindNewestSaveFolder",
                BindingFlags.Static | BindingFlags.NonPublic, binder: null, types: new[] { typeof(string) },
                modifiers: null)!;
            Directory.CreateDirectory(extensionless);
            DateTime baseline = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            string newerNhd = Path.Combine(extensionless, "Town.nhd");
            File.WriteAllBytes(newerNhd, new byte[] { 1 });
            File.SetLastWriteTimeUtc(newerNhd, baseline.AddDays(1));

            // Rooted names make Path.Combine ignore every discovered root: only this temp tree is read.
            foreach (string name in new[] { extensionless, suffixed })
                Assert((string?)selector.Invoke(null, new object[] { name }) == extensionless,
                    "The original selector must find an extensionless-only save.");

            Directory.CreateDirectory(suffixed);
            string olderNhd = Path.Combine(suffixed, "Town.nhd");
            File.WriteAllBytes(olderNhd, new byte[] { 2 });
            File.SetLastWriteTimeUtc(olderNhd, baseline);
            foreach (string name in new[] { extensionless, suffixed })
                Assert((string?)selector.Invoke(null, new object[] { name }) == extensionless,
                    "The original selector must prefer the newer extensionless save.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test("CheckpointSave.FolderSelectionBothFormsAndStableTies")]
    private static void CheckpointSaveFolderSelectionBothFormsAndStableTies()
    {
        // Candidate order is root first, then suffixed before extensionless within that root.
        foreach (var scenario in new[]
        {
            (Times: new[] { 0, 2, 0, 0 }, Expected: 1), // Extensionless only.
            (Times: new[] { 1, 2, 0, 0 }, Expected: 1), // Newer extensionless.
            (Times: new[] { 2, 1, 0, 0 }, Expected: 0), // Newer suffixed.
            (Times: new[] { 2, 2, 2, 2 }, Expected: 0), // Both tie breakers.
            (Times: new[] { 0, 2, 2, 0 }, Expected: 1), // Earlier root beats later suffix.
            (Times: new[] { 0, 0, 2, 2 }, Expected: 2), // Suffix first in later root.
            (Times: new[] { 1, 1, 2, 3 }, Expected: 3), // Newest beats root order.
        })
        {
            string root = NewSavesDirectory();
            try
            {
                string[] roots = [Path.Combine(root, "first"), Path.Combine(root, "second")];
                string[] folders = roots.SelectMany(path => new[] { Path.Combine(path, "Town.sims3"), Path.Combine(path, "Town") }).ToArray();
                DateTime baseline = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                for (int i = 0; i < folders.Length; i++)
                {
                    if (scenario.Times[i] == 0) continue;
                    Directory.CreateDirectory(folders[i]);
                    string nhd = Path.Combine(folders[i], "Town.nhd");
                    File.WriteAllBytes(nhd, [1]);
                    File.SetLastWriteTimeUtc(nhd, baseline.AddDays(scenario.Times[i]));
                    string olderNhd = Path.Combine(folders[i], "Other.nhd");
                    File.WriteAllBytes(olderNhd, [2]);
                    File.SetLastWriteTimeUtc(olderNhd, baseline);
                }

                foreach (string name in new[] { "Town", "Town.sims3" })
                    Assert(HeadlessSims3Host.FindNewestSaveFolder(name, roots) == folders[scenario.Expected],
                        $"Wrong folder for {name} with timestamps {string.Join(",", scenario.Times)}.");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test("CheckpointSave.FolderSelectionRequiresFlatNhd")]
    private static void CheckpointSaveFolderSelectionRequiresFlatNhd()
    {
        string root = NewSavesDirectory();
        try
        {
            string suffixed = Path.Combine(root, "Town.sims3");
            string extensionless = Path.Combine(root, "Town");
            Directory.CreateDirectory(Path.Combine(suffixed, "nested"));
            Directory.CreateDirectory(extensionless);
            DateTime baseline = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (string invalid in new[] { Path.Combine(suffixed, "Meta.data"), Path.Combine(suffixed, "nested", "Town.nhd") })
            {
                File.WriteAllBytes(invalid, [1]);
                File.SetLastWriteTimeUtc(invalid, baseline.AddDays(2));
            }
            foreach (string name in new[] { "Town", "Town.sims3" })
                Assert(HeadlessSims3Host.FindNewestSaveFolder(name, [root]) == null, "A save requires a flat NHD file.");

            string nhd = Path.Combine(extensionless, "Town.nhd");
            File.WriteAllBytes(nhd, [1]);
            File.SetLastWriteTimeUtc(nhd, baseline);
            foreach (string name in new[] { "Town", "Town.sims3" })
                Assert(HeadlessSims3Host.FindNewestSaveFolder(name, [root]) == extensionless,
                    "Newer unrelated or nested files must not outrank a valid extensionless save.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test("CheckpointSave.ExtensionlessSelectionKeepsBootRecoverableBackup")]
    private static void CheckpointSaveExtensionlessSelectionKeepsBootRecoverableBackup()
    {
        foreach (bool publicSave in new[] { false, true })
        {
            using CheckpointSaveHost fixture = new(null, reuse: false) { WriteExtensionless = true };
            if (publicSave)
                fixture.Host.SaveGameAsync("Boot.sims3", fixture.Token).GetAwaiter().GetResult();
            else
                Assert(fixture.Checkpoint()?.FolderName == "Boot.sims3", "The shipped folder must stay suffixed.");

            string canonical = Path.Combine(fixture.Root, "Boot.sims3");
            string extensionless = Path.Combine(fixture.Root, "Boot");
            string kept = canonical + LastGoodSave.Suffix;
            Assert(HeadlessSims3Host.FindNewestSaveFolder("Boot", [fixture.Root]) == extensionless,
                "The real save path must select the newer extensionless result.");
            Assert(fixture.Saves.SequenceEqual(["Boot.sims3"]), "SAVE_GAME must still request the suffixed slot.");
            Assert(LastGoodSave.Verify(kept) == null, "Both save paths must keep the canonical boot backup.");
            Assert(!Directory.Exists(extensionless + LastGoodSave.Suffix), "Do not create an undiscoverable extensionless backup.");
            Assert(File.ReadAllBytes(Path.Combine(kept, "Meta.data")).SequenceEqual(FakeDbpf(1, 64, fixture.Fill)),
                "The backup must contain the selected extensionless bytes, not the stale suffixed save.");

            Directory.Delete(canonical, recursive: true);
            Directory.Delete(extensionless, recursive: true);
            typeof(HeadlessSims3Host).GetMethod("CheckBootSave", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, ["Boot.sims3"]);
            Assert(File.ReadAllBytes(Path.Combine(canonical, "Meta.data")).SequenceEqual(FakeDbpf(1, 64, fixture.Fill)),
                "The actual boot lookup must discover and restore the canonical backup.");
        }
    }

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
        public bool WriteExtensionless { get; set; }

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
                if (WriteExtensionless) target = id;
                WriteSaveFolder(Path.Combine(Root, target), FakeSaveFiles(Fill));
                if (WriteExtensionless)
                {
                    DateTime baseline = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    foreach (string form in new[] { id + ".sims3", id })
                    {
                        string folder = Path.Combine(Root, form);
                        if (!Directory.Exists(folder)) continue;
                        foreach (string nhd in Directory.GetFiles(folder, "*.nhd"))
                            File.SetLastWriteTimeUtc(nhd, form == id ? baseline.AddDays(1) : baseline);
                    }
                }
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
