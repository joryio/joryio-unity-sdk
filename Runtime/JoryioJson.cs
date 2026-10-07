using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Joryio.Runtime
{
    /// <summary>
    /// Minimal JSON writer for event properties, attributes and bridge payloads.
    ///
    /// Unity's JsonUtility cannot serialize a Dictionary, and pulling in
    /// Newtonsoft would add a dependency that collides with whatever the game
    /// already uses - the same class of conflict that makes bundled binaries a
    /// bad idea.
    ///
    /// Every value a game can plausibly hand us keeps its JSON type. This used
    /// to know int, long, float, double and decimal only, and wrote everything
    /// else through ToString(): a `short` level became the STRING "7", a nested
    /// Dictionary became "System.Collections.Generic.Dictionary`2[...]", a
    /// DateTime became local time in the device's culture, and float.NaN became
    /// the bare token NaN - which is not JSON, so the bridge's parser rejected
    /// the WHOLE payload and the event arrived with no properties at all
    /// (audit 2026-10-06).
    /// </summary>
    internal static class JoryioJson
    {
        /// <summary>
        /// Deeper than any real property bag. Stops a self-referencing
        /// collection from recursing until the stack overflows - which would
        /// crash the game, not just drop an event.
        /// </summary>
        private const int MaxDepth = 32;

        internal static string Serialize(IDictionary<string, object> properties)
        {
            if (properties == null || properties.Count == 0) return "{}";
            var sb = new StringBuilder();
            AppendObject(sb, properties, 0);
            return sb.ToString();
        }

        /// <summary>Any single value, as JSON. Used for the one-key attribute wrapper and arrays.</summary>
        internal static string SerializeValue(object value)
        {
            var sb = new StringBuilder();
            AppendValue(sb, value, 0);
            return sb.ToString();
        }

        private static void AppendObject(StringBuilder sb, IEnumerable<KeyValuePair<string, object>> entries, int depth)
        {
            sb.Append('{');
            var first = true;
            foreach (var kv in entries)
            {
                if (kv.Key == null) continue;
                if (!first) sb.Append(',');
                first = false;
                AppendString(sb, kv.Key);
                sb.Append(':');
                AppendValue(sb, kv.Value, depth + 1);
            }
            sb.Append('}');
        }

        private static void AppendValue(StringBuilder sb, object value, int depth)
        {
            if (depth > MaxDepth)
            {
                sb.Append("null");
                return;
            }

            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    AppendString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case char c:
                    AppendString(sb, c.ToString());
                    return;

                // InvariantCulture throughout: a device set to a locale using a
                // decimal comma would otherwise emit 1,5 and produce JSON the
                // server cannot parse - a bug that only appears for some users,
                // in some countries.
                case sbyte v: sb.Append(v.ToString(CultureInfo.InvariantCulture)); return;
                case byte v: sb.Append(v.ToString(CultureInfo.InvariantCulture)); return;
                case short v: sb.Append(v.ToString(CultureInfo.InvariantCulture)); return;
                case ushort v: sb.Append(v.ToString(CultureInfo.InvariantCulture)); return;
                case int v: sb.Append(v.ToString(CultureInfo.InvariantCulture)); return;
                case uint v: sb.Append(v.ToString(CultureInfo.InvariantCulture)); return;
                case long v: sb.Append(v.ToString(CultureInfo.InvariantCulture)); return;
                case ulong v: sb.Append(v.ToString(CultureInfo.InvariantCulture)); return;
                case float f:
                    // NaN and the infinities have no JSON spelling. Written raw
                    // they invalidate the whole document, so they become null:
                    // one unknown value instead of a lost event.
                    if (float.IsNaN(f) || float.IsInfinity(f)) sb.Append("null");
                    else sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                    else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return;

                // Timestamps: ISO-8601 in UTC with a real Z, never the device's
                // local time with a Z pasted on (SDK contract 7). An Unspecified
                // kind is treated as local, which is what DateTime.Now produces
                // and what ToUniversalTime assumes.
                case DateTime dt:
                    AppendString(sb, dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                    return;
                case DateTimeOffset dto:
                    AppendString(sb, dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                    return;
                case Enum e:
                    AppendString(sb, e.ToString());
                    return;

                // Nested objects. The generic case first (it is what games
                // write); the non-generic IDictionary catches Hashtable and any
                // Dictionary<string, T> whose T is not object.
                case IDictionary<string, object> dict:
                    AppendObject(sb, dict, depth);
                    return;
                case IDictionary map:
                    AppendObject(sb, Entries(map), depth);
                    return;

                // Arrays and lists - after string, which is also IEnumerable.
                case IEnumerable seq:
                    sb.Append('[');
                    var first = true;
                    foreach (var item in seq)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        AppendValue(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;

                default:
                    AppendString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
            }
        }

        private static IEnumerable<KeyValuePair<string, object>> Entries(IDictionary map)
        {
            foreach (DictionaryEntry entry in map)
            {
                if (entry.Key == null) continue;
                yield return new KeyValuePair<string, object>(
                    Convert.ToString(entry.Key, CultureInfo.InvariantCulture), entry.Value);
            }
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
