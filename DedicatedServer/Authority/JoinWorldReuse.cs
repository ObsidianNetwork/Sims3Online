using System.Globalization;
using System.Text;

namespace SimsOnline.DedicatedServer.Authority;

/// <summary>One part of a join fingerprint: how many entries it covers and the hash of their sorted list.</summary>
public readonly record struct FingerprintPart(int Count, string Hash);

/// <summary>
/// what a joiner gets only from the save, as the server's mod reports it (SACS_RECORDER fingerprint-saved and
/// fingerprint-now; SimsOnlineServerHostMod/JoinFingerprint.cs). <see cref="Scope"/> names the households of the Sims
/// claimed during this run and their lots ("h:ids|l:ids"), or "-" when none resolved; the three parts cover the objects
/// on those lots, the inventories and the relationships. <see cref="ScanMs"/> is the scan's time inside the game.
/// </summary>
public sealed record WorldFingerprint(
    string Scope,
    FingerprintPart Objects,
    FingerprintPart Inventories,
    FingerprintPart Relationships,
    double ScanMs)
{
    public const string NoScope = "-";

    /// <summary>The fingerprint in a mod reply ("scope=...;objects=N:hash;inventories=N:hash;relationships=N:hash;
    /// ms=x"), or null with <paramref name="error"/> saying why: the mod's own "error=..." , no fingerprint taken yet
    /// ("none"), or anything else (an older mod answers its usage text or status).</summary>
    public static WorldFingerprint? Parse(string? report, out string? error)
    {
        string text = (report ?? string.Empty).Trim();
        if (text.Length == 0 || text == "none")
        {
            error = "no fingerprint taken";
            return null;
        }

        if (text.StartsWith("error=", StringComparison.Ordinal))
        {
            error = text["error=".Length..];
            return null;
        }

        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string field in text.Split(';'))
        {
            int equals = field.IndexOf('=');
            if (equals > 0)
            {
                fields[field[..equals]] = field[(equals + 1)..];
            }
        }

        if (fields.TryGetValue("scope", out string? scope) && scope.Length > 0
            && TryPart(fields, "objects", out FingerprintPart objects)
            && TryPart(fields, "inventories", out FingerprintPart inventories)
            && TryPart(fields, "relationships", out FingerprintPart relationships))
        {
            double ms = fields.TryGetValue("ms", out string? msText)
                && double.TryParse(msText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
            error = null;
            return new WorldFingerprint(scope, objects, inventories, relationships, ms);
        }

        error = "not a fingerprint: " + (text.Length > 80 ? text[..80] + "..." : text);
        return null;
    }

    private static bool TryPart(Dictionary<string, string> fields, string name, out FingerprintPart part)
    {
        part = default;
        if (!fields.TryGetValue(name, out string? value))
        {
            return false;
        }

        int colon = value.IndexOf(':');
        if (colon <= 0 || colon == value.Length - 1
            || !int.TryParse(value[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            return false;
        }

        part = new FingerprintPart(count, value[(colon + 1)..]);
        return true;
    }
}

/// <summary>
/// "reuse if unchanged" (docs/sim145-followup-scopes.md, Part 1): a join checkpoint serves the last save when
/// the fingerprint taken with that save equals one taken at the join; otherwise it saves fresh, as before. Every doubt
/// is a fresh save: no fingerprint with the last save, a scan that failed or timed out, nothing claimed yet, or a
/// different scope (another household claimed, a Sim on another lot).
/// </summary>
public static class JoinWorldReuse
{
    public const string NoFingerprintYet = "no-fingerprint-yet";
    public const string Scope = "scope";
    public const string Objects = "objects";
    public const string Inventories = "inventories";
    public const string Relationships = "relationships";
    public const string Error = "error";

    /// <summary>Every miss reason, in the order the counters print them.</summary>
    public static readonly IReadOnlyList<string> MissReasons = [NoFingerprintYet, Scope, Objects, Inventories, Relationships, Error];

    /// <summary><see cref="Reuse"/>, or the miss's reasons (one, or each part that changed) and what they mean.</summary>
    public sealed record Decision(bool Reuse, IReadOnlyList<string> Misses, string Detail);

    public static Decision Decide(WorldFingerprint? atSave, WorldFingerprint? atJoin, string? scanError)
    {
        if (atSave == null)
        {
            return Miss(NoFingerprintYet, "the last save has no fingerprint");
        }

        if (atJoin == null || scanError != null)
        {
            return Miss(Error, "the join's scan failed: " + (scanError ?? "no reply"));
        }

        if (atJoin.Scope == WorldFingerprint.NoScope)
        {
            return Miss(Scope, "nothing claimed yet, so nothing to compare");
        }

        if (atJoin.Scope != atSave.Scope)
        {
            return Miss(Scope, $"scope {atSave.Scope} at the save, {atJoin.Scope} now");
        }

        List<string> changed = new();
        StringBuilder detail = new();
        Compare(Objects, atSave.Objects, atJoin.Objects, changed, detail);
        Compare(Inventories, atSave.Inventories, atJoin.Inventories, changed, detail);
        Compare(Relationships, atSave.Relationships, atJoin.Relationships, changed, detail);
        return changed.Count == 0
            ? new Decision(true, Array.Empty<string>(), "nothing a joiner needs changed since the last save")
            : new Decision(false, changed, detail.ToString());
    }

    private static Decision Miss(string reason, string detail) => new(false, [reason], detail);

    private static void Compare(string name, FingerprintPart atSave, FingerprintPart now, List<string> changed, StringBuilder detail)
    {
        if (atSave == now)
        {
            return;
        }

        changed.Add(name);
        detail.Append(detail.Length > 0 ? ", " : string.Empty)
            .Append(CultureInfo.InvariantCulture, $"{name} {atSave.Count} -> {now.Count}")
            .Append(atSave.Count == now.Count ? " (same count, other entries)" : string.Empty);
    }
}

/// <summary>
/// the join checkpoints' counters, printed on each join's "[world] join reuse" line (scripts/join_fingerprint.py
/// counters reads the newest). A miss counts once in <see cref="Misses"/> and once under each of its reasons. Scan ms is
/// the fingerprint's time inside the game, taken in a save or at a join. Save ms is each fresh join checkpoint's save,
/// the stall every player sees.
/// Seconds saved adds, per hit, the last measured fresh save's time (a join checkpoint's, or a public save's when that
/// was the last).
/// </summary>
public sealed class JoinReuseCounters
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _missesBy = JoinWorldReuse.MissReasons.ToDictionary(r => r, _ => 0);
    private double _lastSaveForEstimateMs;

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public int SavesSkipped { get; private set; }

    public double SecondsSaved { get; private set; }

    public double? LastScanMs { get; private set; }

    public double? MaxScanMs { get; private set; }

    public double? LastSaveMs { get; private set; }

    public double? MaxSaveMs { get; private set; }

    public int MissesBy(string reason)
    {
        lock (_lock)
        {
            return _missesBy.TryGetValue(reason, out int count) ? count : 0;
        }
    }

    public void RecordScan(double ms)
    {
        lock (_lock)
        {
            LastScanMs = ms;
            MaxScanMs = Math.Max(MaxScanMs ?? ms, ms);
        }
    }

    /// <summary>A fresh save's time: a join checkpoint's counts in the save ms; either kind feeds the seconds-saved
    /// estimate.</summary>
    public void RecordSave(double ms, bool joinCheckpoint)
    {
        lock (_lock)
        {
            _lastSaveForEstimateMs = ms;
            if (joinCheckpoint)
            {
                LastSaveMs = ms;
                MaxSaveMs = Math.Max(MaxSaveMs ?? ms, ms);
            }
        }
    }

    public void Record(JoinWorldReuse.Decision decision)
    {
        lock (_lock)
        {
            if (decision.Reuse)
            {
                Hits++;
                SavesSkipped++;
                SecondsSaved += _lastSaveForEstimateMs / 1000.0;
                return;
            }

            Misses++;
            foreach (string reason in decision.Misses)
            {
                _missesBy[reason] = _missesBy.TryGetValue(reason, out int count) ? count + 1 : 1;
            }
        }
    }

    /// <summary>"hits=1 misses=2 (no-fingerprint-yet=1 scope=1 objects=0 ...) scan-ms last=2.0 max=3.3 save-ms
    /// last=9876 max=11539 saves-skipped=1 seconds-saved=9.9".</summary>
    public string Format()
    {
        lock (_lock)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string reasons = string.Join(" ", JoinWorldReuse.MissReasons.Select(r => $"{r}={_missesBy[r]}"));
            return string.Create(c, $"hits={Hits} misses={Misses} ({reasons}) scan-ms last={Ms(LastScanMs, "F1")} max={Ms(MaxScanMs, "F1")} ")
                + string.Create(c, $"save-ms last={Ms(LastSaveMs, "F0")} max={Ms(MaxSaveMs, "F0")} saves-skipped={SavesSkipped} seconds-saved={SecondsSaved:F1}");
        }
    }

    private static string Ms(double? value, string format) =>
        value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "-";
}
