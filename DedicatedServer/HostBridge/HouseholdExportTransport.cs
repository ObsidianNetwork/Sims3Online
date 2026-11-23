using System.Globalization;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.HostBridge;

public enum HouseholdExportTransportError
{
    None = 0,
    MetadataMissing,
    MetadataMalformed,
    MetadataDuplicateField,
    MetadataUnknownField,
    MetadataMissingField,
    MetadataInvalidValue,
    BridgeUnavailable,
    InvalidSlot,
    SlotNotReady,
    CorrelationMismatch,
    PayloadLengthOutOfRange,
    PayloadLengthMismatch,
    HeaderCrcMismatch,
    PayloadCrcMismatch,
    BulkReadFailed,
    HouseholdMismatch,
    TableMetadataMalformed,
    ValidatorRejected,
}

public sealed record HouseholdExportMetadata(
    ulong HouseholdId,
    int SlotIndex,
    int TotalBytes,
    int PropertyStreamBytes,
    int ResKeyTableBytes,
    int ObjectIdTableBytes,
    uint Crc32,
    int EntryCount,
    int ResKeyCount,
    int ObjectIdCount,
    // v2 inventory segments. 0 when no inventories exported.
    int InventoryCount = 0,
    string? InventorySegments = null);

public sealed record HouseholdExportMetadataDecodeResult(
    HouseholdExportMetadata? Metadata,
    HouseholdExportTransportError Error,
    string? Detail)
{
    public bool IsSuccess => Error == HouseholdExportTransportError.None && Metadata != null;

    public static HouseholdExportMetadataDecodeResult Success(HouseholdExportMetadata metadata) =>
        new(metadata, HouseholdExportTransportError.None, null);

    public static HouseholdExportMetadataDecodeResult Failure(
        HouseholdExportTransportError error,
        string detail) => new(null, error, detail);
}

public sealed record HostBulkReadResult(
    byte[]? Payload,
    HouseholdExportTransportError Error,
    string? Detail)
{
    public bool IsSuccess => Error == HouseholdExportTransportError.None && Payload != null;

    public static HostBulkReadResult Success(byte[] payload) =>
        new(payload, HouseholdExportTransportError.None, null);

    public static HostBulkReadResult Failure(
        HouseholdExportTransportError error,
        string detail) => new(null, error, detail);
}

public sealed record HouseholdExportPayloadDecodeResult(
    HouseholdExportPayload? Payload,
    HouseholdExportTransportError Error,
    HouseholdExportRejectionReason? RejectionReason,
    string? Detail)
{
    public bool IsSuccess => Error == HouseholdExportTransportError.None && Payload != null;

    public static HouseholdExportPayloadDecodeResult Success(HouseholdExportPayload payload) =>
        new(payload, HouseholdExportTransportError.None, null, null);

    public static HouseholdExportPayloadDecodeResult Failure(
        HouseholdExportTransportError error,
        string detail,
        HouseholdExportRejectionReason? rejectionReason = null) =>
        new(null, error, rejectionReason, detail);
}

public sealed class HouseholdExportTransportException : Exception
{
    public HouseholdExportTransportException(HouseholdExportTransportError error, string message)
        : base(message)
    {
        Error = error;
    }

    public HouseholdExportTransportError Error { get; }
}


internal sealed record InventorySegmentDescriptor(int SlotIndex, int PropertyStreamBytes, int ResKeyTableBytes, int ObjectIdTableBytes);

public static class HouseholdExportTransport
{
    public static HouseholdExportPayloadDecodeResult DecodePayload(
        ulong expectedHouseholdId,
        HouseholdExportMetadata metadata,
        byte[] bulkPayload)
    {
        if (metadata.HouseholdId != expectedHouseholdId)
        {
            return HouseholdExportPayloadDecodeResult.Failure(
                HouseholdExportTransportError.HouseholdMismatch,
                $"Export metadata household {metadata.HouseholdId} did not match requested household {expectedHouseholdId}.");
        }

        if (metadata.TotalBytes <= 0 || metadata.TotalBytes > BridgeSharedRegionLayout.BulkSlotPayloadMax)
        {
            return HouseholdExportPayloadDecodeResult.Failure(
                HouseholdExportTransportError.PayloadLengthOutOfRange,
                $"Export payload length {metadata.TotalBytes} is outside the single BULK slot range.");
        }

        if (metadata.PropertyStreamBytes <= 0
            || metadata.ResKeyTableBytes < 0
            || metadata.ObjectIdTableBytes < 0
            || metadata.EntryCount <= 0
            || metadata.ResKeyCount < 0
            || metadata.ObjectIdCount < 0
            || (metadata.ResKeyCount > 0 && metadata.ResKeyTableBytes == 0)
            || (metadata.ObjectIdCount > 0 && metadata.ObjectIdTableBytes == 0))
        {
            return HouseholdExportPayloadDecodeResult.Failure(
                HouseholdExportTransportError.TableMetadataMalformed,
                "Export property-stream or table metadata is inconsistent.");
        }

        int describedLength;
        try
        {
            describedLength = checked(metadata.PropertyStreamBytes
                + metadata.ResKeyTableBytes
                + metadata.ObjectIdTableBytes);
        }
        catch (OverflowException)
        {
            return HouseholdExportPayloadDecodeResult.Failure(
                HouseholdExportTransportError.PayloadLengthOutOfRange,
                "Export table byte lengths overflowed Int32.");
        }

        // when inventory segments are present, totalBytes includes them.
        // Parse inventory segment descriptors if present.
        List<InventorySegmentDescriptor> invDescs = new();
        if (metadata.InventoryCount > 0 && !string.IsNullOrEmpty(metadata.InventorySegments))
        {
            string[] segParts = metadata.InventorySegments.Split('|');
            foreach (string seg in segParts)
            {
                string[] fields = seg.Split(',');
                if (fields.Length < 4
                    || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out int slotIdx)
                    || !int.TryParse(fields[1], CultureInfo.InvariantCulture, out int propBytes)
                    || !int.TryParse(fields[2], CultureInfo.InvariantCulture, out int resKeyBytes)
                    || !int.TryParse(fields[3], CultureInfo.InvariantCulture, out int objIdBytes))
                {
                    return HouseholdExportPayloadDecodeResult.Failure(
                        HouseholdExportTransportError.TableMetadataMalformed,
                        $"Inventory segment descriptor '{seg}' is malformed.");
                }
                invDescs.Add(new InventorySegmentDescriptor(slotIdx, propBytes, resKeyBytes, objIdBytes));
            }
        }

        // Adjust describedLength check to include inventory segments.
        int inventoryTotalBytes = 0;
        foreach (var desc in invDescs)
        {
            inventoryTotalBytes = checked(inventoryTotalBytes + desc.PropertyStreamBytes + desc.ResKeyTableBytes + desc.ObjectIdTableBytes);
        }

        int fullDescribedLength;
        try
        {
            fullDescribedLength = checked(describedLength + inventoryTotalBytes);
        }
        catch (OverflowException)
        {
            return HouseholdExportPayloadDecodeResult.Failure(
                HouseholdExportTransportError.PayloadLengthOutOfRange,
                "Export total byte lengths overflowed Int32.");
        }

        if (fullDescribedLength != metadata.TotalBytes || bulkPayload.Length != metadata.TotalBytes)
        {
            return HouseholdExportPayloadDecodeResult.Failure(
                HouseholdExportTransportError.PayloadLengthMismatch,
                $"Export lengths disagree: metadata total={metadata.TotalBytes}, household={describedLength}, inventory={inventoryTotalBytes}, full={fullDescribedLength}, bulk={bulkPayload.Length}.");
        }

        uint actualCrc = BridgeCrc32.Compute(bulkPayload);
        if (actualCrc != metadata.Crc32)
        {
            return HouseholdExportPayloadDecodeResult.Failure(
                HouseholdExportTransportError.PayloadCrcMismatch,
                $"Export payload CRC {actualCrc:X8} did not match metadata CRC {metadata.Crc32:X8}.");
        }

        byte[] propertyStream = new byte[metadata.PropertyStreamBytes];
        byte[] resKeyTable = new byte[metadata.ResKeyTableBytes];
        byte[] objectIdTable = new byte[metadata.ObjectIdTableBytes];
        Buffer.BlockCopy(bulkPayload, 0, propertyStream, 0, propertyStream.Length);
        Buffer.BlockCopy(bulkPayload, propertyStream.Length, resKeyTable, 0, resKeyTable.Length);
        Buffer.BlockCopy(
            bulkPayload,
            propertyStream.Length + resKeyTable.Length,
            objectIdTable,
            0,
            objectIdTable.Length);
        
        // extract inventory segments from the BULK payload after the household triplet.
        List<InventoryExportPayload>? simInventories = null;
        if (invDescs.Count > 0)
        {
            simInventories = new List<InventoryExportPayload>(invDescs.Count);
            int invOffset = describedLength;
            foreach (InventorySegmentDescriptor desc in invDescs)
            {
                int segTotal = desc.PropertyStreamBytes + desc.ResKeyTableBytes + desc.ObjectIdTableBytes;
                if (invOffset + segTotal > bulkPayload.Length)
                {
                    return HouseholdExportPayloadDecodeResult.Failure(
                        HouseholdExportTransportError.PayloadLengthMismatch,
                        $"Inventory segment at offset {invOffset} exceeds bulk payload length {bulkPayload.Length}.");
                }

                byte[] invProp = new byte[desc.PropertyStreamBytes];
                byte[] invResKeys = new byte[desc.ResKeyTableBytes];
                byte[] invObjIds = new byte[desc.ObjectIdTableBytes];
                Buffer.BlockCopy(bulkPayload, invOffset, invProp, 0, invProp.Length);
                invOffset += invProp.Length;
                Buffer.BlockCopy(bulkPayload, invOffset, invResKeys, 0, invResKeys.Length);
                invOffset += invResKeys.Length;
                Buffer.BlockCopy(bulkPayload, invOffset, invObjIds, 0, invObjIds.Length);
                invOffset += invObjIds.Length;

                simInventories.Add(new InventoryExportPayload(
                    desc.SlotIndex, invProp, invResKeys, invObjIds, ItemCount: 0));
            }
        }

        HouseholdExportPayload payload = new(
            metadata.HouseholdId,
            propertyStream,
            resKeyTable,
            objectIdTable,
            metadata.EntryCount,
            metadata.ResKeyCount,
            metadata.ObjectIdCount,
            simInventories);
        HouseholdExportRejectionReason rejection = HouseholdExportValidator.Validate(payload);
        return rejection == HouseholdExportRejectionReason.Accepted
            ? HouseholdExportPayloadDecodeResult.Success(payload)
            : HouseholdExportPayloadDecodeResult.Failure(
                HouseholdExportTransportError.ValidatorRejected,
                $"Household export validator rejected the payload: {rejection}.",
                rejection);
    }
}
