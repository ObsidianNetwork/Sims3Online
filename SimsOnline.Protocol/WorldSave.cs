namespace SimsOnline.DedicatedServer.Protocol;

/// <summary>One file of a TS3 save folder ("Name.sims3": the .nhd world file, Meta.data, the export and travel DBs).</summary>
public sealed record WorldSaveFile(string Name, byte[] Data);

/// <summary>
/// The server TS3's world as a joining client loads it: the save folder the server just checkpointed
/// (SAVE_GAME under its own save name). A client that loads this starts from the server's current state, so
/// every Sim and object in it has the server's ids.
/// </summary>
public sealed record WorldSave(string SaveName, IReadOnlyList<WorldSaveFile> Files)
{
    /// <summary>Raw bytes per WorldFileChunk event (the JSON line carries them as base64).</summary>
    public const int ChunkBytes = 256 * 1024;

    /// <summary>The folder name TS3 lists and loads ("Name.sims3").</summary>
    public string FolderName => SaveName.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase) ? SaveName : SaveName + ".sims3";

    public long TotalBytes => Files.Sum(f => (long)f.Data.Length);

    /// <summary>The save as WorldFileChunk events, file by file, in order. An empty file is one empty chunk.</summary>
    public IEnumerable<ServerEvent.WorldFileChunk> ToChunks(int chunkBytes = ChunkBytes)
    {
        for (int index = 0; index < Files.Count; index++)
        {
            WorldSaveFile file = Files[index];
            int offset = 0;
            do
            {
                int length = Math.Min(chunkBytes, file.Data.Length - offset);
                yield return new ServerEvent.WorldFileChunk(FolderName, index, Files.Count, file.Name, offset, file.Data.Length, file.Data[offset..(offset + length)]);
                offset += length;
            }
            while (offset < file.Data.Length);
        }
    }

    /// <summary>Reads a save folder's files (flat, as TS3 writes them) as save <paramref name="saveName"/>.</summary>
    public static WorldSave ReadFolder(string folder, string saveName)
    {
        string[] paths = Directory.GetFiles(folder);
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        return new WorldSave(saveName, paths.Select(p => new WorldSaveFile(Path.GetFileName(p), File.ReadAllBytes(p))).ToList());
    }
}

/// <summary>
/// Client side: assembles WorldFileChunk events into a save folder under the client TS3's Saves directory. Files
/// go to a staging folder first; on completion the staging folder replaces "Name.sims3". A folder this writer
/// did not create (no <see cref="MarkerFileName"/>) is never replaced: the player's own save of the same name
/// stays, and the write fails.
/// </summary>
public sealed class WorldSaveWriter
{
    public const string MarkerFileName = "simsonline-world.txt";

    private readonly string _savesDirectory;
    private string? _folderName;
    private string? _stagingFolder;
    private FileStream? _current;
    private int _currentIndex = -1;

    public WorldSaveWriter(string savesDirectory)
    {
        _savesDirectory = savesDirectory;
    }

    public bool IsComplete { get; private set; }

    public long BytesWritten { get; private set; }

    /// <summary>The finished folder ("Name.sims3") once complete.</summary>
    public string? FolderName => IsComplete ? _folderName : null;

    /// <summary>Adds the next chunk. On any failure the partial save is discarded (the staging folder removed) and the
    /// exception rethrown; an existing save is left as it was.</summary>
    public void Add(ServerEvent.WorldFileChunk chunk)
    {
        try
        {
            AddCore(chunk);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    private void Abort()
    {
        _current?.Dispose();
        _current = null;
        if (_stagingFolder != null && Directory.Exists(_stagingFolder))
        {
            try
            {
                Directory.Delete(_stagingFolder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private void AddCore(ServerEvent.WorldFileChunk chunk)
    {
        if (IsComplete)
        {
            throw new InvalidOperationException("The world save is already complete.");
        }

        if (_folderName == null)
        {
            if (chunk.FolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !chunk.FolderName.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Bad save folder name: {chunk.FolderName}");
            }

            _folderName = chunk.FolderName;
            string target = Path.Combine(_savesDirectory, _folderName);
            if (Directory.Exists(target) && !File.Exists(Path.Combine(target, MarkerFileName)))
            {
                throw new IOException($"{target} exists and wasn't written by SimsOnline; refusing to replace it.");
            }

            _stagingFolder = target + ".incoming";
            if (Directory.Exists(_stagingFolder))
            {
                Directory.Delete(_stagingFolder, recursive: true);
            }

            Directory.CreateDirectory(_stagingFolder);
        }
        else if (chunk.FolderName != _folderName)
        {
            throw new InvalidDataException($"Chunk for {chunk.FolderName} while writing {_folderName}.");
        }

        if (chunk.FileIndex != _currentIndex)
        {
            if (chunk.FileIndex != _currentIndex + 1 || chunk.Offset != 0)
            {
                throw new InvalidDataException($"Out-of-order chunk: file {chunk.FileIndex} offset {chunk.Offset} after file {_currentIndex}.");
            }

            _current?.Dispose();
            if (chunk.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || chunk.FileName == MarkerFileName)
            {
                throw new InvalidDataException($"Bad save file name: {chunk.FileName}");
            }

            _current = new FileStream(Path.Combine(_stagingFolder!, chunk.FileName), FileMode.CreateNew, FileAccess.Write);
            _currentIndex = chunk.FileIndex;
        }

        if (_current!.Position != chunk.Offset)
        {
            throw new InvalidDataException($"Chunk offset {chunk.Offset} doesn't follow {_current.Position} in {chunk.FileName}.");
        }

        _current.Write(chunk.Data);
        BytesWritten += chunk.Data.Length;
        if (chunk.FileIndex == chunk.FileCount - 1 && _current.Position == chunk.FileLength)
        {
            _current.Dispose();
            _current = null;
            File.WriteAllText(Path.Combine(_stagingFolder!, MarkerFileName), $"Written by SimsOnline from the server's world, {DateTimeOffset.UtcNow:O}.\n");
            string target = Path.Combine(_savesDirectory, _folderName!);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(_stagingFolder!, target);
            IsComplete = true;
        }
    }
}
