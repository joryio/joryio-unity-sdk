#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;

namespace Joryio.Runtime.Platform
{
    /// <summary>
    /// JNI into the Java bridge.
    ///
    /// One AndroidJavaClass held for the process rather than constructed per
    /// call: each construction is a JNI class lookup, and this is on the path of
    /// every tracked event.
    ///
    /// Arguments are never null here: JoryioSDK rejects a null string before it
    /// gets this far, because AndroidJavaObject cannot infer a JNI signature
    /// from a null and throws instead of passing one.
    /// </summary>
    internal sealed class JoryioAndroid : IJoryioNative
    {
        private const string BridgeClass = "io.joryio.unity.JoryioUnityBridge";
        private static readonly AndroidJavaClass Bridge = new AndroidJavaClass(BridgeClass);

        public void Initialize(string sdkKey, string apiHost, string configJson) =>
            Bridge.CallStatic("initialize", sdkKey, apiHost, configJson);

        public void SetCallbackTarget(string gameObjectName) =>
            Bridge.CallStatic("setCallbackTarget", gameObjectName);

        public void Track(string eventName, string propertiesJson) =>
            Bridge.CallStatic("track", eventName, propertiesJson);

        public void TrackScreen(string screenName, string propertiesJson) =>
            Bridge.CallStatic("trackScreen", screenName, propertiesJson);

        public void Identify(string userId) => Bridge.CallStatic("identify", userId);
        public void Reset() => Bridge.CallStatic("reset");
        public void ResetTo(string anonymousId, int requestId) => Bridge.CallStatic("resetTo", anonymousId, requestId);
        public void Flush() => Bridge.CallStatic("flush");
        public string GetUserId() => Bridge.CallStatic<string>("getUserId");
        public string GetAnonymousId() => Bridge.CallStatic<string>("getAnonymousId");
        public string GetFlag(string key, string defaultJson) => Bridge.CallStatic<string>("getFlag", key, defaultJson);
        public bool GetBoolFlag(string key, bool defaultValue) => Bridge.CallStatic<bool>("getBoolFlag", key, defaultValue);
        public string GetStringFlag(string key, string defaultValue) => Bridge.CallStatic<string>("getStringFlag", key, defaultValue);
        public double GetNumberFlag(string key, double defaultValue) => Bridge.CallStatic<double>("getNumberFlag", key, defaultValue);
        public string GetJsonFlag(string key, string defaultJson) => Bridge.CallStatic<string>("getJsonFlag", key, defaultJson);
        public string GetAllFlags() => Bridge.CallStatic<string>("getAllFlags");
        public void RefreshFlags(int requestId) => Bridge.CallStatic("refreshFlags", requestId);
        public void EnableFlagsUpdates() => Bridge.CallStatic("enableFlagsUpdates");
        public void AddAlias(string aliasLabel, string aliasName) => Bridge.CallStatic("addAlias", aliasLabel, aliasName);
        public void IdentifyFromLink(string url) => Bridge.CallStatic("identifyFromLink", url);
        public void SetAttributes(string attributesJson) => Bridge.CallStatic("setAttributes", attributesJson);
        public void SetAttribute(string key, string valueJson) => Bridge.CallStatic("setAttribute", key, valueJson);
        public void IncrementAttribute(string key, double by) => Bridge.CallStatic("incrementAttribute", key, by);
        public void UnsetAttribute(string key) => Bridge.CallStatic("unsetAttribute", key);
        public void OptIn() => Bridge.CallStatic("optIn");
        public void OptOut() => Bridge.CallStatic("optOut");
        public void UpdateConsent(string consent) => Bridge.CallStatic("updateConsent", consent);

        public void SetSubscription(string channel, string status) =>
            Bridge.CallStatic("setSubscription", channel, status);

        public void AddToSubscriptionGroup(string groupId, string channel) =>
            Bridge.CallStatic("addToSubscriptionGroup", groupId, channel);

        public void RemoveFromSubscriptionGroup(string groupId, string channel) =>
            Bridge.CallStatic("removeFromSubscriptionGroup", groupId, channel);
        public bool IsUserOptedOut() => Bridge.CallStatic<bool>("isUserOptedOut");
        public void WipeData() => Bridge.CallStatic("wipeData");
        public string GetTrackingConsent() => Bridge.CallStatic<string>("getTrackingConsent");
        public void RegisterPushToken(string token, int requestId) => Bridge.CallStatic("registerPushToken", token, requestId);
        public void ReportPushDelivered(string trackingId) => Bridge.CallStatic("reportPushDelivered", trackingId);
        public void UnregisterPush() => Bridge.CallStatic("unregisterPush");
        public string GetDeviceToken() => Bridge.CallStatic<string>("getDeviceToken");
        public void TrackPushClick(string trackingId) => Bridge.CallStatic("trackPushClick", trackingId);
        public void RequestPushPermission(int requestId) => Bridge.CallStatic("requestPushPermission", requestId);
        public void GetPushPermissionStatus(int requestId) => Bridge.CallStatic("getPushPermissionStatus", requestId);
        public void IsPushEnabled(int requestId) => Bridge.CallStatic("isPushEnabled", requestId);
        public void UpdateBadgeCount(int count) => Bridge.CallStatic("updateBadgeCount", count);
        public void ClearBadge() => Bridge.CallStatic("clearBadge");
        public string GetSessionId() => Bridge.CallStatic<string>("getSessionId");
        public string GetSessionInfo() => Bridge.CallStatic<string>("getSessionInfo");
        public void SetSdkAuthenticationToken(string token) => Bridge.CallStatic("setSdkAuthenticationToken", token);
        public void EnableSdkAuthenticationErrors() => Bridge.CallStatic("enableSdkAuthenticationErrors");
        public void SyncInAppCampaigns() => Bridge.CallStatic("syncInAppCampaigns");
        public void ResetDisplayedCampaigns() => Bridge.CallStatic("resetDisplayedCampaigns");
        public void EvaluateInAppCampaigns() => Bridge.CallStatic("evaluateInAppCampaigns");
        public void EnableInAppCallback(string capabilitiesJson) => Bridge.CallStatic("enableInAppCallback", capabilitiesJson);
        public void ClearInAppCallback() => Bridge.CallStatic("clearInAppCallback");

        // elementId may legitimately be absent, and a null cannot cross JNI as a
        // typed argument - "" stands for "none" and the bridge maps it back.
        public void TrackInAppImpression(string campaignId, string action, string elementId) =>
            Bridge.CallStatic("trackInAppImpression", campaignId, action, elementId ?? string.Empty);

        public void GetPlacements(string keysJson, int requestId) => Bridge.CallStatic("getPlacements", keysJson, requestId);
        public void RefreshPlacements(string keysJson) => Bridge.CallStatic("refreshPlacements", keysJson ?? string.Empty);
        public void EnablePlacementsUpdates() => Bridge.CallStatic("enablePlacementsUpdates");
        public void DisablePlacementsUpdates() => Bridge.CallStatic("disablePlacementsUpdates");
        public void MarkPlacement(string placementKey, string itemId, string action, string elementId) =>
            Bridge.CallStatic("markPlacement", placementKey, itemId ?? string.Empty, action, elementId ?? string.Empty);

        public void GetCards(string cursor, int requestId) => Bridge.CallStatic("getCards", cursor ?? string.Empty, requestId);
        public int GetUnreadCardCount() => Bridge.CallStatic<int>("getUnreadCardCount");
        public void MarkCard(string cardId, string action) => Bridge.CallStatic("markCard", cardId ?? string.Empty, action ?? string.Empty);
        public void RefreshCards() => Bridge.CallStatic("refreshCards");
        public void EnableCardsUpdates() => Bridge.CallStatic("enableCardsUpdates");
        public void DisableCardsUpdates() => Bridge.CallStatic("disableCardsUpdates");

        public void EnableLinkHandler() => Bridge.CallStatic("enableLinkHandler");
        public void DisableLinkHandler() => Bridge.CallStatic("disableLinkHandler");
        public void CompleteLink(string linkId, bool handled) => Bridge.CallStatic("completeLink", linkId ?? string.Empty, handled);

        public void SubmitInAppForm(string campaignId, string valuesJson, int requestId) =>
            Bridge.CallStatic("submitInAppForm", campaignId, valuesJson, requestId);

        public void Ecommerce(string method, string payloadJson) =>
            Bridge.CallStatic("ecommerce", method, payloadJson);
    }
}
#endif
