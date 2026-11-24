using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace SimsOnlineServerHost
{
    // Experiment C (internal documentation): internal calls the native SimLink sidecar (SimLink/, an ASI
    // plugin) registers in this TS3 when the controller asks it to. In this Mono an internal call that isn't
    // registered is fatal, so nothing here may run before the controller's SACS_RECORDER sidecar-on, which it only
    // sends once the sidecar reports its calls registered. Every call site is inside SimLinkShadow's NoInlining
    // methods, which nothing reaches before Enable.
    internal static class SimLinkNative
    {
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int Version();

        // Finds how a managed string is laid out in this runtime (where its length and characters sit) from a known
        // string; returns that offset, or < 0.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int Probe(string text, int length);

        // Copies one line into the sidecar's ring with the game's QueryPerformanceCounter; never blocks.
        // Returns 1 when written, 0 when dropped.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int Publish(int lane, int seq, string line, int length);

        // Client shadow: drains the controller -> game ring and stamps when each line became visible here.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int InboundPoll();

        // Client shadow: one line that arrived through SACS_REPLAY; the sidecar compares it with the ring's copy
        // and sends the controller a sample. Returns 1 match, 0 mismatch, 2 not in the ring, -1 bad arguments.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int NoteBridge(int seq, string line, int length);

        // Client ring path: finds how this runtime lays out a char[] from a known array; returns the element offset.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int ProbeArray(char[] probe, int length);

        // Client ring path: copies whole inbound lines, each followed by '\n', into buffer and consumes them. Returns
        // the chars written, 0 when empty, or -(chars needed) when the first line doesn't fit.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int InboundRead(char[] buffer, int capacity);

        // tier 1 (native version 2): one of a SACS driver's two LCG seeds. which 0 = the Random-node seed (driver
        // +0xA8), 1 = the spare seed (+0x34). Returns the seed, or 0 when the handle does not resolve to a driver (or
        // this TS3W.exe build differs); the game itself treats 0 as "reseed from rdtsc on the next draw".
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsReadSeed(uint driverHandle, int which);

        // Native v16: 1 no pending work, 0 work remains, -1 unavailable. Live owners are a separate check.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsHasNoPendingWork(uint driverHandle);

        // Native v17: sequence-list guard state, checks and prevented sentinel reads.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsSequenceDrain(int which);

        // Writes the non-zero seeds into the handle's driver and reads them back: 1 written, 0 no driver, -1 the game's
        // bytes differ from this build, -2 exception, -3 the read-back differs.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsWriteSeeds(uint driverHandle, int contextSeed, int spareSeed);

        // tier 3 (native version 3, SimLink/src/sacs_patch.cpp): the code patches SimLink installs at load.
        // SacsSetSwitches sets the switch mask the thunks read (bit 0 = keyed path picks; mask < 0 only reads it) and
        // returns the previous mask. SacsPatchState: state | detail << 8 (1 installed; see sacs_patch.h). The pick log:
        // SacsPickCount records so far; SacsPickWord one word of a record (word 9 is seq + 1 once it is whole);
        // SacsPickCounter diagnostics (0 queries, 1 invalid walkers, 2 draws, 3 keyed, 4 requests, 5 table full,
        // 6 draws outside a query).
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsSetSwitches(int mask);

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsPatchState();

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsPickCount();

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsPickWord(int seq, int word);

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsPickCounter(int which);

        // site A' (native version 4): the order log, one record per phase-0 execution of a decision graph. Switch
        // bit 1 = file-order picks, bit 2 = log only. SacsOrderCount records so far; SacsOrderWord one word of a record
        // (word 9 is seq + 1 once whole); SacsOrderCounter diagnostics (see SacsOrder).
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsOrderCount();

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsOrderWord(int seq, int word);

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsOrderCounter(int which);

        // item 3 (native version 5): the keyed Random-node draw, switch bit 3; diagnostics (see SacsRandom).
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsRandomCounter(int which);

        // Item 3's input log (native version 6), switch bit 4: SacsRandomCount records so far; SacsRandomWord one word of
        // a record (word 9 is seq + 1 once whole).
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsRandomCount();

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsRandomWord(int seq, int word);

        //'s GC rate gate (native version 7, SimLink/src/gc_gate.cpp), switch bit 5: SacsGcCounter reads a
        // counter (see SacsGc), SacsGcInterval sets the interval in ms (ms < 0 only reads it) and returns the previous.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsGcCounter(int which);

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsGcInterval(int ms);

        //'s explicit-free ring (native version 8, SimLink/src/free_ring.cpp): SacsFreeRing reads a counter
        // (see SacsFreeRing).
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsFreeRing(int which);

        //'s chunk reserve (native version 10, on by default from 11; SimLink/src/chunk_reserve.cpp):
        // SacsChunkReserve reads a counter (see SacsChunkReserve).
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsChunkReserve(int which);

        //'s on-screen walk (native version 12, SimLink/src/onscreen.cpp): SacsOnScreen reads a counter (see
        // SacsOnScreen).
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsOnScreen(int which);

        // option C (native version 13, SimLink/src/walker_copy_logic.h): a driver's walker counters copied to a
        // client's copy instead of both games starting them again (see SacsWalkerCopy). Errors: -1 build bytes differ,
        // -2 exception, -3 no driver, -4 index out of range, -5 the carry's old handle unknown, -6 walker table full.
        // SacsWalkerRead snapshots walker `index` of the handle's driver without creating or resetting anything and
        // returns the walker count (> index), 0 past the last walker, or an error; SacsWalkerWord reads the snapshot
        // (0 spare, 1 generation, 2 draws, 3 boundaries, 4 executions, 5 1 when SimLink had counts under that spare).
        // SacsWalkerWrite puts counts on the copy's walker under the spare they were read with: 1 written, 2 kept until
        // the walker exists, 3 kept until the driver carries that spare. SacsWalkerCarry copies an old copy driver's
        // counts onto the driver that replaced it (the walkers carried). SacsWalkerCounter: diagnostics.
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsWalkerRead(uint driverHandle, int index);

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsWalkerWord(int word);

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsWalkerWrite(uint driverHandle, int index, int spare, int generation, int draws, int boundaries, int executions);

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsWalkerCarry(uint oldHandle, uint newHandle, int spare);

        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsWalkerCounter(int which);

        // The GC heap lever (native version 14, SimLink/src/gc_heap.cpp): SacsGcHeap reads a counter (see SacsGcHeap).
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern int SacsGcHeap(int which);
    }

    // Shadow mode: every recorder line (SacsRecorder.Ring) is also published to the sidecar. The bridge stream is
    // unchanged; the controller compares the two. Lane 1 = a real SACS callback, lane 2 = a line the recorder's
    // observers generate while the controller's dump runs (positions, postures, hands, routes): only lane 1 measures
    // how long an animation call waits to reach the controller.
    internal static class SimLinkShadow
    {
        internal const string ProbeText = "SimLink-probe";
        internal const string ArrayProbeText = "SimLink-array!";
        internal const int MaxBufferChars = 1 << 20;
        internal const int CallbackLane = 1;
        internal const int PolledLane = 2;
        // Server ring path: one record per page of the presentation stream, the same text as a SACS_RECORDER dump reply.
        internal const int PageLane = 6;
        internal static bool PushReady;

        internal static bool Enabled;
        internal static bool ClientEnabled;
        // Client ring path (SIMSONLINE_SIMLINK_CLIENT_RING=1 on the client controller): SimAnimation lines arrive
        // through the inbound ring and are applied from it; the bridge no longer carries them.
        internal static bool RingEnabled;
        internal static int ArrayProbeOffset;
        internal static long RingReads;
        internal static long RingRuns; // ring task updates (one per simulator update while the ring path is on)
        internal static long RingLines;
        internal static long RingErrors;
        internal static string RingLastError = "-";
        private static char[] sBuffer = new char[16384];
        internal static long InboundSeen;
        internal static long BridgeNoted;
        private static int sNativeVersion;

        // Set wherever the mod asks SimLink its version (the handshakes below).'s switches are on by default
        // (2026-10-21), and only SacsPicks.Sync writes them into SimLink, so the first answer pushes them: without a
        // tunable set or a status read, SimLink would keep its load-time mask of 0.
        internal static int NativeVersion
        {
            get { return sNativeVersion; }
            set
            {
                bool changed = value != sNativeVersion;
                sNativeVersion = value;
                if (changed)
                {
                    SacsPicks.Sync();
                }
            }
        }

        internal static int ProbeOffset;
        internal static long Published;
        internal static long Dropped;
        internal static string LastError = "-";

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string Enable()
        {
            try
            {
                NativeVersion = SimLinkNative.Version();
                ProbeOffset = NativeVersion > 0 ? SimLinkNative.Probe(ProbeText, ProbeText.Length) : -100;
                Enabled = NativeVersion > 0 && ProbeOffset > 0;
            }
            catch (Exception ex)
            {
                Enabled = false;
                LastError = ex.GetType().Name;
            }

            return Describe();
        }

        // Server ring path (push-on, sent by the server controller after registration): the string layout is all the
        // page publisher needs.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool EnablePush()
        {
            try
            {
                NativeVersion = SimLinkNative.Version();
                ProbeOffset = NativeVersion > 0 ? SimLinkNative.Probe(ProbeText, ProbeText.Length) : -100;
                PushReady = ProbeOffset > 0;
            }
            catch (Exception ex)
            {
                PushReady = false;
                LastError = ex.GetType().Name;
            }

            return PushReady;
        }

        // One page into the ring; false when the ring had no room (the caller keeps its cursor and retries).
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool PublishPage(int seq, string page)
        {
            return SimLinkNative.Publish(PageLane, seq, page, page.Length) != 0;
        }

        // Client ring path (sent by the client controller after registration). Exclusive with the client shadow:
        // both consume the inbound ring.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string EnableClientRing()
        {
            try
            {
                ClientEnabled = false;
                NativeVersion = SimLinkNative.Version();
                ProbeOffset = NativeVersion > 0 ? SimLinkNative.Probe(ProbeText, ProbeText.Length) : -100;
                char[] probe = ArrayProbeText.ToCharArray();
                ArrayProbeOffset = ProbeOffset > 0 ? SimLinkNative.ProbeArray(probe, probe.Length) : -100;
                RingEnabled = ArrayProbeOffset > 0;
            }
            catch (Exception ex)
            {
                RingEnabled = false;
                LastError = ex.GetType().Name;
            }

            return Describe();
        }

        // The lines waiting in the inbound ring ("seq|kind|..."), or null when there are none.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string[] ReadInboundLines()
        {
            int written = SimLinkNative.InboundRead(sBuffer, sBuffer.Length);
            if (written < 0)
            {
                int need = -written + 1;
                if (need > MaxBufferChars)
                {
                    RingErrors++;
                    RingLastError = "line of " + need.ToString(CultureInfo.InvariantCulture) + " chars";
                    return null;
                }

                sBuffer = new char[Math.Max(need, sBuffer.Length * 2)];
                written = SimLinkNative.InboundRead(sBuffer, sBuffer.Length);
            }

            if (written <= 0)
            {
                return null;
            }

            RingReads++;
            string[] parts = new string(sBuffer, 0, written).Split('\n');
            int count = parts.Length;
            while (count > 0 && parts[count - 1].Length == 0)
            {
                count--;
            }

            if (count != parts.Length)
            {
                string[] trimmed = new string[count];
                Array.Copy(parts, trimmed, count);
                parts = trimmed;
            }

            RingLines += parts.Length;
            return parts;
        }

        // Client shadow (sent by the client controller after registration): the bridge route still replays; the
        // sidecar compares it with the ring route.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string EnableClient()
        {
            try
            {
                if (RingEnabled)
                {
                    LastError = "ring-path-on";
                    return Describe();
                }

                NativeVersion = SimLinkNative.Version();
                ProbeOffset = NativeVersion > 0 ? SimLinkNative.Probe(ProbeText, ProbeText.Length) : -100;
                ClientEnabled = NativeVersion > 0 && ProbeOffset > 0;
            }
            catch (Exception ex)
            {
                ClientEnabled = false;
                LastError = ex.GetType().Name;
            }

            return Describe();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PollInbound()
        {
            InboundSeen += SimLinkNative.InboundPoll();
        }

        // The lines of one SACS_REPLAY batch, as decoded ("seq|kind|..."); synthetic seq-0 lines are skipped.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void NoteBridgeLines(string[] lines)
        {
            SimLinkNative.InboundPoll();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int split = line.IndexOf('|');
                int seq;
                if (split > 0 && int.TryParse(line.Substring(0, split), NumberStyles.None, CultureInfo.InvariantCulture, out seq) && seq > 0)
                {
                    SimLinkNative.NoteBridge(seq, line, line.Length);
                    BridgeNoted++;
                }
            }
        }

        // tier 1: the seed calls exist from native version 2. NativeVersion is set by whichever Enable the
        // controller sent (sidecar-on, push-on, client-ring-on, client-on); until then nothing may call them.
        internal static bool SeedsReady
        {
            get { return NativeVersion >= 2; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int HasNoPendingWork(uint driverHandle)
        {
            return SimLinkNative.SacsHasNoPendingWork(driverHandle);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int ReadSeed(uint driverHandle, int which)
        {
            return SimLinkNative.SacsReadSeed(driverHandle, which);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int WriteSeeds(uint driverHandle, int contextSeed, int spareSeed)
        {
            return SimLinkNative.SacsWriteSeeds(driverHandle, contextSeed, spareSeed);
        }

        internal static bool PicksReady
        {
            get { return NativeVersion >= 3; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int SetSwitches(int mask)
        {
            return SimLinkNative.SacsSetSwitches(mask);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int PatchState()
        {
            return SimLinkNative.SacsPatchState();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int PickCount()
        {
            return SimLinkNative.SacsPickCount();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int PickWord(int seq, int word)
        {
            return SimLinkNative.SacsPickWord(seq, word);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int PickCounter(int which)
        {
            return SimLinkNative.SacsPickCounter(which);
        }

        internal static bool OrderReady
        {
            get { return NativeVersion >= 4; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int OrderCount()
        {
            return SimLinkNative.SacsOrderCount();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int OrderWord(int seq, int word)
        {
            return SimLinkNative.SacsOrderWord(seq, word);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int OrderCounter(int which)
        {
            return SimLinkNative.SacsOrderCounter(which);
        }

        internal static bool RandomReady
        {
            get { return NativeVersion >= 5; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int RandomCounter(int which)
        {
            return SimLinkNative.SacsRandomCounter(which);
        }

        internal static bool RandomLogReady
        {
            get { return NativeVersion >= 6; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int RandomCount()
        {
            return SimLinkNative.SacsRandomCount();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int RandomWord(int seq, int word)
        {
            return SimLinkNative.SacsRandomWord(seq, word);
        }

        // the GC rate gate's calls exist from native version 7.
        internal static bool GcGateReady
        {
            get { return NativeVersion >= 7; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int GcCounter(int which)
        {
            return SimLinkNative.SacsGcCounter(which);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int GcInterval(int ms)
        {
            return SimLinkNative.SacsGcInterval(ms);
        }

        // the explicit-free ring's call exists from native version 8.
        internal static bool FreeRingReady
        {
            get { return NativeVersion >= 8; }
        }

        // the ring's layout 2 (each free's thread, the script thread) and its counters 6-8, from native version 9.
        internal static bool FreeRingThreadsReady
        {
            get { return NativeVersion >= 9; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int FreeRing(int which)
        {
            return SimLinkNative.SacsFreeRing(which);
        }

        // the chunk reserve's call exists from native version 10.
        internal static bool ChunkReserveReady
        {
            get { return NativeVersion >= 10; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int ChunkReserve(int which)
        {
            return SimLinkNative.SacsChunkReserve(which);
        }

        // the on-screen walk's call exists from native version 12.
        internal static bool OnScreenReady
        {
            get { return NativeVersion >= 12; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int OnScreen(int which)
        {
            return SimLinkNative.SacsOnScreen(which);
        }

        // option C: the walker counter calls exist from native version 13.
        internal static bool WalkersReady
        {
            get { return NativeVersion >= 13; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int WalkerRead(uint driverHandle, int index)
        {
            return SimLinkNative.SacsWalkerRead(driverHandle, index);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int WalkerWord(int word)
        {
            return SimLinkNative.SacsWalkerWord(word);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int WalkerWrite(uint driverHandle, int index, int spare, uint[] counts)
        {
            return SimLinkNative.SacsWalkerWrite(driverHandle, index, spare, unchecked((int)counts[0]), unchecked((int)counts[1]),
                unchecked((int)counts[2]), unchecked((int)counts[3]));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int WalkerCarry(uint oldHandle, uint newHandle, int spare)
        {
            return SimLinkNative.SacsWalkerCarry(oldHandle, newHandle, spare);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int WalkerCounter(int which)
        {
            return SimLinkNative.SacsWalkerCounter(which);
        }

        // The GC heap lever's call exists from native version 14.
        internal static bool GcHeapReady
        {
            get { return NativeVersion >= 14; }
        }

        internal static bool SequenceDrainReady
        {
            get { return NativeVersion >= 17; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int SequenceDrain(int which)
        {
            return SimLinkNative.SacsSequenceDrain(which);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int GcHeap(int which)
        {
            return SimLinkNative.SacsGcHeap(which);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Publish(long seq, string line, bool directSacs)
        {
            if (SimLinkNative.Publish(directSacs ? CallbackLane : PolledLane, (int)seq, line, line.Length) != 0)
            {
                Published++;
            }
            else
            {
                Dropped++;
            }
        }

        // Managed state only: safe whether or not the sidecar is registered.
        internal static string Describe()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "sidecar=" + (Enabled ? "on" : "off") + ",client=" + (ClientEnabled ? "on" : "off") + ",ring=" + (RingEnabled ? "on" : "off")
                + ",arrayOffset=" + ArrayProbeOffset.ToString(c) + ",ringRuns=" + RingRuns.ToString(c) + ",ringReads=" + RingReads.ToString(c) + ",ringLines=" + RingLines.ToString(c)
                + ",ringErrors=" + RingErrors.ToString(c) + ",ringLastError=" + RingLastError + ",inboundSeen=" + InboundSeen.ToString(c)
                + ",bridgeNoted=" + BridgeNoted.ToString(c) + ",version=" + NativeVersion.ToString(c) + ",probeOffset=" + ProbeOffset.ToString(c)
                + ",published=" + Published.ToString(c) + ",dropped=" + Dropped.ToString(c) + ",error=" + LastError;
        }
    }
}
