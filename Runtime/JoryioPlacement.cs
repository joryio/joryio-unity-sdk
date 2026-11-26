using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Joryio.Runtime.Platform;

namespace Joryio.Runtime
{
    /// <summary>
    /// Placements (docs/PLACEMENTS_PLAN.md, BACKLOG D444): named spots in the
    /// game - <c>home_hero</c>, <c>shop_offer</c> - that Joryio fills per
    /// player from placement campaigns and journey steps.
    ///
    /// Unity has no native view inside a game scene, so the SDK hands the
    /// game the data (<see cref="JoryioSDK.GetPlacements"/>) and the game
    /// draws it. Fetching, caching, the rate limit, consent and the reports
    /// are the native iOS / Android SDK's: every Mark* call goes back to the
    /// native object, which drops it when the player changed since
    /// (identify, reset). <see cref="JoryioPlacementVisibility"/> measures
    /// viewability for a RectTransform.
    /// </summary>
    public static partial class JoryioSDK
    {
        /// <summary>Share of an item that must be on screen to count as seen - the same on every Joryio SDK.</summary>
        public const double PLACEMENT_VIEWABLE_FRACTION = 0.5;

        /// <summary>A placement key is fetched from the network at most once per this many seconds - the same on every Joryio SDK.</summary>
        public const int PLACEMENT_MIN_REFRESH_SECONDS = 30;

        /// <summary>After a request that got no answer (or a 5xx) its keys may go again after this many seconds; a 429 keeps the full window - the same on every Joryio SDK (the native SDK applies it).</summary>
        public const int PLACEMENT_RETRY_AFTER_FAILURE_SECONDS = 3;

        /// <summary>
        /// A product card's tap is reported as <c>clicked</c> with the elementId
        /// <c>product:&lt;product id&gt;</c> (at most 128 characters) - the same on every
        /// Joryio SDK, whose placement views report a drawn product card this way.
        /// The game draws its own cards: pass <see cref="PlacementProductElementId"/>
        /// to <see cref="JoryioPlacementItem.MarkClicked"/>.
        /// </summary>
        public const string PLACEMENT_PRODUCT_ELEMENT_PREFIX = "product:";

        /// <summary>Width of a product card the other SDKs' placement views draw when the app sets none (points / dp / CSS px).</summary>
        public const int PLACEMENT_PRODUCT_CARD_WIDTH = 160;

        /// <summary>
        /// "Show when" (BACKLOG D465): the moment a placement campaign's content is
        /// revealed on a screen - the <c>kind</c> values the wire may carry. The native
        /// SDK (iOS / Android) holds the content and hands it over at its moment: an item
        /// reaches <see cref="GetPlacements"/> / <see cref="OnPlacementsUpdated"/> only once
        /// revealed. Apps honour <c>load</c>, <c>seconds</c> (foreground seconds on the
        /// screen) and <c>event</c>; <c>scroll</c> / <c>exit</c> are web-only and never shown.
        /// </summary>
        public static readonly string[] SHOW_WHEN_KINDS = { "load", "seconds", "scroll", "exit", "event" };

        /// <summary>The elementId to report a product card's click with: <c>product:&lt;id&gt;</c>, at most 128 characters.</summary>
        public static string PlacementProductElementId(string productId)
        {
            var id = PLACEMENT_PRODUCT_ELEMENT_PREFIX + (productId ?? "");
            return id.Length > 128 ? id.Substring(0, 128) : id;
        }

        // The OnPlacementsUpdated handlers, in subscription order (main thread).
        private static readonly List<Action<Dictionary<string, JoryioPlacement>>> _placementsHandlers = new List<Action<Dictionary<string, JoryioPlacement>>>();

        /// <summary>Whether a game handler is subscribed (Initialize switches the native updates on for one held before it).</summary>
        private static bool HasPlacementsHandlers => _placementsHandlers.Count > 0;

        /// <summary>
        /// The placements for <paramref name="placementKeys"/>, to draw in the
        /// game. Report with <see cref="JoryioPlacementItem.MarkDisplayed"/>
        /// (once at least half of it was on screen for IN_APP_DISPLAY_DWELL_MS -
        /// <see cref="JoryioPlacementVisibility"/> does that),
        /// MarkClicked and MarkDismissed; for a held-out player
        /// (<see cref="JoryioPlacement.IsControl"/>), JoryioPlacement.MarkDisplayed.
        /// Answers an empty dictionary before Initialize or in the Editor.
        /// </summary>
        public static void GetPlacements(string[] placementKeys, Action<Dictionary<string, JoryioPlacement>> onResult)
        {
            var settle = Once(onResult);
            var empty = new Dictionary<string, JoryioPlacement>();
            if (!Ready(nameof(GetPlacements))) { settle(empty); return; }
            var keys = KeysJson(placementKeys);
            Ask(nameof(GetPlacements), id => Native.GetPlacements(keys, id), v => settle(JoryioPlacementJson.ParsePlacements(v)), () => settle(empty));
        }

        /// <summary>Ask the server again (null = every placement the SDK knows). At most once per key per PLACEMENT_MIN_REFRESH_SECONDS.</summary>
        public static void RefreshPlacements(string[] placementKeys = null)
        {
            if (!Ready(nameof(RefreshPlacements))) return;
            var keys = placementKeys == null ? string.Empty : KeysJson(placementKeys);
            Call(nameof(RefreshPlacements), () => Native.RefreshPlacements(keys));
        }

        /// <summary>
        /// Calls <paramref name="handler"/> with the placements whose content
        /// changed. Returns a <see cref="JoryioSubscription"/>: Cancel() it (or
        /// Dispose it) to stop - the same subscription and cancel on every
        /// Joryio SDK. The native updates are switched on with the first handler
        /// and off with the last; a handler added before Initialize is kept and
        /// switched on by Initialize.
        /// </summary>
        public static JoryioSubscription OnPlacementsUpdated(Action<Dictionary<string, JoryioPlacement>> handler)
        {
            if (handler == null) return new JoryioSubscription(null);
            var first = !HasPlacementsHandlers;
            _placementsHandlers.Add(handler);
            if (first && _initialized) Call(nameof(OnPlacementsUpdated), () => Native.EnablePlacementsUpdates());
            return new JoryioSubscription(() => RemovePlacementsHandler(handler));
        }

        private static void RemovePlacementsHandler(Action<Dictionary<string, JoryioPlacement>> handler)
        {
            var i = _placementsHandlers.FindIndex(h => ReferenceEquals(h, handler));
            if (i < 0) return;
            _placementsHandlers.RemoveAt(i);
            if (!HasPlacementsHandlers && _initialized) Call(nameof(OnPlacementsUpdated), () => Native.DisablePlacementsUpdates());
        }

        internal static void RaisePlacementsUpdated(string json)
        {
            if (!HasPlacementsHandlers) return;
            Dictionary<string, JoryioPlacement> placements;
            try { placements = JoryioPlacementJson.ParsePlacements(json); }
            catch (Exception e)
            {
                Debug.LogError($"[Joryio] Could not parse placements: {e.Message}");
                return;
            }
            // A copy: a handler may cancel its own (or another) subscription. Each
            // handler runs on its own, so one that throws does not silence the rest.
            foreach (var handler in _placementsHandlers.ToArray())
            {
                try { handler(placements); }
                catch (Exception e) { Debug.LogError($"[Joryio] OnPlacementsUpdated handler threw: {e}"); }
            }
        }

        internal static void MarkPlacement(string placementKey, string itemId, string action, string elementId)
        {
            if (!Ready(nameof(MarkPlacement), placementKey, action)) return;
            Call(nameof(MarkPlacement), () => Native.MarkPlacement(placementKey, itemId ?? string.Empty, action, elementId ?? string.Empty));
        }

        private static string KeysJson(string[] keys)
        {
            var list = new List<object>();
            if (keys != null) foreach (var k in keys) if (!string.IsNullOrEmpty(k)) list.Add(k);
            return JoryioJson.SerializeValue(list);
        }
    }

    /// <summary>A button of a native item.</summary>
    public sealed class JoryioPlacementButton
    {
        public string Id { get; }
        public string Text { get; }
        /// <summary>url, deep_link, dismiss, request_push_permission... (the in-app button actions).</summary>
        public string Action { get; }
        public string Url { get; }

        internal JoryioPlacementButton(string id, string text, string action, string url)
        {
            Id = id; Text = text; Action = action; Url = url;
        }
    }

    /// <summary>A product card of a recommendation placement item (D444 Phase 2b). Same names on all five SDKs.</summary>
    public sealed class JoryioPlacementProduct
    {
        public string Id { get; }
        public string Title { get; }
        public string Price { get; }
        public string CompareAtPrice { get; }
        /// <summary>Null when the product is not discounted.</summary>
        public double? DiscountPercent { get; }
        public string ImageUrl { get; }
        public string Url { get; }

        internal JoryioPlacementProduct(string id, string title, string price, string compareAtPrice, double? discountPercent, string imageUrl, string url)
        {
            Id = id; Title = title; Price = price; CompareAtPrice = compareAtPrice; DiscountPercent = discountPercent; ImageUrl = imageUrl; Url = url;
        }
    }

    /// <summary>One item to draw, and the calls that report what the player did with it.</summary>
    public sealed class JoryioPlacementItem
    {
        public string PlacementKey { get; }
        public string Id { get; }
        public string VariantId { get; }
        /// <summary>native, html or data.</summary>
        public string Kind { get; }
        /// <summary>The placement's fields: string, double, bool or null values.</summary>
        public IReadOnlyDictionary<string, object> Data { get; }
        public string Title { get; }
        public string Body { get; }
        public string ImageUrl { get; }
        public JoryioPlacementButton[] Buttons { get; }
        /// <summary>Products picked for the player by a recommendation model; empty when the item has none. Draw them as cards.</summary>
        public JoryioPlacementProduct[] Products { get; }

        internal JoryioPlacementItem(string placementKey, string id, string variantId, string kind, Dictionary<string, object> data,
            string title, string body, string imageUrl, JoryioPlacementButton[] buttons, JoryioPlacementProduct[] products = null)
        {
            PlacementKey = placementKey; Id = id; VariantId = variantId; Kind = kind; Data = data;
            Title = title; Body = body; ImageUrl = imageUrl; Buttons = buttons;
            Products = products ?? new JoryioPlacementProduct[0];
        }

        /// <summary>A viewable impression (the game drew it). The native SDK counts it once per delivery.</summary>
        public void MarkDisplayed() => JoryioSDK.MarkPlacement(PlacementKey, Id, "displayed", null);

        /// <summary>A tap; elementId names the button.</summary>
        public void MarkClicked(string elementId = null) => JoryioSDK.MarkPlacement(PlacementKey, Id, "clicked", elementId);

        /// <summary>The player closed it. The placement's dismissal rule applies on the server.</summary>
        public void MarkDismissed() => JoryioSDK.MarkPlacement(PlacementKey, Id, "dismissed", null);
    }

    /// <summary>A placement as the server answered it for this player.</summary>
    public sealed class JoryioPlacement
    {
        public string PlacementKey { get; }
        public JoryioPlacementItem[] Items { get; }
        /// <summary>Held out (the placement's control group): nothing to show.</summary>
        public bool IsControl { get; }
        /// <summary>collapse or keep_space.</summary>
        public string EmptyBehavior { get; }
        public int MaxItems { get; }
        public int TtlSeconds { get; }

        internal JoryioPlacement(string placementKey, JoryioPlacementItem[] items, bool isControl, string emptyBehavior, int maxItems, int ttlSeconds)
        {
            PlacementKey = placementKey; Items = items; IsControl = isControl; EmptyBehavior = emptyBehavior; MaxItems = maxItems; TtlSeconds = ttlSeconds;
        }

        /// <summary>For a held-out player (IsControl): the empty spot was on screen (in_app.control_viewed).</summary>
        public void MarkDisplayed() => JoryioSDK.MarkPlacement(PlacementKey, string.Empty, "control", null);
    }

    /// <summary>The bridge JSON (Placement.toJson on iOS / Android) to the C# objects.</summary>
    internal static class JoryioPlacementJson
    {
        internal static Dictionary<string, JoryioPlacement> ParsePlacements(string json)
        {
            var result = new Dictionary<string, JoryioPlacement>();
            if (string.IsNullOrEmpty(json)) return result;
            if (!(MiniJson.Parse(json) is Dictionary<string, object> root)) return result;
            foreach (var entry in root)
            {
                if (!(entry.Value is Dictionary<string, object> p)) continue;
                var key = Str(p, "placementKey") ?? entry.Key;
                var items = new List<JoryioPlacementItem>();
                if (p.TryGetValue("items", out var rawItems) && rawItems is List<object> list)
                {
                    foreach (var raw in list)
                    {
                        if (!(raw is Dictionary<string, object> i) || Str(i, "id") == null) continue;
                        var data = i.TryGetValue("data", out var d) && d is Dictionary<string, object> dd ? dd : new Dictionary<string, object>();
                        var content = i.TryGetValue("content", out var c) ? c as Dictionary<string, object> : null;
                        var buttons = new List<JoryioPlacementButton>();
                        if (content != null && content.TryGetValue("buttons", out var rb) && rb is List<object> bl)
                        {
                            foreach (var b in bl)
                                if (b is Dictionary<string, object> bo)
                                    buttons.Add(new JoryioPlacementButton(Str(bo, "id") ?? "", Str(bo, "text") ?? "", Str(bo, "action") ?? "", Str(bo, "url")));
                        }
                        var products = new List<JoryioPlacementProduct>();
                        if (i.TryGetValue("products", out var rp) && rp is List<object> pl)
                        {
                            foreach (var pr in pl)
                                if (pr is Dictionary<string, object> po && Str(po, "id") != null)
                                    products.Add(new JoryioPlacementProduct(Str(po, "id"), Str(po, "title"), Str(po, "price"), Str(po, "compareAtPrice"),
                                        po.TryGetValue("discountPercent", out var dp) && dp is double dpd ? dpd : (double?)null,
                                        Str(po, "imageUrl"), Str(po, "url")));
                        }
                        items.Add(new JoryioPlacementItem(key, Str(i, "id"), Str(i, "variantId"), Str(i, "kind") ?? "data", data,
                            content == null ? null : Str(content, "title"),
                            content == null ? null : Str(content, "body"),
                            content == null ? null : Str(content, "imageUrl"),
                            buttons.ToArray(),
                            products.ToArray()));
                    }
                }
                result[entry.Key] = new JoryioPlacement(
                    key,
                    items.ToArray(),
                    p.TryGetValue("isControl", out var ic) && ic is bool icb && icb,
                    Str(p, "emptyBehavior") == "keep_space" ? "keep_space" : "collapse",
                    Int(p, "maxItems", 1),
                    Int(p, "ttlSeconds", 300));
            }
            return result;
        }

        private static string Str(Dictionary<string, object> o, string k) => o.TryGetValue(k, out var v) ? v as string : null;

        private static int Int(Dictionary<string, object> o, string k, int fallback) =>
            o.TryGetValue(k, out var v) && v is double d ? (int)d : fallback;
    }

    /// <summary>A small JSON reader (objects, arrays, strings, numbers as double, bool, null).</summary>
    internal static class MiniJson
    {
        internal static object Parse(string json)
        {
            var i = 0;
            try
            {
                var v = Value(json, ref i);
                Ws(json, ref i);
                return i == json.Length ? v : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("end");
            var c = s[i];
            if (c == '{')
            {
                i++;
                var o = new Dictionary<string, object>();
                Ws(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    var k = Str(s, ref i);
                    Ws(s, ref i);
                    if (s[i++] != ':') throw new FormatException(":");
                    o[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("}");
                }
            }
            if (c == '[')
            {
                i++;
                var a = new List<object>();
                Ws(s, ref i);
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("]");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            var start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("value");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("\"");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                var c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                var e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
        }
    }
}
