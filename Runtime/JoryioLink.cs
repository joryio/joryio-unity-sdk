using System;
using System.Collections.Generic;
using UnityEngine;

namespace Joryio.Runtime
{
    /// <summary>
    /// A link the native SDK is about to open - an in-app message button or
    /// link the native renderer draws, or an iOS push tap - handed to the
    /// game's <see cref="JoryioSDK.SetLinkHandler"/> first.
    /// </summary>
    public sealed class JoryioLink
    {
        /// <summary>The wire values of <see cref="Source"/>, the same on every Joryio SDK.</summary>
        public const string SourceInApp = "in_app";
        public const string SourcePlacement = "placement";
        public const string SourceCard = "card";
        public const string SourcePush = "push";

        internal readonly string LinkId;

        /// <summary>An http(s) link or an app deep link (myapp://cart); a push link may be another scheme the system opens.</summary>
        public string Url { get; }
        /// <summary>in_app, placement, card or push (iOS only: Android never opens a push link itself).</summary>
        public string Source { get; }
        public string CampaignId { get; }
        public string PlacementKey { get; }
        public string ItemId { get; }
        public string CardId { get; }
        /// <summary>True for an app-scheme link, false for http(s).</summary>
        public bool IsAppDeepLink { get; }

        internal JoryioLink(string linkId, string url, string source, string campaignId, string placementKey, string itemId, string cardId, bool isAppDeepLink)
        {
            LinkId = linkId;
            Url = url;
            Source = source;
            CampaignId = campaignId;
            PlacementKey = placementKey;
            ItemId = itemId;
            CardId = cardId;
            IsAppDeepLink = isAppDeepLink;
        }

        /// <summary>
        /// Open the link as the native SDK does without a handler - for a
        /// handler that returned true and decides later. Runs at most once;
        /// the native side keeps the 32 most recent links for this.
        /// </summary>
        public void OpenDefault() => JoryioSDK.CompleteLink(LinkId, false);

        internal static JoryioLink Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || !(MiniJson.Parse(json) is Dictionary<string, object> o)) return null;
            var id = Str(o, "linkId");
            var url = Str(o, "url");
            var source = Str(o, "source");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url)) return null;
            if (source != SourceInApp && source != SourcePlacement && source != SourceCard && source != SourcePush) return null;
            return new JoryioLink(id, url, source, Str(o, "campaignId"), Str(o, "placementKey"), Str(o, "itemId"), Str(o, "cardId"),
                o.TryGetValue("isAppDeepLink", out var b) && b is bool flag && flag);
        }

        internal static string LinkIdOf(string json) =>
            !string.IsNullOrEmpty(json) && MiniJson.Parse(json) is Dictionary<string, object> o ? Str(o, "linkId") : null;

        private static string Str(Dictionary<string, object> o, string k) => o.TryGetValue(k, out var v) ? v as string : null;
    }

    public static partial class JoryioSDK
    {
        private static Func<JoryioLink, bool> _linkHandler;

        /// <summary>
        /// Receive EVERY link the native SDK is about to open - an in-app
        /// message button or link the native renderer draws, an iOS push tap -
        /// before it opens: http(s) links and app deep links alike, with where
        /// it came from (<see cref="JoryioLink.Source"/>) and the ids that go
        /// with it. Return true to take the link over (route it in the game),
        /// false to let the native SDK open it as it does without a handler.
        /// To decide later, return true and call <see cref="JoryioLink.OpenDefault"/>.
        /// A refused scheme (javascript:, data:, intent:, ...) never reaches it.
        /// One handler: a second call replaces the first. Same name on iOS,
        /// Android, React Native and web (setLinkHandler).
        /// </summary>
        public static void SetLinkHandler(Func<JoryioLink, bool> handler)
        {
            if (handler == null)
            {
                ClearLinkHandler();
                return;
            }
            var first = _linkHandler == null;
            _linkHandler = handler;
            // The native handler is app-wide; switched on at Initialize when set before it.
            if (first && _initialized) Call(nameof(SetLinkHandler), () => Native.EnableLinkHandler());
        }

        /// <summary>Remove the handler set with SetLinkHandler: links open as they do without one.</summary>
        public static void ClearLinkHandler()
        {
            var had = _linkHandler != null;
            _linkHandler = null;
            if (had && _initialized) Call(nameof(ClearLinkHandler), () => Native.DisableLinkHandler());
        }

        internal static void CompleteLink(string linkId, bool handled)
        {
            if (string.IsNullOrEmpty(linkId)) return;
            Invoke(nameof(CompleteLink), () => Native.CompleteLink(linkId, handled));
        }

        /// <summary>A link from native (JoryioCallbackReceiver.OnLink): ask the handler; a decline goes back to native.</summary>
        internal static void RaiseLink(string json)
        {
            var link = JoryioLink.Parse(json);
            var handler = _linkHandler;
            if (link == null || handler == null)
            {
                CompleteLink(link?.LinkId ?? JoryioLink.LinkIdOf(json), false);
                return;
            }
            bool taken;
            try { taken = handler(link); }
            catch (Exception e)
            {
                // A throwing handler declines: the link still opens.
                Debug.LogError($"[Joryio] SetLinkHandler handler threw; the link opens as without one: {e}");
                taken = false;
            }
            if (!taken) CompleteLink(link.LinkId, false);
        }
    }
}
