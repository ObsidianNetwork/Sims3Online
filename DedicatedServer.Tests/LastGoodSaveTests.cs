using System.Buffers.Binary;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// the controller's last good copy of the server's save (LastGoodSave): the whole-save check, the kept copy
/// with its manifest, and the boot restore.
/// </summary>
internal static partial class Program
{
    // A DBPF 2.0 file as TS3 writes one: the header, the records, then the index (4 + 32 bytes per record) at the end.
    private static byte[] FakeDbpf(int records, int payloadBytes, byte fill = 7)
    {
        int indexSize = 4 + 32 * records;
        byte[] data = new byte[0x60 + payloadBytes + indexSize];
        "DBPF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x24), (uint)records);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C), (uint)indexSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), (uint)(data.Length - indexSize));
        data.AsSpan(0x60, payloadBytes).Fill(fill);
        return data;
    }

    private static List<WorldSaveFile> FakeSaveFiles(byte fill = 7) => new()
    {
        new("1a5tf1l3.dat", "Sunset Valley_0x0859db3c"u8.ToArray()),
        new("Meta.data", FakeDbpf(1, 64, fill)),
        new("Sunset Valley_0x0859db3c.nhd", FakeDbpf(12, 4096, fill)),
        new("TravelDB.package", FakeDbpf(3, 256, fill)),
    };

    private static string WriteSaveFolder(string folder, IEnumerable<WorldSaveFile> files)
    {
        Directory.CreateDirectory(folder);
        foreach (WorldSaveFile file in files)
        {
            File.WriteAllBytes(Path.Combine(folder, file.Name), file.Data);
        }

        return folder;
    }

    private static string NewSavesDirectory()
    {
        string saves = Path.Combine(Path.GetTempPath(), "simsonline-lastgood-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(saves);
        return saves;
    }

    [Test("LastGoodSave.FolderForCanonicalizesBothSaveForms")]
    private static void LastGoodSaveFolderForCanonicalizesBothSaveForms()
    {
        string folder = Path.Combine("Saves", "Town");
        foreach (string spelling in new[] { folder, folder + ".sims3" })
        {
            Assert(LastGoodSave.FolderFor(spelling) == folder + ".sims3.lastgood",
                "Both selected folder forms must share the boot recovery identity.");
            Assert(LastGoodSave.FolderFor(spelling + Path.DirectorySeparatorChar) == folder + ".sims3.lastgood",
                "A trailing directory separator must not change backup identity.");
        }
        Assert(LastGoodSave.FolderFor(folder + ".SIMS3") == folder + ".SIMS3.lastgood",
            "An existing case-insensitive suffix must not be duplicated.");
    }

    [Test("LastGoodSave.ProblemFindsCutAndMissingFiles")]
    private static void LastGoodSaveProblemFindsCutAndMissingFiles()
    {
        Assert(LastGoodSave.Problem(FakeSaveFiles()) == null, "A whole save should pass.");

        List<WorldSaveFile> cut = FakeSaveFiles();
        cut[2] = cut[2] with { Data = cut[2].Data[..^100] };
        string? problem = LastGoodSave.Problem(cut);
        Assert(problem != null && problem.StartsWith("Sunset Valley_0x0859db3c.nhd: DBPF index ends at", StringComparison.Ordinal),
            "An .nhd cut short (C2's partial copy, or a kill mid-copy) should fail on its index: " + problem);

        List<WorldSaveFile> noNhd = FakeSaveFiles().Where(f => !f.Name.EndsWith(".nhd", StringComparison.Ordinal)).ToList();
        Assert(LastGoodSave.Problem(noNhd) == "no .nhd file", "A save without its .nhd (the .backup C2 left) should fail.");

        List<WorldSaveFile> empty = FakeSaveFiles();
        empty[0] = empty[0] with { Data = Array.Empty<byte>() };
        Assert(LastGoodSave.Problem(empty) == "1a5tf1l3.dat is empty", "An empty file should fail.");

        byte[] wrongCount = FakeDbpf(12, 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(wrongCount.AsSpan(0x24), 13);
        Assert(LastGoodSave.DbpfProblem(wrongCount)?.Contains("does not fit 13 records", StringComparison.Ordinal) == true,
            "An index size that does not match the record count should fail.");
        Assert(LastGoodSave.DbpfProblem("plain text"u8) == null, "A file that is not DBPF is not checked.");
    }

    [Test("LastGoodSave.KeepWritesManifestAndReplacesTheCopy")]
    private static void LastGoodSaveKeepWritesManifestAndReplacesTheCopy()
    {
        string saves = NewSavesDirectory();
        try
        {
            string folder = WriteSaveFolder(Path.Combine(saves, "Option4BookChain.sims3"), FakeSaveFiles());
            string kept = LastGoodSave.Keep(folder, WorldSave.ReadFolder(folder, "Option4BookChain.sims3"), DateTimeOffset.UtcNow);
            Assert(kept == folder + LastGoodSave.Suffix, "The copy should sit next to the save: " + kept);
            Assert(LastGoodSave.Verify(kept) == null, "The kept copy should match its manifest.");
            List<LastGoodSave.ManifestEntry> entries = LastGoodSave.ParseManifest(File.ReadAllText(Path.Combine(kept, LastGoodSave.ManifestFileName)));
            Assert(entries.Count == 4 && entries.Any(e => e.Name == "Sunset Valley_0x0859db3c.nhd" && e.Sha256.Length == 64),
                "The manifest should list every file, names with spaces included.");

            WriteSaveFolder(folder, FakeSaveFiles(fill: 9));
            LastGoodSave.Keep(folder, WorldSave.ReadFolder(folder, "Option4BookChain.sims3"), DateTimeOffset.UtcNow);
            Assert(File.ReadAllBytes(Path.Combine(kept, "Meta.data"))[0x60] == 9, "A later save should replace the copy.");
            Assert(LastGoodSave.Verify(kept) == null, "The replaced copy should match its manifest.");
            Assert(!Directory.Exists(kept + ".incoming") && !Directory.Exists(kept + ".old"), "No staging or old folder should be left.");

            File.WriteAllBytes(Path.Combine(kept, "TravelDB.package"), FakeDbpf(3, 256, fill: 1));
            Assert(LastGoodSave.Verify(kept) == "TravelDB.package does not match its manifest", "A changed file in the copy should fail the check.");
        }
        finally
        {
            Directory.Delete(saves, recursive: true);
        }
    }

    [Test("LastGoodSave.KeepRefusesAPartialSave")]
    private static void LastGoodSaveKeepRefusesAPartialSave()
    {
        string saves = NewSavesDirectory();
        try
        {
            string folder = WriteSaveFolder(Path.Combine(saves, "Option4BookChain.sims3"), FakeSaveFiles());
            string kept = LastGoodSave.Keep(folder, WorldSave.ReadFolder(folder, "Option4BookChain.sims3"), DateTimeOffset.UtcNow);

            byte[] nhd = File.ReadAllBytes(Path.Combine(folder, "Sunset Valley_0x0859db3c.nhd"));
            File.WriteAllBytes(Path.Combine(folder, "Sunset Valley_0x0859db3c.nhd"), nhd[..^500]);
            bool threw = false;
            try
            {
                LastGoodSave.Keep(folder, WorldSave.ReadFolder(folder, "Option4BookChain.sims3"), DateTimeOffset.UtcNow);
            }
            catch (InvalidDataException ex)
            {
                threw = ex.Message.Contains("not whole", StringComparison.Ordinal);
            }

            Assert(threw, "A partial save should not be kept.");
            Assert(LastGoodSave.Verify(kept) == null, "The previous copy should stay as it was.");
            Assert(File.ReadAllBytes(Path.Combine(kept, "Sunset Valley_0x0859db3c.nhd")).Length == nhd.Length, "The previous .nhd should stay whole.");
        }
        finally
        {
            Directory.Delete(saves, recursive: true);
        }
    }

    [Test("LastGoodSave.BootRestoresAMissingOrCutSave")]
    private static void LastGoodSaveBootRestoresAMissingOrCutSave()
    {
        string saves = NewSavesDirectory();
        try
        {
            DateTimeOffset now = new(2026, 10, 21, 23, 5, 0, TimeSpan.Zero);
            string folder = WriteSaveFolder(Path.Combine(saves, "Option4BookChain.sims3"), FakeSaveFiles());
            LastGoodSave.Keep(folder, WorldSave.ReadFolder(folder, "Option4BookChain.sims3"), now);

            LastGoodSave.BootCheck same = LastGoodSave.EnsureBootable(folder, now);
            Assert(!same.Restored && same.Detail == "whole, the same as the last good copy", "A whole save is loaded as it is: " + same.Detail);

            // A commit cut short: the .nhd half copied, the files after it not copied yet.
            File.WriteAllBytes(Path.Combine(folder, "Sunset Valley_0x0859db3c.nhd"), FakeDbpf(12, 4096)[..2000]);
            File.Delete(Path.Combine(folder, "TravelDB.package"));
            LastGoodSave.BootCheck cut = LastGoodSave.EnsureBootable(folder, now);
            string aside = folder + ".broken-20261021-230500";
            Assert(cut.Restored && cut.Detail.Contains(Path.GetFileName(aside), StringComparison.Ordinal), "A cut save should be restored: " + cut.Detail);
            Assert(Directory.Exists(aside) && Directory.GetFiles(aside).Length == 3, "The cut save should be kept aside, not deleted.");
            Assert(LastGoodSave.Problem(WorldSave.ReadFolder(folder, "x").Files) == null, "The restored save should be whole.");
            Assert(!File.Exists(Path.Combine(folder, LastGoodSave.ManifestFileName)), "The restored save should not carry the manifest.");
            Assert(LastGoodSave.Verify(LastGoodSave.FolderFor(folder)) == null, "The copy should stay after a restore.");

            Directory.Delete(folder, recursive: true);
            LastGoodSave.BootCheck missing = LastGoodSave.EnsureBootable(folder, now.AddMinutes(1));
            Assert(missing.Restored && Directory.Exists(folder), "A missing save should be restored: " + missing.Detail);

            WriteSaveFolder(folder, FakeSaveFiles(fill: 9));
            LastGoodSave.BootCheck newer = LastGoodSave.EnsureBootable(folder, now);
            Assert(!newer.Restored && newer.Detail.StartsWith("whole, not the same", StringComparison.Ordinal)
                && File.ReadAllBytes(Path.Combine(folder, "Meta.data"))[0x60] == 9,
                "A whole save the controller did not keep is left alone: " + newer.Detail);

            Directory.Delete(LastGoodSave.FolderFor(folder), recursive: true);
            WriteSaveFolder(folder + ".backup", FakeSaveFiles());
            File.Delete(Path.Combine(folder, "Sunset Valley_0x0859db3c.nhd"));
            LastGoodSave.BootCheck noCopy = LastGoodSave.EnsureBootable(folder, now);
            Assert(!noCopy.Restored && noCopy.Detail.StartsWith("NOT WHOLE (no .nhd file) and no last good copy", StringComparison.Ordinal)
                && noCopy.Detail.Contains("Option4BookChain.sims3.backup is whole", StringComparison.Ordinal),
                "With no copy the save is loaded as it is, and a whole .backup is named: " + noCopy.Detail);
        }
        finally
        {
            Directory.Delete(saves, recursive: true);
        }
    }
}
