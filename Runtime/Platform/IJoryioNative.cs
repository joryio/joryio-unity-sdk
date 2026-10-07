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
        void Flush();

        string GetUserId();
        string GetAnonymousId();

        /// <summary>Attach a labelled alias; works for an anonymous player too.</summary>
        void AddAlias(string aliasLabel, string aliasName);
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

        /// <summary>One entry point for every e-commerce event; method is the tracker's name.</summary>
        void Ecommerce(string method, string payloadJson);
    }
}
