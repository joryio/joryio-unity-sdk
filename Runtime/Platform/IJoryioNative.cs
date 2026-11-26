namespace Joryio.Runtime.Platform
{
    /// <summary>
    /// One shape for every platform, including the ones that do nothing.
    ///
    /// The no-op is a STUB IMPLEMENTATION rather than compile-time exclusion of
    /// the API, which is the pattern Firebase uses and the reason it matters:
    /// if the methods vanished off-mobile, every game would have to wrap each
    /// analytics call in `#if UNITY_IOS || UNITY_ANDROID`, and would get it
    /// wrong in exactly one place. Same surface everywhere, does nothing in the
    /// Editor and on desktop, warns once.
    ///
    /// Every method here must exist in BOTH bridges under the same name -
    /// unity-bridge-parity.spec.ts checks the Swift @_cdecl symbols and the
    /// Java statics against this list, because nothing else can.
    /// </summary>
    internal interface IJoryioNative
    {
        void Initialize(string sdkKey, string apiHost, string configJson);
        void SetCallbackTarget(string gameObjectName);

        void Track(string eventName, string propertiesJson);
        void TrackScreen(string screenName, string propertiesJson);
        void Identify(string userId);
        void Reset();
        /// <summary>Answers "true" (adopted) or "false" (refused) through <paramref name="requestId"/>.</summary>
        void ResetTo(string anonymousId, int requestId);
        void Flush();

        string GetUserId();
        string GetAnonymousId();

        /// <summary>Attach a labelled alias; works for an anonymous player too.</summary>
        void AddAlias(string aliasLabel, string aliasName);

        /// <summary>Click-link identity: the URL that opened the app, passed to native unchanged (native reads jr_t).</summary>
        void IdentifyFromLink(string url);
        void SetAttributes(string attributesJson);
        void SetAttribute(string key, string valueJson);
        void IncrementAttribute(string key, double by);
        void UnsetAttribute(string key);

        void OptIn();
        void OptOut();

        /// <summary>"granted", "denied" or "pending".</summary>
        void UpdateConsent(string consent);

        // Marketing subscription - a different consent from OptIn/OptOut.
        void SetSubscription(string channel, string status);
        void AddToSubscriptionGroup(string groupId, string channel);
        void RemoveFromSubscriptionGroup(string groupId, string channel);
        bool IsUserOptedOut();
        void WipeData();
        /// <summary>"granted" / "denied" / "pending", or null/empty when the native SDK has no decision yet.</summary>
        string GetTrackingConsent();

        /// <summary>Answers through the request id with a JSON PushRegistrationResult.</summary>
        void RegisterPushToken(string token, int requestId);

        /// <summary>Report that a push arrived on this device.</summary>
        void ReportPushDelivered(string trackingId);
        void UnregisterPush();
        string GetDeviceToken();
        void TrackPushClick(string trackingId);

        // Callback-shaped on BOTH platforms because the natives disagree about
        // async: these are `async` on iOS and plain functions on Android. The
        // request id routes an answer back to the caller that asked for it.
        void RequestPushPermission(int requestId);
        void GetPushPermissionStatus(int requestId);
        void IsPushEnabled(int requestId);

        // iOS app icon badge. Android has no badge API of its own (launchers
        // decide), so the Android bridge logs and does nothing.
        void UpdateBadgeCount(int count);
        void ClearBadge();

        string GetSessionId();

        /// <summary>JSON: { sessionId, sessionStartMs, durationMs }.</summary>
        string GetSessionInfo();
        void SetSdkAuthenticationToken(string token);

        /// <summary>Route SDK authentication failures to OnSdkAuthError.</summary>
        void EnableSdkAuthenticationErrors();

        void SyncInAppCampaigns();
        void ResetDisplayedCampaigns();
        void EvaluateInAppCampaigns();

        void EnableInAppCallback(string capabilitiesJson);

        /// <summary>
        /// Hand in-app rendering back to the native SDK: the natives'
        /// clearInAppMessageCallback. Without it a null handler left the
        /// native callback installed, so every message was handed to C#,
        /// dropped there, and still occupied the display slot.
        /// </summary>
        void ClearInAppCallback();

        /// <summary>elementId: the tapped button's id for a click, otherwise null.</summary>
        void TrackInAppImpression(string campaignId, string action, string elementId);

        /// <summary>Answers through the request id with a JSON InAppFormSubmitResponse.</summary>
        void SubmitInAppForm(string campaignId, string valuesJson, int requestId);

        // Feature flags (BACKLOG D446). Every getter carries the game's
        // default to the native getter, which answers value-or-default and
        // logs the exposure. Values of any type cross as JSON text.
        string GetFlag(string key, string defaultJson);
        bool GetBoolFlag(string key, bool defaultValue);
        string GetStringFlag(string key, string defaultValue);
        double GetNumberFlag(string key, double defaultValue);
        string GetJsonFlag(string key, string defaultJson);
        /// <summary>JSON object, key -> value. Logs no exposure.</summary>
        string GetAllFlags();
        /// <summary>Answers "true" through <paramref name="requestId"/> when the answer is in.</summary>
        void RefreshFlags(int requestId);
        /// <summary>Start OnFlagsUpdated callbacks (one native subscription; C# fans out).</summary>
        void EnableFlagsUpdates();

        /// <summary>One entry point for every e-commerce event; method is the tracker's name.</summary>
        void Ecommerce(string method, string payloadJson);

        // Placements (D444). The native SDK fetches, caches and reports; these
        // carry JSON across. keysJson: a JSON array of keys ("" = all, refresh only).

        /// <summary>Answers through the request id with the placements JSON (Placement.toJson per key).</summary>
        void GetPlacements(string keysJson, int requestId);
        void RefreshPlacements(string keysJson);
        /// <summary>Native updates go to JoryioCallbackReceiver.OnPlacementsUpdated.</summary>
        void EnablePlacementsUpdates();
        void DisablePlacementsUpdates();
        /// <summary>action: displayed | clicked | dismissed (an item) or control (the holdout spot).</summary>
        void MarkPlacement(string placementKey, string itemId, string action, string elementId);

        // Content cards (D444 Phase 3). The native SDK fetches, caches and reports.

        /// <summary>Answers through the request id with the feed JSON (CardFeed.toJson). cursor: "" for the feed, a nextCursor for the next page.</summary>
        void GetCards(string cursor, int requestId);
        /// <summary>From the native cache, never a network call.</summary>
        int GetUnreadCardCount();
        /// <summary>action: viewed | read | clicked | dismissed (the wire values).</summary>
        void MarkCard(string cardId, string action);
        void RefreshCards();
        /// <summary>Native updates go to JoryioCallbackReceiver.OnCardsUpdated.</summary>
        void EnableCardsUpdates();
        void DisableCardsUpdates();

        // Links (SetLinkHandler). The native handler is app-wide.

        /// <summary>Every link native is about to open goes to JoryioCallbackReceiver.OnLink and waits for CompleteLink.</summary>
        void EnableLinkHandler();
        void DisableLinkHandler();
        /// <summary>handled false: native opens the link as it does without a handler.</summary>
        void CompleteLink(string linkId, bool handled);
    }
}
