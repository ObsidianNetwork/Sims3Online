using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

// Experiment C: the SimLink sidecar's shared-memory layout (SimLink/src/simlink_layout.h) as the controller reads it.
internal static partial class Program
{
    // Off Windows the cases that call this are skipped: the runner prints SKIP and counts them apart from PASS. The simlink-windows
    // CI job runs them with --fail-on-skip.
    private static void SkipUnlessWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw Skipped("named memory maps require Windows");
        }
    }

    private static string RequireSimLinkSelftest()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw Skipped("simlink_selftest.exe is a Windows program");
        }

        return FindSimLinkSelftest() ?? throw Skipped("simlink_selftest.exe not built (python scripts/sidecar.py build)");
    }

    // A managed producer following the native layout, publishing more than the ring holds while the reader drains:
    // records wrap, pad records appear, every record arrives once and in order.
    [Test("SimLink.ReaderDrainsWrappedRing")]
    private static void SimLinkReaderDrainsWrappedRing()
    {
        SkipUnlessWindows();
        const int ringSize = 4096;
        string name = @"Local\SimsOnline.SimLink.test-" + Guid.NewGuid().ToString("N");
        using MemoryMappedFile file = MemoryMappedFile.CreateNew(name, SimLinkShadowReader.HeaderSize + ringSize);
        using MemoryMappedViewAccessor view = file.CreateViewAccessor();
        WriteTestHeader(view, ringSize);
        using SimLinkShadowReader reader = SimLinkShadowReader.TryOpenByName(name, out string reason)
            ?? throw new InvalidOperationException("open failed: " + reason);
        Assert(reader.RingSize == ringSize, "ring size from the header");

        var received = new List<SimLinkShadowReader.Record>();
        long write = 0;
        int pads = 0;
        for (int i = 1; i <= 500; i++)
        {
            string payload = i + "|T|" + new string('x', i % 97);
            int need = Align8(SimLinkShadowReader.RecordHeaderSize + payload.Length * 2);
            while (true)
            {
                long read = view.ReadInt64(56);
                int position = (int)(write & (ringSize - 1));
                int tail = ringSize - position;
                long total = need <= tail ? need : tail + need;
                if (write - read + total <= ringSize)
                {
                    if (need > tail)
                    {
                        view.Write(SimLinkShadowReader.HeaderSize + position, tail);
                        view.Write(SimLinkShadowReader.HeaderSize + position + 4, SimLinkShadowReader.KindPad);
                        write += tail;
                        position = 0;
                        pads++;
                    }

                    int at = SimLinkShadowReader.HeaderSize + position;
                    view.Write(at, need);
                    view.Write(at + 4, SimLinkShadowReader.KindData);
                    view.Write(at + 6, (ushort)1);
                    view.Write(at + 8, i);
                    view.Write(at + 12, payload.Length);
                    view.Write(at + 16, 1000L + i);
                    char[] chars = payload.ToCharArray();
                    view.WriteArray(at + SimLinkShadowReader.RecordHeaderSize, chars, 0, chars.Length);
                    write += need;
                    view.Write(48, write);
                    break;
                }

                reader.Drain(received);
            }
        }

        reader.Drain(received);
        Assert(received.Count == 500, $"500 records, got {received.Count}");
        Assert(pads > 0, "the test must wrap the ring");
        for (int i = 0; i < received.Count; i++)
        {
            int seq = i + 1;
            Assert(received[i].Seq == seq && received[i].Lane == 1 && received[i].Qpc == 1000L + seq, $"record {seq} fields");
            Assert(received[i].Payload == seq + "|T|" + new string('x', seq % 97), $"record {seq} payload");
        }
    }

    // The real native ring code (SimLink/build/Release/simlink_selftest.exe, x86) writing while this 64-bit reader
    // drains: header, framing, UTF-16 payloads and wrap-around across languages and bitness. Skipped when the
    // self-test hasn't been built (SimLink/README.md).
    [Test("SimLink.ReaderDrainsNativeSelftest")]
    private static void SimLinkReaderDrainsNativeSelftest()
    {
        string exe = RequireSimLinkSelftest();

        const int count = 3000;
        string name = @"Local\SimsOnline.SimLink.selftest-" + Guid.NewGuid().ToString("N");
        using var process = Process.Start(new ProcessStartInfo(exe, $"{name} {count} 16384 15")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("selftest did not start");
        try
        {
            string? ready = process.StandardOutput.ReadLine();
            Assert(ready == "ready", $"selftest ready line: {ready}");
            using SimLinkShadowReader reader = SimLinkShadowReader.TryOpenByName(name, out string reason)
                ?? throw new InvalidOperationException("open failed: " + reason);
            Assert(reader.QpcFrequency > 0 && reader.Pid == process.Id, "header pid and QPC frequency");
            var received = new List<SimLinkShadowReader.Record>();
            Stopwatch clock = Stopwatch.StartNew();
            while (received.Count < count && clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (reader.Drain(received) == 0)
                {
                    Thread.Sleep(1);
                }
            }

            Assert(received.Count == count, $"{count} records, got {received.Count}");
            for (int i = 0; i < count; i++)
            {
                int seq = i + 1;
                Assert(received[i].Seq == seq, $"record {seq} in order");
                Assert(received[i].Payload == seq + "|T|" + new string('x', seq % 97), $"record {seq} payload");
                Assert(received[i].Qpc > 0 && received[i].Qpc <= received[i].ArrivalQpc, $"record {seq} stamped before arrival");
            }

            Assert(reader.Published == count && reader.Contended == 0, $"native counters published={reader.Published} contended={reader.Contended}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    // The reverse direction (client shadow): this process writes with SimLinkShadowReader.Publish, the native consumer
    // (simlink_selftest.exe consume, x86) drains through a 16 KB ring and checks order and payloads.
    [Test("SimLink.NativeConsumerDrainsDotnetWriter")]
    private static void SimLinkNativeConsumerDrainsDotnetWriter()
    {
        string exe = RequireSimLinkSelftest();

        const int count = 3000;
        string name = @"Local\SimsOnline.SimLink.selftest-in-" + Guid.NewGuid().ToString("N");
        using var process = Process.Start(new ProcessStartInfo(exe, $"consume {name} {count} 16384 20")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("selftest did not start");
        try
        {
            string? ready = process.StandardOutput.ReadLine();
            Assert(ready == "ready", $"selftest ready line: {ready}");
            using SimLinkShadowReader writer = SimLinkShadowReader.TryOpenByName(name, out string reason)
                ?? throw new InvalidOperationException("open failed: " + reason);
            Stopwatch clock = Stopwatch.StartNew();
            for (int seq = 1; seq <= count && clock.Elapsed < TimeSpan.FromSeconds(15);)
            {
                string payload = seq + "|C|" + new string('y', seq % 89);
                if (writer.Publish(1, seq, payload, Stopwatch.GetTimestamp()))
                {
                    seq++;
                }
                else
                {
                    Thread.Sleep(0); // full: the consumer catches up
                }
            }

            string? result = process.StandardOutput.ReadLine();
            Assert(process.WaitForExit(20000) && process.ExitCode == 0, $"native consumer: {result} exit={(process.HasExited ? process.ExitCode : -1)}");
            Assert(result == $"consumed={count} bad=0", $"native consumer: {result}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    // The client ring path's early-stop: the native consumer takes at most 7 records per call and resumes on the next
    // (the game reads only what fits its buffer); 3,000 .NET-written records through 16 KB arrive in order and intact.
    [Test("SimLink.NativeChunkedConsumerResumes")]
    private static void SimLinkNativeChunkedConsumerResumes()
    {
        string exe = RequireSimLinkSelftest();

        const int count = 3000;
        string name = @"Local\SimsOnline.SimLink.selftest-chunk-" + Guid.NewGuid().ToString("N");
        using var process = Process.Start(new ProcessStartInfo(exe, $"consume-chunked {name} {count} 16384 20")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("selftest did not start");
        try
        {
            Assert(process.StandardOutput.ReadLine() == "ready", "selftest ready");
            using SimLinkShadowReader writer = SimLinkShadowReader.TryOpenByName(name, out string reason)
                ?? throw new InvalidOperationException("open failed: " + reason);
            Stopwatch clock = Stopwatch.StartNew();
            for (int seq = 1; seq <= count && clock.Elapsed < TimeSpan.FromSeconds(15);)
            {
                if (writer.Publish(1, seq, seq + "|C|" + new string('y', seq % 89), Stopwatch.GetTimestamp()))
                {
                    seq++;
                }
                else
                {
                    Thread.Sleep(0);
                }
            }

            string? result = process.StandardOutput.ReadLine();
            Assert(process.WaitForExit(20000) && process.ExitCode == 0 && result == $"consumed={count} bad=0", $"chunked consumer: {result}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    // A whole SimAnimation event or nothing: a batch that fits is written with each line's seq (0 for synthetic
    // lines); one that doesn't fit writes nothing at all.
    [Test("SimLink.BatchPublishIsAllOrNothing")]
    private static void SimLinkBatchPublishIsAllOrNothing()
    {
        SkipUnlessWindows();
        const int ringSize = 4096;
        string name = @"Local\SimsOnline.SimLink.test-batch-" + Guid.NewGuid().ToString("N");
        using MemoryMappedFile file = MemoryMappedFile.CreateNew(name, SimLinkShadowReader.HeaderSize + ringSize);
        using MemoryMappedViewAccessor view = file.CreateViewAccessor();
        WriteTestHeader(view, ringSize);
        using SimLinkShadowReader ring = SimLinkShadowReader.TryOpenByName(name, out string reason)
            ?? throw new InvalidOperationException("open failed: " + reason);

        var fits = new[] { "0|A|5|1|2|3|4|5", "41|E|5|1|2", "42|R|5|1|2|0" };
        Assert(ring.PublishBatch(1, fits, 7), "a small batch fits");
        var received = new List<SimLinkShadowReader.Record>();
        ring.Drain(received);
        Assert(received.Count == 3 && received[0].Seq == 0 && received[1].Seq == 41 && received[2].Seq == 42, "seqs from the line prefixes");
        Assert(received[2].Payload == "42|R|5|1|2|0", "payload is the whole line");

        string big = new string('z', 400); // ~824 bytes per record
        var tooMuch = Enumerable.Range(1, 6).Select(i => i + "|P|" + big).ToArray();
        long before = ring.WritePosition;
        Assert(!ring.PublishBatch(1, tooMuch, 7), "a batch larger than the free space is refused");
        Assert(ring.WritePosition == before, "nothing written for a refused batch");
        Assert(ring.PublishBatch(1, tooMuch.Take(3).ToArray(), 7), "a smaller batch still fits afterwards");
    }

    // The client ring path's batches: .NET writes batch k = (k % 5) + 1 lines with PublishBatches (each closed by a
    // marker), the native ReadBatch (simlink_selftest.exe consume-batches) must return exactly one whole batch per read.
    [Test("SimLink.NativeReadsOneBatchPerRead")]
    private static void SimLinkNativeReadsOneBatchPerRead()
    {
        string exe = RequireSimLinkSelftest();

        const int batches = 1500;
        string name = @"Local\SimsOnline.SimLink.selftest-batches-" + Guid.NewGuid().ToString("N");
        using var process = Process.Start(new ProcessStartInfo(exe, $"consume-batches {name} {batches} 16384 20")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("selftest did not start");
        try
        {
            Assert(process.StandardOutput.ReadLine() == "ready", "selftest ready");
            using SimLinkShadowReader writer = SimLinkShadowReader.TryOpenByName(name, out string reason)
                ?? throw new InvalidOperationException("open failed: " + reason);
            Stopwatch clock = Stopwatch.StartNew();
            for (int k = 1; k <= batches && clock.Elapsed < TimeSpan.FromSeconds(15);)
            {
                string[] lines = Enumerable.Range(1, (k % 5) + 1).Select(i => k + "|B|" + i).ToArray();
                if (writer.PublishBatches(new IReadOnlyList<string>[] { lines }, Stopwatch.GetTimestamp()))
                {
                    k++;
                }
                else
                {
                    Thread.Sleep(0);
                }
            }

            string? result = process.StandardOutput.ReadLine();
            Assert(process.WaitForExit(20000) && process.ExitCode == 0 && result == $"batches={batches} bad=0", $"batch consumer: {result}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    [Test("SimLink.ComparerMatchesDumpAgainstShadow")]
    private static void SimLinkComparerMatchesDumpAgainstShadow()
    {
        var comparer = new SimLinkShadowComparer(1000); // 1 tick = 1 ms
        comparer.AddShadow(new[]
        {
            new SimLinkShadowReader.Record(1, 10, 100, "R|5|1|2|0", 102),
            new SimLinkShadowReader.Record(1, 11, 101, "P|5|3|4", 103),
            new SimLinkShadowReader.Record(1, 12, 101, "P|9|9|9", 103), // a driver outside the targets
            new SimLinkShadowReader.Record(2, 14, 105, "Pz|1|2|3|4", 106), // polled during the dump: not timed
        });
        comparer.ObserveDump(new[] { "0|A|5|1|2|3|4|5", "10|R|5|1|2|0", "11|P|5|3|DIFFERENT", "13|E|5|1|2" }, 13, 150);
        Assert(comparer.DumpLines == 3, "synthetic seq-0 lines are not compared");
        Assert(comparer.Matched == 2 && comparer.PayloadMismatches == 1 && comparer.MissingInShadow == 1, comparer.Summary());
        comparer.ObserveDump(new[] { "14|Pz|1|2|3|4" }, 14, 150);
        Assert(comparer.Matched == 3 && comparer.MatchedPolled == 1 && comparer.PayloadMismatches == 1, comparer.Summary());
        Assert(comparer.Summary().Contains("/n:2", StringComparison.Ordinal), "only lane-1 records are timed: " + comparer.Summary());
        string summary = comparer.Summary();
        Assert(summary.Contains("source_to_shadow_ms=p50:", StringComparison.Ordinal) && summary.Contains("source_to_dump_ms=p50:", StringComparison.Ordinal), summary);
    }

    // Server ring path: lane-6 pages written into the ring reach the session's queue whole and in order, mirrored lines
    // (lanes 1/2) go to the comparer instead, a burst of pages is one wake, and leftovers can be discarded.
    [Test("SimLink.ServerPushPagesQueueInOrder")]
    private static void SimLinkServerPushPagesQueueInOrder()
    {
        SkipUnlessWindows();
        const int ringSize = 262144; // holds a full 60,000-char page (~120 KB)
        string name = @"Local\SimsOnline.SimLink.test-push-" + Guid.NewGuid().ToString("N");
        using MemoryMappedFile file = MemoryMappedFile.CreateNew(name, SimLinkShadowReader.HeaderSize + ringSize);
        using MemoryMappedViewAccessor view = file.CreateViewAccessor();
        WriteTestHeader(view, ringSize);
        using SimLinkShadowReader writer = SimLinkShadowReader.TryOpenByName(name, out string reason)
            ?? throw new InvalidOperationException("open failed: " + reason);
        SimLinkShadowReader reader = SimLinkShadowReader.TryOpenByName(name, out reason)
            ?? throw new InvalidOperationException("open failed: " + reason);
        using SimLinkShadowSession session = SimLinkShadowSession.ForTest(reader, mirror: true);
        session.UsePages();
        session.PushStarted();

        Assert(writer.Publish(1, 11, "P|7|4|5", 100), "mirrored line 11");
        Assert(writer.Publish(1, 12, "R|7|9|6|0|1", 101), "mirrored line 12");
        Assert(writer.Publish(6, 12, "seq=12;more=0\n11|P|7|4|5\n12|R|7|9|6|0|1", 102), "page 12");
        Assert(writer.Publish(6, 12, "seq=12;more=0", 103), "heartbeat page");
        Assert(writer.Publish(6, 13, "seq=13;more=0\n13|X|7", 104), "page 13");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (session.PagesReceived < 3 && DateTime.UtcNow < deadline)
        {
            session.WaitForPageAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None).GetAwaiter().GetResult();
        }

        Assert(session.PagesReceived == 3, "three pages queued, got " + session.PagesReceived);
        Assert(DateTime.UtcNow - session.LastPageUtc < TimeSpan.FromSeconds(5), "a page refreshes the liveness clock");
        var taken = new List<SimLinkShadowSession.Page>();
        while (session.TryTakePage(out SimLinkShadowSession.Page page))
        {
            taken.Add(page);
        }

        Assert(taken.Count == 3 && taken[0].Seq == 12 && taken[1].Seq == 12 && taken[2].Seq == 13, "pages in publish order");
        List<string> first = PresentationStream.ParseDump(taken[0].Text, out long seq0, out bool more0);
        List<string> beat = PresentationStream.ParseDump(taken[1].Text, out long seq1, out _);
        List<string> last = PresentationStream.ParseDump(taken[2].Text, out long seq2, out _);
        Assert(seq0 == 12 && !more0 && first.SequenceEqual(new[] { "11|P|7|4|5", "12|R|7|9|6|0|1" }), "a page parses like a dump reply");
        Assert(seq1 == 12 && beat.Count == 0, "a heartbeat is a header with no lines");
        Assert(seq2 == 13 && last.SequenceEqual(new[] { "13|X|7" }), "page 13");
        Assert(taken.All(p => p.ArrivalQpc >= p.PublishQpc), "arrival stamped after publish");

        session.ObservePage(first, seq0, taken[0].ArrivalQpc);
        session.ObservePage(last, seq2, taken[2].ArrivalQpc);
        SimLinkShadowComparer comparer = session.Comparer;
        Assert(comparer.Matched == 2 && comparer.PayloadMismatches == 0, "mirrored lines match the page: " + comparer.Summary());
        Assert(comparer.MissingInShadow == 1, "13 was never mirrored: " + comparer.Summary());
        Assert(comparer.Summary().Contains("source_to_page_ms=", StringComparison.Ordinal), comparer.Summary());
        Assert(!session.WaitForPageAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None).GetAwaiter().GetResult(),
            "a burst of three pages leaves no extra wakes");

        Assert(writer.Publish(6, 14, "seq=14;more=0", 105) && writer.Publish(6, 15, "seq=15;more=0", 106), "two leftover pages");
        Assert(session.DiscardQueuedPages() == 2 && !session.TryTakePage(out _), "leftovers discarded");

        // A full page (the mod's PushPageChars) as one record, wrapping the ring.
        var bigLines = Enumerable.Range(100, 1000).Select(i => i + "|P|7|" + new string('q', 54)).ToList();
        string bigPage = "seq=1099;more=1\n" + string.Join("\n", bigLines);
        Assert(bigPage.Length > 60000, "a page as large as the mod sends: " + bigPage.Length);
        for (int round = 0; round < 3; round++)
        {
            Assert(writer.Publish(6, 1099, bigPage, 107), "big page " + round);
            SimLinkShadowSession.Page big = default;
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!session.TryTakePage(out big) && DateTime.UtcNow < deadline)
            {
                session.WaitForPageAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None).GetAwaiter().GetResult();
            }

            List<string> parsed = PresentationStream.ParseDump(big.Text, out long bigSeq, out bool bigMore);
            Assert(bigSeq == 1099 && bigMore && parsed.SequenceEqual(bigLines), "big page " + round + " arrives whole");
        }
    }

    // Server ring path in the controller: with the switch on, the stream turns push on from the ring's end, sends the
    // targets instead of dumping, takes the pushed pages as the dump pages, and after ServerPushSilence without a
    // page switches push off and dumps again from the last page's cursor. After ServerPushRearmDelay it turns push on
    // again from the end of the last dump; a push that fails again quickly doubles the delay.
    [Test("SimLink.SessionServerPushReplacesDumpsAndRearms")]
    private static void AuthoritativeSessionServerPushReplacesDumps()
    {
        SkipUnlessWindows();
        const int ringSize = 65536;
        string name = @"Local\SimsOnline.SimLink.test-pushsession-" + Guid.NewGuid().ToString("N");
        using MemoryMappedFile file = MemoryMappedFile.CreateNew(name, SimLinkShadowReader.HeaderSize + ringSize);
        using MemoryMappedViewAccessor view = file.CreateViewAccessor();
        WriteTestHeader(view, ringSize);
        using SimLinkShadowReader writer = SimLinkShadowReader.TryOpenByName(name, out string reason)
            ?? throw new InvalidOperationException("open failed: " + reason);
        SimLinkShadowReader reader = SimLinkShadowReader.TryOpenByName(name, out reason)
            ?? throw new InvalidOperationException("open failed: " + reason);
        using SimLinkShadowSession pages = SimLinkShadowSession.ForTest(reader, mirror: false);

        InMemoryAuthoritativeHost host = new InMemoryAuthoritativeHost();
        AuthoritativeSession session = new AuthoritativeSession(host) { SimLinkSessionOverride = pages };
        string fromNow = PresentationStream.FromNowCursor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool pushOn = false;
        Queue<string> dumpReplies = new Queue<string>();
        host.SacsRecorderHandler = args => args[0] switch
        {
            "ring-on" => "recorder=on;ring=on",
            "push-on" => (pushOn = true) ? "push=on,cursor=" + args[1] : null,
            "dump" when !pushOn && dumpReplies.Count > 0 => dumpReplies.Dequeue(),
            "push-off" => (pushOn = false) ? null : "push=off",
            "push-targets" => "push=" + (pushOn ? "on" : "off"),
            "dump" when pushOn => null, // the mod refuses dumps while pushing
            "dump" when args[1] == fromNow => "seq=10;more=0\n",
            "dump" => "seq=" + args[1] + ";more=0\n",
            "snapshot" => "drivers=7",
            "snapshot-driver" => "lines=1;next=-1\n0|A|7|1|1|2|3|0",
            _ => null
        };
        List<ServerEvent> sent = new List<ServerEvent>();
        Func<IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueue = (events, _) => { sent.AddRange(events); return Task.FromResult(true); };
        SimLinkShadowSession.ServerRingOverride = true;
        try
        {
            Assert(session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult() == 0, "Nobody claimed: nothing streamed.");
            Assert(host.SacsRecorderCalls.SequenceEqual(new[] { "ring-on", "push-off", "dump " + fromNow, "push-on 10 -" }),
                "Push starts from the ring's end. Got " + string.Join(" / ", host.SacsRecorderCalls));
            Assert(session.ServerPushActive, "push on");

            session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 1, "alice"), CancellationToken.None).GetAwaiter().GetResult();
            session.HandleCommandAsync(new ClientCommand.ClaimSim(Guid.NewGuid(), 1, 1uL), CancellationToken.None).GetAwaiter().GetResult();
            SimulationSnapshot index = new SimulationSnapshot(5L, "W", "S", GameSpeed.Normal, new List<SimSnapshot>
            {
                new SimSnapshot(1uL, "Owner", 0uL, new Vector3Dto(0f, 0f, 0f), null, 1, null, 1000uL),
                new SimSnapshot(2uL, "Visitor", 0uL, new Vector3Dto(0f, 0f, 0f), null, null, null, 1000uL)
            }, Array.Empty<ObjectSnapshot>());
            session.UpdateInterestLotIndex(index);
            host.SacsRecorderCalls.Clear();
            Assert(writer.Publish(6, 12, "seq=12;more=0\n11|P|7|4|5\n12|R|7|9|6|0|1", 1), "page 12");
            session.WaitForPresentationWorkAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            int lines = session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            Assert(host.SacsRecorderCalls.SequenceEqual(new[] { "push-targets 1,2", "snapshot 1,2", "snapshot-driver 7 0" }),
                "Targets, then history; no dump while pushing. Got " + string.Join(" / ", host.SacsRecorderCalls));
            Assert(lines == 3 && sent.Count == 1 && sent[0] is ServerEvent.SimAnimation animation
                && animation.Lines.SequenceEqual(new[] { "0|A|7|1|1|2|3|0", "11|P|7|4|5", "12|R|7|9|6|0|1" }), "History then the pushed page's lines.");

            host.SacsRecorderCalls.Clear();
            session.ServerPushSilence = TimeSpan.FromMilliseconds(200);
            session.ServerPushRearmDelay = TimeSpan.FromMilliseconds(600);
            Thread.Sleep(500); // well past the 200 ms silence, also on a loaded test run
            session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            Assert(host.SacsRecorderCalls.SequenceEqual(new[] { "push-off", "dump 12 1,2" }),
                "Silence: push off, dumps resume from the last page's cursor. Got " + string.Join(" / ", host.SacsRecorderCalls));
            Assert(!session.ServerPushActive && session.ServerPushFallbacks == 1, "one fallback");

            // Re-arm: dumps until the delay has passed, then push-on from the end of that tick's dump.
            host.SacsRecorderCalls.Clear();
            dumpReplies.Enqueue("seq=14;more=0\n13|X|7\n14|P|7|1|1");
            session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            Assert(host.SacsRecorderCalls.SequenceEqual(new[] { "dump 12 1,2" }), "Within the delay: dumps only. Got " + string.Join(" / ", host.SacsRecorderCalls));
            Thread.Sleep(900);
            host.SacsRecorderCalls.Clear();
            session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            Assert(host.SacsRecorderCalls.SequenceEqual(new[] { "dump 14 1,2", "push-on 14 1,2" }),
                "After the delay: this tick's dump, then push from where it ended. Got " + string.Join(" / ", host.SacsRecorderCalls));
            Assert(session.ServerPushActive && session.ServerPushRearms == 1, "re-armed once");

            sent.Clear();
            host.SacsRecorderCalls.Clear();
            Assert(writer.Publish(6, 16, "seq=16;more=0\n16|R|7|9|6|0|1", 2), "page 16");
            session.WaitForPresentationWorkAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            Assert(host.SacsRecorderCalls.Count == 0 && sent.Count == 1 && sent[0] is ServerEvent.SimAnimation again
                && again.Lines.SequenceEqual(new[] { "16|R|7|9|6|0|1" }), "Pages again after the re-arm, no dump. Got " + string.Join(" / ", host.SacsRecorderCalls));

            // Failing again right away: the next re-arm waits twice as long.
            Thread.Sleep(500);
            host.SacsRecorderCalls.Clear();
            session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            Assert(host.SacsRecorderCalls.SequenceEqual(new[] { "push-off", "dump 16 1,2" }) && session.ServerPushFallbacks == 2,
                "Second fallback. Got " + string.Join(" / ", host.SacsRecorderCalls));
            Thread.Sleep(800); // past 600 ms, short of 1,200 ms
            host.SacsRecorderCalls.Clear();
            session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            Assert(host.SacsRecorderCalls.SequenceEqual(new[] { "dump 16 1,2" }), "Backoff doubled: no re-arm yet. Got " + string.Join(" / ", host.SacsRecorderCalls));
            Thread.Sleep(700);
            host.SacsRecorderCalls.Clear();
            session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            Assert(host.SacsRecorderCalls.SequenceEqual(new[] { "dump 16 1,2", "push-on 16 1,2" }) && session.ServerPushRearms == 2,
                "Re-armed after the doubled delay. Got " + string.Join(" / ", host.SacsRecorderCalls));
        }
        finally
        {
            SimLinkShadowSession.ServerRingOverride = null;
        }
    }

    // Client ring backlog: the writer's claim takes every unread record once (lines and markers, in order), leaves the
    // read position at the write position, and a consumer that comes afterwards finds nothing.
    [Test("SimLink.ClaimTakesUnreadOnce")]
    private static void SimLinkClaimTakesUnreadOnce()
    {
        SkipUnlessWindows();
        const int ringSize = 4096;
        string name = @"Local\SimsOnline.SimLink.test-claim-" + Guid.NewGuid().ToString("N");
        using MemoryMappedFile file = MemoryMappedFile.CreateNew(name, SimLinkShadowReader.HeaderSize + ringSize);
        using MemoryMappedViewAccessor view = file.CreateViewAccessor();
        WriteTestHeader(view, ringSize);
        using SimLinkShadowReader writer = SimLinkShadowReader.TryOpenByName(name, out string reason)
            ?? throw new InvalidOperationException("open failed: " + reason);
        using SimLinkShadowReader consumer = SimLinkShadowReader.TryOpenByName(name, out reason)
            ?? throw new InvalidOperationException("open failed: " + reason);

        Assert(writer.PublishBatches(new IReadOnlyList<string>[] { new[] { "1|B|1", "1|B|2" }, new[] { "2|B|1" } }, 9), "two batches");
        var first = new List<SimLinkShadowReader.Record>();
        List<SimLinkShadowReader.Record> claimed = writer.ClaimUnread();
        Assert(claimed.Count == 5, "3 lines + 2 markers, got " + claimed.Count);
        Assert(claimed.Where(r => r.Lane == SimLinkShadowReader.LaneReplayLine).Select(r => r.Payload).SequenceEqual(new[] { "1|B|1", "1|B|2", "2|B|1" }), "lines in order");
        Assert(writer.ReadPosition == writer.WritePosition, "the claim leaves nothing unread");
        Assert(writer.ClaimUnread().Count == 0 && consumer.Drain(first) == 0, "nothing left for a second claim or the consumer");
        Assert(writer.PublishBatches(new IReadOnlyList<string>[] { new[] { "3|B|1" } }, 9) && consumer.Drain(first) == 2, "later records still flow");
    }

    // The native side of the claim (simlink_selftest.exe claim-race): a read the writer claims mid-way commits nothing
    // and returns nothing. Skipped when the self-test hasn't been built (SimLink/README.md).
    [Test("SimLink.NativeClaimRace")]
    private static void SimLinkNativeClaimRace()
    {
        string selftest = RequireSimLinkSelftest();

        using Process process = Process.Start(new ProcessStartInfo(selftest, "claim-race") { RedirectStandardOutput = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("selftest did not start");
        string output = process.StandardOutput.ReadToEnd();
        Assert(process.WaitForExit(10000) && process.ExitCode == 0 && output.Contains("claim-race ok", StringComparison.Ordinal), "claim-race: " + output);
    }

    // item 3 (simlink_selftest.exe keyed): SimLink's keyed Random draw computes the key and value that
    // scripts/sacs_random.py computes for the same fields. Skipped when the self-test hasn't been built.
    [Test("SimLink.NativeKeyedRandomVectors")]
    private static void SimLinkNativeKeyedRandomVectors()
    {
        string selftest = RequireSimLinkSelftest();

        using Process process = Process.Start(new ProcessStartInfo(selftest, "keyed") { RedirectStandardOutput = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("selftest did not start");
        string output = process.StandardOutput.ReadToEnd();
        Assert(process.WaitForExit(10000) && process.ExitCode == 0 && output.Contains("keyed ok", StringComparison.Ordinal), "keyed: " + output);
    }

    // option C (simlink_selftest.exe walkers): SimLink's rules for copied walker counters (where a set goes, the
    // pending table, the residual window, the re-resolve carry's pick, the handle table). Skipped when the self-test
    // hasn't been built.
    [Test("SimLink.NativeWalkerCopyLogic")]
    private static void SimLinkNativeWalkerCopyLogic()
    {
        string selftest = RequireSimLinkSelftest();

        using Process process = Process.Start(new ProcessStartInfo(selftest, "walkers") { RedirectStandardOutput = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("selftest did not start");
        string output = process.StandardOutput.ReadToEnd();
        Assert(process.WaitForExit(10000) && process.ExitCode == 0 && output.Contains("walkers ok", StringComparison.Ordinal), "walkers: " + output);
    }

    [Test("SimLink.NativePickFallbackLogic")]
    private static void SimLinkNativePickFallbackLogic()
    {
        string selftest = RequireSimLinkSelftest();

        using Process process = Process.Start(new ProcessStartInfo(selftest, "pickfallback") { RedirectStandardOutput = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("selftest did not start");
        string output = process.StandardOutput.ReadToEnd();
        Assert(process.WaitForExit(10000) && process.ExitCode == 0 && output.Contains("pickfallback ok", StringComparison.Ordinal), "pickfallback: " + output);
    }

    [Test("SimLink.NativeSiteDigestLogic")]
    private static void SimLinkNativeSiteDigestLogic()
    {
        string selftest = RequireSimLinkSelftest();

        using Process process = Process.Start(new ProcessStartInfo(selftest, "sitedigest") { RedirectStandardOutput = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("selftest did not start");
        string output = process.StandardOutput.ReadToEnd();
        Assert(process.WaitForExit(10000) && process.ExitCode == 0 && output.Contains("sitedigest ok", StringComparison.Ordinal), "sitedigest: " + output);
    }

    // Client ring stuck: Reclaim stops routing and returns every line the game hasn't read, in order; later events
    // take the bridge.
    [Test("SimLink.ClientReclaimsStuckBacklog")]
    private static void SimLinkClientReclaimsStuckBacklog()
    {
        SkipUnlessWindows();
        const int ringSize = 4096;
        string inName = @"Local\SimsOnline.SimLink.test-in-" + Guid.NewGuid().ToString("N");
        string outName = @"Local\SimsOnline.SimLink.test-out-" + Guid.NewGuid().ToString("N");
        using MemoryMappedFile inFile = MemoryMappedFile.CreateNew(inName, SimLinkShadowReader.HeaderSize + ringSize);
        using MemoryMappedFile outFile = MemoryMappedFile.CreateNew(outName, SimLinkShadowReader.HeaderSize + ringSize);
        using MemoryMappedViewAccessor inView = inFile.CreateViewAccessor();
        using MemoryMappedViewAccessor outView = outFile.CreateViewAccessor();
        WriteTestHeader(inView, ringSize);
        WriteTestHeader(outView, ringSize);
        SimLinkShadowReader inbound = SimLinkShadowReader.TryOpenByName(inName, out string reason) ?? throw new InvalidOperationException(reason);
        SimLinkShadowReader outbound = SimLinkShadowReader.TryOpenByName(outName, out reason) ?? throw new InvalidOperationException(reason);
        var log = new List<string>();
        using SimLinkClientShadowSession session = SimLinkClientShadowSession.ForTest(outbound, inbound, log.Add);

        Assert(session.TryRoute(new[] { "5|R|1|2|0", "6|P|1|2" }) && session.TryRoute(new[] { "7|X|1" }), "two events into the ring");
        Assert(session.Reclaim() == 3 && session.Stuck, "three unread lines reclaimed");
        List<string>? reclaimed = session.TakeReclaimedLines();
        Assert(reclaimed != null && reclaimed.SequenceEqual(new[] { "5|R|1|2|0", "6|P|1|2", "7|X|1" }), "in order, markers left out");
        Assert(!session.TryRoute(new[] { "8|X|1" }) && session.TakeReclaimedLines() == null, "later events take the bridge; nothing reclaimed twice");
        Assert(session.LinesReclaimed == 3 && session.LinesReclaimedSent == 3, "counted");
    }

    // Client ring re-arm: after the delay (and once the backlog went to the bridge) the mod's ring path is switched on
    // again and events route through the ring; stuck again right away doubles the delay; a refusal keeps the bridge.
    [Test("SimLink.ClientRingRearms")]
    private static void SimLinkClientRingRearms()
    {
        SkipUnlessWindows();
        const int ringSize = 4096;
        string inName = @"Local\SimsOnline.SimLink.test-rin-" + Guid.NewGuid().ToString("N");
        string outName = @"Local\SimsOnline.SimLink.test-rout-" + Guid.NewGuid().ToString("N");
        using MemoryMappedFile inFile = MemoryMappedFile.CreateNew(inName, SimLinkShadowReader.HeaderSize + ringSize);
        using MemoryMappedFile outFile = MemoryMappedFile.CreateNew(outName, SimLinkShadowReader.HeaderSize + ringSize);
        using MemoryMappedViewAccessor inView = inFile.CreateViewAccessor();
        using MemoryMappedViewAccessor outView = outFile.CreateViewAccessor();
        WriteTestHeader(inView, ringSize);
        WriteTestHeader(outView, ringSize);
        SimLinkShadowReader inbound = SimLinkShadowReader.TryOpenByName(inName, out string reason) ?? throw new InvalidOperationException(reason);
        SimLinkShadowReader outbound = SimLinkShadowReader.TryOpenByName(outName, out reason) ?? throw new InvalidOperationException(reason);
        var sent = new List<string>();
        bool modAccepts = true;
        Func<string, CancellationToken, Task<string?>> send = (subcommand, _) =>
        {
            sent.Add(subcommand);
            return Task.FromResult<string?>(modAccepts ? "sidecar=off,client=off,ring=on" : "sidecar=off,client=off,ring=off");
        };
        using SimLinkClientShadowSession session = SimLinkClientShadowSession.ForTest(outbound, inbound, _ => { }, send);
        using SimLinkShadowReader game = SimLinkShadowReader.TryOpenByName(inName, out reason) ?? throw new InvalidOperationException(reason);
        session.RingRearmDelay = TimeSpan.FromMilliseconds(300);

        // A quiet world: no animation event carries the backlog to the bridge, so the re-arm puts it in the ring.
        Assert(session.TryRoute(new[] { "5|R|1|2|0" }) && session.Reclaim() == 1, "stuck with one line");
        Assert(!session.MaybeRearmAsync(CancellationToken.None).GetAwaiter().GetResult(), "not before the delay");
        Thread.Sleep(400);
        Assert(session.MaybeRearmAsync(CancellationToken.None).GetAwaiter().GetResult() && sent.SequenceEqual(new[] { "sidecar-client-ring" }), "re-armed with the backlog held");
        var read = new List<SimLinkShadowReader.Record>();
        game.Drain(read);
        Assert(read.Where(r => r.Lane == SimLinkShadowReader.LaneReplayLine).Select(r => r.Payload).SequenceEqual(new[] { "5|R|1|2|0" }),
            "the held backlog went into the ring");
        Assert(session.TakeReclaimedLines() == null && session.LinesReclaimedSent == 1, "carried once");
        Assert(!session.Stuck && session.RingRearms == 1 && session.TryRoute(new[] { "6|R|1|2|0" }), "events take the ring again");

        Assert(session.Reclaim() == 1 && session.Stuck && session.TakeReclaimedLines()!.Count == 1, "stuck again right away");
        Thread.Sleep(400); // past 300 ms, short of the doubled 600 ms
        Assert(!session.MaybeRearmAsync(CancellationToken.None).GetAwaiter().GetResult(), "the delay doubled");
        Thread.Sleep(400);
        modAccepts = false;
        Assert(!session.MaybeRearmAsync(CancellationToken.None).GetAwaiter().GetResult() && session.Stuck && !session.TryRoute(new[] { "7|X|1" }),
            "a refused re-arm stays on the bridge");
        Assert(sent.Count == 2, "one more try, refused");
    }

    // Server ring path pacing: with a minimum interval, pages that arrive within it of the last broadcast are held and
    // go out together; the first page after a quiet spell is not held.
    [Test("SimLink.SessionPacesPushedPages")]
    private static void AuthoritativeSessionPacesPushedPages()
    {
        SkipUnlessWindows();
        const int ringSize = 65536;
        string name = @"Local\SimsOnline.SimLink.test-pacing-" + Guid.NewGuid().ToString("N");
        using MemoryMappedFile file = MemoryMappedFile.CreateNew(name, SimLinkShadowReader.HeaderSize + ringSize);
        using MemoryMappedViewAccessor view = file.CreateViewAccessor();
        WriteTestHeader(view, ringSize);
        using SimLinkShadowReader writer = SimLinkShadowReader.TryOpenByName(name, out string reason)
            ?? throw new InvalidOperationException("open failed: " + reason);
        SimLinkShadowReader reader = SimLinkShadowReader.TryOpenByName(name, out reason)
            ?? throw new InvalidOperationException("open failed: " + reason);
        using SimLinkShadowSession pages = SimLinkShadowSession.ForTest(reader, mirror: false);

        InMemoryAuthoritativeHost host = new InMemoryAuthoritativeHost();
        AuthoritativeSession session = new AuthoritativeSession(host)
        {
            SimLinkSessionOverride = pages,
            PresentationMinIntervalOverride = TimeSpan.FromMilliseconds(400)
        };
        string fromNow = PresentationStream.FromNowCursor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        host.SacsRecorderHandler = args => args[0] switch
        {
            "ring-on" => "recorder=on;ring=on",
            "push-on" => "push=on,cursor=" + args[1],
            "push-off" => "push=off",
            "push-targets" => "push=on",
            "dump" when args[1] == fromNow => "seq=10;more=0\n",
            "snapshot" => "drivers=-",
            _ => null
        };
        List<ServerEvent> sent = new List<ServerEvent>();
        Func<IReadOnlyList<ServerEvent>, CancellationToken, Task<bool>> enqueue = (events, _) => { sent.AddRange(events); return Task.FromResult(true); };
        SimLinkShadowSession.ServerRingOverride = true;
        try
        {
            session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 1, "alice"), CancellationToken.None).GetAwaiter().GetResult();
            session.HandleCommandAsync(new ClientCommand.ClaimSim(Guid.NewGuid(), 1, 1uL), CancellationToken.None).GetAwaiter().GetResult();
            session.UpdateInterestLotIndex(new SimulationSnapshot(5L, "W", "S", GameSpeed.Normal, new List<SimSnapshot>
            {
                new SimSnapshot(1uL, "Owner", 0uL, new Vector3Dto(0f, 0f, 0f), null, 1, null, 1000uL)
            }, Array.Empty<ObjectSnapshot>()));
            Assert(session.ServerPushActive, "push on");

            var clock = Stopwatch.StartNew();
            Assert(writer.Publish(6, 11, "seq=11;more=0\n11|P|7|1|1", 1), "page 11");
            session.WaitForPresentationWorkAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            Assert(clock.ElapsedMilliseconds < 300, "the first page after a quiet spell is not held: " + clock.ElapsedMilliseconds + " ms");
            session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            Assert(sent.Count == 1, "page 11 broadcast");

            clock.Restart();
            Assert(writer.Publish(6, 12, "seq=12;more=0\n12|P|7|2|2", 2) && writer.Publish(6, 13, "seq=13;more=0\n13|P|7|3|3", 3), "pages 12 and 13");
            session.WaitForPresentationWorkAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            Assert(clock.ElapsedMilliseconds >= 300, "held until the interval since the last broadcast: " + clock.ElapsedMilliseconds + " ms");
            session.PublishPresentationAsync(enqueue, CancellationToken.None).GetAwaiter().GetResult();
            Assert(sent.Count == 2 && sent[1] is ServerEvent.SimAnimation together && together.Lines.SequenceEqual(new[] { "12|P|7|2|2", "13|P|7|3|3" }),
                "both held pages in one broadcast");

            session.PresentationMinIntervalOverride = TimeSpan.Zero;
            clock.Restart();
            Assert(writer.Publish(6, 14, "seq=14;more=0\n14|P|7|4|4", 4), "page 14");
            session.WaitForPresentationWorkAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            Assert(clock.ElapsedMilliseconds < 300, "no interval: no hold: " + clock.ElapsedMilliseconds + " ms");
        }
        finally
        {
            SimLinkShadowSession.ServerRingOverride = null;
        }
    }

    // The client's dispatcher merges SimAnimation events in a row into one dispatch (in order around other events), and
    // still reports every raw line to afterLine (the client ring's in-flight count depends on it).
    [Test("MirrorCoordinator.MergesAnimationRuns")]
    private static void MirrorCoordinatorMergesAnimationRuns()
    {
        var dispatched = new List<string>();
        var after = new List<string>();
        Task<bool> Dispatch(MirrorEngine? engine, string line, bool json, CancellationToken token, Func<ClientCommand, Task> send)
        {
            dispatched.Add(line);
            return Task.FromResult(false);
        }

        string Animation(params string[] lines) => PublicProtocolCodec.EncodeEvent(new ServerEvent.SimAnimation(lines.ToList()));
        string a1 = Animation("1|R|5|1|2|0"), a2 = Animation("2|P|5|3|4"), a3 = Animation("4|X|5");
        string delta = MakeSnapshotDeltaLine(5, 42uL, 5);
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        foreach (string line in new[] { a1, a2, delta, a3 })
        {
            channel.Writer.TryWrite(line);
        }

        channel.Writer.Complete();
        var coordinator = new MirrorCoordinator(new CoordinatorConfig(SelfPlayerId: 1, MaxFoldPerCycle: 10, ResyncCooldown: TimeSpan.FromSeconds(60)));
        coordinator.RunAsync(channel.Reader, null, false, CancellationToken.None, _ => Task.CompletedTask, Dispatch,
            afterLine: (line, _) => { after.Add(line); return Task.FromResult(false); }).GetAwaiter().GetResult();

        Assert(dispatched.Count == 3, "two animations merged, then the delta, then the last animation: " + dispatched.Count);
        Assert(PublicProtocolCodec.DecodeEvent(dispatched[0]) is ServerEvent.SimAnimation merged
            && merged.Lines.SequenceEqual(new[] { "1|R|5|1|2|0", "2|P|5|3|4" }), "the run merged in order");
        Assert(PublicProtocolCodec.DecodeEvent(dispatched[1]) is ServerEvent.SnapshotDelta && dispatched[2] == a3, "order kept around the delta");
        Assert(after.SequenceEqual(new[] { a1, a2, delta, a3 }), "afterLine for every raw line, in order");
    }

    private static void WriteTestHeader(MemoryMappedViewAccessor view, int ringSize)
    {
        byte[] magic = "SIMLINK1"u8.ToArray();
        view.WriteArray(0, magic, 0, magic.Length);
        view.Write(8, SimLinkShadowReader.LayoutVersion);
        view.Write(12, SimLinkShadowReader.HeaderSize);
        view.Write(16, Environment.ProcessId);
        view.Write(28, SimLinkShadowReader.HeaderSize);
        view.Write(32, ringSize);
        view.Write(40, Stopwatch.Frequency);
    }

    private static int Align8(int value) => (value + 7) & ~7;

    private static string? FindSimLinkSelftest()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "SimLink", "build", "Release", "simlink_selftest.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
