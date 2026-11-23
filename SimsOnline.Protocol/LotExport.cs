namespace SimsOnline.DedicatedServer.Protocol;

/// <summary>
/// slice 6: one owned Sim's class A state inside a lot export bundle
/// (see docs/archive/dev-149-slice5-partial-save-reconciliation.md, "Class A field inventory").
/// Positions are the slice 6 spike scope; <see cref="CurrentInteractionKey"/> rides along for
/// divergence diagnostics and later interaction-queue merge extensions.
/// </summary>
public sealed record LotSimExport(
    ulong ObjectId,
    Vector3Dto Position,
    string? CurrentInteractionKey = null);

/// <summary>
/// the lot-scoped deep-state export payload produced by
/// <c>Household.ExportContent</c> on the owning client. Carries the serialized property
/// stream bytes plus the remap metadata needed for <c>Household.ImportContent</c> on the
/// server. Transported via the bridge BULK slot path (512 KiB capacity), NOT the 4 KB
/// control frame — the control frame carries only the household id, sizes, CRC and entry counts.
///
/// The bytes are the raw output of <c>Household.ExportContent(ResKeyTable, ObjectIdTable,
/// IPropertyStreamWriter)</c> — proven mid-sim by (413,394 bytes Martinez) and
/// proven cross-household by (collision-driven SimDescriptionId remap via
/// <c>MakeUniqueId</c>). The ResKeyTable and ObjectIdTable are serialized alongside the
/// property stream so the server can reconstruct the reader for ImportContent.
/// </summary>
public sealed record HouseholdExportPayload(
    ulong HouseholdId,
    byte[] PropertyStreamBytes,
    byte[] ResKeyTableBytes,
    byte[] ObjectIdTableBytes,
    int EntryCount,
    int ResKeyCount,
    int ObjectIdCount,

    // per-Sim inventory segments. Empty list = no inventories.
    IReadOnlyList<InventoryExportPayload>? SimInventories = null,

    // shared household inventories.
    InventoryExportPayload? SharedFridgeInventory = null,
    InventoryExportPayload? SharedFamilyInventory = null);

/// <summary>
/// one serialized inventory segment (Sim, fridge, or family).
/// Each IExportableContent produces its own (property, keys, objectIds) triplet.
/// SlotIndex: 0..N-1 = Sim descriptor slot; -1 = shared fridge; -2 = shared family.
/// </summary>
public sealed record InventoryExportPayload(
    int SlotIndex,
    byte[] PropertyStreamBytes,
    byte[] ResKeyTableBytes,
    byte[] ObjectIdTableBytes,
    int ItemCount);

/// <summary>
/// slice 6: client-native owned active-lot class A bundle. Produced by the client
/// controller from its own TS3 snapshot (lot export), submitted to the dedicated server via
/// <see cref="ClientCommand.SubmitLotExport"/>, validated/lot-scoped by the controller, then
/// merged into the server TS3 through the MERGE_LOT_STATE bridge command before SAVE_GAME.
/// Must stay lot-scoped: class B (global SP, off-lot townies, world clock) never rides here.
///
/// <see cref="DeepExport"/> carries the <c>Household.ExportContent</c> property
/// stream for deep reconciliation. quarantines that path until an engine-supported,
/// cold-reload-safe lifecycle exists. A null payload uses the independent position-only
/// path only for unsolicited legacy submissions; requested responses remain quarantined.
/// </summary>
public sealed record LotExportBundle(
    int PlayerId,
    ulong LotId,
    long ClientTick,
    IReadOnlyList<LotSimExport> Sims,
    HouseholdExportPayload? DeepExport = null);

/// <summary>
/// validation result for a deep export payload. The controller rejects
/// incomplete, oversized, malformed, or unsupported exports before touching server state.
/// </summary>
public enum HouseholdExportRejectionReason
{
    Accepted = 0,
    /// <summary>Payload exceeds the 512 KiB BULK slot capacity.</summary>
    Oversized = 1,
    /// <summary>CRC32 mismatch — bytes corrupted in transit.</summary>
    CrcMismatch = 2,
    /// <summary>Entry count or table sizes are zero/negative — malformed.</summary>
    Malformed = 3,
    /// <summary>Property stream is empty but entry count > 0, or vice versa.</summary>
    Inconsistent = 4,
    /// <summary>ObjectIdTable is non-empty but ObjectGuid remap was not proven ( caveat).</summary>
    ObjectGuidRemapUnproven = 5,
}

/// <summary>
/// result of a live <c>Household.ImportContent</c> on the server TS3.
/// Carries import success/failure and, on success, a summary the controller can verify.
/// </summary>
public sealed record HouseholdImportResult(
    bool Success,
    ulong HouseholdId,
    int EntryCount,
    IReadOnlyList<(ulong OriginalId, ulong RemappedId)> RemappedSimDescriptionIds,
    string? FailureReason);

/// <summary>
/// validates a <see cref="HouseholdExportPayload"/> before the controller
/// accepts it for live import. Rejects incomplete, oversized, malformed, or unsupported
/// exports so a bad payload never reaches server state.
/// </summary>
public static class HouseholdExportValidator
{
    /// <summary>The maximum total payload size (property stream + tables) the 512 KiB BULK slot can carry.</summary>
    public const int MaxBulkSlotBytes = 512 * 1024;

    /// <summary>If true, the ObjectGuid remap path requires explicit proof before production use ( caveat).</summary>
    public const bool ObjectGuidRemapRequiresProof = true;

    public static HouseholdExportRejectionReason Validate(HouseholdExportPayload? payload)
    {
        if (payload == null)
        {
            return HouseholdExportRejectionReason.Malformed;
        }

        if (payload.PropertyStreamBytes == null || payload.PropertyStreamBytes.Length == 0)
        {
            return HouseholdExportRejectionReason.Inconsistent;
        }

        if (payload.ResKeyTableBytes == null || payload.ObjectIdTableBytes == null)
        {
            return HouseholdExportRejectionReason.Malformed;
        }

        if (payload.EntryCount <= 0)
        {
            return HouseholdExportRejectionReason.Inconsistent;
        }

        int totalBytes = payload.PropertyStreamBytes.Length
            + payload.ResKeyTableBytes.Length
            + payload.ObjectIdTableBytes.Length;
        if (totalBytes > MaxBulkSlotBytes)
        {
            return HouseholdExportRejectionReason.Oversized;
        }

        // caveat: ObjectIdTable.Count was zero in the probe. If the production path
        // encounters a non-empty ObjectIdTable, ObjectGuid remap is UNPROVEN — reject unless a
        // targeted proof has been run. This guard prevents silent reliance on an unproven path.
        if (ObjectGuidRemapRequiresProof && payload.ObjectIdCount > 0)
        {
            return HouseholdExportRejectionReason.ObjectGuidRemapUnproven;
        }

        // validate inventory segments if present.
        if (payload.SimInventories != null)
        {
            foreach (InventoryExportPayload? inv in payload.SimInventories)
            {
                HouseholdExportRejectionReason invResult = ValidateInventory(inv);
                if (invResult != HouseholdExportRejectionReason.Accepted)
                {
                    return invResult;
                }
                totalBytes += inv.PropertyStreamBytes.Length + inv.ResKeyTableBytes.Length + inv.ObjectIdTableBytes.Length;
            }
        }

        if (payload.SharedFridgeInventory != null)
        {
            HouseholdExportRejectionReason fridgeResult = ValidateInventory(payload.SharedFridgeInventory);
            if (fridgeResult != HouseholdExportRejectionReason.Accepted)
            {
                return fridgeResult;
            }
            totalBytes += payload.SharedFridgeInventory.PropertyStreamBytes.Length
                + payload.SharedFridgeInventory.ResKeyTableBytes.Length
                + payload.SharedFridgeInventory.ObjectIdTableBytes.Length;
        }

        if (payload.SharedFamilyInventory != null)
        {
            HouseholdExportRejectionReason famResult = ValidateInventory(payload.SharedFamilyInventory);
            if (famResult != HouseholdExportRejectionReason.Accepted)
            {
                return famResult;
            }
            totalBytes += payload.SharedFamilyInventory.PropertyStreamBytes.Length
                + payload.SharedFamilyInventory.ResKeyTableBytes.Length
                + payload.SharedFamilyInventory.ObjectIdTableBytes.Length;
        }

        // Re-check total after inventory segments.
        if (totalBytes > MaxBulkSlotBytes)
        {
            return HouseholdExportRejectionReason.Oversized;
        }

        return HouseholdExportRejectionReason.Accepted;
    }

    private static HouseholdExportRejectionReason ValidateInventory(InventoryExportPayload? inv)
    {
        if (inv == null)
        {
            return HouseholdExportRejectionReason.Malformed;
        }

        if (inv.PropertyStreamBytes == null || inv.PropertyStreamBytes.Length == 0)
        {
            return HouseholdExportRejectionReason.Inconsistent;
        }

        if (inv.ResKeyTableBytes == null || inv.ObjectIdTableBytes == null)
        {
            return HouseholdExportRejectionReason.Malformed;
        }

        return HouseholdExportRejectionReason.Accepted;
    }
}
