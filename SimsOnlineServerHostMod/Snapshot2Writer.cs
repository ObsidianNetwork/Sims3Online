// C#2 ONLY. Same compilation constraints as OutboundLaneQueue.cs:
//   1. Sims 3 script mod via csc.exe 2.0 (non-recursive *.cs glob)
//   2. DedicatedServer.Tests via source-link
// NO var, NO LINQ, NO lambdas, NO async, NO C#3+ syntax.
using System;
using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    /// <summary>
    /// Phase 2b: binary snapshot encoder for the v11 bulk transport.
    /// Writes SNAPSHOT2 binary directly into a byte[] buffer — never builds the
    /// ~2 MiB ASCII SNAPSHOT| line that caused the UTF-8 GetBytes cliff in Phase 2a.
    ///
    /// Binary format (little-endian):
    ///   u32  magic = 0x32505353 ("SPS2")
    ///   u32  version = 1
    ///   u64  tick
    ///   u32  worldNameLen + UTF-8 bytes
    ///   u32  saveNameLen + UTF-8 bytes
    ///   u32  speed (0=Pause,1=Normal,2=Double,3=Triple,4=Skip)
    ///   u32  simCount
    ///     per sim:
    ///       u64 objectId
    ///       u32 nameLen + UTF-8 bytes
    ///       u64 householdId
    ///       f32 x, y, z
    ///       u64 currentInteractionId (0 = none)
    ///       u32 interactionKeyLen + UTF-8 bytes (0 = none)
    ///       u32 owningPlayerId (0xFFFFFFFF = none)
    ///       u64 lotId (0 = none)
    ///       u32 interactionQueueCount (0xFFFFFFFF = none)
    ///       u64 postureContainerObjectId (0 = none; forward-compat Gap A)
    ///       u32 postureTypeLen + UTF-8 bytes (0 = StandingPosture; forward-compat)
    ///   u32  objectCount
    ///     per object:
    ///       u64 objectId
    ///       u32 scriptClassLen + UTF-8 bytes
    ///       f32 x, y, z
    ///       u64 lotId (0 = none)
    /// </summary>
    internal sealed class Snapshot2Writer
    {
        public const uint Magic = 0x32505353u;
        // 2: each Sim ends with its SimDescriptionId (W2 Sim identity).
        public const uint Version = 2u;

        // 0xFFFFFFFF sentinel for nullable u32 fields.
        private const uint NullInt32 = 0xFFFFFFFFu;

        private byte[] _buffer;
        private int _position;

        public Snapshot2Writer(int initialCapacity)
        {
            _buffer = new byte[initialCapacity];
            _position = 0;
        }

        public int Length
        {
            get { return _position; }
        }

        public byte[] ToArray()
        {
            byte[] result = new byte[_position];
            Array.Copy(_buffer, 0, result, 0, _position);
            return result;
        }

        public void WriteUInt32(uint value)
        {
            Ensure(4);
            Array.Copy(BitConverter.GetBytes(value), 0, _buffer, _position, 4);
            _position += 4;
        }

        public void WriteInt64(long value)
        {
            Ensure(8);
            Array.Copy(BitConverter.GetBytes(value), 0, _buffer, _position, 8);
            _position += 8;
        }

        public void WriteUInt64(ulong value)
        {
            Ensure(8);
            Array.Copy(BitConverter.GetBytes(value), 0, _buffer, _position, 8);
            _position += 8;
        }

        public void WriteSingle(float value)
        {
            Ensure(4);
            Array.Copy(BitConverter.GetBytes(value), 0, _buffer, _position, 4);
            _position += 4;
        }

        public void WriteString(string value)
        {
            if (value == null || value.Length == 0)
            {
                WriteUInt32(0u);
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteUInt32((uint)bytes.Length);
            Ensure(bytes.Length);
            Array.Copy(bytes, 0, _buffer, _position, bytes.Length);
            _position += bytes.Length;
        }

        public void WriteNullableUInt64(ulong value, bool hasValue)
        {
            WriteUInt64(hasValue ? value : 0uL);
        }

        public void WriteNullableInt32(int value, bool hasValue)
        {
            WriteUInt32(hasValue ? (uint)value : NullInt32);
        }

        public void WriteHeader(long tick, string worldName, string saveName, int speed)
        {
            WriteUInt32(Magic);
            WriteUInt32(Version);
            WriteInt64(tick);
            WriteString(worldName);
            WriteString(saveName);
            WriteUInt32((uint)speed);
        }

        public void WriteSimCount(int count)
        {
            WriteUInt32((uint)count);
        }

        public void WriteObjectCount(int count)
        {
            WriteUInt32((uint)count);
        }

        private void Ensure(int needed)
        {
            if (_position + needed <= _buffer.Length)
            {
                return;
            }

            int newSize = _buffer.Length * 2;
            while (newSize < _position + needed)
            {
                newSize *= 2;
            }

            byte[] grown = new byte[newSize];
            Array.Copy(_buffer, 0, grown, 0, _position);
            _buffer = grown;
        }

        /// <summary>
        /// Encode a speed name string to the binary enum value.
        /// </summary>
        public static int SpeedToOrdinal(string speed)
        {
            if (speed == null)
            {
                return 0;
            }

            if (speed == "Pause")
            {
                return 0;
            }

            if (speed == "Normal")
            {
                return 1;
            }

            if (speed == "Double")
            {
                return 2;
            }

            if (speed == "Triple")
            {
                return 3;
            }

            if (speed == "Skip")
            {
                return 4;
            }

            return 0;
        }
    }
}