using System;
using System.Collections.Generic;
using UnityEngine;

namespace Joryio.Runtime
{
    /// <summary>
    /// Content cards (docs/PLACEMENTS_PLAN.md section 11, BACKLOG D444 Phase 3):
    /// a persistent feed per player - a message center inside the game. Cards
    /// come from card campaigns and the journey step "Add card".
    ///
    /// Data only: Unity has no native view inside a game scene, so the game
    /// draws the feed itself from <see cref="JoryioSDK.GetCards(Action{JoryioCardFeed})"/>
    /// and reports with MarkCardViewed / MarkCardRead / MarkCardClicked /
    /// DismissCard. Fetching, caching, the rate limit, consent, an anonymous
    /// player's device state, the silent push and the reports are the native
    /// iOS / Android SDK's; every report goes back to the native card, which
    /// drops it when the player changed since (identify, reset).
    /// </summary>
    public static partial class JoryioSDK
    {
        /// <summary>The feed goes to the network at most once per this many seconds; extra calls coalesce - the same on every Joryio SDK.</summary>
        public const int CARD_MIN_REFRESH_SECONDS = 30;

        /// <summary>How long the feed is served from the device cache before it is asked for again - the same on every Joryio SDK.</summary>
        public const int CARD_FEED_TTL_SECONDS = 300;

        // The OnCardsUpdated handlers, in subscription order (main thread).
        private static readonly List<Action<JoryioCardFeed>> _cardsHandlers = new List<Action<JoryioCardFeed>>();

        /// <summary>Whether a game handler is subscribed (Initialize switches the native updates on for one held before it).</summary>
        private static bool HasCardsHandlers => _cardsHandlers.Count > 0;

        /// <summary>
        /// The player's card feed - pinned first, newest first - with the
        /// unread count for a badge, served from the native cache for
        /// CARD_FEED_TTL_SECONDS. Answers the empty feed before Initialize or
        /// in the Editor.
        /// </summary>
        public static void GetCards(Action<JoryioCardFeed> onResult) => GetCards(null, onResult);

        /// <summary>
        /// The page after <paramref name="cursor"/> (a feed's NextCursor; null
        /// for the feed). The native SDK adds the page to the cached feed.
        /// </summary>
        public static void GetCards(string cursor, Action<JoryioCardFeed> onResult)
        {
            var settle = Once(onResult);
            if (!Ready(nameof(GetCards))) { settle(JoryioCardFeed.Empty); return; }
            var c = cursor ?? string.Empty;
            Ask(nameof(GetCards), id => Native.GetCards(c, id), v => settle(JoryioCardJson.ParseFeed(v)), () => settle(JoryioCardFeed.Empty));
        }

        /// <summary>Unread cards, from the native cache (never a network call): the badge. 0 before Initialize.</summary>
        public static int GetUnreadCardCount() => Read(nameof(GetUnreadCardCount), () => Math.Max(0, Native.GetUnreadCardCount()), 0);

        /// <summary>The card was on screen (viewed): once at least half of it was visible for IN_APP_DISPLAY_DWELL_MS. Once per card.</summary>
        public static void MarkCardViewed(JoryioCard card) => MarkCard(nameof(MarkCardViewed), card?.Id, "viewed");
        public static void MarkCardViewed(string cardId) => MarkCard(nameof(MarkCardViewed), cardId, "viewed");

        /// <summary>The player read the card (read): it stops counting as unread at once.</summary>
        public static void MarkCardRead(JoryioCard card) => MarkCard(nameof(MarkCardRead), card?.Id, "read");
        public static void MarkCardRead(string cardId) => MarkCard(nameof(MarkCardRead), cardId, "read");

        /// <summary>The player tapped the card or its button (clicked, which also marks it read). Open its Url yourself.</summary>
        public static void MarkCardClicked(JoryioCard card) => MarkCard(nameof(MarkCardClicked), card?.Id, "clicked");
        public static void MarkCardClicked(string cardId) => MarkCard(nameof(MarkCardClicked), cardId, "clicked");

        /// <summary>The player removed the card (dismissed): it leaves the feed at once and never comes back.</summary>
        public static void DismissCard(JoryioCard card) => MarkCard(nameof(DismissCard), card?.Id, "dismissed");
        public static void DismissCard(string cardId) => MarkCard(nameof(DismissCard), cardId, "dismissed");

        /// <summary>Ask the server for the feed again. At most once per CARD_MIN_REFRESH_SECONDS; extra calls coalesce.</summary>
        public static void RefreshCards()
        {
            if (!Ready(nameof(RefreshCards))) return;
            Call(nameof(RefreshCards), () => Native.RefreshCards());
        }

        /// <summary>
        /// Calls <paramref name="handler"/> with the whole feed whenever it
        /// changes (a refresh, a report, an identity change). Returns a
        /// <see cref="JoryioSubscription"/>: Cancel() it (or Dispose it)
        /// to stop - the same subscription and the same cancel on every Joryio
        /// SDK. The native updates are switched on with the first handler and
        /// off with the last; a handler added before Initialize is kept and
        /// switched on by Initialize.
        /// </summary>
        public static JoryioSubscription OnCardsUpdated(Action<JoryioCardFeed> handler)
        {
            if (handler == null) return new JoryioSubscription(null);
            var first = !HasCardsHandlers;
            _cardsHandlers.Add(handler);
            if (first && _initialized) Call(nameof(OnCardsUpdated), () => Native.EnableCardsUpdates());
            return new JoryioSubscription(() => RemoveCardsHandler(handler));
        }

        private static void RemoveCardsHandler(Action<JoryioCardFeed> handler)
        {
            // By reference, so the same lambda subscribed twice is two subscriptions.
            var i = _cardsHandlers.FindIndex(h => ReferenceEquals(h, handler));
            if (i < 0) return;
            _cardsHandlers.RemoveAt(i);
            if (!HasCardsHandlers && _initialized) Call(nameof(OnCardsUpdated), () => Native.DisableCardsUpdates());
        }

        internal static void RaiseCardsUpdated(string json)
        {
            if (!HasCardsHandlers) return;
            var feed = JoryioCardJson.ParseFeed(json);
            // A copy: a handler may cancel its own (or another) subscription.
            foreach (var handler in _cardsHandlers.ToArray())
            {
                try { handler(feed); }
                catch (Exception e) { Debug.LogError($"[Joryio] OnCardsUpdated handler threw: {e}"); }
            }
        }

        private static void MarkCard(string method, string cardId, string action)
        {
            if (!Ready(method, cardId)) return;
            Call(method, () => Native.MarkCard(cardId, action));
        }
    }

    /// <summary>One card of the player's feed - Card on the other Joryio SDKs.</summary>
    public sealed class JoryioCard
    {
        /// <summary>Stable id of this card for this player.</summary>
        public string Id { get; }
        /// <summary>Campaign id, or journey:&lt;canvasId&gt;:&lt;nodeId&gt; for a journey card.</summary>
        public string CampaignId { get; }
        public string VariantId { get; }
        public string Title { get; }
        public string Body { get; }
        public string ImageUrl { get; }
        /// <summary>Button label; null = no button (the whole card opens Url when set).</summary>
        public string ButtonText { get; }
        /// <summary>An http(s) URL or the game's own deep link; null = nothing to open. Open only http(s) with Application.OpenURL; route a deep link yourself; never open javascript:, data:, file: or intent: links.</summary>
        public string Url { get; }
        public bool Pinned { get; }
        /// <summary>ISO 8601.</summary>
        public string CreatedAt { get; }
        /// <summary>ISO 8601, or null.</summary>
        public string ExpiresAt { get; }
        public bool Read { get; }
        public bool Clicked { get; }

        internal JoryioCard(string id, string campaignId, string variantId, string title, string body, string imageUrl,
            string buttonText, string url, bool pinned, string createdAt, string expiresAt, bool read, bool clicked)
        {
            Id = id; CampaignId = campaignId; VariantId = variantId; Title = title; Body = body; ImageUrl = imageUrl;
            ButtonText = buttonText; Url = url; Pinned = pinned; CreatedAt = createdAt; ExpiresAt = expiresAt; Read = read; Clicked = clicked;
        }
    }

    /// <summary>The player's feed (or one page of it) - CardFeed on the other Joryio SDKs.</summary>
    public sealed class JoryioCardFeed
    {
        /// <summary>Pinned first, then newest first.</summary>
        public JoryioCard[] Cards { get; }
        /// <summary>Unread cards across the whole feed, not this page: the badge.</summary>
        public int UnreadCount { get; }
        /// <summary>Pass to GetCards(cursor, ...) for the next page; null on the last one.</summary>
        public string NextCursor { get; }
        /// <summary>server (read / dismissed kept per player on the server) or device (an anonymous player: this device keeps them).</summary>
        public string StateScope { get; }

        internal JoryioCardFeed(JoryioCard[] cards, int unreadCount, string nextCursor, string stateScope)
        {
            Cards = cards; UnreadCount = unreadCount; NextCursor = nextCursor; StateScope = stateScope;
        }

        internal static JoryioCardFeed Empty => new JoryioCardFeed(new JoryioCard[0], 0, null, "server");
    }

    /// <summary>The bridge JSON (CardFeed.toJson on iOS / Android) to the C# objects. A bad answer is the empty feed.</summary>
    internal static class JoryioCardJson
    {
        internal static JoryioCardFeed ParseFeed(string json)
        {
            if (string.IsNullOrEmpty(json)) return JoryioCardFeed.Empty;
            if (!(MiniJson.Parse(json) is Dictionary<string, object> root)) return JoryioCardFeed.Empty;
            var cards = new List<JoryioCard>();
            if (root.TryGetValue("cards", out var raw) && raw is List<object> list)
            {
                foreach (var item in list)
                {
                    if (!(item is Dictionary<string, object> c) || string.IsNullOrEmpty(Str(c, "id"))) continue;
                    cards.Add(new JoryioCard(
                        Str(c, "id"), Str(c, "campaignId") ?? string.Empty, Str(c, "variantId"), Str(c, "title"), Str(c, "body"),
                        Str(c, "imageUrl"), Str(c, "buttonText"), Str(c, "url"), Bool(c, "pinned"), Str(c, "createdAt") ?? string.Empty,
                        Str(c, "expiresAt"), Bool(c, "read"), Bool(c, "clicked")));
                }
            }
            var unread = root.TryGetValue("unreadCount", out var u) && u is double d && d > 0 ? (int)d : 0;
            return new JoryioCardFeed(cards.ToArray(), unread, Str(root, "nextCursor"), Str(root, "stateScope") == "device" ? "device" : "server");
        }

        private static string Str(Dictionary<string, object> o, string k) => o.TryGetValue(k, out var v) ? v as string : null;

        private static bool Bool(Dictionary<string, object> o, string k) => o.TryGetValue(k, out var v) && v is bool b && b;
    }
}
