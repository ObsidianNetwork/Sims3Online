using System.Collections.Generic;
using System.Globalization;
using SimsOnline.DedicatedServer.Protocol;
using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// extract actor + book object id from a book-chain batch for INJECT_READ_INTENT.
/// </summary>
internal static class BookChainReadIntentResolver
{
    public static bool TryResolveFromBundles(
        IReadOnlyList<InteractionReplayBundle> bundles,
        out ulong actorObjectId,
        out ulong bookObjectId,
        out IReadOnlyDictionary<string, string> parameters)
    {
        actorObjectId = 0;
        bookObjectId = 0;
        parameters = new Dictionary<string, string>();
        InteractionReplayBundle? readBook = null;
        for (int i = 0; i < bundles.Count; i++)
        {
            if (InteractionReplayApplySorter.IsReadBookTerminalHead(bundles[i].InteractionKey))
            {
                readBook = bundles[i];
            }
        }

        if (readBook == null)
        {
            return false;
        }

        actorObjectId = readBook.ActorObjectId;
        parameters = WithSeatPin(readBook.Parameters, bundles);
        if (TryGetBookId(parameters, out _))
        {
            // Cross-process identity is bookId (class B inventory guid is host-local).
            bookObjectId = 0;
            return true;
        }

        if (readBook.TargetObjectId is ulong readTarget && readTarget != 0uL)
        {
            bookObjectId = readTarget;
            return true;
        }

        for (int i = 0; i < bundles.Count; i++)
        {
            InteractionReplayBundle bundle = bundles[i];
            if (InteractionReplayApplySorter.IsGetBookPrecursor(bundle.InteractionKey)
                && bundle.TargetObjectId is ulong getBookTarget
                && getBookTarget != 0uL)
            {
                bookObjectId = getBookTarget;
                return true;
            }
        }

        return false;
    }

    public static bool TryResolveFromInteractions(
        IReadOnlyList<ServerEvent.InteractionQueued> interactions,
        out ulong actorObjectId,
        out ulong bookObjectId,
        out IReadOnlyDictionary<string, string> parameters)
    {
        actorObjectId = 0;
        bookObjectId = 0;
        parameters = new Dictionary<string, string>();
        ServerEvent.InteractionQueued? readBook = null;
        for (int i = 0; i < interactions.Count; i++)
        {
            if (InteractionReplayApplySorter.IsReadBookTerminalHead(interactions[i].InteractionKey))
            {
                readBook = interactions[i];
            }
        }

        if (readBook == null)
        {
            return false;
        }

        actorObjectId = readBook.ActorObjectId;
        if (readBook.TargetObjectId is ulong target && target != 0uL)
        {
            bookObjectId = target;
            return true;
        }

        for (int i = 0; i < interactions.Count; i++)
        {
            ServerEvent.InteractionQueued iq = interactions[i];
            if (InteractionReplayApplySorter.IsGetBookPrecursor(iq.InteractionKey)
                && iq.TargetObjectId is ulong getBookTarget
                && getBookTarget != 0uL)
            {
                bookObjectId = getBookTarget;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// carry the deferred ForceAlign Sit's seat target + slot params alongside the
    /// ReadBook parameters so the client can pin the seat. Without this the inject's Sit gets a
    /// zero hit point and the engine (<c>Sit.GetContainmentSlotClosestToHit</c>) falls back to the
    /// client Sim's position, picking the empty slot nearest the client -- which diverges from the
    /// host's seat. The seat object id + slot keys are whitelisted on the INJECT_READ_INTENT wire.
    /// </summary>
    private static IReadOnlyDictionary<string, string> WithSeatPin(
        IReadOnlyDictionary<string, string> readBookParameters,
        IReadOnlyList<InteractionReplayBundle> bundles)
    {
        for (int i = 0; i < bundles.Count; i++)
        {
            InteractionReplayBundle sit = bundles[i];
            if (!InteractionReplayApplySorter.IsSitTransitionDeferred(sit.InteractionKey))
            {
                continue;
            }

            if (!(sit.TargetObjectId is ulong seatObjectId) || seatObjectId == 0uL)
            {
                continue;
            }

            Dictionary<string, string> merged = new(readBookParameters);
            merged["seatObjectId"] = seatObjectId.ToString(CultureInfo.InvariantCulture);
            CopyIfPresent(sit.Parameters, merged, "seatArea");
            CopyIfPresent(sit.Parameters, merged, "seatContainmentSlot");
            CopyIfPresent(sit.Parameters, merged, "seatRoutingSlot");
            CopyIfPresent(sit.Parameters, merged, "seatIkSuffix");
            return merged;
        }

        return readBookParameters;
    }

    private static void CopyIfPresent(
        IReadOnlyDictionary<string, string> source,
        IDictionary<string, string> target,
        string key)
    {
        if (source != null && source.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
        {
            target[key] = value;
        }
    }

    private static bool TryGetBookId(IReadOnlyDictionary<string, string> parameters, out string bookId)
    {
        bookId = string.Empty;
        return parameters != null
            && parameters.TryGetValue("bookId", out string? value)
            && !string.IsNullOrEmpty(value)
            && (bookId = value).Length > 0;
    }
}
