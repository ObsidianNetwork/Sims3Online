namespace SimsOnline.DedicatedServer.Protocol;

/// <summary>
/// one Sim description as the server TS3 exports it (SimDescription.ExportContent, the save path):
/// the property stream plus the resource key and object id tables it refers to. A client imports it as a
/// fresh description that keeps the server's SimDescriptionId, so SPAWN_SIM can instantiate a Sim the server
/// created at runtime (tourists, new service NPCs, babies).
/// </summary>
public sealed record SimDescriptionPayload(ulong SimDescriptionId, byte[] PropertyStream, byte[] ResKeyTable, byte[] ObjectIdTable)
{
    /// <summary>The bytes the bridge BULK slot carries: property stream, then key table, then object id table.</summary>
    public byte[] Combined()
    {
        byte[] combined = new byte[PropertyStream.Length + ResKeyTable.Length + ObjectIdTable.Length];
        PropertyStream.CopyTo(combined, 0);
        ResKeyTable.CopyTo(combined, PropertyStream.Length);
        ObjectIdTable.CopyTo(combined, PropertyStream.Length + ResKeyTable.Length);
        return combined;
    }

    /// <summary>Splits <see cref="Combined"/> bytes back; null when the lengths don't add up.</summary>
    public static SimDescriptionPayload? FromCombined(ulong simDescriptionId, byte[] combined, int propertyBytes, int resKeyBytes, int objectIdBytes)
    {
        if (propertyBytes < 0 || resKeyBytes < 0 || objectIdBytes < 0 || propertyBytes + resKeyBytes + objectIdBytes != combined.Length)
        {
            return null;
        }

        return new SimDescriptionPayload(
            simDescriptionId,
            combined[..propertyBytes],
            combined[propertyBytes..(propertyBytes + resKeyBytes)],
            combined[(propertyBytes + resKeyBytes)..]);
    }

    /// <summary>CRC-32 (IEEE 802.3, the bridge BULK checksum) of <see cref="Combined"/>.</summary>
    public uint Crc32()
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in Combined())
        {
            crc ^= value;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(crc & 1));
            }
        }

        return ~crc;
    }
}
