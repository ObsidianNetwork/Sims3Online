using System;
using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    internal static class BridgeBulkBuffer
    {
        private const int StateOffset = 0;
        private const int CorrelationIdOffset = 8;
        private const int TotalLengthOffset = 24;
        private const int CrcOffset = 28;
        private const int PayloadOffset = BridgeSharedRegionLayout.BulkSlotHeaderSize;

        public static bool TryPublishHostBulk(string correlationId, byte[] payload, out int slotIndex, out uint crc32)
        {
            return TryPublishHostBulkBytes(correlationId, payload, 0, payload == null ? 0 : payload.Length, out slotIndex, out crc32);
        }

        public static bool TryPublishHostBulkBytes(string correlationId, byte[] payload, int offset, int length, out int slotIndex, out uint crc32)
        {
            slotIndex = -1;
            crc32 = 0;
            if (!BridgeMemoryBuffer.MultiSlotEnabled || payload == null || length <= 0 || length > BridgeSharedRegionLayout.BulkSlotPayloadMax)
            {
                return false;
            }

            if (offset < 0 || offset + length > payload.Length)
            {
                return false;
            }

            if (!TryFindWritableSlot(out slotIndex))
            {
                return false;
            }

            crc32 = BridgeCrc32.Compute(payload, offset, length);
            int byteOffset = BridgeSharedRegionLayout.BulkSlotByteOffset(slotIndex);
            WriteUInt64(byteOffset + StateOffset, BridgeSharedRegionLayout.BulkStateHostWriting);
            // TS3's mscorlib is missing Guid.ToByteArray() — manually convert the correlation
            // ID hex string to 16 bytes (same layout as Guid.ToByteArray: little-endian groups).
            byte[] corrBytes = CorrelationIdToBytes(correlationId);
            Array.Copy(corrBytes, 0, BridgeMemoryBuffer.Region, byteOffset + CorrelationIdOffset, 16);
            WriteInt32(byteOffset + TotalLengthOffset, length);
            WriteUInt32(byteOffset + CrcOffset, crc32);
            Array.Copy(payload, offset, BridgeMemoryBuffer.Region, byteOffset + PayloadOffset, length);
            WriteUInt64(byteOffset + StateOffset, BridgeSharedRegionLayout.BulkStateHostReady);
            return true;
        }

        public static bool HasWritableHostSlots(int requiredCount)
        {
            if (!BridgeMemoryBuffer.MultiSlotEnabled || requiredCount <= 0 || requiredCount > BridgeSharedRegionLayout.BulkSlotCount)
            {
                return false;
            }

            int available = 0;
            for (int i = 0; i < BridgeSharedRegionLayout.BulkSlotCount; i++)
            {
                int byteOffset = BridgeSharedRegionLayout.BulkSlotByteOffset(i);
                ulong state = ReadUInt64(byteOffset + StateOffset);
                if (state == BridgeSharedRegionLayout.BulkStateEmpty || state == BridgeSharedRegionLayout.BulkStateHostConsumed)
                {
                    available++;
                    if (available >= requiredCount)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary> Phase 2a inbound: mod consumes a ctl-published bulk slot after BULK_REF probe-upload.</summary>
        public static bool TryConsumeCtlBulk(int slotIndex, int expectedLength, uint expectedCrc32, out byte[] payload)
        {
            payload = null;
            if (!BridgeMemoryBuffer.MultiSlotEnabled || slotIndex < 0 || slotIndex >= BridgeSharedRegionLayout.BulkSlotCount
                || expectedLength <= 0 || expectedLength > BridgeSharedRegionLayout.BulkSlotPayloadMax)
            {
                return false;
            }

            int byteOffset = BridgeSharedRegionLayout.BulkSlotByteOffset(slotIndex);
            ulong state = ReadUInt64(byteOffset + StateOffset);
            if (state != BridgeSharedRegionLayout.BulkStateCtlReady)
            {
                return false;
            }

            int totalLength = ReadInt32(byteOffset + TotalLengthOffset);
            uint crc32 = ReadUInt32(byteOffset + CrcOffset);
            if (totalLength != expectedLength || crc32 != expectedCrc32)
            {
                return false;
            }

            payload = new byte[totalLength];
            Array.Copy(BridgeMemoryBuffer.Region, byteOffset + PayloadOffset, payload, 0, totalLength);
            if (BridgeCrc32.Compute(payload) != expectedCrc32)
            {
                payload = null;
                return false;
            }

            WriteUInt64(byteOffset + StateOffset, BridgeSharedRegionLayout.BulkStateCtlConsumed);
            return true;
        }

        private static bool TryFindWritableSlot(out int slotIndex)
        {
            for (int i = 0; i < BridgeSharedRegionLayout.BulkSlotCount; i++)
            {
                int byteOffset = BridgeSharedRegionLayout.BulkSlotByteOffset(i);
                ulong state = ReadUInt64(byteOffset + StateOffset);
                if (state == BridgeSharedRegionLayout.BulkStateEmpty || state == BridgeSharedRegionLayout.BulkStateHostConsumed)
                {
                    slotIndex = i;
                    return true;
                }
            }

            slotIndex = -1;
            return false;
        }

        private static ulong ReadUInt64(int byteOffset)
        {
            return BitConverter.ToUInt64(BridgeMemoryBuffer.Region, byteOffset);
        }

        private static int ReadInt32(int byteOffset)
        {
            return BitConverter.ToInt32(BridgeMemoryBuffer.Region, byteOffset);
        }

        private static uint ReadUInt32(int byteOffset)
        {
            return BitConverter.ToUInt32(BridgeMemoryBuffer.Region, byteOffset);
        }

        private static void WriteUInt64(int byteOffset, ulong value)
        {
            Array.Copy(BitConverter.GetBytes(value), 0, BridgeMemoryBuffer.Region, byteOffset, 8);
        }

        private static void WriteInt32(int byteOffset, int value)
        {
            Array.Copy(BitConverter.GetBytes(value), 0, BridgeMemoryBuffer.Region, byteOffset, 4);
        }

        private static void WriteUInt32(int byteOffset, uint value)
        {
            Array.Copy(BitConverter.GetBytes(value), 0, BridgeMemoryBuffer.Region, byteOffset, 4);
        }

        // TS3's mscorlib is missing Guid.ToByteArray(). Manually convert a correlation ID
        // hex string (e.g. "249c01fe3bfd4d139e79ae43adec5126") to 16 bytes in the same
        // layout as Guid.ToByteArray(): the .NET Guid stores int32+a16+int16+8bytes in
        // little-endian groups. We replicate that layout so the controller's Guid(bytes)
        // round-trips to the same correlation ID.
        private static byte[] CorrelationIdToBytes(string correlationId)
        {
            byte[] bytes = new byte[16];
            if (correlationId == null || correlationId.Length < 32)
            {
                return bytes;
            }

            // Remove dashes if present (N format has none, D format has them).
            string hex = correlationId.Replace("-", string.Empty);
            if (hex.Length < 32)
            {
                return bytes;
            }

            // .NET Guid byte layout (little-endian groups):
            // bytes 0..3  = int32 little-endian (hex[6..8], hex[4..6], hex[2..4], hex[0..2])
            // bytes 4..5  = int16 little-endian (hex[10..12], hex[8..10])
            // bytes 6..7  = int16 little-endian (hex[14..16], hex[12..14])
            // bytes 8..15 = 8 bytes big-endian (hex[16..32] as-is)
            bytes[0] = HexByte(hex[6], hex[7]);
            bytes[1] = HexByte(hex[4], hex[5]);
            bytes[2] = HexByte(hex[2], hex[3]);
            bytes[3] = HexByte(hex[0], hex[1]);
            bytes[4] = HexByte(hex[10], hex[11]);
            bytes[5] = HexByte(hex[8], hex[9]);
            bytes[6] = HexByte(hex[14], hex[15]);
            bytes[7] = HexByte(hex[12], hex[13]);
            for (int i = 0; i < 8; i++)
            {
                bytes[8 + i] = HexByte(hex[16 + i * 2], hex[17 + i * 2]);
            }
            return bytes;
        }

        private static byte HexByte(char high, char low)
        {
            return (byte)((HexVal(high) << 4) | HexVal(low));
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }
            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }
            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }
            return 0;
        }
    }
}
