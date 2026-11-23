// The tester kit's fork builder helper. build-fork.ps1 calls one verb per step:
//
//   ForkTool extract <Game\Bin> <out dir>              the ten script assemblies of gameplay/scripts/simcore.package,
//                                                       named as refs/sims3/README.md names them, and s3sa-header.json
//   ForkTool patch <decompile dir> <fork-patch.json> <hooks dir>
//                                                       checks every decompiled file against the patch's base hashes,
//                                                       applies the edits, adds the hook sources, checks the result
//   ForkTool restore <original gameplay_02.dll> <rebuilt dll>
//                                                       BeforeFieldInit and Synchronized as the original has them
//   ForkTool pack <rebuilt dll> <s3sa-header.json> <out .package>
//                                                       the one-resource override package the dev stack installs
//   ForkTool rsp <tree> <assemblies dir> <out dll> <rsp file>
//                                                       csc's response file: scratch/build-g02-cs73.ps1's flags plus
//                                                       /deterministic, the game's references, the sources in one order
//   ForkTool identify <dir>                            "<path>\t<assembly name>" for every managed PE and every S3SA
//                                                       resource of every package under dir (tester_kit.py's EA check)
//   ForkTool sha256 <file>
//
// Nothing here carries game text: the patch file holds base hashes, offsets and our inserted text.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimsOnline.ForkTool;

public static class Program
{
    const uint S3SA = 0x073FAA07;
    const ulong GameplaySystemsInstance = 0x03D6C8D903CE868CUL;

    // Assembly name -> the file name the fork scripts and refs/sims3/README.md use.
    static readonly Dictionary<string, string> FileNames = new()
    {
        ["Sims3StoreObjects"] = "gameplay_00.dll", ["Sims3GameplayObjects"] = "gameplay_01.dll",
        ["Sims3GameplaySystems"] = "gameplay_02.dll", ["UI"] = "gameplay_03.dll",
        ["SimIFace"] = "scripts_00.dll", ["ScriptCore"] = "scripts_01.dll", ["Sims3Metadata"] = "scripts_02.dll",
        ["System.Xml"] = "simcore_00.dll", ["System"] = "simcore_01.dll", ["mscorlib"] = "simcore_02.dll",
    };

    public static int Main(string[] args)
    {
        try
        {
            switch (args.Length > 0 ? args[0] : "")
            {
                case "extract" when args.Length == 3: Extract(args[1], args[2]); return 0;
                case "patch" when args.Length == 4: Patch(args[1], args[2], args[3]); return 0;
                case "restore" when args.Length == 3: Console.WriteLine(Restore(args[1], args[2])); return 0;
                case "pack" when args.Length == 4: Pack(args[1], args[2], args[3]); return 0;
                case "rsp" when args.Length == 5: WriteRsp(args[1], args[2], args[3], args[4]); return 0;
                case "identify" when args.Length == 2: Identify(args[1]); return 0;
                case "sha256" when args.Length == 2: Console.WriteLine(Sha256(File.ReadAllBytes(args[1]))); return 0;
                default:
                    Console.Error.WriteLine("usage: ForkTool extract|patch|restore|pack|sha256 ... (see Program.cs)");
                    return 2;
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine("FAILED: " + error.Message);
            return 1;
        }
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // ---- DBPF 2.0 and S3SA ---------------------------------------------------------------------------------------

    public record Entry(uint Type, uint Group, ulong Instance, uint Offset, uint StoredSize, uint MemSize, ushort Compressed);

    public static List<Entry> ReadIndex(byte[] data)
    {
        if (data.Length < 96 || Encoding.ASCII.GetString(data, 0, 4) != "DBPF")
            throw new InvalidDataException("not a DBPF package");
        if (BitConverter.ToUInt32(data, 4) != 2 || BitConverter.ToUInt32(data, 8) != 0)
            throw new InvalidDataException("not a DBPF 2.0 package");
        int count = BitConverter.ToInt32(data, 0x24);
        int size = BitConverter.ToInt32(data, 0x2C);
        int cursor = BitConverter.ToInt32(data, 0x40);
        if (cursor == 0) cursor = BitConverter.ToInt32(data, 0x28);
        int end = cursor + size;
        if (cursor < 96 || end > data.Length) throw new InvalidDataException("DBPF index outside the file");
        uint Word() { if (cursor + 4 > end) throw new InvalidDataException("DBPF index truncated"); uint v = BitConverter.ToUInt32(data, cursor); cursor += 4; return v; }
        uint flags = Word();
        if ((flags & ~7u) != 0) throw new InvalidDataException("unsupported DBPF index flags");
        uint? constType = (flags & 1) != 0 ? Word() : null, constGroup = (flags & 2) != 0 ? Word() : null, constHigh = (flags & 4) != 0 ? Word() : null;
        var entries = new List<Entry>();
        for (int i = 0; i < count; i++)
        {
            uint type = constType ?? Word(), group = constGroup ?? Word(), high = constHigh ?? Word();
            uint low = Word(), offset = Word(), stored = Word(), mem = Word(), extra = Word();
            entries.Add(new Entry(type, group, ((ulong)high << 32) | low, offset, stored & 0x7FFFFFFF, mem, (ushort)(extra & 0xFFFF)));
        }
        return entries;
    }

    public record S3saHeader(byte Version, string GameVersion, uint Unknown2);

    // An S3SA resource: version, [game version], unknown2, md5sum[64], block count, md5 table (8 bytes per 512-byte
    // block, bit 0 set: a zero block not stored), the stored blocks XOR-scrambled through the table.
    public static (S3saHeader Header, byte[] Assembly) ReadS3sa(byte[] data, int offset, int length)
    {
        using var reader = new BinaryReader(new MemoryStream(data, offset, length, false));
        byte version = reader.ReadByte();
        string gameVersion = version > 1 ? Encoding.Unicode.GetString(reader.ReadBytes(reader.ReadInt32() * 2)) : "";
        uint unknown2 = reader.ReadUInt32();
        reader.ReadBytes(64);
        int blocks = reader.ReadUInt16();
        byte[] table = reader.ReadBytes(blocks * 8);
        ulong seed = 0;
        for (int i = 0; i < table.Length; i += 8) seed += BitConverter.ToUInt64(table, i);
        ulong key = (ulong)(table.Length - 1) & seed;
        byte[] clear = new byte[blocks * 512];
        for (int b = 0; b < blocks; b++)
        {
            if ((table[b * 8] & 1) != 0) continue;
            byte[] block = reader.ReadBytes(512);
            if (block.Length != 512) throw new InvalidDataException("S3SA resource truncated");
            for (int k = 0; k < 512; k++)
            {
                byte stored = block[k];
                clear[b * 512 + k] = (byte)(stored ^ table[key]);
                key = (key + stored) % (ulong)table.Length;
            }
        }
        return (new S3saHeader(version, gameVersion, unknown2), clear);
    }

    // The unsigned resource the dev stack's prepare-g02-cold.ps1 -OverridePackage writes: the original's version
    // fields, a zero md5sum and a zero md5 table, so every block is stored as is.
    public static byte[] WriteS3sa(S3saHeader header, byte[] assembly)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(header.Version);
        if (header.Version > 1) { writer.Write(header.GameVersion.Length); writer.Write(Encoding.Unicode.GetBytes(header.GameVersion)); }
        writer.Write(header.Unknown2);
        writer.Write(new byte[64]);
        int blocks = (assembly.Length >> 9) + ((assembly.Length & 0x1FF) != 0 ? 1 : 0);
        if (blocks > ushort.MaxValue) throw new InvalidDataException("assembly too large for one S3SA resource");
        writer.Write((ushort)blocks);
        writer.Write(new byte[blocks * 8]);
        writer.Write(assembly);
        writer.Write(new byte[blocks * 512 - assembly.Length]);
        writer.Flush();
        return stream.ToArray();
    }

    // One resource after a 96-byte header, then a compact index (type, group and instance high constant), as s3pi
    // writes a new package.
    public static byte[] WritePackage(uint type, uint group, ulong instance, byte[] resource)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var header = new byte[96];
        Encoding.ASCII.GetBytes("DBPF").CopyTo(header, 0);
        BitConverter.GetBytes(2u).CopyTo(header, 4);
        BitConverter.GetBytes(1u).CopyTo(header, 0x24);
        BitConverter.GetBytes(36u).CopyTo(header, 0x2C);
        BitConverter.GetBytes(3u).CopyTo(header, 0x3C);
        BitConverter.GetBytes((uint)(96 + resource.Length)).CopyTo(header, 0x40);
        writer.Write(header);
        writer.Write(resource);
        writer.Write(7u); writer.Write(type); writer.Write(group); writer.Write((uint)(instance >> 32));
        writer.Write((uint)instance); writer.Write(96u); writer.Write((uint)resource.Length | 0x80000000u);
        writer.Write((uint)resource.Length); writer.Write((ushort)0); writer.Write((ushort)1);
        writer.Flush();
        return stream.ToArray();
    }

    static string AssemblyName(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image, false));
        var metadata = pe.GetMetadataReader();
        return metadata.GetString(metadata.GetAssemblyDefinition().Name);
    }

    static void Extract(string gameBin, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var found = new HashSet<string>();
        foreach (string name in new[] { "gameplay.package", "scripts.package", "simcore.package" })
        {
            string path = Path.Combine(gameBin, name);
            byte[] data = File.ReadAllBytes(path);
            foreach (var entry in ReadIndex(data).Where(e => e.Type == S3SA && e.StoredSize > 0))
            {
                if (entry.Compressed != 0 || entry.StoredSize != entry.MemSize)
                    throw new InvalidDataException($"{name}: S3SA {entry.Instance:X16} is compressed; this builder reads stored ones (TS3 1.67 Steam)");
                var (header, assembly) = ReadS3sa(data, (int)entry.Offset, (int)entry.StoredSize);
                string assemblyName = AssemblyName(assembly);
                if (!FileNames.TryGetValue(assemblyName, out string? file)) continue;
                File.WriteAllBytes(Path.Combine(outDir, file), assembly);
                found.Add(file);
                Console.WriteLine($"{file}  {assemblyName}  {Sha256(assembly)}");
                if (name == "gameplay.package" && entry.Instance == GameplaySystemsInstance)
                    File.WriteAllText(Path.Combine(outDir, "s3sa-header.json"), JsonSerializer.Serialize(header));
            }
        }
        var missing = FileNames.Values.Where(f => !found.Contains(f)).ToList();
        if (missing.Count > 0) throw new InvalidDataException("not found in the game's packages: " + string.Join(", ", missing));
        if (!File.Exists(Path.Combine(outDir, "s3sa-header.json")))
            throw new InvalidDataException($"gameplay.package has no S3SA {GameplaySystemsInstance:X16}");
    }

    static string? TryAssemblyName(byte[] image)
    {
        if (image.Length < 2 || image[0] != (byte)'M' || image[1] != (byte)'Z') return null;
        try
        {
            using var pe = new PEReader(new MemoryStream(image, false));
            if (!pe.HasMetadata) return null;
            var metadata = pe.GetMetadataReader();
            return metadata.IsAssembly ? metadata.GetString(metadata.GetAssemblyDefinition().Name) : null;
        }
        catch (BadImageFormatException) { return null; }
    }

    static void Identify(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            string rel = Rel(root, path);
            byte[] data = File.ReadAllBytes(path);
            if (TryAssemblyName(data) is string name) Console.WriteLine(rel + "\t" + name);
            if (data.Length < 4 || Encoding.ASCII.GetString(data, 0, 4) != "DBPF") continue;
            foreach (var entry in ReadIndex(data).Where(e => e.Type == S3SA && e.StoredSize > 0))
            {
                if (entry.Compressed != 0) { Console.WriteLine(rel + "\t?compressed-S3SA"); continue; }
                Console.WriteLine(rel + "\t" + (TryAssemblyName(ReadS3sa(data, (int)entry.Offset, (int)entry.StoredSize).Assembly) ?? "?"));
            }
        }
    }

    static void Pack(string dll, string headerJson, string outPackage)
    {
        var header = JsonSerializer.Deserialize<S3saHeader>(File.ReadAllText(headerJson))
                     ?? throw new InvalidDataException("empty " + headerJson);
        byte[] package = WritePackage(S3SA, 0, GameplaySystemsInstance, WriteS3sa(header, File.ReadAllBytes(dll)));
        File.WriteAllBytes(outPackage, package);
        Console.WriteLine($"{outPackage}  {package.Length} bytes  {Sha256(package)}");
    }

    // ---- the patch -----------------------------------------------------------------------------------------------

    public sealed class ForkPatch
    {
        public int schema { get; set; }
        public Dictionary<string, string> @base { get; set; } = new();
        public List<string> lf { get; set; } = new();
        public Dictionary<string, List<JsonElement>> edits { get; set; } = new();
        public Dictionary<string, string> added { get; set; } = new();
        public string tree_sha256 { get; set; } = "";
    }

    static byte[] ToLf(byte[] text)
    {
        using var output = new MemoryStream(text.Length);
        for (int i = 0; i < text.Length; i++)
            if (!(text[i] == (byte)'\r' && i + 1 < text.Length && text[i + 1] == (byte)'\n')) output.WriteByte(text[i]);
        return output.ToArray();
    }

    static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>sha256 over "path\tsha256\n" of every .cs file, paths sorted ordinal (tester_kit.py computes the
    /// same).</summary>
    public static string TreeSha256(string root)
    {
        var lines = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(p => (Rel(root, p), Sha256(File.ReadAllBytes(p))))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)
            .Select(t => t.Item1 + "\t" + t.Item2 + "\n");
        return Sha256(Encoding.UTF8.GetBytes(string.Concat(lines)));
    }

    static void Patch(string tree, string patchPath, string hooksDir)
    {
        var patch = JsonSerializer.Deserialize<ForkPatch>(File.ReadAllText(patchPath))
                    ?? throw new InvalidDataException("empty " + patchPath);
        if (patch.schema != 1) throw new InvalidDataException($"fork patch schema {patch.schema}, this builder reads 1");
        var present = Directory.EnumerateFiles(tree, "*.cs", SearchOption.AllDirectories).Select(p => Rel(tree, p)).ToHashSet();
        var problems = new List<string>();
        problems.AddRange(patch.@base.Keys.Where(k => !present.Contains(k)).Select(k => "missing " + k));
        problems.AddRange(present.Where(k => !patch.@base.ContainsKey(k)).Select(k => "extra " + k));
        foreach (var (rel, sha) in patch.@base)
            if (present.Contains(rel) && Sha256(File.ReadAllBytes(Path.Combine(tree, rel))) != sha) problems.Add("differs " + rel);
        if (problems.Count > 0)
            throw new InvalidDataException($"the decompile is not the one the patch was made from ({problems.Count} files): "
                                           + string.Join("; ", problems.Take(8)));
        Console.WriteLine($"decompile matches: {patch.@base.Count} files");

        int edits = 0;
        // A file in lf: its CRLF become LF first (the repair waves rewrote it so); the edits apply to that text.
        var lf = patch.lf.ToHashSet();
        foreach (string rel in lf.Where(r => !patch.edits.ContainsKey(r)))
            File.WriteAllBytes(Path.Combine(tree, rel), ToLf(File.ReadAllBytes(Path.Combine(tree, rel))));
        foreach (var (rel, rows) in patch.edits)
        {
            string path = Path.Combine(tree, rel);
            byte[] text = File.ReadAllBytes(path);
            if (lf.Contains(rel)) text = ToLf(text);
            using var output = new MemoryStream(text.Length + 4096);
            int cursor = 0;
            foreach (var row in rows)
            {
                int start = row[0].GetInt32(), end = row[1].GetInt32();
                if (start < cursor || end < start || end > text.Length) throw new InvalidDataException($"bad edit in {rel} at {start}");
                output.Write(text, cursor, start - cursor);
                output.Write(Encoding.UTF8.GetBytes(row[2].GetString() ?? ""));
                cursor = end;
                edits++;
            }
            output.Write(text, cursor, text.Length - cursor);
            File.WriteAllBytes(path, output.ToArray());
        }
        foreach (var (rel, sha) in patch.added)
        {
            byte[] source = File.ReadAllBytes(Path.Combine(hooksDir, Path.GetFileName(rel)));
            if (Sha256(source) != sha) throw new InvalidDataException("hook source differs from the kit's: " + rel);
            string target = Path.Combine(tree, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, source);
        }
        string result = TreeSha256(tree);
        if (result != patch.tree_sha256) throw new InvalidDataException($"patched tree {result} is not the host's {patch.tree_sha256}");
        Console.WriteLine($"patched: {edits} edits in {patch.edits.Count} files, {patch.added.Count} hook files; tree {result[..12]} as on the host");
    }

    // scratch/build-g02-cs73.ps1's flags, plus /deterministic: the source order and the flags fix the output, so every
    // friend's build is the host's byte for byte.
    static readonly string[] CscFlags =
        { "/noconfig", "/nostdlib+", "/t:library", "/platform:anycpu", "/optimize+", "/unsafe+", "/langversion:7.3",
          "/deterministic+", "/nologo", "/warn:0" };
    static readonly string[] CscReferences =
        { "simcore_02.dll", "simcore_01.dll", "simcore_00.dll", "gameplay_00.dll", "gameplay_01.dll", "gameplay_02.dll",
          "gameplay_03.dll", "scripts_00.dll", "scripts_01.dll", "scripts_02.dll" };

    static void WriteRsp(string tree, string assemblies, string outDll, string rspPath)
    {
        var lines = new List<string>(CscFlags) { $"/out:\"{Path.GetFullPath(outDll)}\"" };
        lines.AddRange(CscReferences.Select(r => $"/r:\"{Path.GetFullPath(Path.Combine(assemblies, r))}\""));
        lines.AddRange(Directory.EnumerateFiles(tree, "*.cs", SearchOption.AllDirectories)
            .Select(p => Rel(tree, p)).OrderBy(r => r, StringComparer.Ordinal)
            .Select(r => $"\"{Path.GetFullPath(Path.Combine(tree, r))}\""));
        File.WriteAllLines(rspPath, lines);
        Console.WriteLine($"{rspPath}: {lines.Count - CscFlags.Length - 1 - CscReferences.Length} sources");
    }

    // ---- metadata restore (scratch/restore-g02-metadata.ps1) -------------------------------------------------------

    static string TypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        TypeDefinitionHandle parent = type.GetDeclaringType();
        return parent.IsNil
            ? metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name)
            : TypeName(metadata, parent) + "+" + metadata.GetString(type.Name);
    }

    /// <summary>Puts back the original's BeforeFieldInit type flags and Synchronized interface-method flags, which C#
    /// cannot express, in place in the rebuilt assembly's metadata tables.</summary>
    public static string Restore(string originalPath, string rebuiltPath)
    {
        const uint BeforeFieldInit = 0x00100000;
        const ushort Synchronized = 0x0020;
        var originalTypes = new Dictionary<string, uint>();
        var originalSynchronized = new HashSet<string>();
        using (var pe = new PEReader(File.OpenRead(originalPath)))
        {
            MetadataReader metadata = pe.GetMetadataReader();
            foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
            {
                string name = TypeName(metadata, handle);
                if (name.Contains('<')) continue;
                TypeDefinition type = metadata.GetTypeDefinition(handle);
                originalTypes.Add(name, (uint)type.Attributes);
                if ((type.Attributes & TypeAttributes.Interface) == 0) continue;
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                    if ((method.ImplAttributes & MethodImplAttributes.Synchronized) != 0)
                        originalSynchronized.Add(name + "::" + metadata.GetString(method.Name));
                }
            }
        }
        byte[] bytes = File.ReadAllBytes(rebuiltPath);
        int typeChanges = 0, methodChanges = 0;
        using (var pe = new PEReader(new MemoryStream(bytes, false)))
        {
            MetadataReader metadata = pe.GetMetadataReader();
            if (pe.PEHeaders.CorHeader!.Flags != CorFlags.ILOnly) throw new InvalidDataException("Build must match original ILOnly CLR flags.");
            int metadataOffset = pe.PEHeaders.MetadataStartOffset;
            int typeTable = metadataOffset + metadata.GetTableMetadataOffset(TableIndex.TypeDef);
            int typeRowSize = metadata.GetTableRowSize(TableIndex.TypeDef);
            int methodTable = metadataOffset + metadata.GetTableMetadataOffset(TableIndex.MethodDef);
            int methodRowSize = metadata.GetTableRowSize(TableIndex.MethodDef);
            foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
            {
                string name = TypeName(metadata, handle);
                if (!originalTypes.TryGetValue(name, out uint oldFlags)) continue;
                TypeDefinition type = metadata.GetTypeDefinition(handle);
                uint newFlags = (uint)type.Attributes;
                if (((oldFlags ^ newFlags) & BeforeFieldInit) != 0)
                {
                    int offset = typeTable + (MetadataTokens.GetRowNumber(handle) - 1) * typeRowSize;
                    if (BitConverter.ToUInt32(bytes, offset) != newFlags) throw new InvalidDataException("Type row offset guard failed: " + name);
                    uint restored = (newFlags & ~BeforeFieldInit) | (oldFlags & BeforeFieldInit);
                    BitConverter.GetBytes(restored).CopyTo(bytes, offset);
                    typeChanges++;
                }
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                    string key = name + "::" + metadata.GetString(method.Name);
                    if (!originalSynchronized.Contains(key)) continue;
                    ushort flags = (ushort)method.ImplAttributes;
                    if ((flags & Synchronized) != 0) continue;
                    int offset = methodTable + (MetadataTokens.GetRowNumber(methodHandle) - 1) * methodRowSize + 4;
                    if (BitConverter.ToUInt16(bytes, offset) != flags) throw new InvalidDataException("Method row offset guard failed: " + key);
                    BitConverter.GetBytes((ushort)(flags | Synchronized)).CopyTo(bytes, offset);
                    methodChanges++;
                }
            }
        }
        using (var pe = new PEReader(new MemoryStream(bytes, false)))
        {
            MetadataReader metadata = pe.GetMetadataReader();
            foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
            {
                string name = TypeName(metadata, handle);
                if (!originalTypes.TryGetValue(name, out uint oldFlags)) continue;
                TypeDefinition type = metadata.GetTypeDefinition(handle);
                if ((((uint)type.Attributes ^ oldFlags) & BeforeFieldInit) != 0) throw new InvalidDataException("BeforeFieldInit verification failed: " + name);
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                    string key = name + "::" + metadata.GetString(method.Name);
                    if (originalSynchronized.Contains(key) && (method.ImplAttributes & MethodImplAttributes.Synchronized) == 0)
                        throw new InvalidDataException("Synchronized verification failed: " + key);
                }
            }
        }
        if (typeChanges != 0 || methodChanges != 0) File.WriteAllBytes(rebuiltPath, bytes);
        return "METADATA_RESTORED types=" + typeChanges + " methods=" + methodChanges;
    }
}
