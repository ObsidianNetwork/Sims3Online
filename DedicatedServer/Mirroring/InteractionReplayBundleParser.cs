using System.Globalization;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

public static class InteractionReplayBundleParser
{
    private static readonly HashSet<string> HexTextFields = new(StringComparer.Ordinal)
    {
        "interactionKey",
        "interactionInstanceType",
        "interactionDefinitionType",
        "targetObjectType",
        "priorityLevel",
        "postureType",
        "policyHint",
        "parameters",
    };

    public static bool TryParseFromCaptureReport(string report, out InteractionReplayBundle? bundle, out string? error)
    {
        bundle = null;
        error = null;
        if (string.IsNullOrWhiteSpace(report))
        {
            error = "empty capture report";
            return false;
        }

        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string segment in report.Split(';'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            int eq = segment.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = segment.Substring(0, eq);
            string value = segment.Substring(eq + 1);
            fields[key] = value;
        }

        if (!fields.TryGetValue("version", out string? versionText)
            || !int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
        {
            error = "missing or invalid version";
            return false;
        }

        if (!TryParseUlong(fields, "actorObjectId", out ulong actorObjectId))
        {
            error = "missing or invalid actorObjectId";
            return false;
        }

        if (!TryParseUlong(fields, "interactionId", out ulong interactionId))
        {
            error = "missing or invalid interactionId";
            return false;
        }

        string interactionKey = DecodeField(fields, "interactionKey");
        if (string.IsNullOrWhiteSpace(interactionKey))
        {
            error = "missing interactionKey";
            return false;
        }

        ulong? targetObjectId = TryParseOptionalUlong(fields, "targetObjectId");
        Vector3Dto? targetPosition = null;
        if (fields.TryGetValue("hasTargetPosition", out string? hasPos) && hasPos == "1")
        {
            if (TryParseFloat(fields, "x", out float x)
                && TryParseFloat(fields, "y", out float y)
                && TryParseFloat(fields, "z", out float z))
            {
                targetPosition = new Vector3Dto(x, y, z);
            }
        }

        bool isTransition = fields.TryGetValue("isTransitionInteraction", out string? transitionText)
            && bool.TryParse(transitionText, out bool transitionValue)
            && transitionValue;

        InteractionQueueRole queueRole = MapQueueRole(fields.TryGetValue("queueRole", out string? queueRoleText) ? queueRoleText : null);
        ReplayPolicy? policyHint = MapPolicyHint(DecodeField(fields, "policyHint"));
        ulong? postureContainerObjectId = TryParseOptionalUlong(fields, "postureContainerObjectId");
        long? hostTick = fields.TryGetValue("hostTick", out string? hostTickText)
            && long.TryParse(hostTickText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long tickValue)
            ? tickValue
            : null;

        bool? isUserDirected = TryParseOptionalBool(fields, "isUserDirected");
        bool? isAutonomous = TryParseOptionalBool(fields, "isAutonomous");

        bundle = new InteractionReplayBundle(
            version,
            actorObjectId,
            interactionId,
            interactionKey,
            NullIfEmpty(DecodeField(fields, "interactionInstanceType")),
            NullIfEmpty(DecodeField(fields, "interactionDefinitionType")),
            targetObjectId is ulong tid && tid != 0 ? tid : null,
            NullIfEmpty(DecodeField(fields, "targetObjectType")),
            targetPosition,
            NullIfEmpty(DecodeField(fields, "priorityLevel")),
            isUserDirected,
            isAutonomous,
            isTransition,
            queueRole,
            NullIfEmpty(DecodeField(fields, "postureType")),
            postureContainerObjectId is ulong pcid && pcid != 0 ? pcid : null,
            hostTick,
            ParseParameters(DecodeField(fields, "parameters")),
            policyHint);

        return true;
    }

    private static Dictionary<string, string> ParseParameters(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, string>();
        }

        Dictionary<string, string> parameters = new(StringComparer.Ordinal);
        foreach (string segment in raw.Split(','))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            int eq = segment.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            parameters[segment.Substring(0, eq)] = segment.Substring(eq + 1);
        }

        return parameters;
    }

    private static InteractionQueueRole MapQueueRole(string? queueRole)
    {
        if (string.IsNullOrWhiteSpace(queueRole))
        {
            return InteractionQueueRole.Unknown;
        }

        return queueRole switch
        {
            "head" => InteractionQueueRole.Head,
            "transition" => InteractionQueueRole.Transition,
            "continuation" => InteractionQueueRole.Continuation,
            "current" => InteractionQueueRole.Current,
            "running" => InteractionQueueRole.Running,
            _ => InteractionQueueRole.Unknown,
        };
    }

    private static ReplayPolicy? MapPolicyHint(string? policyHint)
    {
        if (string.IsNullOrWhiteSpace(policyHint))
        {
            return null;
        }

        return policyHint switch
        {
            "NeverReplay" => ReplayPolicy.NeverReplay,
            "ReplayExact" => ReplayPolicy.ReplayExact,
            "ReplayAsRoute" => ReplayPolicy.ReplayAsRoute,
            "ForceAlign" => ReplayPolicy.ForceAlign,
            "SlideOnly" => ReplayPolicy.SlideOnly,
            _ => Enum.TryParse(policyHint, ignoreCase: true, out ReplayPolicy parsed) ? parsed : null,
        };
    }

    private static string DecodeField(Dictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out string? value))
        {
            return string.Empty;
        }

        if (!HexTextFields.Contains(key) || string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        try
        {
            return BridgeLineCodec.DecodeText(value);
        }
        catch (FormatException)
        {
            return value;
        }
    }

    private static bool TryParseUlong(Dictionary<string, string> fields, string key, out ulong value)
    {
        value = 0;
        return fields.TryGetValue(key, out string? text)
            && ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static ulong? TryParseOptionalUlong(Dictionary<string, string> fields, string key)
    {
        return TryParseUlong(fields, key, out ulong value) ? value : null;
    }

    private static bool TryParseFloat(Dictionary<string, string> fields, string key, out float value)
    {
        value = 0;
        return fields.TryGetValue(key, out string? text)
            && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool? TryParseOptionalBool(Dictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out string? text))
        {
            return null;
        }

        return bool.TryParse(text, out bool value) ? value : null;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
