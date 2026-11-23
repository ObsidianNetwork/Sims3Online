using System.Globalization;
using System.Text;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.Client;

/// <summary>
/// One v1 socket's correlation/id map and receive assembly. Completed records enter the existing
/// mirror queue in its internal JSON form; JSON sockets never pass through this adapter.
/// The caller serializes EncodeCommand together with the socket write.
/// </summary>
internal sealed class PublicWireConnection
{
    private readonly object gate = new();
        private PublicLineCodecV1.CommandIds ids = new();
    private readonly Dictionary<Guid, ulong> correlations = new();
    private readonly Dictionary<ulong, Guid> commands = new();
    private sealed class Delivery
    {
        public ServerEvent? Value;
        public bool Complete;
            public ulong Seq;
    }
    private sealed class AckBulk
    {
        public required Delivery Owner;
        public ulong Next;
        public ulong Count;
        public long Bytes;
    }
        private Func<string, (bool Success, IReadOnlyList<PublicLineCodecV1.EventEnvelope> Events, string? Error)> receiveEvent;
    private readonly Queue<Delivery> pending = new();
    private readonly Dictionary<ulong, Delivery> eventOwners = new();
    private readonly Dictionary<ulong, Delivery> eventBulkOwners = new();
    private readonly Dictionary<ulong, AckBulk> ackOwners = new();
        private readonly Queue<Delivery> ready = new();
    private readonly Action<string>? diagnostic;
    private ulong nextCorr;
    private ulong lastBulkId;
        public uint? Epoch { get; private set; }
        public string? ResumeToken { get; private set; }
        public ulong LastDeliveredSeq { get; private set; }
        public PublicLineCodecV1.ResumeFrame? Resume { get; private set; }
        private bool cursorBlocked;
    private const long MaxBulkBytes = 256 * 1024 * 1024;
    private const int MaxInFlightBulks = 16;
    private const int MaxPendingEvents = 1024;

    public PublicWireConnection(Action<string>? diagnostic = null)
    {
        this.diagnostic = diagnostic;
            receiveEvent = NewReceiver();
        }

        private Func<string, (bool Success, IReadOnlyList<PublicLineCodecV1.EventEnvelope> Events, string? Error)> NewReceiver()
        {
            PublicLineCodecV1.EventReceiver receiver = new(Resolve);
            return line =>
        {
            bool success = receiver.TryReceive(line, out var events, out string? error);
            return (success, events, error);
        };
    }

    internal PublicWireConnection(Action<string>? diagnostic,
        Func<string, (bool Success, IReadOnlyList<PublicLineCodecV1.EventEnvelope> Events, string? Error)> receiveEvent)
        : this(diagnostic)
    {
        this.receiveEvent = receiveEvent;
    }

    public static string ValidateMode(string mode) => mode is "json" or "line"
        ? mode : throw new ArgumentException("--wire must be json or line.");

        public static string ClientCaps(string role = "client", string? resumeCursor = null, string? resumeToken = null)
    {
        string declaredRole = Environment.GetEnvironmentVariable("SIMSONLINE_ROLE") ?? role;
        if (declaredRole is not ("client" or "client2" or "client3")) declaredRole = "client";
            if ((resumeCursor == null) != (resumeToken == null)
                || (resumeToken != null && !PublicLineCodecV1.ValidResumeToken(resumeToken)))
                throw new FormatException("bad resume credential");
            if (resumeCursor != null)
            {
                string[] cursor = resumeCursor.Split('.');
                if (cursor.Length != 2 || !uint.TryParse(cursor[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                    || !ulong.TryParse(cursor[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw new FormatException("bad resume cursor");
            }
            return "ring=" + (Environment.GetEnvironmentVariable("SIMSONLINE_SIMLINK_CLIENT_RING") == "1" ? "1" : "0")
            + ",wfs=" + (Environment.GetEnvironmentVariable("SIMSONLINE_WORLD_FROM_SERVER") == "1" ? "1" : "0")
                + ",role=" + declaredRole
                + (resumeCursor == null ? "" : ",resume=" + resumeCursor + ",resume_token=" + resumeToken);
    }

        public sealed record HandshakeResult(PublicLineCodecV1.WelcomeFrame Welcome, PublicLineCodecV1.ResumeFrame? Resume);

        public static async Task<HandshakeResult> HandshakeAsync(Stream stream, StreamReader reader, string role, CancellationToken token,
            string? resumeCursor = null, string? resumeToken = null)
    {
            byte[] hello = Encoding.UTF8.GetBytes(PublicLineCodecV1.EncodeHello(ClientCaps(role, resumeCursor, resumeToken)) + "\n");
        await stream.WriteAsync(hello, token);
        await stream.FlushAsync(token);
        string response = await reader.ReadLineAsync(token) ?? throw new IOException("Server closed before WELCOME.");
        if (response.StartsWith("ERR|", StringComparison.Ordinal))
        {
            PublicLineCodecV1.ErrorFrame error = PublicLineCodecV1.DecodeError(response);
            throw new IOException(PublicLineCodecV1.RefusalToken(error.Code) + ": " + PublicLineCodecV1.HexDecode(error.HexText));
        }
        PublicLineCodecV1.WelcomeFrame welcome = PublicLineCodecV1.DecodeWelcome(response);
        if (welcome.Version != PublicLineCodecV1.PUBLIC_PROTOCOL_VERSION)
            throw new IOException("version_unsupported");
            PublicLineCodecV1.ResumeFrame? resume = resumeCursor == null ? null
                : PublicLineCodecV1.DecodeResume(await reader.ReadLineAsync(token)
                    ?? throw new IOException("Server closed before resume result."));
            if (resume != null && (resume.Epoch != welcome.Epoch
                || (resume is PublicLineCodecV1.ResumeOkFrame ok
                    && (EpochFromCursor(resumeCursor!) != welcome.Epoch
                        || ok.FromSeq != checked(SeqFromCursor(resumeCursor!) + 1)))))
                throw new IOException("bad resume result");
            return new(welcome, resume);
    }

        private static uint EpochFromCursor(string cursor) => uint.Parse(cursor.Split('.')[0], CultureInfo.InvariantCulture);
        private static ulong SeqFromCursor(string cursor) => ulong.Parse(cursor.Split('.')[1], CultureInfo.InvariantCulture);

        /// <summary>Reuse this instance and the caller's mirror only when this returns true.
        /// Call with no concurrent reader/writer; an unjoined world-fetch uses its own instance.</summary>
        public async Task<bool> ConnectAsync(Stream stream, StreamReader reader, string role, CancellationToken token)
        {
            string? cursor = Epoch.HasValue && ResumeToken != null && !cursorBlocked
                ? Epoch.Value.ToString(CultureInfo.InvariantCulture) + "." + LastDeliveredSeq.ToString(CultureInfo.InvariantCulture) : null;
            HandshakeResult result = await HandshakeAsync(stream, reader, role, token, cursor, cursor == null ? null : ResumeToken);
            bool warm = result.Resume is PublicLineCodecV1.ResumeOkFrame;
            pending.Clear();
            ready.Clear();
            eventOwners.Clear();
            eventBulkOwners.Clear();
            ackOwners.Clear();
            receiveEvent = NewReceiver();
            if (!warm)
            {
                lock (gate)
                {
                    ids = new();
                    correlations.Clear();
                    commands.Clear();
                    nextCorr = 0;
                }
                lastBulkId = 0;
                LastDeliveredSeq = 0;
                ResumeToken = null;
            }
            cursorBlocked = false;
            Epoch = result.Welcome.Epoch;
            Resume = result.Resume;
            return warm;
        }

    public string EncodeCommand(ClientCommand command)
    {
        lock (gate)
        {
            if (!correlations.TryGetValue(command.CommandId, out ulong corr))
            {
                corr = checked(++nextCorr);
                correlations.Add(command.CommandId, corr);
                commands.Add(corr, command.CommandId);
            }
            return PublicLineCodecV1.EncodeCommand(command, corr, ids.Map);
        }
    }

    private Guid Resolve(ulong wireId)
    {
        lock (gate) return ids.Resolve(wireId);
    }

    private Guid CommandId(ulong corr)
    {
        lock (gate)
            return commands.TryGetValue(corr, out Guid id) ? id
                : throw new FormatException("Reply has unknown correlation " + corr.ToString(CultureInfo.InvariantCulture));
    }

    private Delivery Introduce()
    {
        if (pending.Count >= MaxPendingEvents) throw new FormatException("bulk_malformed");
        Delivery delivery = new();
        pending.Enqueue(delivery);
        return delivery;
    }

    private ulong[] BulkReferences(IReadOnlyList<string> fields)
    {
        List<ulong> references = new();
        ulong previous = lastBulkId;
        foreach (string field in fields)
        {
            if (field.Length < 2 || field[0] != 'b'
                || !ulong.TryParse(field.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) continue;
            if (id <= previous) throw new FormatException("bulk_malformed");
            references.Add(id);
            previous = id;
        }
        if (references.Count > MaxInFlightBulks - ackOwners.Count - eventBulkOwners.Count)
            throw new FormatException("bulk_malformed");
        lastBulkId = previous;
        return references.ToArray();
    }

    private void Drop(Delivery owner)
    {
        owner.Complete = true;
        foreach (ulong id in ackOwners.Where(p => ReferenceEquals(p.Value.Owner, owner)).Select(p => p.Key).ToArray())
            ackOwners.Remove(id);
        foreach (ulong id in eventBulkOwners.Where(p => ReferenceEquals(p.Value, owner)).Select(p => p.Key).ToArray())
            eventBulkOwners.Remove(id);
        foreach (ulong seq in eventOwners.Where(p => ReferenceEquals(p.Value, owner)).Select(p => p.Key).ToArray())
            eventOwners.Remove(seq);
    }

    private void DeliverEvents(IReadOnlyList<PublicLineCodecV1.EventEnvelope> events)
    {
        foreach (var envelope in events)
        {
            Delivery delivery = eventOwners[envelope.Seq];
            Drop(delivery);
            delivery.Value = envelope.Value;
        }
    }

    public async Task<string> ReadJsonAsync(StreamReader reader, CancellationToken token)
    {
        while (ready.Count == 0)
        {
            string line = await reader.ReadLineAsync(token) ?? throw new IOException("Server closed the connection.");
            Delivery? introduced = null;
            try
            {
                if (line.StartsWith("ACK|", StringComparison.Ordinal))
                {
                    PublicLineCodecV1.AckFrame ack = PublicLineCodecV1.DecodeAck(line);
                        string? resumeToken = PublicLineCodecV1.ResumeToken(ack);
                        if (resumeToken != null) ResumeToken = resumeToken;
                        if (ack.Verb == "LeaveSession") ResumeToken = null;
                    introduced = Introduce();
                    introduced.Value = new ServerEvent.CommandAccepted(CommandId(ack.Corr));
                    ulong[] references = BulkReferences(ack.Fields);
                    foreach (ulong id in references) ackOwners.Add(id, new AckBulk { Owner = introduced });
                    introduced.Complete = references.Length == 0;
                }
                else if (line.StartsWith("ERR|", StringComparison.Ordinal))
                {
                    PublicLineCodecV1.ErrorFrame error = PublicLineCodecV1.DecodeError(line);
                    string reason = PublicLineCodecV1.RefusalToken(error.Code);
                    string text = PublicLineCodecV1.HexDecode(error.HexText);
                    if (text.Length != 0) reason += "| tooltip: " + text;
                    introduced = Introduce();
                    introduced.Value = error.Corr == 0 ? new ServerEvent.ServerError(reason)
                        : new ServerEvent.CommandRejected(CommandId(error.Corr), reason);
                    introduced.Complete = true;
                }
                else if (line.StartsWith("BULK|", StringComparison.Ordinal)
                    && ulong.TryParse(line.Split('|')[1], out ulong bulkId) && ackOwners.TryGetValue(bulkId, out var bulk))
                {
                    introduced = bulk.Owner;
                    PublicLineCodecV1.BulkChunk chunk = PublicLineCodecV1.DecodeBulkChunk(line);
                    int bytes = Convert.FromBase64String(chunk.Base64).Length;
                    if (chunk.Count == 0 || chunk.Index >= chunk.Count || chunk.Index != bulk.Next
                        || (bulk.Next != 0 && bulk.Count != chunk.Count)
                        || bytes > PublicLineCodecV1.PUBLIC_BULK_CHUNK_RAW_BYTES || bytes > MaxBulkBytes - bulk.Bytes)
                        throw new FormatException("bulk_malformed");
                    bulk.Count = chunk.Count;
                    bulk.Next++;
                    bulk.Bytes += bytes;
                    if (bulk.Next == bulk.Count)
                    {
                        ackOwners.Remove(bulkId);
                        bulk.Owner.Complete = !ackOwners.Values.Any(p => ReferenceEquals(p.Owner, bulk.Owner));
                    }
                }
                else
                {
                    // EV is the sealed v1 tag; the transport also accepts the requested EVT spelling.
                    if (line.StartsWith("EVT|", StringComparison.Ordinal)) line = "EV|" + line[4..];
                    if (line.StartsWith("EV|", StringComparison.Ordinal))
                    {
                        PublicLineCodecV1.EventFrame frame = PublicLineCodecV1.DecodeEvent(line);
                        introduced = Introduce();
                        introduced.Seq = frame.Seq;
                        if (frame.Kind == "PickFailed")
                        {
                            if (frame.Fields.Count != 2) throw new FormatException("bad PickFailed frame");
                            string code = PublicLineCodecV1.RefusalToken(PublicLineCodecV1.ParseRefusal(frame.Fields[0]));
                            string text = PublicLineCodecV1.HexDecode(frame.Fields[1]);
                            introduced.Value = new ServerEvent.CommandRejected(CommandId(frame.Corr), code + (text.Length == 0 ? "" : "| tooltip: " + text));
                            introduced.Complete = true;
                        }
                        else
                        {
                            ulong[] references = BulkReferences(frame.Fields);
                            eventOwners.Add(frame.Seq, introduced);
                            foreach (ulong id in references) eventBulkOwners.Add(id, introduced);
                        }
                    }
                    if (introduced?.Complete != true)
                    {
                        var result = receiveEvent(line);
                        if (!result.Success)
                        {
                            cursorBlocked = true;
                            if (introduced != null) Drop(introduced);
                            else if (line.StartsWith("BULK|", StringComparison.Ordinal)
                                && ulong.TryParse(line.Split('|')[1], out ulong failedId)
                                && eventBulkOwners.TryGetValue(failedId, out Delivery? failed)) Drop(failed);
                            diagnostic?.Invoke(line.StartsWith("BULK|", StringComparison.Ordinal) ? "bulk_malformed" : result.Error ?? "bad_line");
                        }
                        // 237c26b7 drains valid queued events even when it also returns an error.
                        DeliverEvents(result.Events);
                    }
                }
            }
            catch (FormatException ex)
            {
                cursorBlocked = true;
                if (introduced != null)
                {
                    Drop(introduced);
                    introduced.Value = null;
                }
                diagnostic?.Invoke(line.StartsWith("ACK|", StringComparison.Ordinal) || line.StartsWith("BULK|", StringComparison.Ordinal)
                    ? "bulk_malformed" : ex.Message);
            }
            while (pending.TryPeek(out Delivery? head) && head.Complete)
            {
                pending.Dequeue();
                if (head.Value != null) ready.Enqueue(head);
            }
        }
        Delivery delivered = ready.Dequeue();
        if (!cursorBlocked && delivered.Seq != 0) LastDeliveredSeq = delivered.Seq;
        return PublicProtocolCodec.EncodeEvent(delivered.Value!);
    }
}
