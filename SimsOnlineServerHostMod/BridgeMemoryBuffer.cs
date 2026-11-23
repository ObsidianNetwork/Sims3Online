using System;
using System.Text;

namespace SimsOnlineServerHost
{
    internal static class BridgeMemoryBuffer
    {
        public const int BufferSize = BridgeSharedRegionLayout.SlotSize;
        public const int HeaderSize = BridgeSharedRegionLayout.SlotHeaderSize;
        public const ulong HostSenderId = BridgeSharedRegionLayout.HostSenderId;
        public const ulong ServerSenderId = BridgeSharedRegionLayout.ServerSenderId;
        public const ulong ConsumedSenderId = BridgeSharedRegionLayout.ConsumedSenderId;
        public const ulong HostWritingSenderId = BridgeSharedRegionLayout.HostWritingSenderId;

        private const int MarkerLength = 32;
        private const int SequenceOffset = 8;
        private const int LengthOffset = 12;
        private const int PayloadOffset = HeaderSize;
        private const int CompatSlotOffset = 0;

        public static readonly byte[] Region = new byte[BridgeSharedRegionLayout.TotalRegionSize];
        public static readonly byte[] Buffer = Region;

        private static bool sMultiSlotEnabled;
        private static bool sMultiSlotUpgradeRequested;
        private static int sHostWriteCursor;
        private static int sLastServerSequence;
        private static int sCurrentServerSequence;
        private static bool sConsumedStampSeen;
        // last proof the controller is alive (read a command, or observed our frame
        // get consumed). Re-arm staleness is measured from here, NOT from command recency —
        // a live controller can be command-idle indefinitely while events still flow.
        private static DateTime sLastControllerEvidenceUtc = DateTime.MinValue;
        private static ulong sLastSampledSenderId;
        // when we last wrote a host frame. An unread frame's staleness must be
        // measured from its WRITE time, not from controller-evidence age — SESSION_READY is
        // written minutes after the last command (LOAD_SAVE) on every fresh boot, and the
        // beacon must never overwrite a just-written unread frame.
        private static DateTime sLastHostWriteUtc = DateTime.MinValue;

        /// <summary>
        /// true once the controller has ever stamped a frame consumed this session.
        /// Proof the controller speaks the consumed-marker protocol, which disables the
        /// legacy lossy write fallback (overwriting an unread frame is how OK responses died).
        /// </summary>
        public static bool ConsumedStampSeen
        {
            get { return sConsumedStampSeen; }
        }

        static BridgeMemoryBuffer()
        {
            ResetFindMarker();
        }

        public static void Initialize()
        {
        }

        /// <summary>request v11 upgrade after HELLO is consumed (do not call EnableMultiSlot inline).</summary>
        public static void RequestMultiSlotEnable()
        {
            sMultiSlotUpgradeRequested = true;
        }

        /// <summary>
        /// layout bytes 32-63 overlap the compat-slot payload. Upgrade only after the
        /// controller consumed HELLO (ConsumedSenderId on offset 0) and inbound compat commands
        /// were drained by the pump (call after ProcessMemoryCommandOnce).
        /// </summary>
        public static void TryCompleteMultiSlotUpgrade()
        {
            if (!sMultiSlotUpgradeRequested || sMultiSlotEnabled)
            {
                return;
            }

            ulong senderId = BitConverter.ToUInt64(Region, CompatSlotOffset);
            if (senderId == ConsumedSenderId)
            {
                EnableMultiSlot();
                sMultiSlotUpgradeRequested = false;
                return;
            }

            // Fallback: the controller may have already stamped ConsumedSenderId AND written the
            // next command to a ctl slot (e.g. LOAD_SAVE on slot 8) before this tick ran. If a ctl
            // slot holds a fresh ServerSenderId frame, the HELLO has already been consumed —
            // enable multi-slot so the pump can read the pending command instead of wedging.
            for (int i = 0; i < BridgeSharedRegionLayout.CtlSlotCount; i++)
            {
                int slot = BridgeSharedRegionLayout.CtlSlotBase + i;
                int offset = BridgeSharedRegionLayout.SlotByteOffset(slot);
                if (BitConverter.ToUInt64(Region, offset) == ServerSenderId)
                {
                    EnableMultiSlot();
                    sMultiSlotUpgradeRequested = false;
                    return;
                }
            }
        }

        /// <summary>
        /// enable after LOAD_SAVE is read — controller always consumes HELLO before
        /// sending the next command, so the compat frame is safe to repurpose.
        /// </summary>
        public static void CompleteMultiSlotUpgradeAfterCommand()
        {
            if (!sMultiSlotUpgradeRequested || sMultiSlotEnabled)
            {
                return;
            }

            EnableMultiSlot();
            sMultiSlotUpgradeRequested = false;
        }

        /// <summary>upgrade from v10 compat (slot at offset 0) to multi-slot ring.</summary>
        public static void EnableMultiSlot()
        {
            if (sMultiSlotEnabled)
            {
                return;
            }

            WriteControlRegionHeader();
            WriteBulkSubRegionHeader();
            sMultiSlotEnabled = true;
            sHostWriteCursor = 0;
        }

        public static bool MultiSlotEnabled
        {
            get { return sMultiSlotEnabled; }
        }

        /// <summary>
        /// compat-slot payloads overlap bytes 32-63. Re-stamp when stomped so ctl routing
        /// and controller layout detection stay correct between re-attaches.
        /// </summary>
        public static void EnsureControlHeader()
        {
            if (!sMultiSlotEnabled)
            {
                return;
            }

            int layout = BitConverter.ToInt32(Region, BridgeSharedRegionLayout.LayoutVersionOffset);
            if (layout != BridgeSharedRegionLayout.LayoutVersion)
            {
                WriteControlRegionHeader();
            }
        }

        public static string ReadServerLine()
        {
            if (!sMultiSlotEnabled)
            {
                return ReadServerLineAt(CompatSlotOffset);
            }

            // re-attach CONNECT|19 lands on compat — prefer it over ctl stragglers.
            if (BitConverter.ToUInt64(Region, CompatSlotOffset) == ServerSenderId)
            {
                string compat = ReadServerLineAt(CompatSlotOffset);
                if (compat != null)
                {
                    return compat;
                }
            }

            for (int i = 0; i < BridgeSharedRegionLayout.CtlSlotCount; i++)
            {
                int slot = BridgeSharedRegionLayout.CtlSlotBase + i;
                string line = ReadServerLineAt(BridgeSharedRegionLayout.SlotByteOffset(slot));
                if (line != null)
                {
                    return line;
                }
            }

            return ReadServerLineAt(CompatSlotOffset);
        }

        private static string ReadServerLineAt(int byteOffset)
        {
            ulong senderId = BitConverter.ToUInt64(Region, byteOffset);
            if (senderId != ServerSenderId)
            {
                return null;
            }

            int sequence = BitConverter.ToInt32(Region, byteOffset + SequenceOffset);
            // multi-slot ctl frames stay ServerSenderId until the controller
            // overwrites them. Exact-seq dedupe (==) re-reads lower-seq slots after a later
            // command is processed — e.g. slot 8 LOAD_SAVE 724 then slot 9 SET_GAME_SPEED 725
            // causes HandleSetGameSpeed + duplicate OK every pump tick until ctl slot 9 rotates.
            if (sequence <= sLastServerSequence)
            {
                // follow-up (2026-07-03): the cursor survives controller restarts
                // while each controller randomizes its sequence start, so a new session whose
                // draw lands below the dead session's cursor was silently invisible — including
                // its CONNECT. CONNECT (always compat slot 0) is the session-identity reset
                // signal: accept it regardless of ordering and snap the cursor into the new
                // session's sequence space. Safe from stale-frame re-reads because the
                // re-attaching controller stamps leftover ctl frames consumed before CONNECT.
                if (byteOffset != CompatSlotOffset || !IsConnectPayload(byteOffset))
                {
                    return null;
                }
            }

            int length = BitConverter.ToInt32(Region, byteOffset + LengthOffset);
            if (length <= 0 || length > BufferSize - HeaderSize)
            {
                WriteHostLine(sequence, "ERROR|00000000000000000000000000000000|" + BridgeCodec.EncodeText("Invalid memory bridge frame length."));
                return null;
            }

            sLastServerSequence = sequence;
            sCurrentServerSequence = sequence;
            sLastControllerEvidenceUtc = DateTime.UtcNow;
            return Encoding.UTF8.GetString(Region, byteOffset + PayloadOffset, length);
        }

        // a host frame may be overwritten only after the controller consumed it (stamped
        // ConsumedSenderId) or when the buffer holds a server frame (the caller reads it first).
        // Before the consumed-marker handshake the mod wrote blindly and chunked responses lost
        // frames whenever two writes landed inside one controller poll interval.
        public static bool CanWriteHostLine()
        {
            if (!sMultiSlotEnabled)
            {
                return CanWriteHostSlot(CompatSlotOffset);
            }

            for (int attempt = 0; attempt < BridgeSharedRegionLayout.HostSlotCount; attempt++)
            {
                int slot = BridgeSharedRegionLayout.HostSlotBase + ((sHostWriteCursor + attempt) % BridgeSharedRegionLayout.HostSlotCount);
                if (CanWriteHostSlot(BridgeSharedRegionLayout.SlotByteOffset(slot)))
                {
                    sHostWriteCursor = slot - BridgeSharedRegionLayout.HostSlotBase;
                    return true;
                }
            }

            return false;
        }

        private static bool CanWriteHostSlot(int byteOffset)
        {
            ulong senderId = BitConverter.ToUInt64(Region, byteOffset);
            if (senderId == ConsumedSenderId)
            {
                sConsumedStampSeen = true;
                sLastControllerEvidenceUtc = DateTime.UtcNow;
            }

            if (!sMultiSlotEnabled && IsFindMarkerArmed())
            {
                return false;
            }

            if (sMultiSlotEnabled && byteOffset == CompatSlotOffset && IsFindMarkerArmed())
            {
                return false;
            }

            return senderId != HostSenderId && senderId != HostWritingSenderId;
        }

        /// <summary>
        /// controller re-attach: the find-marker is normally destroyed by the first
        /// frame and never restored, so a restarted controller could never rediscover the
        /// buffer. Re-arm it when there has been no controller evidence (command read, or a
        /// frame of ours consumed) for 10s — a live controller immediately refutes the beacon
        /// with a PING, a dead one leaves it armed for the re-attach scanner. An unread host
        /// frame is only overwritten after 120s (chunk streams produce consumption evidence
        /// continuously, so a healthy stream never trips this).
        /// </summary>
        public static bool TryRearmFindMarker()
        {
            // slow LOAD_SAVE (~15-20s) is command-idle on the mod side — the 10s
            // evidence gate would re-arm compat0 mid-load and wedge ctl routing at SESSION_READY.
            if (ServerHostMain.IsBridgeLoadActive())
            {
                return false;
            }

            // NOTE: do NOT clear ctl slots here. ClearStaleCtlServerFrames wipes unread commands
            // (e.g. LOAD_SAVE on slot 8) when the mod hasn't processed them yet — the 10s idle
            // gate fires during slow boots and destroys the pending command before the pump reads
            // it. The controller-side ClearStaleServerCommandFrames (in SendRawAsync) handles
            // orphaned commands on re-attach; the mod must never destroy unread inbound frames.
            double idleSeconds = (DateTime.UtcNow - sLastControllerEvidenceUtc).TotalSeconds;

            // never downgrade to compat-only inbound read while the v11 layout header
            // remains valid — the controller keeps routing ctl commands to slot 8+ and would
            // become deaf after ~10s idle (TryRearmFindMarker wedge; archive 2026-06-19-124540).
            if (sMultiSlotEnabled && !IsV11LayoutHeaderIntact())
            {
                if (!CanRearmMultiSlot())
                {
                    return false;
                }

                sMultiSlotEnabled = false;
            }

            ulong senderId = BitConverter.ToUInt64(Region, CompatSlotOffset);

            if (senderId != sLastSampledSenderId)
            {
                if (senderId == ConsumedSenderId)
                {
                    sLastControllerEvidenceUtc = DateTime.UtcNow;
                }

                sLastSampledSenderId = senderId;
            }

            if (IsFindMarkerArmed())
            {
                return false;
            }

            if (senderId == HostWritingSenderId)
            {
                return false;
            }

            if (senderId == ServerSenderId && BitConverter.ToInt32(Region, CompatSlotOffset + SequenceOffset) > sLastServerSequence)
            {
                return false;
            }

            idleSeconds = (DateTime.UtcNow - sLastControllerEvidenceUtc).TotalSeconds;
            if (senderId == HostSenderId && idleSeconds < 10.0)
            {
                return false;
            }
            else if (idleSeconds < 10.0)
            {
                return false;
            }

            ResetFindMarker();
            return true;
        }

        private static void ClearStaleCtlServerFrames()
        {
            for (int slot = 0; slot < BridgeSharedRegionLayout.ControlSlotCount; slot++)
            {
                int offset = BridgeSharedRegionLayout.SlotByteOffset(slot);
                ulong senderId = BitConverter.ToUInt64(Region, offset);
                if (senderId == ServerSenderId)
                {
                    Array.Clear(Region, offset, BridgeSharedRegionLayout.SlotSize);
                }
            }
        }

        /// <summary>
        /// follow-up (2026-07-03): true when the frame at <paramref name="byteOffset"/>
        /// carries a CONNECT handshake payload. Used to let a new controller session's CONNECT
        /// through the sequence-dedupe gate (its randomized sequence can start below the dead
        /// session's cursor).
        /// </summary>
        private static bool IsConnectPayload(int byteOffset)
        {
            int length = BitConverter.ToInt32(Region, byteOffset + LengthOffset);
            if (length < 8 || length > BufferSize - HeaderSize)
            {
                return false;
            }

            int payload = byteOffset + PayloadOffset;
            return Region[payload] == (byte)'C'
                && Region[payload + 1] == (byte)'O'
                && Region[payload + 2] == (byte)'N'
                && Region[payload + 3] == (byte)'N'
                && Region[payload + 4] == (byte)'E'
                && Region[payload + 5] == (byte)'C'
                && Region[payload + 6] == (byte)'T'
                && Region[payload + 7] == (byte)'|';
        }

        /// <summary>
        /// Graceful controller shutdown (DISCONNECT command): void any leftover command frames
        /// from the ending session (a re-read after the cursor reset would double-execute
        /// them), reset the inbound dedupe cursor so the next session's randomized sequence
        /// space is accepted unconditionally, and re-arm the discovery marker immediately
        /// instead of waiting out the 10s evidence window. Re-attach drops from ~10-20s
        /// (beacon re-arm + scan) to the next scan pass.
        /// </summary>
        public static void NoteControllerDisconnected()
        {
            ClearStaleCtlServerFrames();
            sLastServerSequence = 0;
            sCurrentServerSequence = 0;
            sLastControllerEvidenceUtc = DateTime.MinValue;
            sLastSampledSenderId = 0;
            ResetFindMarker();
        }

        private static bool CanRearmMultiSlot()
        {
            double evidenceIdle = (DateTime.UtcNow - sLastControllerEvidenceUtc).TotalSeconds;
            if (evidenceIdle < 10.0)
            {
                return false;
            }

            for (int slot = 0; slot < BridgeSharedRegionLayout.ControlSlotCount; slot++)
            {
                int offset = BridgeSharedRegionLayout.SlotByteOffset(slot);
                ulong senderId = BitConverter.ToUInt64(Region, offset);
                if (senderId == HostSenderId || senderId == HostWritingSenderId)
                {
                    continue;
                }

                if (senderId == ServerSenderId)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsV11LayoutHeaderIntact()
        {
            int layout = BitConverter.ToInt32(Region, BridgeSharedRegionLayout.LayoutVersionOffset);
            return layout == BridgeSharedRegionLayout.LayoutVersion;
        }

        public static void NoteControllerActivity()
        {
            sLastControllerEvidenceUtc = DateTime.UtcNow;
        }

        /// <summary>full 32-byte marker check (not just sender-id prefix).</summary>
        public static bool IsFindMarkerFullyArmed()
        {
            for (int i = 0; i < 32; i++)
            {
                if (Region[i] != BridgeSharedRegionLayout.ControlMarkerByte(i))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsFindMarkerArmed()
        {
            return IsFindMarkerFullyArmed();
        }

        public static void WriteHostLine(string line)
        {
            WriteHostLine(sCurrentServerSequence, line);
        }

        public static void WriteHostLine(int sequence, string line)
        {
            byte[] payload = Encoding.UTF8.GetBytes(line);
            if (payload.Length > BufferSize - HeaderSize)
            {
                payload = Encoding.UTF8.GetBytes("ERROR|00000000000000000000000000000000|" + BridgeCodec.EncodeText("Bridge response is too large."));
            }

            int byteOffset;
            if (!sMultiSlotEnabled)
            {
                byteOffset = CompatSlotOffset;
            }
            else
            {
                int slot = BridgeSharedRegionLayout.HostSlotBase + sHostWriteCursor;
                byteOffset = BridgeSharedRegionLayout.SlotByteOffset(slot);
            }

            WriteHostLineAt(byteOffset, sequence, payload);
            if (sMultiSlotEnabled)
            {
                sHostWriteCursor = (sHostWriteCursor + 1) % BridgeSharedRegionLayout.HostSlotCount;
            }
        }

        /// <summary>
        /// CONNECT/HELLO handshake always uses compat slot 0, even when multi-slot is
        /// already enabled (warm re-attach). ConnectAsync reads compat before RefreshLayoutMode.
        /// </summary>
        public static void WriteHostLineCompat(string line)
        {
            byte[] payload = Encoding.UTF8.GetBytes(line);
            if (payload.Length > BufferSize - HeaderSize)
            {
                payload = Encoding.UTF8.GetBytes("ERROR|00000000000000000000000000000000|" + BridgeCodec.EncodeText("Bridge response is too large."));
            }

            WriteHostLineAt(CompatSlotOffset, sCurrentServerSequence, payload);
        }

        /// <summary>
        /// controller re-attach — consume orphan host-ring replies from the prior session
        /// so ctl writes are not displaced by stale HELLO/OK lines.
        /// </summary>
        public static void ClearHostOutboundSlotsForReconnect()
        {
            for (int i = 0; i < BridgeSharedRegionLayout.HostSlotCount; i++)
            {
                int offset = BridgeSharedRegionLayout.SlotByteOffset(BridgeSharedRegionLayout.HostSlotBase + i);
                ulong senderId = BitConverter.ToUInt64(Region, offset);
                if (senderId == HostSenderId || senderId == HostWritingSenderId)
                {
                    Array.Copy(BitConverter.GetBytes(ConsumedSenderId), 0, Region, offset, 8);
                }
            }
        }

        private static void WriteHostLineAt(int byteOffset, int sequence, byte[] payload)
        {
            int clearLength = BufferSize - 8;
            if (byteOffset == CompatSlotOffset)
            {
                // compat shares bytes 0-31 with marker + slot header; bytes 32-63
                // are the v11 control header — never Array.Clear past byte 31 on multi-slot sessions.
                if (sMultiSlotEnabled)
                {
                    clearLength = 32 - 8;
                }
                else
                {
                    clearLength = Math.Min(clearLength, PayloadOffset + payload.Length - 8);
                    if (clearLength < 0)
                    {
                        clearLength = 0;
                    }
                }
            }

            Array.Copy(BitConverter.GetBytes(HostWritingSenderId), 0, Region, byteOffset, 8);
            if (clearLength > 0)
            {
                Array.Clear(Region, byteOffset + 8, clearLength);
            }

            Array.Copy(BitConverter.GetBytes(sequence), 0, Region, byteOffset + SequenceOffset, 4);
            Array.Copy(BitConverter.GetBytes(payload.Length), 0, Region, byteOffset + LengthOffset, 4);
            Array.Copy(payload, 0, Region, byteOffset + PayloadOffset, payload.Length);
            Array.Copy(BitConverter.GetBytes(HostSenderId), 0, Region, byteOffset, 8);
            sLastHostWriteUtc = DateTime.UtcNow;
        }

        private static void WriteControlRegionHeader()
        {
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.LayoutVersion), 0, Region, BridgeSharedRegionLayout.LayoutVersionOffset, 4);
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.ControlSlotCount), 0, Region, 36, 4);
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.SlotSize), 0, Region, 40, 4);
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.SlotHeaderSize), 0, Region, 44, 4);
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.HostSlotBase), 0, Region, 48, 4);
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.HostSlotCount), 0, Region, 52, 4);
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.CtlSlotBase), 0, Region, 56, 4);
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.CtlSlotCount), 0, Region, 60, 4);
        }

        private static void WriteBulkSubRegionHeader()
        {
            int baseOffset = BridgeSharedRegionLayout.BulkRegionBaseOffset();
            for (int i = 0; i < MarkerLength; i++)
            {
                Region[baseOffset + i] = BridgeSharedRegionLayout.BulkMarkerByte(i);
            }

            int headerOffset = baseOffset + 32;
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.LayoutVersion), 0, Region, headerOffset, 4);
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.BulkSlotCount), 0, Region, headerOffset + 4, 4);
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.BulkSlotSize), 0, Region, headerOffset + 8, 4);
            Array.Copy(BitConverter.GetBytes((uint)BridgeSharedRegionLayout.BulkSlotHeaderSize), 0, Region, headerOffset + 12, 4);
        }

        private static void ResetFindMarker()
        {
            // only rewrite the 32-byte discovery marker. Do NOT clear bytes 32-63 —
            // that wipes layoutVersion and forces the controller's v10 scanner down the
            // "bytes 32-255 must be zero" path, which permanently fails once v11 slots hold
            // data (re-attach wedged for 120s). The v11 control header stays intact.
            for (int i = 0; i < MarkerLength; i++)
            {
                Region[i] = BridgeSharedRegionLayout.ControlMarkerByte(i);
            }
        }
    }
}
