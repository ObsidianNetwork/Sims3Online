using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimsOnline.DedicatedServer.HostBridge;

namespace SimsOnline.DedicatedServer.Network;

/// <summary>
/// bridgectl backend: loopback-only admin listener that forwards raw wire commands to
/// the live host bridge. Deliberately NOT part of the public client listener — the public
/// protocol has no auth pre-P6, and this surface bypasses every session ownership/role gate.
///
/// Line protocol (newline-delimited UTF-8):
///   in:  [@timeoutSeconds] NAME [arg ...]    e.g. "DIAG_QUEUE 18031287978496800304" or "@60 REQUEST_SNAPSHOT"
///        "commands" lists the known wire-command inventory.
///        In an argument, "-" is an empty wire part and "\-" a literal "-" part; no other token is escaped.
///        A few names are answered by the controller itself, not the mod: EVENTS_DROPPED (mod events it could not
///        decode, by kind, on any controller) and the verbs the local callback answers (the server's PRESENTATION_SENT).
///   out: one JSON object per request: { ok, accepted?, reason?, report?, error? }
///
/// Every command carries an explicit timeout (default 15s): raw Bridge.SendAsync bypasses the
/// player/heavy classification, and a wedged command would otherwise hold the single
/// serialized frame slot indefinitely.
/// </summary>
public sealed class BridgeAdminListener : IAsyncDisposable
{
    private const int DefaultTimeoutSeconds = 15;
    private const int MaxTimeoutSeconds = 600;

    private readonly HostBridgeServer _bridge;
    private readonly string _endpoint;
    private readonly Func<string, string[], string?>? _local;
    private TcpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _acceptTask;

    /// <param name="local">The controller's own verbs (name upper-cased, args): a report answers the request here, null
    /// forwards it to the mod (PRESENTATION_SENT).</param>
    public BridgeAdminListener(HostBridgeServer bridge, string endpoint, Func<string, string[], string?>? local = null)
    {
        _bridge = bridge;
        _endpoint = endpoint;
        _local = local;
    }

    public string? BoundEndpoint { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        string[] parts = _endpoint.Split(':');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress? address) || !int.TryParse(parts[1], out int port))
        {
            throw new FormatException($"--bridgectl-listen must be address:port. Value: {_endpoint}");
        }

        if (!IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException($"--bridgectl-listen must bind a loopback address (no auth exists pre-P6). Value: {_endpoint}");
        }

        _listener = new TcpListener(address, port);
        _listener.Start();
        BoundEndpoint = _listener.LocalEndpoint.ToString();
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptTask = Task.Run(() => AcceptLoopAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                continue;
            }

            // Sequential clients are fine for a dev tool; commands serialize on the bridge
            // write-lock anyway. Fire-and-forget so a hung client doesn't block accepts.
            _ = Task.Run(() => ServeClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using TcpClient owned = client;
        try
        {
            using NetworkStream stream = owned.GetStream();
            using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await ReadAdminLineAsync(stream, cancellationToken);
                if (line == null)
                {
                    break;
                }

                string response;
                if (line.StartsWith("BULK_UPLOAD ", StringComparison.OrdinalIgnoreCase))
                {
                    response = await HandleBulkUploadAsync(stream, line.Trim(), cancellationToken);
                }
                else
                {
                    response = await HandleRequestAsync(line.Trim(), cancellationToken);
                }

                await writer.WriteLineAsync(response);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // Client went away mid-exchange — normal for a CLI.
        }
    }

    private static async Task<string?> ReadAdminLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        // Byte-at-a-time so a trailing BULK_UPLOAD binary body is never swallowed by StreamReader's buffer.
        StringBuilder builder = new();
        byte[] buffer = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            char c = (char)buffer[0];
            if (c == '\n')
            {
                if (builder.Length > 0 && builder[^1] == '\r')
                {
                    builder.Length--;
                }

                return builder.ToString();
            }

            builder.Append(c);
        }
    }

    private async Task<string> HandleRequestAsync(string request, CancellationToken cancellationToken)
    {
        if (request.Length == 0)
        {
            return Serialize(new { ok = false, error = "empty request" });
        }

        if (string.Equals(request, "commands", StringComparison.OrdinalIgnoreCase))
        {
            return Serialize(new { ok = true, commands = BridgeLineCodec.KnownCommands });
        }

        int timeoutSeconds = DefaultTimeoutSeconds;
        string[] tokens = request.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int start = 0;
        if (tokens[0].StartsWith('@'))
        {
            if (!int.TryParse(tokens[0][1..], out timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > MaxTimeoutSeconds)
            {
                return Serialize(new { ok = false, error = $"invalid timeout '{tokens[0]}' (use @1..@{MaxTimeoutSeconds})" });
            }

            start = 1;
        }

        if (tokens.Length <= start)
        {
            return Serialize(new { ok = false, error = "missing command name" });
        }

        string name = tokens[start].ToUpperInvariant();
        string[] args = tokens[(start + 1)..];
        // REPL convention: a literal "-" arg means "empty" (space-tokenized input cannot
        // express an empty wire part, e.g. DIAG_WALK's hexPath for "walk from the root").
        // "\-" is the escape for an argument that IS a literal "-" — what the codec writes for a
        // null or an empty id list (card 56 gate 6's three literal arguments, "SET_SIM_SCOPE|<id>|-|-").
        // Only that whole token is special, so any other token with a backslash passes through as it
        // came, and EncodeRawCommand still joins the arguments verbatim (bridgectl.py admin_request sends it).
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-")
            {
                args[i] = string.Empty;
            }
            else if (args[i] == @"\-")
            {
                args[i] = "-";
            }
        }

        string? localReport = name == "EVENTS_DROPPED" ? BridgeEventDrops.Shared.Describe() : _local?.Invoke(name, args);
        if (localReport != null)
        {
            return Serialize(new { ok = true, accepted = true, report = localReport });
        }

        BridgeCommand.Raw command = new(Guid.NewGuid(), name, args);

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            BridgeResponse response = await _bridge.SendAsync(command, timeout.Token);
            return Serialize(new
            {
                ok = true,
                accepted = response.IsAccepted,
                reason = response.RejectionReason,
                report = response.ProbeReport,
                snapshot = response.Snapshot == null
                    ? null
                    : $"tick={response.Snapshot.Tick} sims={response.Snapshot.Sims.Count} objects={response.Snapshot.Objects.Count}",
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Serialize(new { ok = false, error = $"timeout after {timeoutSeconds}s (command may still complete mod-side; late response is dropped with a diagnostic)" });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = ex.Message });
        }
    }

    private async Task<string> HandleBulkUploadAsync(Stream stream, string request, CancellationToken cancellationToken)
    {
        string[] tokens = request.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3 || !string.Equals(tokens[0], "BULK_UPLOAD", StringComparison.OrdinalIgnoreCase))
        {
            return Serialize(new { ok = false, error = "usage: BULK_UPLOAD <blobId> <byteCount> then raw bytes" });
        }

        string blobId = tokens[1];
        if (!int.TryParse(tokens[2], out int byteCount) || byteCount <= 0 || byteCount > BridgeSharedRegionLayout.BulkSlotPayloadMax)
        {
            return Serialize(new { ok = false, error = $"invalid byteCount (max {BridgeSharedRegionLayout.BulkSlotPayloadMax})" });
        }

        byte[] payload = new byte[byteCount];
        int read = 0;
        while (read < byteCount)
        {
            int chunk = await stream.ReadAsync(payload.AsMemory(read, byteCount - read), cancellationToken);
            if (chunk == 0)
            {
                return Serialize(new { ok = false, error = $"short read: got {read} of {byteCount} bytes" });
            }

            read += chunk;
        }

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(MaxTimeoutSeconds));
            BridgeResponse response = await _bridge.UploadInboundBlobAsync(blobId, payload, timeout.Token);
            return Serialize(new
            {
                ok = true,
                accepted = response.IsAccepted,
                reason = response.RejectionReason,
                report = response.ProbeReport,
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Serialize(new { ok = false, error = $"timeout after {MaxTimeoutSeconds}s" });
        }
        catch (Exception ex)
        {
            return Serialize(new { ok = false, error = ex.Message });
        }
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value);

    public async ValueTask DisposeAsync()
    {
        _shutdown?.Cancel();
        _listener?.Stop();
        if (_acceptTask != null)
        {
            try
            {
                await _acceptTask;
            }
            catch
            {
            }
        }

        _shutdown?.Dispose();
    }
}
