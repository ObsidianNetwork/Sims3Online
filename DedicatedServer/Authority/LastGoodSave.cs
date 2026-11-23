using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Authority;

/// <summary>
/// the controller's own last good copy of the server's save, kept next to it as "Name.sims3.lastgood".
/// TS3 replaces a save, and rotates its ".backup", by deleting the folder and copying file by file with no rollback
/// (docs/engine/save-load-pipeline.md, "Commit and backup rotation"), so a failure or a kill part-way leaves either
/// one partial. After every save that answered OK the controller checks what TS3 wrote and keeps it here: written to
/// a staging folder, each file's sha256 checked against the bytes read, a manifest added, then swapped in. At boot,
/// <see cref="EnsureBootable"/> restores it when the save is missing or not whole. TS3's load menu lists only
/// "Saves\*.sims3" (a FindFirstFileW pattern, save-load-pipeline.md "The load menu's list"), so it never offers this
/// folder.
/// </summary>
public static class LastGoodSave
{
    public const string Suffix = ".lastgood";
    public const string ManifestFileName = "simsonline-lastgood.txt";

    /// <summary>"…\Name.sims3" → "…\Name.sims3.lastgood".</summary>
    public static string FolderFor(string saveFolder) => Path.TrimEndingDirectorySeparator(saveFolder) + Suffix;

    /// <summary>
    /// Why these files are not a whole TS3 save, or null when they are: an .nhd is present, and every DBPF file is
    /// complete. A DBPF 2.0 file ends with its index (save-load-pipeline.md, "Package record layout"): index start
    /// (u32 @0x40) plus index size (u32 @0x2C) is the file's length, and the size is 4 + 32 × the record count (u32
    /// @0x24). A copy cut short fails that; a file not yet copied is missing from the set.
    /// </summary>
    public static string? Problem(IReadOnlyList<WorldSaveFile> files)
    {
        if (!files.Any(f => f.Name.EndsWith(".nhd", StringComparison.OrdinalIgnoreCase)))
        {
            return "no .nhd file";
        }

        foreach (WorldSaveFile file in files)
        {
            if (file.Name == ManifestFileName || file.Name == WorldSaveWriter.MarkerFileName)
            {
                continue;
            }

            if (file.Data.Length == 0)
            {
                return $"{file.Name} is empty";
            }

            string? problem = DbpfProblem(file.Data);
            if (problem != null)
            {
                return $"{file.Name}: {problem}";
            }
        }

        return null;
    }

    /// <summary>Null when <paramref name="data"/> is not a DBPF file or is a complete one; otherwise what is wrong.</summary>
    public static string? DbpfProblem(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || !data[..4].SequenceEqual("DBPF"u8))
        {
            return null;
        }

        if (data.Length < 0x60)
        {
            return $"DBPF header cut short ({data.Length} bytes)";
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[0x24..]);
        uint indexSize = BinaryPrimitives.ReadUInt32LittleEndian(data[0x2C..]);
        uint indexStart = BinaryPrimitives.ReadUInt32LittleEndian(data[0x40..]);
        if ((long)indexStart + indexSize != data.Length)
        {
            return $"DBPF index ends at {(long)indexStart + indexSize}, the file at {data.Length}";
        }

        if (indexSize != 4 + 32L * count)
        {
            return $"DBPF index size {indexSize} does not fit {count} records";
        }

        return null;
    }

    /// <summary>One file of a manifest: its name, length and sha256 (lower-case hex).</summary>
    public sealed record ManifestEntry(string Name, long Length, string Sha256);

    /// <summary>
    /// Keeps <paramref name="save"/> (the files TS3 just wrote to <paramref name="saveFolder"/>) as its last good copy
    /// and returns that folder. Throws when the files are not a whole save, or when a written file does not read back
    /// the same; the previous copy is then left as it was.
    /// </summary>
    public static string Keep(string saveFolder, WorldSave save, DateTimeOffset now)
    {
        string? problem = Problem(save.Files);
        if (problem != null)
        {
            throw new InvalidDataException($"not kept: the save is not whole ({problem})");
        }

        string target = FolderFor(saveFolder);
        string staging = target + ".incoming";
        DeleteIfPresent(staging);
        Directory.CreateDirectory(staging);
        try
        {
            List<ManifestEntry> entries = new();
            foreach (WorldSaveFile file in save.Files)
            {
                if (file.Name == ManifestFileName)
                {
                    continue;
                }

                string path = Path.Combine(staging, file.Name);
                using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
                {
                    stream.Write(file.Data);
                    stream.Flush(flushToDisk: true);
                }

                string written = Sha256(File.ReadAllBytes(path));
                string expected = Sha256(file.Data);
                if (written != expected)
                {
                    throw new IOException($"{file.Name} read back as {written}, written as {expected}");
                }

                entries.Add(new ManifestEntry(file.Name, file.Data.Length, expected));
            }

            File.WriteAllText(Path.Combine(staging, ManifestFileName), FormatManifest(Path.GetFileName(saveFolder), now, entries));
            Swap(staging, target);
            return target;
        }
        catch
        {
            DeleteIfPresent(staging);
            throw;
        }
    }

    /// <summary>What <see cref="EnsureBootable"/> found and did; <see cref="Restored"/> when it put the last good copy
    /// back.</summary>
    public sealed record BootCheck(bool Restored, string Detail);

    /// <summary>
    /// Before the server loads <paramref name="saveFolder"/>: when it is missing or not whole and a whole last good copy
    /// exists, the save is moved aside ("Name.sims3.broken-yyyyMMdd-HHmmss", never deleted) and the copy restored in its
    /// place (the copy stays). A whole save that differs from the copy is left alone: a save the controller did not keep
    /// (the game's own), which a restore would undo.
    /// </summary>
    public static BootCheck EnsureBootable(string saveFolder, DateTimeOffset now)
    {
        string lastGood = FolderFor(saveFolder);
        List<WorldSaveFile>? files = Directory.Exists(saveFolder) ? ReadFiles(saveFolder) : null;
        string? saveProblem = files == null ? "missing" : Problem(files);
        string? copyProblem = Directory.Exists(lastGood) ? Verify(lastGood) : "none kept";
        if (saveProblem == null)
        {
            return new BootCheck(false, copyProblem != null ? $"whole (last good copy: {copyProblem})"
                : SameFiles(files!, ParseManifest(File.ReadAllText(Path.Combine(lastGood, ManifestFileName))))
                    ? "whole, the same as the last good copy"
                    : "whole, not the same as the last good copy (a save the controller did not keep): left as it is");
        }

        if (copyProblem != null)
        {
            string backup = Path.TrimEndingDirectorySeparator(saveFolder) + ".backup";
            string backupNote = Directory.Exists(backup) && Problem(ReadFiles(backup)) == null
                ? $"; {Path.GetFileName(backup)} is whole and can be put back by hand"
                : string.Empty;
            return new BootCheck(false, $"NOT WHOLE ({saveProblem}) and no last good copy to restore ({copyProblem}){backupNote}; loading it as it is");
        }

        string? aside = null;
        if (files != null)
        {
            aside = $"{Path.TrimEndingDirectorySeparator(saveFolder)}.broken-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
            Directory.Move(saveFolder, aside);
        }

        string restoring = Path.TrimEndingDirectorySeparator(saveFolder) + ".restoring";
        DeleteIfPresent(restoring);
        Directory.CreateDirectory(restoring);
        foreach (string file in Directory.GetFiles(lastGood))
        {
            if (Path.GetFileName(file) != ManifestFileName)
            {
                File.Copy(file, Path.Combine(restoring, Path.GetFileName(file)));
            }
        }

        Directory.Move(restoring, saveFolder);
        return new BootCheck(true, $"NOT WHOLE ({saveProblem}): restored from {Path.GetFileName(lastGood)}"
            + (aside != null ? $", the broken folder kept as {Path.GetFileName(aside)}" : string.Empty));
    }

    /// <summary>Null when every file the copy's manifest lists is there with its length and sha256, and nothing else is.</summary>
    public static string? Verify(string lastGoodFolder)
    {
        string manifestPath = Path.Combine(lastGoodFolder, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return "no manifest";
        }

        List<ManifestEntry> entries = ParseManifest(File.ReadAllText(manifestPath));
        if (entries.Count == 0)
        {
            return "empty manifest";
        }

        foreach (ManifestEntry entry in entries)
        {
            string path = Path.Combine(lastGoodFolder, entry.Name);
            if (!File.Exists(path))
            {
                return $"{entry.Name} missing";
            }

            byte[] data = File.ReadAllBytes(path);
            if (data.Length != entry.Length || Sha256(data) != entry.Sha256)
            {
                return $"{entry.Name} does not match its manifest";
            }
        }

        int extra = Directory.GetFiles(lastGoodFolder).Length - 1 - entries.Count;
        return extra == 0 ? null : $"{extra} file(s) not in the manifest";
    }

    public static string FormatManifest(string saveFolderName, DateTimeOffset at, IEnumerable<ManifestEntry> entries)
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"# the last good copy of {saveFolderName}, kept {at:O}. sha256 length name\n");
        foreach (ManifestEntry entry in entries)
        {
            text.Append(CultureInfo.InvariantCulture, $"{entry.Sha256} {entry.Length} {entry.Name}\n");
        }

        return text.ToString();
    }

    public static List<ManifestEntry> ParseManifest(string text)
    {
        List<ManifestEntry> entries = new();
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            // The name is the rest of the line: TS3's own file names hold spaces ("Sunset Valley_0x0859db3c.nhd").
            string[] parts = trimmed.Split(' ', 3);
            if (parts.Length == 3 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long length))
            {
                entries.Add(new ManifestEntry(parts[2], length, parts[0]));
            }
        }

        return entries;
    }

    private static bool SameFiles(IReadOnlyList<WorldSaveFile> files, IReadOnlyList<ManifestEntry> entries) =>
        entries.Count == files.Count
        && entries.All(e => files.Any(f => f.Name == e.Name && f.Data.Length == e.Length && Sha256(f.Data) == e.Sha256));

    private static List<WorldSaveFile> ReadFiles(string folder) =>
        Directory.GetFiles(folder).Select(p => new WorldSaveFile(Path.GetFileName(p), File.ReadAllBytes(p))).ToList();

    // The new copy replaces the old one: old aside, new in, old removed. A failure between the two moves leaves the
    // old copy at "<target>.old", which the next Keep clears.
    private static void Swap(string staging, string target)
    {
        string old = target + ".old";
        DeleteIfPresent(old);
        if (Directory.Exists(target))
        {
            Directory.Move(target, old);
        }

        Directory.Move(staging, target);
        DeleteIfPresent(old);
    }

    private static void DeleteIfPresent(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
