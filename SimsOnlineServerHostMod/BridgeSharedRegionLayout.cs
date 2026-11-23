using System;

namespace SimsOnlineServerHost
{
    internal static class BridgeSharedRegionLayout
    {
        public const int LayoutVersion = 11;
        public const int ProtocolVersion = 23;
        public const int HelloWireVersion = 6;

        public const int SlotSize = 4096;
        public const int SlotHeaderSize = 16;
        public const int SlotPayloadMax = SlotSize - SlotHeaderSize;
        public const int ControlHeaderSize = 64;
        public const int ControlSlotCount = 16;
        public const int HostSlotBase = 0;
        public const int HostSlotCount = 8;
        public const int CtlSlotBase = 8;
        public const int CtlSlotCount = 8;

        public const int BulkSlotCount = 4;
        public const int BulkSlotSize = 524288;
        public const int BulkHeaderSize = 64;
        public const int BulkSlotHeaderSize = 32;

        public const int ControlRegionSize = ControlHeaderSize + ControlSlotCount * SlotSize;
        public const int BulkRegionSize = BulkHeaderSize + BulkSlotCount * BulkSlotSize;
        public const int TotalRegionSize = ControlRegionSize + BulkRegionSize;

        public const ulong HostSenderId = 0x92348DE77B2A9F34UL;
        public const ulong ServerSenderId = 0xFFB435D1231AF018UL;
        public const ulong ConsumedSenderId = 0xAC0DE5EDFEEDFACEUL;
        public const ulong HostWritingSenderId = 0xB17EC0DE57A6E511UL;

        public const int LayoutVersionOffset = 32;

        public const string DefaultHelloCaps = "multi-slot=16,bulk=4x512k,layout=11,snapshot2=1";

        public static int SlotByteOffset(int slotIndex)
        {
            return ControlHeaderSize + slotIndex * SlotSize;
        }

        public static int BulkRegionBaseOffset()
        {
            return ControlRegionSize;
        }

        public static int BulkSlotByteOffset(int slotIndex)
        {
            return BulkRegionBaseOffset() + BulkHeaderSize + slotIndex * BulkSlotSize;
        }

        public const int BulkSlotPayloadMax = BulkSlotSize - BulkSlotHeaderSize;

        public const ulong BulkStateEmpty = 0x0000000000000000UL;
        public const ulong BulkStateHostWriting = 0xC011C01100000001UL;
        public const ulong BulkStateHostReady = 0xC011C01100000002UL;
        public const ulong BulkStateHostConsumed = 0xC011C01100000003UL;
        public const ulong BulkStateCtlWriting = 0xC011C01100000011UL;
        public const ulong BulkStateCtlReady = 0xC011C01100000012UL;
        public const ulong BulkStateCtlConsumed = 0xC011C01100000013UL;

        public static byte ControlMarkerByte(int index)
        {
            return (byte)(((0x53 + (index * 0x2D) + ((index & 3) * 0x11)) ^ (0xA7 - (index * 3))) & 0xFF);
        }

        public static byte BulkMarkerByte(int index)
        {
            return (byte)(((0x42 + (index * 0x1F) + ((index & 7) * 0x09)) ^ (0x5C - (index * 5))) & 0xFF);
        }
    }
}
