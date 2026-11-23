namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>
/// Phase 2b: binary snapshot decoder. Reads SNAPSHOT2 binary payload
/// from a bulk slot and produces a SimulationSnapshot record.
///
/// Format: see Snapshot2Writer on the mod side (mirror constants).
/// Magic 0x32505353 ("SPS2"), version 1 or 2 (2 ends each Sim with its SimDescriptionId), little-endian.
/// </summary>
public static class Snapshot2BinaryReader
{
    public const uint Magic = 0x32505353u;
    public const uint Version = 2u;
    public const uint MinVersion = 1u;
    private const uint NullInt32 = 0xFFFFFFFFu;

    /// <summary>
    /// Returns true if the payload starts with the SNAPSHOT2 magic.
    /// Used to distinguish SNAPSHOT2 binary from UTF-8 SNAPSHOT| text in bulk slots.
    /// </summary>
    public static bool IsSnapshot2(ReadOnlySpan<byte> payload)
    {
        return payload.Length >= 8
            && BitConverter.ToUInt32(payload[..4]) == Magic;
    }

    /// <summary>
    /// Decode a SNAPSHOT2 binary payload into a SimulationSnapshot.
    /// Returns null on any parse failure.
    /// </summary>
    public static Protocol.SimulationSnapshot? TryDecode(ReadOnlySpan<byte> payload)
    {
        try
        {
            return TryDecodeInternal(payload);
        }
        catch
        {
            return null;
        }
    }

    private static Protocol.SimulationSnapshot? TryDecodeInternal(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            return null;
        }

        int position = 0;

        uint magic = ReadUInt32(payload, ref position);
        if (magic != Magic)
        {
            return null;
        }

        uint version = ReadUInt32(payload, ref position);
        if (version < MinVersion || version > Version)
        {
            return null;
        }

        long tick = ReadInt64(payload, ref position);
        string worldName = ReadString(payload, ref position);
        string saveName = ReadString(payload, ref position);
        uint speedOrdinal = ReadUInt32(payload, ref position);
        Protocol.GameSpeed speed = OrdinalToSpeed(speedOrdinal);

        uint simCount = ReadUInt32(payload, ref position);
        List<Protocol.SimSnapshot> sims = new((int)Math.Min(simCount, 10_000));
        for (int i = 0; i < (int)simCount; i++)
        {
            Protocol.SimSnapshot? sim = TryDecodeSim(payload, ref position, version);
            if (sim == null)
            {
                return null;
            }
            sims.Add(sim);
        }

        uint objectCount = ReadUInt32(payload, ref position);
        List<Protocol.ObjectSnapshot> objects = new((int)Math.Min(objectCount, 50_000));
        for (int i = 0; i < (int)objectCount; i++)
        {
            Protocol.ObjectSnapshot? obj = TryDecodeObject(payload, ref position);
            if (obj == null)
            {
                return null;
            }
            objects.Add(obj);
        }

        return new Protocol.SimulationSnapshot(tick, worldName, saveName, speed, sims, objects);
    }

    private static Protocol.SimSnapshot? TryDecodeSim(ReadOnlySpan<byte> payload, ref int position, uint version)
    {
        if (position + 8 > payload.Length)
        {
            return null;
        }

        ulong objectId = ReadUInt64(payload, ref position);
        string name = ReadString(payload, ref position);

        if (position + 8 > payload.Length)
        {
            return null;
        }
        ulong householdId = ReadUInt64(payload, ref position);

        if (position + 12 > payload.Length)
        {
            return null;
        }
        float x = ReadSingle(payload, ref position);
        float y = ReadSingle(payload, ref position);
        float z = ReadSingle(payload, ref position);

        if (position + 8 > payload.Length)
        {
            return null;
        }
        ulong rawInteractionId = ReadUInt64(payload, ref position);
        ulong? currentInteractionId = rawInteractionId == 0 ? null : rawInteractionId;

        string? interactionKey = ReadNullableString(payload, ref position);

        uint rawOwningPlayer = ReadUInt32(payload, ref position);
        int? owningPlayerId = rawOwningPlayer == NullInt32 ? null : (int)rawOwningPlayer;

        ulong rawLotId = ReadUInt64(payload, ref position);
        ulong? lotId = rawLotId == 0 ? null : rawLotId;

        uint rawQueueCount = ReadUInt32(payload, ref position);
        int? interactionQueueCount = rawQueueCount == NullInt32 ? null : (int)rawQueueCount;

        // Posture container + type (Phase 2b / Gap A). Absent tail bytes default to null.
        ulong rawPostureContainer = 0;
        if (position + 8 <= payload.Length)
        {
            rawPostureContainer = ReadUInt64(payload, ref position);
        }

        string? postureType = null;
        if (position + 4 <= payload.Length)
        {
            postureType = ReadNullableString(payload, ref position);
        }

        ulong? postureContainerObjectId = rawPostureContainer == 0 ? null : rawPostureContainer;

        ulong? simDescriptionId = null;
        if (version >= 2)
        {
            ulong rawDescription = ReadUInt64(payload, ref position);
            simDescriptionId = rawDescription == 0 ? null : rawDescription;
        }

        return new Protocol.SimSnapshot(
            objectId,
            name,
            householdId,
            new Protocol.Vector3Dto(x, y, z),
            currentInteractionId,
            owningPlayerId,
            interactionKey,
            lotId,
            interactionQueueCount,
            postureContainerObjectId,
            postureType,
            simDescriptionId);
    }

    private static Protocol.ObjectSnapshot? TryDecodeObject(ReadOnlySpan<byte> payload, ref int position)
    {
        if (position + 8 > payload.Length)
        {
            return null;
        }

        ulong objectId = ReadUInt64(payload, ref position);
        string scriptClass = ReadString(payload, ref position);

        if (position + 12 > payload.Length)
        {
            return null;
        }
        float x = ReadSingle(payload, ref position);
        float y = ReadSingle(payload, ref position);
        float z = ReadSingle(payload, ref position);

        ulong rawLotId = ReadUInt64(payload, ref position);
        ulong? lotId = rawLotId == 0 ? null : rawLotId;

        return new Protocol.ObjectSnapshot(objectId, scriptClass, new Protocol.Vector3Dto(x, y, z), lotId);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int position)
    {
        uint value = BitConverter.ToUInt32(data[position..]);
        position += 4;
        return value;
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int position)
    {
        ulong value = BitConverter.ToUInt64(data[position..]);
        position += 8;
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> data, ref int position)
    {
        long value = BitConverter.ToInt64(data[position..]);
        position += 8;
        return value;
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, ref int position)
    {
        float value = BitConverter.ToSingle(data[position..]);
        position += 4;
        return value;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int position)
    {
        uint length = ReadUInt32(data, ref position);
        if (length == 0)
        {
            return string.Empty;
        }

        if (position + (int)length > data.Length)
        {
            throw new InvalidOperationException($"String length {length} exceeds remaining payload.");
        }

        string value = System.Text.Encoding.UTF8.GetString(data[position..(position + (int)length)]);
        position += (int)length;
        return value;
    }

    private static string? ReadNullableString(ReadOnlySpan<byte> data, ref int position)
    {
        uint length = ReadUInt32(data, ref position);
        if (length == 0)
        {
            return null;
        }

        if (position + (int)length > data.Length)
        {
            throw new InvalidOperationException($"String length {length} exceeds remaining payload.");
        }

        string value = System.Text.Encoding.UTF8.GetString(data[position..(position + (int)length)]);
        position += (int)length;
        return value;
    }

    private static Protocol.GameSpeed OrdinalToSpeed(uint ordinal)
    {
        return ordinal switch
        {
            0 => Protocol.GameSpeed.Pause,
            1 => Protocol.GameSpeed.Normal,
            2 => Protocol.GameSpeed.Speed2,
            3 => Protocol.GameSpeed.Speed3,
            4 => Protocol.GameSpeed.Ultra,
            _ => Protocol.GameSpeed.Pause,
        };
    }
}