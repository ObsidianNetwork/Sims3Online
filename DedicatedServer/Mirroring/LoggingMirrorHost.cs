using System.Globalization;
using System.Text.Json;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// M4 P4.2.2.1 — observability-only <see cref="IMirrorHost"/> that logs each
/// Apply* invocation as a one-line summary to a <see cref="TextWriter"/>.
///
/// Used by <c>SimsOnline.Client</c> as the default <see cref="IMirrorHost"/> during the
/// P4.2.2.1 wiring slice, where the mirror loop runs end-to-end (TCP receive →
/// <see cref="MirrorEngine"/> → translator → host) but no local TS3 has been launched
/// yet. Superseded in P4.2.2.2 by <c>HeadlessSims3Host</c> running in client role,
/// driving a real local TS3 via the memory bridge — the swap is one line at the call
/// site because <c>HeadlessSims3Host</c> already implements <see cref="IMirrorHost"/>.
/// </summary>
/// <remarks>
/// Every Apply* method writes exactly one line and increments <see cref="AppliedCount"/>.
/// When <c>jsonStdout</c> is true, the line is a single-line JSON object matching the
/// existing <c>SimsOnline.Client</c> <c>--json-stdout</c> envelope; otherwise the line is
/// human-readable (<c>[HH:mm:ss] mirror_applied: ApplyPosition {...}</c>).
///
/// Thread-safety: <see cref="AppliedCount"/> is updated via <see cref="Interlocked"/>.
/// The underlying <see cref="TextWriter"/> is assumed to be the caller's; concurrent
/// writes are not expected because <see cref="MirrorEngine"/> dispatches sequentially
/// per <see cref="ServerEvent"/>. Each write is flushed so a crash mid-test never loses
/// an applied command.
/// </remarks>
public sealed class LoggingMirrorHost : IMirrorHost
{
    private readonly TextWriter _writer;
    private readonly bool _jsonStdout;
    private int _appliedCount;

    public LoggingMirrorHost(TextWriter writer, bool jsonStdout)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _jsonStdout = jsonStdout;
    }

    /// <summary>Total number of Apply* calls successfully written to the log.</summary>
    public int AppliedCount => Volatile.Read(ref _appliedCount);

    public Task ApplyPositionAsync(ulong simObjectId, Vector3Dto targetPosition, CancellationToken cancellationToken) =>
        WriteAsync("ApplyPosition", new
        {
            sim_object_id = simObjectId,
            x = targetPosition.X,
            y = targetPosition.Y,
            z = targetPosition.Z
        }, cancellationToken);

    /// <summary>log a batched position apply as one line carrying the Sim count and pairs.</summary>
    public Task ApplyPositionsAsync(IReadOnlyList<(ulong SimObjectId, Vector3Dto Position)> positions, CancellationToken cancellationToken) =>
        WriteAsync("ApplyPositions", new
        {
            count = positions.Count,
            positions = positions.Select(p => new
            {
                sim_object_id = p.SimObjectId,
                x = p.Position.X,
                y = p.Position.Y,
                z = p.Position.Z
            }).ToArray()
        }, cancellationToken);

    public Task ApplyMotiveAsync(ulong simObjectId, string motiveName, float value, CancellationToken cancellationToken) =>
        WriteAsync("ApplyMotive", new
        {
            sim_object_id = simObjectId,
            motive_name = motiveName,
            value = value
        }, cancellationToken);

    public Task ApplyCashAsync(ulong householdId, int funds, CancellationToken cancellationToken) =>
        WriteAsync("ApplyCash", new
        {
            household_id = householdId,
            funds = funds
        }, cancellationToken);

    public Task ApplySkillAsync(ulong simObjectId, string skillName, int level, CancellationToken cancellationToken) =>
        WriteAsync("ApplySkill", new
        {
            sim_object_id = simObjectId,
            skill_name = skillName,
            level = level
        }, cancellationToken);

    public Task ApplyAutonomyAsync(ulong simObjectId, bool disabled, CancellationToken cancellationToken) =>
        WriteAsync("ApplyAutonomy", new
        {
            sim_object_id = simObjectId,
            disabled = disabled
        }, cancellationToken);

    public Task ApplyInteractionQueueAsync(InteractionRequest interaction, CancellationToken cancellationToken) =>
        WriteAsync("ApplyInteractionQueue", new
        {
            actor_object_id = interaction.ActorObjectId,
            interaction_key = interaction.InteractionKey,
            target_object_id = interaction.TargetObjectId,
            target_x = interaction.TargetPosition?.X,
            target_y = interaction.TargetPosition?.Y,
            target_z = interaction.TargetPosition?.Z,
            parameters = interaction.Parameters
        }, cancellationToken);

    public Task ApplyInteractionsAsync(IReadOnlyList<InteractionRequest> interactions, CancellationToken cancellationToken) =>
        WriteAsync("ApplyInteractions", new
        {
            count = interactions.Count,
            interactions = interactions.Select(i => new
            {
                actor_object_id = i.ActorObjectId,
                interaction_key = i.InteractionKey,
                target_object_id = i.TargetObjectId,
                target_x = i.TargetPosition?.X,
                target_y = i.TargetPosition?.Y,
                target_z = i.TargetPosition?.Z
            }).ToArray()
        }, cancellationToken);

    public Task ApplyForceAlignAsync(InteractionReplayBundle bundle, CancellationToken cancellationToken) =>
        WriteAsync("ApplyForceAlign", new
        {
            actor_object_id = bundle.ActorObjectId,
            interaction_key = bundle.InteractionKey,
            posture_container_object_id = bundle.PostureContainerObjectId,
            is_transition_interaction = bundle.IsTransitionInteraction,
            queue_role = bundle.QueueRole.ToString()
        }, cancellationToken);

    public Task SetGameSpeedAsync(GameSpeed speed, CancellationToken cancellationToken) =>
        WriteAsync("SetGameSpeed", new
        {
            speed = speed.ToString()
        }, cancellationToken);

    public Task SetWorldTimeAsync(WorldTime time, CancellationToken cancellationToken) =>
        WriteAsync("SetWorldTime", new
        {
            day = time.Day,
            hours = time.Hours,
            minutes = time.Minutes
        }, cancellationToken);

    public Task ShowNotificationAsync(string text, CancellationToken cancellationToken) =>
        WriteAsync("ShowNotification", new
        {
            text = text
        }, cancellationToken);

    // No TS3 to select a Sim in, and selecting is not a mirror application: writing it would count
    // toward AppliedCount (and --exit-after-mirror-applied).
    public Task SetActiveSimAsync(ulong simObjectId, CancellationToken cancellationToken) => Task.CompletedTask;

    // No TS3 to show a dialog in; nobody answers, so the server's timeout applies.
    public Task ShowDialogAsync(int requestId, ulong simObjectId, string kind, IReadOnlyList<string> args, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<MirroredInteractionState?> QueryInteractionStateAsync(ulong simObjectId, CancellationToken cancellationToken) =>
        Task.FromResult<MirroredInteractionState?>(null);

    public Task InjectReadIntentAsync(
        ulong simObjectId,
        ulong bookObjectId,
        string mode,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken) =>
        WriteAsync("InjectReadIntent", new
        {
            sim_object_id = simObjectId,
            book_object_id = bookObjectId,
            mode = mode,
            parameters = parameters
        }, cancellationToken);

    private async Task WriteAsync(string commandType, object payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string line;
        if (_jsonStdout)
        {
            line = JsonSerializer.Serialize(new
            {
                kind = "mirror_applied",
                ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                payload = new { command_type = commandType, args = payload }
            });
        }
        else
        {
            line = string.Format(
                CultureInfo.InvariantCulture,
                "[{0:HH:mm:ss}] mirror_applied: {1} {2}",
                DateTimeOffset.UtcNow,
                commandType,
                JsonSerializer.Serialize(payload));
        }
        await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);
        Interlocked.Increment(ref _appliedCount);
    }
}
