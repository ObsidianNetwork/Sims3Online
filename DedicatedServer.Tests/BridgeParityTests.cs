using System.Reflection;
using System.Text;
using SimsOnline.DedicatedServer.HostBridge;
using ControllerCrc32 = SimsOnline.DedicatedServer.HostBridge.BridgeCrc32;
using ControllerLayout = SimsOnline.DedicatedServer.HostBridge.BridgeSharedRegionLayout;
using ModCrc32 = SimsOnlineServerHost.BridgeCrc32;
using ModLayout = SimsOnlineServerHost.BridgeSharedRegionLayout;

namespace SimsOnline.DedicatedServer.Tests;

// Bridge framing parity: the mod's own BridgeSharedRegionLayout.cs and BridgeCrc32.cs (linked from
// SimsOnlineServerHostMod) against the controller's copies in DedicatedServer/HostBridge. A change on one side only
// fails here; a framing change edits both files in the same change.
internal static partial class Program
{
    // Every constant either side declares, by name and value: layout, protocol and HELLO versions, slot counts and
    // sizes, bulk slots, offsets, region sizes, sender ids, bulk states and the HELLO caps.
    [Test("BridgeShared.ModLayoutConstantsMatchController")]
    private static void BridgeSharedModLayoutConstantsMatchController()
    {
        Dictionary<string, object?> mod = LayoutConstants(typeof(ModLayout));
        Dictionary<string, object?> controller = LayoutConstants(typeof(ControllerLayout));
        string[] expected =
        {
            "LayoutVersion", "ProtocolVersion", "HelloWireVersion", "SlotSize", "SlotHeaderSize", "ControlHeaderSize",
            "ControlSlotCount", "HostSlotBase", "HostSlotCount", "CtlSlotBase", "CtlSlotCount", "BulkSlotCount",
            "BulkSlotSize", "BulkHeaderSize", "BulkSlotHeaderSize", "ControlRegionSize", "BulkRegionSize",
            "TotalRegionSize", "LayoutVersionOffset", "DefaultHelloCaps",
        };
        foreach (string name in expected)
        {
            Assert(mod.ContainsKey(name) && controller.ContainsKey(name), $"both layouts declare {name}");
        }

        var mismatches = new List<string>();
        foreach (string name in mod.Keys.Union(controller.Keys).Order(StringComparer.Ordinal))
        {
            if (!mod.TryGetValue(name, out object? modValue))
            {
                mismatches.Add($"{name} is declared by the controller only");
            }
            else if (!controller.TryGetValue(name, out object? controllerValue))
            {
                mismatches.Add($"{name} is declared by the mod only");
            }
            else if (!Equals(modValue, controllerValue))
            {
                mismatches.Add($"{name}: mod {modValue}, controller {controllerValue}");
            }
        }

        Assert(mismatches.Count == 0, string.Join("; ", mismatches));
    }

    // The byte offsets both sides compute: every control slot, the bulk region base and every bulk slot.
    [Test("BridgeShared.ModSlotOffsetsMatchController")]
    private static void BridgeSharedModSlotOffsetsMatchController()
    {
        int controlSlots = Math.Max(ModLayout.ControlSlotCount, ControllerLayout.ControlSlotCount);
        for (int slot = 0; slot < controlSlots; slot++)
        {
            Assert(ModLayout.SlotByteOffset(slot) == ControllerLayout.SlotByteOffset(slot),
                $"control slot {slot}: mod {ModLayout.SlotByteOffset(slot)}, controller {ControllerLayout.SlotByteOffset(slot)}");
        }

        Assert(ModLayout.BulkRegionBaseOffset() == ControllerLayout.BulkRegionBaseOffset(),
            $"bulk region base: mod {ModLayout.BulkRegionBaseOffset()}, controller {ControllerLayout.BulkRegionBaseOffset()}");
        int bulkSlots = Math.Max(ModLayout.BulkSlotCount, ControllerLayout.BulkSlotCount);
        for (int slot = 0; slot < bulkSlots; slot++)
        {
            Assert(ModLayout.BulkSlotByteOffset(slot) == ControllerLayout.BulkSlotByteOffset(slot),
                $"bulk slot {slot}: mod {ModLayout.BulkSlotByteOffset(slot)}, controller {ControllerLayout.BulkSlotByteOffset(slot)}");
        }
    }

    // The sentinel markers: the bytes the mod's layout gives for the control and bulk markers are the bytes the
    // controller's scanner (ProcessMemoryBridge) looks for, and the controller's layout gives the same.
    [Test("BridgeShared.ModMarkersMatchController")]
    private static void BridgeSharedModMarkersMatchController()
    {
        byte[] control = ScannerMarker("FindMarker");
        byte[] bulk = ScannerMarker("BulkFindMarker");
        Assert(control.Length == 32 && bulk.Length == 32, $"32-byte markers, got {control.Length} and {bulk.Length}");
        for (int i = 0; i < control.Length; i++)
        {
            Assert(ModLayout.ControlMarkerByte(i) == control[i] && ControllerLayout.ControlMarkerByte(i) == control[i],
                $"control marker byte {i}: mod {ModLayout.ControlMarkerByte(i):X2}, scanner {control[i]:X2}");
            Assert(ModLayout.BulkMarkerByte(i) == bulk[i] && ControllerLayout.BulkMarkerByte(i) == bulk[i],
                $"bulk marker byte {i}: mod {ModLayout.BulkMarkerByte(i):X2}, scanner {bulk[i]:X2}");
        }
    }

    // CRC32 (IEEE): both give the standard check values, and the same result for a 64 KiB buffer and a slice of it.
    [Test("BridgeShared.ModCrc32MatchesController")]
    private static void BridgeSharedModCrc32MatchesController()
    {
        byte[] check = Encoding.ASCII.GetBytes("123456789");
        Assert(ModCrc32.Compute(check) == 0xCBF43926u, $"mod CRC32 of \"123456789\": {ModCrc32.Compute(check):X8}");
        Assert(ControllerCrc32.Compute(check) == 0xCBF43926u, $"controller CRC32 of \"123456789\": {ControllerCrc32.Compute(check):X8}");
        byte[] fox = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");
        Assert(ModCrc32.Compute(fox) == 0x414FA339u && ControllerCrc32.Compute(fox) == 0x414FA339u, "CRC32 of the fox sentence");

        byte[] data = new byte[65536];
        new Random(1234).NextBytes(data);
        Assert(ModCrc32.Compute(data) == ControllerCrc32.Compute(data), "CRC32 of a 64 KiB buffer");
        Assert(ModCrc32.Compute(data, 100, 4000) == ControllerCrc32.Compute(data, 100, 4000), "CRC32 of a slice");
    }

    private static Dictionary<string, object?> LayoutConstants(Type layout) =>
        layout.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .ToDictionary(field => field.Name, field => field.GetRawConstantValue());

    private static byte[] ScannerMarker(string field) =>
        (byte[])(typeof(ProcessMemoryBridge).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException($"ProcessMemoryBridge.{field} reflection target was not found."));
}
