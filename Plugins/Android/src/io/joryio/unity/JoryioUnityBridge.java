package io.joryio.unity;

import android.app.Activity;
import android.util.Log;
import com.unity3d.player.UnityPlayer;
import io.joryio.sdk.Joryio;
import io.joryio.sdk.JoryioConfig;
import io.joryio.sdk.SdkAuthError;
import io.joryio.sdk.TrackingConsent;
import io.joryio.sdk.ecommerce.EcommerceCartItem;
import io.joryio.sdk.ecommerce.EcommerceOrder;
import io.joryio.sdk.ecommerce.EcommerceProduct;
import io.joryio.sdk.ecommerce.EcommerceTracker;
import io.joryio.sdk.models.InAppContent;
import io.joryio.sdk.models.MessageType;
import io.joryio.sdk.models.SessionInfo;
import io.joryio.sdk.push.PushPermissionStatus;
import org.json.JSONArray;
import org.json.JSONObject;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import kotlin.jvm.functions.Function1;

/**
 * Unity to Joryio Android bridge.
 *
 * WHY A JAVA CLASS AND NOT DIRECT JNI. C# can reach Kotlin through
 * AndroidJavaClass/AndroidJavaObject, but every call then pays for it: a Kotlin
 * companion object is reached as a nested INSTANCE field, default arguments do
 * not exist at the JVM level so every optional parameter must be passed
 * explicitly, and Map&lt;String, Any?&gt; has to be built as a java.util.HashMap
 * through reflection from C#. Doing that at each of ~40 call sites is where the
 * bugs would live.
 *
 * This class flattens all of it to static methods taking strings, so the C# side
 * is one AndroidJavaClass and a CallStatic per method - which is also what makes
 * the C# identical in shape to the iOS side.
 *
 * STRINGS, NOT OBJECTS, on the boundary. Properties cross as JSON and are parsed
 * here. It costs a parse per event and removes an entire class of marshalling
 * bug; the SDK is going to serialise them a moment later anyway.
 *
 * KEPT THROUGH R8 by Editor/JoryioAndroidPostProcess.cs: C# calls every method
 * here by name, which minification cannot see.
 */
public final class JoryioUnityBridge {

    private static final String TAG = "JoryioUnity";

    private static String callbackTarget = "JoryioCallbackReceiver";

    private JoryioUnityBridge() {}

    /** The GameObject that receives callbacks. C# owns its lifetime and name. */
    public static void setCallbackTarget(String name) {
        callbackTarget = name;
    }

    public static void initialize(String sdkKey, String apiHost, String configJson) {
        // The game's Activity, not the application context. The SDK keeps the
        // application context for itself either way, and seeds its "current
        // activity" from an Activity handed in here - which is what lets an
        // in-app message or permission prompt appear in a game that
        // initialises late, after its activity was already resumed and the
        // SDK's lifecycle listener had nothing left to observe.
        Activity activity = UnityPlayer.currentActivity;
        if (activity == null) {
            Log.e(TAG, "initialize: no current Unity activity; the SDK was not started");
            throw new IllegalStateException("UnityPlayer.currentActivity is null");
        }
        JSONObject raw = parse(configJson);

        // Every JoryioConfig field has a Kotlin default, but defaults do not
        // exist at the JVM level - Java must pass the whole constructor. So
        // the defaults are READ off a default instance rather than retyped
        // here, and the call is positional against JoryioConfig.kt: if that
        // class gains a field, this is a compile error, which is intended.
        //
        // It used to be `new JoryioConfig()` and nothing else, which compiled
        // and honoured none of the game's settings - Debug, BatchSize, every
        // one of them silently the native default (audit 2026-09-26, D26).
        //
        // Keys are the C# JoryioConfig.ToJson() spelling. Durations arrive in
        // SECONDS (the C# names say so); this SDK takes milliseconds.
        JoryioConfig d = new JoryioConfig();
        JoryioConfig config = new JoryioConfig(
            d.getUserId(),
            raw.has("anonymousId") ? raw.optString("anonymousId", null) : d.getAnonymousId(),
            raw.optInt("batchSize", d.getBatchSize()),
            secondsToMillis(raw, "flushIntervalSeconds", d.getFlushInterval()),
            raw.optBoolean("sendImmediately", d.getSendImmediately()),
            raw.optInt("maxQueueSize", d.getMaxQueueSize()),
            secondsToMillis(raw, "sessionTimeoutSeconds", d.getSessionTimeout()),
            raw.optBoolean("trackSessionStart", d.getTrackSessionStart()),
            raw.optInt("maxRetries", d.getMaxRetries()),
            raw.optLong("retryBackoffMs", d.getRetryBackoffMs()),
            secondsToMillis(raw, "requestTimeoutSeconds", d.getRequestTimeout()),
            raw.optBoolean("enableSdkAuthentication", d.getEnableSdkAuthentication()),
            raw.has("sdkAuthenticationToken") ? raw.optString("sdkAuthenticationToken", null) : d.getSdkAuthenticationToken(),
            raw.optBoolean("allowHtmlJsInAppMessages", d.getAllowHtmlJsInAppMessages()),
            raw.optBoolean("optOut", d.getOptOut()),
            // Absent when the game left JoryioConfig.TrackingConsent unset:
            // then the native DEFAULT, so a denial stored by an earlier launch
            // survives (contract 24, consent model A). C# never sends
            // "granted" on the game's behalf.
            consent(raw.optString("trackingConsent", null), d.getTrackingConsent()),
            raw.optBoolean("requestPushPermissionAtLaunch", d.getRequestPushPermissionAtLaunch()),
            raw.optBoolean("debug", d.getEnableDebug()),
            // Left null so the level derives from enableDebug, as it does on
            // iOS (JoryioConfig.effectiveLogLevel).
            d.getLogLevel(),
            // Android-only (iOS has no channels): the channel's user-visible
            // name. Was not reachable from Unity at all, so every game showed
            // the English default in system Settings.
            raw.has("notificationChannelName") ? raw.optString("notificationChannelName", null) : d.getNotificationChannelName()
        );
        Joryio.initialize(activity, sdkKey, apiHost, config);
    }

    private static long secondsToMillis(JSONObject raw, String key, long fallbackMillis) {
        if (!raw.has(key)) return fallbackMillis;
        return Math.round(raw.optDouble(key, fallbackMillis / 1000.0) * 1000.0);
    }

    /** "granted" / "denied" / "pending" - the C# TrackingConsent wire values. */
    static TrackingConsent consent(String wire, TrackingConsent fallback) {
        if (wire == null) return fallback;
        switch (wire) {
            case "granted": return TrackingConsent.GRANTED;
            case "denied": return TrackingConsent.DENIED;
            case "pending": return TrackingConsent.PENDING;
            default: return fallback;
        }
    }

    // ── Analytics ────────────────────────────────────────────────────────────

    public static void track(String eventName, String propertiesJson) {
        Joryio.getInstance().track(eventName, toMap(propertiesJson));
    }

    public static void trackScreen(String screenName, String propertiesJson) {
        Joryio.getInstance().trackScreen(screenName, toMap(propertiesJson));
    }

    public static void identify(String userId) {
        Joryio.getInstance().identify(userId);
    }

    public static void reset() {
        Joryio.getInstance().reset();
    }

    public static void flush() {
        Joryio.getInstance().flush();
    }

    public static String getUserId() {
        String id = Joryio.getInstance().getUserId();
        return id == null ? "" : id;
    }

    public static String getAnonymousId() {
        return Joryio.getInstance().getAnonymousId();
    }

    // ── In-app ───────────────────────────────────────────────────────────────

    /**
     * Route in-app messages to C# instead of the SDK's own renderer.
     *
     * Opt-in, because taking this path means the GAME draws the message and the
     * SDK can no longer see whether it did. A host that never reports emits no
     * in_app.displayed, so the campaign reads as shown to nobody and its
     * frequency caps never count it, which is why the C# side hands the game an
     * object whose lifecycle reports rather than a raw JSON string.
     */
    public static void enableInAppCallback(String capabilitiesJson) {
        // The game declares what it can draw - see the iOS bridge for why an
        // absent declaration is not neutral: the server resolves it to "no".
        List<String> caps = null;
        try {
            JSONArray arr = new JSONArray(capabilitiesJson);
            caps = new ArrayList<>();
            for (int i = 0; i < arr.length(); i++) caps.add(arr.getString(i));
            if (caps.isEmpty()) caps = null;
        } catch (Exception ignored) {
            // Malformed or absent: fall back to the SDK describing its own
            // renderer, which is the pre-existing behaviour.
        }
        Joryio.getInstance().setInAppMessageCallback(campaign -> {
            // The whole message, not just id + name. The payload used to carry
            // only those two, so a game that installed a handler had nothing
            // to draw but the campaign's internal name (audit 2026-09-26,
            // U25). Field names are the React Native bridge's, byte for byte,
            // so one document describes both.
            InAppContent content = campaign.getContent();
            if (content == null) {
                // No content is not a message. Handing it over would make the
                // game report a display of nothing.
                Log.w(TAG, "Campaign " + campaign.getId() + " has no content; not emitting");
                return kotlin.Unit.INSTANCE;
            }
            JSONObject payload = new JSONObject();
            try {
                payload.put("id", campaign.getId());
                payload.put("name", campaign.getName());
                payload.put("type", typeWireValue(campaign.getType()));
                if (content instanceof InAppContent.Html) {
                    InAppContent.Html html = (InAppContent.Html) content;
                    payload.put("kind", "html");
                    payload.put("html", html.getHtml());
                    payload.put("css", html.getCss());
                    // The resolved width cap (modal / slideup only); the game
                    // draws the message, so it needs the number.
                    Integer maxWidth = campaign.getMaxWidth();
                    if (maxWidth != null) payload.put("maxWidth", maxWidth.intValue());
                } else if (content instanceof InAppContent.Native) {
                    InAppContent.Native n = (InAppContent.Native) content;
                    payload.put("kind", "native");
                    if (n.getTitle() != null) payload.put("title", n.getTitle());
                    payload.put("body", n.getBody());
                    if (n.getImageUrl() != null) payload.put("imageUrl", n.getImageUrl());
                    payload.put("closeButton", n.getCloseButton());
                    payload.put("backdropDismissible", n.getBackdropDismissible());
                    // Author style overrides, forwarded rather than applied:
                    // the GAME draws the message, so resolving them here would
                    // compute the campaign's colours and then throw them away.
                    // Keys and shape are the React Native bridge's, byte for
                    // byte; an absent key means inherit.
                    InAppContent.NativeStyle st = n.getStyle();
                    if (st != null) {
                        JSONObject style = new JSONObject();
                        if (st.getBackgroundColor() != null) style.put("backgroundColor", st.getBackgroundColor());
                        if (st.getTextColor() != null) style.put("textColor", st.getTextColor());
                        if (st.getPrimaryButtonColor() != null) style.put("primaryButtonColor", st.getPrimaryButtonColor());
                        if (st.getPrimaryButtonTextColor() != null) style.put("primaryButtonTextColor", st.getPrimaryButtonTextColor());
                        if (st.getCornerRadius() != null) style.put("cornerRadius", st.getCornerRadius().doubleValue());
                        if (st.getFontSize() != null) style.put("fontSize", st.getFontSize().doubleValue());
                        if (st.getTitleWeight() != null) style.put("titleWeight", st.getTitleWeight());
                        if (st.getTextAlign() != null) style.put("textAlign", st.getTextAlign());
                        if (st.getFontFamily() != null) style.put("fontFamily", st.getFontFamily());
                        payload.put("style", style);
                    }
                    JSONArray buttons = new JSONArray();
                    for (InAppContent.Button b : n.getButtons()) {
                        JSONObject button = new JSONObject();
                        button.put("id", b.getId());
                        button.put("text", b.getText());
                        // Wire spelling, same as React Native: dismiss | url |
                        // deep_link | request_push_permission.
                        button.put("action", b.getAction().name().toLowerCase(Locale.ROOT));
                        if (b.getUrl() != null) button.put("url", b.getUrl());
                        buttons.put(button);
                    }
                    payload.put("buttons", buttons);
                }
            } catch (Exception ignored) {
                // A malformed payload must not take the SDK's callback thread
                // down with it; an empty message is recoverable, a crash is not.
            }
            send("OnInAppMessage", payload.toString());
            return kotlin.Unit.INSTANCE;
        }, caps);
    }

    /**
     * The presentation type as the server spells it. A value this SDK version
     * does not know reaches here as null (Gson leaves an unknown enum value
     * null), and calling name() on it threw inside the payload builder - so C#
     * received a message with an id and a name and nothing to draw (scan 5,
     * N8). Unknown is "custom", the host decides placement: the same as iOS,
     * which decodes unknown types to .custom.
     */
    static String typeWireValue(MessageType type) {
        return type == null ? "custom" : type.name().toLowerCase(Locale.ROOT);
    }

    /**
     * Hand in-app rendering back to the native SDK (contract addendum 18): the
     * C# side calls this for SetInAppMessageHandler(null) and
     * ClearInAppMessageHandler. Without it the callback installed above stayed
     * in place after the game dropped its handler, so every message was sent to
     * C#, discarded there, and drawn by nobody.
     */
    public static void clearInAppCallback() {
        Joryio.getInstance().clearInAppMessageCallback();
    }

    /**
     * @param elementId the tapped button's id for a click; "" stands for none,
     *             because C# cannot pass a typed null through JNI.
     */
    public static void trackInAppImpression(String campaignId, String action, String elementId) {
        Joryio.getInstance().trackInAppImpression(campaignId, action, elementId == null || elementId.isEmpty() ? null : elementId);
    }

    /**
     * A form inside an HTML in-app message the game rendered itself. Answers
     * through the request id with the native InAppFormSubmitResponse JSON,
     * which the SDK already produces - including on failure, as ok:false.
     */
    public static void submitInAppForm(String campaignId, String valuesJson, final int requestId) {
        Joryio.getInstance().submitInAppForm(campaignId, toMap(valuesJson), json -> {
            reply(requestId, json);
            return kotlin.Unit.INSTANCE;
        });
    }

    // ── Identity and attributes ──────────────────────────────────────────────

    /** A labelled alias. Works for an anonymous player too (SDK contract 2). */
    public static void addAlias(String aliasLabel, String aliasName) {
        Joryio.getInstance().addAlias(aliasLabel, aliasName);
    }

    public static void setAttributes(String attributesJson) {
        Joryio.getInstance().setAttributes(toMap(attributesJson));
    }

    public static void setAttribute(String key, String valueJson) {
        // The value crosses as a one-key JSON object rather than a raw string so
        // a number stays a number. Passing everything as text is how an SDK ends
        // up with "42" where the segment builder expects 42, and the mismatch
        // only shows up as a segment that matches nobody.
        //
        // A null value UNSETS the attribute (contract addendum 19), as on iOS.
        // C# already routes null to unsetAttribute; this covers a JSON null
        // reaching here anyway, which used to be dropped without a word.
        Object value = toJava(parse(valueJson).opt("v"));
        if (value == null) {
            Joryio.getInstance().unsetAttribute(key);
            return;
        }
        Joryio.getInstance().setAttribute(key, value);
    }

    public static void incrementAttribute(String key, double by) {
        Joryio.getInstance().incrementAttribute(key, by);
    }

    public static void unsetAttribute(String key) {
        Joryio.getInstance().unsetAttribute(key);
    }

    // ── Consent ──────────────────────────────────────────────────────────────

    // BEFORE initialize() these go to the companion forms, which record the
    // decision; initialize() applies it before anything is sent (contract 25).
    //
    // AFTER it, the INSTANCE. The companion forms run through the SDK's
    // pre-init buffer, which drains on the Android main thread - and Unity
    // calls from its own thread, so until that drain had run an optOut() made
    // right after Initialize sat in the buffer while the next track(), which
    // goes straight to the instance, overtook it (scan 3, Unity 6). The
    // instance acts at once, in call order.
    //
    // Two separate switches (contract 24): optIn/optOut never change consent,
    // updateConsent never changes the opt-out. Neither bridge maps one onto
    // the other.
    public static void optIn() {
        if (Joryio.isInitialized()) Joryio.getInstance().optIn();
        else Joryio.Companion.optIn();
    }

    public static void optOut() {
        if (Joryio.isInitialized()) Joryio.getInstance().optOut();
        else Joryio.Companion.optOut();
    }

    /**
     * The opt-out switch only. Safe before initialize(): the companion answers
     * from a decision made earlier in this launch, or the one stored by an
     * earlier launch - not a default false (contract 44).
     */
    public static boolean isUserOptedOut() { return Joryio.Companion.isUserOptedOut(); }

    public static void updateConsent(String consent) {
        TrackingConsent parsed = consent(consent, null);
        if (parsed == null) {
            Log.e(TAG, "updateConsent: unknown value '" + consent + "' - ignored");
            return;
        }
        if (Joryio.isInitialized()) Joryio.getInstance().updateConsent(parsed);
        else Joryio.Companion.updateConsent(parsed);
    }

    // Marketing subscription - a different consent from optIn/optOut above:
    // that is whether we may OBSERVE, this is whether we may MESSAGE.
    public static void setSubscription(String channel, String status) {
        Joryio.getInstance().setSubscription(channel, status);
    }

    public static void addToSubscriptionGroup(String groupId, String channel) {
        Joryio.getInstance().addToSubscriptionGroup(groupId, channel);
    }

    public static void removeFromSubscriptionGroup(String groupId, String channel) {
        Joryio.getInstance().removeFromSubscriptionGroup(groupId, channel);
    }
    /**
     * Before initialize() the companion records the wipe and runs it inside
     * init, ahead of the Session Start, in-app sync and push registration
     * (contract 25); afterwards the instance wipes at once.
     */
    public static void wipeData() {
        if (Joryio.isInitialized()) Joryio.getInstance().wipeData();
        else Joryio.Companion.wipeData();
    }

    /**
     * "granted" / "denied" / "pending". Before initialize() the companion
     * answers from the decision made in this launch or stored by an earlier
     * one (contract 44), and null only when nothing was decided anywhere -
     * which C# reports as Granted, how the SDK behaves then.
     */
    public static String getTrackingConsent() {
        TrackingConsent c = Joryio.Companion.getTrackingConsent();
        return c == null ? null : c.name().toLowerCase(java.util.Locale.ROOT);
    }

    // ── Push ─────────────────────────────────────────────────────────────────

    /** Answers through the request id with the native PushRegistrationResult as JSON. */
    public static void registerPushToken(String token, final int requestId) {
        Joryio.getInstance().registerPushToken(token, r -> {
            JSONObject json = new JSONObject();
            try {
                json.put("success", r.getSuccess());
                json.put("message", r.getMessage() == null ? JSONObject.NULL : r.getMessage());
                json.put("isUnauthorized", r.isUnauthorized());
                json.put("isRetryable", r.isRetryable());
            } catch (Exception ignored) {
                // A malformed payload must not take the callback thread down.
            }
            reply(requestId, json.toString());
            return kotlin.Unit.INSTANCE;
        });
    }

    /** Report that a push arrived on this device. */
    public static void reportPushDelivered(String trackingId) {
        Joryio.getInstance().reportPushDelivered(trackingId);
    }

    public static void unregisterPush() { Joryio.getInstance().unregisterPush(); }

    public static String getDeviceToken() {
        String t = Joryio.getInstance().getDeviceToken();
        return t == null ? "" : t;
    }

    public static void trackPushClick(String trackingId) {
        Joryio.getInstance().trackPushClick(trackingId);
    }

    //
    // SYNCHRONOUS HERE, ASYNC ON iOS.
    //
    // isPushEnabled and getPushPermissionStatus are plain functions on Android
    // and `async` on iOS. The C# API cannot expose that difference without
    // making every game write platform-specific code, so all three answer
    // through the same callback on BOTH platforms: iOS awaits and replies,
    // Android replies immediately. Uniform surface, honest underneath.
    //
    // The request id is what lets two in-flight calls resolve to the right
    // handler - without it a second call delivers its answer to the first one's
    // callback, a bug that only appears under a double tap.
    //

    public static void isPushEnabled(int requestId) {
        reply(requestId, Joryio.getInstance().isPushEnabled() ? "true" : "false");
    }

    // granted / denied / notDetermined / unavailable - the iOS bridge's words
    // and React Native's. This used to send the enum name lowercased, so a game
    // read "not_determined" on Android and "notDetermined" on iOS for the one
    // state a push primer exists for, and a check written on one platform never
    // matched on the other.
    public static void getPushPermissionStatus(int requestId) {
        reply(requestId, permissionWireValue(Joryio.getInstance().getPushPermissionStatus()));
    }

    static String permissionWireValue(PushPermissionStatus status) {
        switch (status) {
            case GRANTED: return "granted";
            case DENIED: return "denied";
            case NOT_DETERMINED: return "notDetermined";
            default: return "unavailable";
        }
    }

    public static void requestPushPermission(final int requestId) {
        // Needs the Activity, not the Context: the runtime permission dialog is
        // presented by it. UnityPlayer.currentActivity is the game's own - a
        // GameActivity (Unity 6 default, an androidx ComponentActivity) or the
        // classic UnityPlayerActivity (a plain Activity). The native SDK owns
        // waiting for the player's answer on both; C# settles once.
        Activity activity = UnityPlayer.currentActivity;
        if (activity == null) {
            reply(requestId, "false");
            return;
        }
        Joryio.getInstance().requestPushPermission(activity, status -> {
            reply(requestId, status == PushPermissionStatus.GRANTED ? "true" : "false");
            return kotlin.Unit.INSTANCE;
        });
    }

    // iOS has an app-icon badge API; Android does not (the launcher decides,
    // from the notifications present). Exported so the C# surface is the same
    // on both platforms, and logged so a call is visibly a no-op.
    private static boolean badgeWarned;

    public static void updateBadgeCount(int count) { warnBadge(); }

    public static void clearBadge() { warnBadge(); }

    private static void warnBadge() {
        if (badgeWarned) return;
        badgeWarned = true;
        Log.i(TAG, "UpdateBadgeCount/ClearBadge are iOS only; Android launchers badge from notifications themselves.");
    }

    // ── Session, queue, auth ─────────────────────────────────────────────────

    public static String getSessionId() { return Joryio.getInstance().getSessionId(); }

    /** { sessionId, sessionStartMs, durationMs } - the C# side builds the tuple. */
    public static String getSessionInfo() {
        SessionInfo info = Joryio.getInstance().getSessionInfo();
        JSONObject json = new JSONObject();
        try {
            long start = info.getSessionStart().getTime();
            json.put("sessionId", info.getSessionId());
            json.put("sessionStartMs", start);
            json.put("durationMs", Math.max(0L, System.currentTimeMillis() - start));
        } catch (Exception ignored) {
            // An empty object reads as "no session" on the C# side.
        }
        return json.toString();
    }

    // getQueueSize is deliberately NOT exposed. It is a plain function on both
    // natives now (Android's was `suspend` until 2026-09-26), so nothing stops
    // it technically - it is left out because it is a diagnostic no game needs.
    // If it is ever added, add it to BOTH bridges and the C# surface in one
    // change: an API that exists on one platform is the drift this project's
    // parity tripwire exists to prevent.

    /**
     * Safe before initialize() (rule 84): the companion setter keeps the token
     * for the constructor, ahead of its first request, and through a wipe
     * made before initialize(), so a withdrawal held for the player being
     * erased still carries their token. getInstance() threw before init.
     * Afterwards the instance takes it at once, in call order with the other
     * instance calls (the companion would queue it until its buffer drains).
     */
    public static void setSdkAuthenticationToken(String token) {
        if (Joryio.isInitialized()) Joryio.getInstance().setSdkAuthenticationToken(token);
        else Joryio.Companion.setSdkAuthenticationToken(token);
    }

    /**
     * Forward SDK authentication failures to C# as OnSdkAuthError. Same four
     * fields and the same reason spelling as React Native: the enum name
     * lower-cased (SUB_MISMATCH becomes sub_mismatch), which is the wire value.
     * Safe before initialize(), like setSdkAuthenticationToken.
     */
    public static void enableSdkAuthenticationErrors() {
        Function1<SdkAuthError, kotlin.Unit> handler = error -> {
            JSONObject json = new JSONObject();
            try {
                json.put("reason", error.getReason().name().toLowerCase(Locale.ROOT));
                if (error.getRawReason() != null) json.put("rawReason", error.getRawReason());
                if (error.getEndpoint() != null) json.put("endpoint", error.getEndpoint());
                json.put("refreshExhausted", error.getRefreshExhausted());
            } catch (Exception ignored) {
                // See enableInAppCallback.
            }
            send("OnSdkAuthError", json.toString());
            return kotlin.Unit.INSTANCE;
        };
        if (Joryio.isInitialized()) Joryio.getInstance().setSdkAuthenticationErrorHandler(handler);
        else Joryio.Companion.setSdkAuthenticationErrorHandler(handler);
    }

    // ── In-app campaign control ──────────────────────────────────────────────

    public static void syncInAppCampaigns() { Joryio.getInstance().syncInAppCampaigns(); }

    public static void resetDisplayedCampaigns() { Joryio.getInstance().resetDisplayedCampaigns(); }

    public static void evaluateInAppCampaigns() { Joryio.getInstance().evaluateInAppCampaigns(); }

    // ── E-commerce ───────────────────────────────────────────────────────────

    /**
     * ONE bridge method for every e-commerce event, as in React Native. The
     * canonical event names and property keys live in the native tracker;
     * rebuilding them in C# would be another copy of a vocabulary that has no
     * server-side alias layer. The method list MUST match the iOS bridge's
     * switch and JoryioEcommerce.cs.
     *
     * Every argument is passed explicitly: the tracker's Kotlin defaults do not
     * exist for Java, so the defaults are restated here (quantity 1, no extra
     * properties) - the same values the Kotlin signatures declare.
     */
    public static void ecommerce(String method, String payloadJson) {
        JSONObject p = parse(payloadJson);
        // JoryioEcommerce.cs refuses a call missing a required argument; this
        // is the second line of defence, so the defaults below (an empty id, a
        // zero amount) can never turn a missing argument into a zero order.
        if (!ecommerceArgumentsPresent(method, p)) return;
        // The INSTANCE's tracker once the SDK is up, like every other call in
        // this bridge. create()'s tracker sends through the static path, whose
        // buffer drains on the Android main thread - so in the first moments
        // after Initialize a Purchase could land after a later Identify or
        // Reset made from Unity's thread, and be filed under the wrong player
        // (scan 5, N6). create() is kept only for a call before initialize(),
        // which C# does not make (it refuses e-commerce before Initialize).
        EcommerceTracker tracker = Joryio.isInitialized()
            ? Joryio.getInstance().ecommerce(null)
            : EcommerceTracker.create();
        Map<String, Object> none = Collections.emptyMap();
        switch (method) {
            case "viewProduct": tracker.viewProduct(product(p.optJSONObject("product"))); break;
            case "viewCategory": tracker.viewCategory(p.optString("categoryId", ""), p.optString("categoryName", ""), none); break;
            case "search": tracker.search(p.optString("query", ""), optInt(p, "resultCount"), none); break;
            case "addToCart": tracker.addToCart(cartItem(p.optJSONObject("item")), optDouble(p, "cartValue")); break;
            case "removeFromCart": tracker.removeFromCart(product(p.optJSONObject("product")), p.optInt("quantity", 1)); break;
            case "cartViewed": tracker.cartViewed(cartItems(p.optJSONArray("items")), p.optDouble("cartValue", 0)); break;
            case "updateCart": tracker.updateCart(cartItems(p.optJSONArray("items")), p.optDouble("cartValue", 0)); break;
            case "startCheckout": tracker.startCheckout(cartItems(p.optJSONArray("items")), p.optDouble("cartValue", 0), none); break;
            case "addPaymentInfo": tracker.addPaymentInfo(p.optString("paymentMethod", ""), none); break;
            case "purchase": tracker.purchase(order(p.optJSONObject("order"))); break;
            case "orderFulfilled": tracker.orderFulfilled(p.optString("orderId", ""), optString(p, "trackingNumber"), optString(p, "carrier")); break;
            case "orderDelivered": tracker.orderDelivered(p.optString("orderId", "")); break;
            case "orderCancelled": tracker.orderCancelled(p.optString("orderId", ""), optString(p, "reason")); break;
            case "orderRefunded": tracker.orderRefunded(p.optString("orderId", ""), optDouble(p, "refundAmount"), optString(p, "reason")); break;
            case "addToWishlist": tracker.addToWishlist(product(p.optJSONObject("product"))); break;
            case "removeFromWishlist": tracker.removeFromWishlist(product(p.optJSONObject("product"))); break;
            case "shareProduct": tracker.shareProduct(product(p.optJSONObject("product")), p.optString("shareMethod", "")); break;
            case "applyCoupon": tracker.applyCoupon(p.optString("couponCode", ""), optDouble(p, "discountAmount"), optString(p, "discountType")); break;
            case "removeCoupon": tracker.removeCoupon(p.optString("couponCode", "")); break;
            case "submitReview": tracker.submitReview(p.optString("productId", ""), p.optInt("rating", 0), optString(p, "reviewText")); break;
            default:
                // Loudly, not silently: a typo'd method name would otherwise be
                // a no-op that looks exactly like the SDK working.
                Log.e(TAG, "Unknown ecommerce method '" + method + "' - event NOT sent");
        }
    }

    /**
     * The required arguments of each e-commerce call (contract item 42), the
     * same list as JoryioEcommerce.cs and the iOS bridge. False, with a log
     * line, when one is missing, empty or not a finite number.
     */
    static boolean ecommerceArgumentsPresent(String method, JSONObject p) {
        if (method == null) return true; // the switch logs the unknown method
        switch (method) {
            case "viewProduct": case "removeFromCart": case "addToWishlist": case "removeFromWishlist":
                return needText(method, p.optJSONObject("product"), "productId");
            case "shareProduct":
                return needText(method, p.optJSONObject("product"), "productId") && needText(method, p, "shareMethod");
            case "addToCart":
                return needText(method, p.optJSONObject("item"), "productId");
            case "viewCategory":
                return needText(method, p, "categoryId") && needText(method, p, "categoryName");
            case "search":
                return needText(method, p, "query");
            case "cartViewed": case "updateCart": case "startCheckout":
                return needArray(method, p, "items") && needNumber(method, p, "cartValue");
            case "addPaymentInfo":
                return needText(method, p, "paymentMethod");
            case "purchase": {
                JSONObject order = p.optJSONObject("order");
                return needText(method, order, "orderId") && needArray(method, order, "items") && needNumber(method, order, "value");
            }
            case "orderFulfilled": case "orderDelivered": case "orderCancelled": case "orderRefunded":
                return needText(method, p, "orderId");
            case "applyCoupon": case "removeCoupon":
                return needText(method, p, "couponCode");
            case "submitReview":
                return needText(method, p, "productId");
            default:
                return true;
        }
    }

    private static boolean needText(String method, JSONObject o, String key) {
        // Blank counts as missing, as in the native trackers (one guard set).
        if (o != null && o.opt(key) instanceof String && !((String) o.opt(key)).trim().isEmpty()) return true;
        Log.w(TAG, "ecommerce." + method + "() needs a non-empty " + key + " - event NOT sent");
        return false;
    }

    private static boolean needNumber(String method, JSONObject o, String key) {
        Object v = o == null ? null : o.opt(key);
        if (v instanceof Number) {
            double d = ((Number) v).doubleValue();
            if (!Double.isNaN(d) && !Double.isInfinite(d)) return true;
        }
        Log.w(TAG, "ecommerce." + method + "() needs a number " + key + " - event NOT sent");
        return false;
    }

    private static boolean needArray(String method, JSONObject o, String key) {
        if (o != null && o.optJSONArray(key) != null) return true;
        Log.w(TAG, "ecommerce." + method + "() needs an array of " + key + " - event NOT sent");
        return false;
    }

    private static EcommerceProduct product(JSONObject m) {
        if (m == null) m = new JSONObject();
        return new EcommerceProduct(
            m.optString("productId", ""),
            m.optString("name", ""),
            m.optDouble("price", 0),
            optInt(m, "quantity"),
            optString(m, "category"),
            optString(m, "brand"),
            optString(m, "variant"),
            optString(m, "variantId"),
            optString(m, "sku"),
            optString(m, "imageUrl"),
            optString(m, "url"));
    }

    private static EcommerceCartItem cartItem(JSONObject m) {
        if (m == null) m = new JSONObject();
        return new EcommerceCartItem(
            m.optString("productId", ""),
            m.optString("name", ""),
            m.optDouble("price", 0),
            // Defaults to 1, not 0: a zero-quantity line is a silently wrong
            // revenue number. Matches iOS and React Native.
            m.optInt("quantity", 1),
            optString(m, "category"),
            optString(m, "brand"),
            optString(m, "variant"),
            optString(m, "variantId"),
            optString(m, "sku"),
            optString(m, "imageUrl"),
            optString(m, "url"));
    }

    private static List<EcommerceCartItem> cartItems(JSONArray arr) {
        List<EcommerceCartItem> items = new ArrayList<>();
        if (arr == null) return items;
        for (int i = 0; i < arr.length(); i++) items.add(cartItem(arr.optJSONObject(i)));
        return items;
    }

    private static EcommerceOrder order(JSONObject m) {
        if (m == null) m = new JSONObject();
        return new EcommerceOrder(
            m.optString("orderId", ""),
            cartItems(m.optJSONArray("items")),
            m.optDouble("value", 0),
            optString(m, "currency"),
            optDouble(m, "shipping"),
            optDouble(m, "tax"),
            optDouble(m, "discount"),
            optString(m, "coupon"));
    }

    private static String optString(JSONObject o, String key) {
        return o.has(key) && !o.isNull(key) ? o.optString(key) : null;
    }

    private static Integer optInt(JSONObject o, String key) {
        return o.has(key) && !o.isNull(key) ? o.optInt(key) : null;
    }

    private static Double optDouble(JSONObject o, String key) {
        return o.has(key) && !o.isNull(key) ? o.optDouble(key) : null;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static void reply(int requestId, String value) {
        JSONObject payload = new JSONObject();
        try {
            payload.put("id", requestId);
            payload.put("value", value);
        } catch (Exception ignored) {
            // See enableInAppCallback: a malformed payload must not take the
            // callback thread down with it.
        }
        send("OnNativeResult", payload.toString());
    }


    private static void send(String method, String payload) {
        UnityPlayer.UnitySendMessage(callbackTarget, method, payload);
    }

    private static JSONObject parse(String json) {
        if (json == null || json.isEmpty()) return new JSONObject();
        try {
            return new JSONObject(json);
        } catch (Exception e) {
            return new JSONObject();
        }
    }

    /**
     * Properties as plain Java collections, all the way down.
     *
     * This used to copy the top level only, so a nested object or array
     * reached the SDK as an org.json JSONObject/JSONArray - which the SDK's
     * Gson serialiser writes as its INTERNALS ({"nameValuePairs":{...}}), not
     * as the object. Any nested property arrived on the server under a key
     * nobody had written. Same for JSONObject.NULL, which is not null.
     */
    static Map<String, Object> toMap(String json) {
        return toMap(parse(json));
    }

    static Map<String, Object> toMap(JSONObject obj) {
        Map<String, Object> map = new HashMap<>();
        for (Iterator<String> it = obj.keys(); it.hasNext(); ) {
            String key = it.next();
            map.put(key, toJava(obj.opt(key)));
        }
        return map;
    }

    static List<Object> toList(JSONArray arr) {
        List<Object> list = new ArrayList<>(arr.length());
        for (int i = 0; i < arr.length(); i++) list.add(toJava(arr.opt(i)));
        return list;
    }

    static Object toJava(Object value) {
        if (value == null || value == JSONObject.NULL) return null;
        if (value instanceof JSONObject) return toMap((JSONObject) value);
        if (value instanceof JSONArray) return toList((JSONArray) value);
        return value;
    }
}
