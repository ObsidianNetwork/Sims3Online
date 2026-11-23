using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

namespace SimsOnline.DedicatedServer.Network;

/// <summary>
/// G14: the object paths of the NaN and Infinity values in a message, for the line written when its encoding
/// fails. System.Text.Json refuses non-finite numbers without AllowNamedFloatingPointLiterals, and its exception names
/// no field. Walks public properties, sequences and dictionaries; bounded in depth and nodes, cycle-safe.
/// </summary>
internal static class NonFiniteValues
{
    private const int MaxDepth = 16;
    private const int MaxNodes = 200_000;

    public static IReadOnlyList<string> Find(object? root, int limit = 8)
    {
        List<string> found = new();
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        int nodes = 0;
        Walk(root, string.Empty, 0);
        return found;

        void Walk(object? value, string path, int depth)
        {
            if (value == null || found.Count >= limit || depth > MaxDepth || ++nodes > MaxNodes)
            {
                return;
            }

            switch (value)
            {
                case double d:
                    if (!double.IsFinite(d))
                    {
                        found.Add(Name(path) + "=" + d.ToString(CultureInfo.InvariantCulture));
                    }
                    return;
                case float f:
                    if (!float.IsFinite(f))
                    {
                        found.Add(Name(path) + "=" + f.ToString(CultureInfo.InvariantCulture));
                    }
                    return;
                case string or decimal or bool or char or Enum or DateTime or DateTimeOffset or TimeSpan or Guid:
                    return;
            }

            Type type = value.GetType();
            if (type.IsPrimitive || (!type.IsValueType && !seen.Add(value)))
            {
                return;
            }

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    Walk(entry.Value, $"{path}[{entry.Key}]", depth + 1);
                }
                return;
            }

            if (value is IEnumerable sequence)
            {
                int index = 0;
                foreach (object? item in sequence)
                {
                    Walk(item, $"{path}[{index++}]", depth + 1);
                }
                return;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0
                    || property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always)
                {
                    continue;
                }

                object? child;
                try
                {
                    child = property.GetValue(value);
                }
                catch
                {
                    continue;
                }

                Walk(child, path.Length == 0 ? property.Name : path + "." + property.Name, depth + 1);
            }
        }
    }

    private static string Name(string path) => path.Length == 0 ? "(root)" : path;
}
