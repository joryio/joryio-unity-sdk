import Foundation
import UIKit
import UserNotifications
// attachCardFeedListener is SDK-internal (SPI): a game listens with JoryioSDK.OnCardsUpdated.
@_spi(JoryioInternal) import Joryio

//
//  Unity <-> Joryio iOS bridge.
//
//  WHY SWIFT AND NOT OBJECTIVE-C. Every comparable vendor writes this shim in
//  Objective-C, because their native SDK exposes an ObjC-visible API. Ours does
//  not: JoryioSDK.swift contains zero `@objc` annotations, so its API is pure
//  Swift and unreachable from ObjC. (The React Native bridge gets away with an
//  ObjC file only because the file that touches the SDK is itself Swift.)
//
//  So the bridge is Swift, and reaches C# the only way Swift can: `@_cdecl`,
//  which emits a plain C symbol that C# can `DllImport("__Internal")`. C# cannot
//  P/Invoke into Swift's mangled symbols, and there is no third option.
//
//  STRING OWNERSHIP. Values returned to C# are `strdup`ed and MUST be released
//  by calling joryio_free_string. Returning a pointer into Swift-managed memory
//  would hand C# a buffer Swift is free to reclaim, and the resulting corruption
//  appears far from its cause.
//

/// Unity's C entry point for pushing a message into a GameObject. Bound by
/// symbol name at link time - it lives in the Unity player, not here, so this
/// file still compiles standalone (which is how it is verified in CI).
@_silgen_name("UnitySendMessage")
private func UnitySendMessage(
    _ gameObject: UnsafePointer<CChar>,
    _ method: UnsafePointer<CChar>,
    _ message: UnsafePointer<CChar>
)

/// The GameObject that receives callbacks. Set by C# during initialize, because
/// the managed side owns the object's lifetime and its name.
private var callbackTarget = "JoryioCallbackReceiver"

private func str(_ p: UnsafePointer<CChar>?) -> String {
    guard let p else { return "" }
    return String(cString: p)
}

/// Duplicate into C-owned memory. See STRING OWNERSHIP above.
private func cstr(_ s: String) -> UnsafeMutablePointer<CChar>? {
    return strdup(s)
}

private func send(_ method: String, _ payload: String) {
    // UnitySendMessage must be called on the main thread; the SDK's callbacks
    // are not guaranteed to arrive there.
    DispatchQueue.main.async {
        callbackTarget.withCString { obj in
            method.withCString { m in
                payload.withCString { p in
                    UnitySendMessage(obj, m, p)
                }
            }
        }
    }
}

private func jsonString(_ value: Any) -> String {
    guard JSONSerialization.isValidJSONObject(value),
          let data = try? JSONSerialization.data(withJSONObject: value),
          let s = String(data: data, encoding: .utf8) else { return "{}" }
    return s
}

private func parseJsonArray(_ raw: String) -> [String] {
    guard !raw.isEmpty,
          let data = raw.data(using: .utf8),
          let arr = try? JSONSerialization.jsonObject(with: data) as? [String] else { return [] }
    return arr
}

private func parseJson(_ raw: String) -> [String: Any] {
    guard !raw.isEmpty,
          let data = raw.data(using: .utf8),
          let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
    return obj
}

// MARK: - Lifecycle

@_cdecl("joryio_set_callback_target")
public func joryio_set_callback_target(_ name: UnsafePointer<CChar>?) {
    callbackTarget = str(name)
}

@_cdecl("joryio_initialize")
public func joryio_initialize(
    _ sdkKey: UnsafePointer<CChar>?,
    _ apiHost: UnsafePointer<CChar>?,
    _ configJson: UnsafePointer<CChar>?
) {
    // sdkKey and apiHost are arguments to initialize, NOT config fields, and
    // every JoryioConfig property is `let` - it is built once through its
    // initializer rather than mutated. Compiling this file against the real SDK
    // is what caught both; the first draft did it the other way round.
    //
    // Keys are the C# JoryioConfig.ToJson() spelling. Durations arrive in
    // SECONDS (the C# names say so), which is what this SDK takes.
    let raw = parseJson(str(configJson))
    let debug = raw["debug"] as? Bool ?? false
    let config = JoryioConfig(
        anonymousId: raw["anonymousId"] as? String,
        batchSize: raw["batchSize"] as? Int ?? ConfigDefaults.batchSize,
        flushInterval: raw["flushIntervalSeconds"] as? TimeInterval ?? ConfigDefaults.flushInterval,
        sendImmediately: raw["sendImmediately"] as? Bool ?? false,
        maxQueueSize: raw["maxQueueSize"] as? Int ?? ConfigDefaults.maxQueueSize,
        sessionTimeout: raw["sessionTimeoutSeconds"] as? TimeInterval ?? ConfigDefaults.sessionTimeout,
        trackSessionStart: raw["trackSessionStart"] as? Bool ?? true,
        requestPushPermissionAtLaunch: raw["requestPushPermissionAtLaunch"] as? Bool ?? false,
        // The ONE in-app option that is not about drawing in-engine: it governs
        // the native views (Joryio/UI), when the game lets the SDK draw.
        // holdInAppMessagesForHandler: the game's SetInAppMessageHandler can
        // only come after Initialize, so without it the first sync could be
        // drawn natively before the handler was set.
        inApp: InAppConfig(
            allowHtmlJsInAppMessages: raw["allowHtmlJsInAppMessages"] as? Bool ?? false,
            holdInAppMessagesForHandler: raw["holdInAppMessagesForHandler"] as? Bool ?? false
        ),
        maxRetries: raw["maxRetries"] as? Int ?? ConfigDefaults.maxRetries,
        retryBackoffMs: raw["retryBackoffMs"] as? Double ?? ConfigDefaults.retryBackoffMs,
        requestTimeout: raw["requestTimeoutSeconds"] as? TimeInterval ?? ConfigDefaults.requestTimeout,
        enableSdkAuthentication: raw["enableSdkAuthentication"] as? Bool ?? false,
        sdkAuthenticationToken: raw["sdkAuthenticationToken"] as? String,
        optOut: raw["optOut"] as? Bool ?? false,
        // Absent when the game left JoryioConfig.TrackingConsent unset: then
        // the SDK's own DEFAULT rather than a hard-coded .granted, so a denial
        // stored by an earlier launch survives (contract 24, consent model A).
        // Read off a default instance so this follows the SDK if that default
        // is "unset".
        trackingConsent: JoryioConfig.TrackingConsent(rawValue: raw["trackingConsent"] as? String ?? "") ?? JoryioConfig().trackingConsent,
        // BOTH, not just the level. enableDebug is what turns on the SDK's
        // verbose paths and unlocks the testing calls (resetDisplayedCampaigns,
        // evaluateInAppCampaigns); mapping Debug to logLevel alone left
        // JoryioConfig.Debug half-working on iOS while it fully worked on
        // Android, whose bridge passes enableDebug.
        enableDebug: debug,
        // nil, as the Android bridge passes null: the SDK derives the level
        // from enableDebug (contract 58), so it follows the SDK's own rule
        // rather than a copy of it pinned here.
        logLevel: nil,
        // iOS only: an https link on one of the game's associated domains goes
        // to its own universal-link handling instead of Safari.
        forwardUniversalLinks: raw["forwardUniversalLinks"] as? Bool ?? false
    )

    // Before initialize, so a token Apple delivers in response to the SDK's own
    // registration (or the one below) is not missed.
    // Every request names this bridge and its version (D427):
    // X-Joryio-SDK-Wrapper: unity + X-Joryio-SDK-Wrapper-Version. C# sends its
    // package version in every config (JoryioConfig.ToJson).
    if let wrapperVersion = raw["sdkWrapperVersion"] as? String {
        Joryio.setSdkWrapper(name: "unity", version: wrapperVersion)
    }
    JoryioUnityAppDelegateListener.shared.install()
    Joryio.shared.initialize(sdkKey: str(sdkKey), apiHost: str(apiHost), config: config)
    JoryioUnityAppDelegateListener.shared.refreshTokenIfAuthorized()
}

// MARK: - APNs device token
//
// A native iOS app hands the SDK its token from its own AppDelegate. A Unity
// game has no AppDelegate of its own - UnityAppController is the delegate, and
// the game cannot edit it - so before this the token was delivered to Unity
// and to nobody else: the SDK never registered the device for push, on every
// Unity iOS game, with no error anywhere (audit 2026-10-06, blocker 1).
//
// Unity's supported hook is the notification its app controller re-posts for
// each delegate callback (Classes/PluginBase/AppDelegateListener.h). Those
// callbacks only exist when UNITY_USES_REMOTE_NOTIFICATIONS is 1, which
// Editor/JoryioPostProcessBuild.cs sets.

/// The NSString values of Unity's kUnityDidRegisterForRemoteNotifications...
/// constants (DEFINE_NOTIFICATION stringifies the identifier). Spelled out
/// rather than linked: this file must also compile outside a Unity export,
/// which is how CI checks it.
private let unityDidRegisterForRemoteNotifications =
    Notification.Name("kUnityDidRegisterForRemoteNotificationsWithDeviceToken")
private let unityDidFailToRegisterForRemoteNotifications =
    Notification.Name("kUnityDidFailToRegisterForRemoteNotificationsWithError")

final class JoryioUnityAppDelegateListener: NSObject {
    static let shared = JoryioUnityAppDelegateListener()

    private var installed = false

    func install() {
        guard !installed else { return }
        installed = true
        // Selector-based on purpose. Unity posts the NSData token (and the
        // NSError) as the notification's userInfo, which is NOT a dictionary.
        // The block-based API bridges NSNotification to Swift's Notification,
        // converting userInfo to [AnyHashable: Any] on the way - from an NSData,
        // which fails at runtime. An @objc method receiving NSNotification
        // touches no bridging until we read the value ourselves.
        NotificationCenter.default.addObserver(
            self, selector: #selector(didRegister(_:)),
            name: unityDidRegisterForRemoteNotifications, object: nil)
        NotificationCenter.default.addObserver(
            self, selector: #selector(didFailToRegister(_:)),
            name: unityDidFailToRegisterForRemoteNotifications, object: nil)
    }

    /// userInfo as the raw object Unity posted, without Swift's dictionary bridge.
    static func rawUserInfo(_ note: NSNotification) -> AnyObject? {
        return note.perform(NSSelectorFromString("userInfo"))?.takeUnretainedValue()
    }

    @objc func didRegister(_ note: NSNotification) {
        guard let token = JoryioUnityAppDelegateListener.rawUserInfo(note) as? Data, !token.isEmpty else { return }
        Joryio.shared.didRegisterForRemoteNotifications(deviceToken: token)
    }

    @objc func didFailToRegister(_ note: NSNotification) {
        guard let error = JoryioUnityAppDelegateListener.rawUserInfo(note) as? Error else { return }
        Joryio.shared.didFailToRegisterForRemoteNotifications(error: error)
    }

    /// Apple's guidance is to register on EVERY launch: tokens change (restore
    /// to a new device, reinstall), and the iOS SDK only registers when it asks
    /// for permission. Once permission exists, nothing else in a Unity game
    /// would ask again, so a changed token would never be seen.
    func refreshTokenIfAuthorized() {
        Task {
            let settings = await UNUserNotificationCenter.current().notificationSettings()
            switch settings.authorizationStatus {
            case .authorized, .provisional, .ephemeral:
                await MainActor.run { UIApplication.shared.registerForRemoteNotifications() }
            default:
                break
            }
        }
    }
}

// MARK: - Analytics

@_cdecl("joryio_track")
public func joryio_track(_ name: UnsafePointer<CChar>?, _ propsJson: UnsafePointer<CChar>?) {
    Joryio.shared.track(str(name), properties: parseJson(str(propsJson)))
}

@_cdecl("joryio_track_screen")
public func joryio_track_screen(_ name: UnsafePointer<CChar>?, _ propsJson: UnsafePointer<CChar>?) {
    Joryio.shared.trackScreen(str(name), properties: parseJson(str(propsJson)))
}

@_cdecl("joryio_identify")
public func joryio_identify(_ userId: UnsafePointer<CChar>?) {
    Joryio.shared.identify(str(userId))
}

@_cdecl("joryio_reset")
public func joryio_reset() {
    Joryio.shared.reset()
}

/// Replies "true" when the id was adopted, "false" when it was refused (a plain reset).
@_cdecl("joryio_reset_to")
public func joryio_reset_to(_ anonymousId: UnsafePointer<CChar>?, _ requestId: Int32) {
    Joryio.shared.resetTo(str(anonymousId)) { adopted in reply(requestId, adopted ? "true" : "false") }
}

@_cdecl("joryio_flush")
public func joryio_flush() {
    Joryio.shared.flush()
}

// MARK: - Reads (caller frees - see STRING OWNERSHIP)

@_cdecl("joryio_get_user_id")
public func joryio_get_user_id() -> UnsafeMutablePointer<CChar>? {
    return cstr(Joryio.shared.getUserId() ?? "")
}

@_cdecl("joryio_get_anonymous_id")
public func joryio_get_anonymous_id() -> UnsafeMutablePointer<CChar>? {
    return cstr(Joryio.shared.getAnonymousId())
}

@_cdecl("joryio_free_string")
public func joryio_free_string(_ p: UnsafeMutablePointer<CChar>?) {
    free(p)
}

// MARK: - In-app

/// Route in-app messages to C# instead of the SDK's own renderer.
///
/// Opt-in, because taking this path means the GAME draws the message and the SDK
/// can no longer see whether it did. The managed wrapper reports displayed /
/// clicked / dismissed through joryio_track_in_app_impression; a host that
/// forgets emits no in_app.displayed at all, so the campaign reads as shown to
/// nobody and its frequency caps never count it, which is why the C# side
/// hands the game an object whose lifecycle does the reporting rather than a
/// raw JSON string.
@_cdecl("joryio_enable_in_app_callback")
public func joryio_enable_in_app_callback(_ capabilitiesJson: UnsafePointer<CChar>?) {
    // The game declares what it can draw. A canvas renders structured native
    // content; it cannot render HTML unless the game also hosts a web view, and
    // the server resolves an ABSENT capability to "no" rather than "unknown" -
    // so saying nothing here would quietly stop HTML campaigns reaching a game
    // that could have shown them, or send native ones to a game that cannot.
    let caps = parseJsonArray(str(capabilitiesJson))
    Joryio.shared.setInAppMessageCallback({ campaign in
        // The whole message, not just id + name. The payload used to carry
        // only those two, so a game that installed a handler had nothing to
        // draw but the campaign's internal name (audit 2026-09-26, U25). Field
        // names are the React Native bridge's, byte for byte, so one document
        // describes both.
        guard let content = campaign.content else {
            // No content is not a message. Handing it over would make the
            // game report a display of nothing.
            return
        }
        var payload: [String: Any] = [
            "id": campaign.id,
            "name": campaign.name,
            "type": campaign.type.rawValue,
        ]
        switch content {
        case let .html(html, css):
            payload["kind"] = "html"
            payload["html"] = html
            payload["css"] = css
            // The resolved width cap (modal / slideup only); the game draws
            // the message, so it needs the number.
            if let maxWidth = campaign.maxWidth { payload["maxWidth"] = maxWidth }
        case let .native(native):
            payload["kind"] = "native"
            if let title = native.title { payload["title"] = title }
            payload["body"] = native.body
            if let imageUrl = native.imageUrl { payload["imageUrl"] = imageUrl }
            payload["closeButton"] = native.closeButton
            payload["backdropDismissible"] = native.backdropDismissible

            // Author style overrides, forwarded rather than applied: the GAME
            // draws the message, so resolving them here would compute the
            // campaign's colours and then throw them away. Keys and shape are
            // the React Native bridge's, byte for byte; absent means inherit.
            if let st = native.style {
                var style: [String: Any] = [:]
                if let v = st.backgroundColor { style["backgroundColor"] = v }
                if let v = st.textColor { style["textColor"] = v }
                if let v = st.primaryButtonColor { style["primaryButtonColor"] = v }
                if let v = st.primaryButtonTextColor { style["primaryButtonTextColor"] = v }
                if let v = st.cornerRadius { style["cornerRadius"] = v }
                if let v = st.fontSize { style["fontSize"] = v }
                if let v = st.titleWeight { style["titleWeight"] = v }
                if let v = st.textAlign { style["textAlign"] = v }
                if let v = st.fontFamily { style["fontFamily"] = v }
                payload["style"] = style
            }

            payload["buttons"] = native.buttons.map { b -> [String: Any] in
                var button: [String: Any] = [
                    "id": b.id,
                    "text": b.text,
                    // Wire spelling, same as React Native: dismiss | url |
                    // deep_link | request_push_permission.
                    "action": buttonActionWireValue(b.action),
                ]
                if let url = b.url { button["url"] = url }
                return button
            }
        }
        send("OnInAppMessage", jsonString(payload))
    }, capabilities: caps.isEmpty ? nil : caps)
}

/// Hand in-app rendering back to the native SDK (contract addendum 18): the C#
/// side calls this for SetInAppMessageHandler(null) and ClearInAppMessageHandler.
/// Without it the callback above stayed installed after the game dropped its
/// handler, so every message was sent to C#, discarded there, and drawn by
/// nobody.
@_cdecl("joryio_clear_in_app_callback")
public func joryio_clear_in_app_callback() {
    Joryio.shared.clearInAppMessageCallback()
}

private func buttonActionWireValue(_ action: ButtonAction) -> String {
    switch action {
    case .dismiss: return "dismiss"
    case .url: return "url"
    case .deepLink: return "deep_link"
    case .requestPushPermission: return "request_push_permission"
    }
}

/// `elementId` is the tapped button's id for a click, nil (a C NULL) otherwise -
/// it feeds the campaign's click map, as on Android.
@_cdecl("joryio_track_in_app_impression")
public func joryio_track_in_app_impression(
    _ campaignId: UnsafePointer<CChar>?,
    _ action: UnsafePointer<CChar>?,
    _ elementId: UnsafePointer<CChar>?
) {
    let element = elementId.map { String(cString: $0) }
    Joryio.shared.trackInAppImpression(
        campaignId: str(campaignId),
        action: str(action),
        elementId: (element?.isEmpty ?? true) ? nil : element)
}

/// A form inside an HTML in-app message the game rendered itself. Answers
/// through the request id with the native InAppFormSubmitResponse as JSON -
/// ok:false on any failure, never silence.
@_cdecl("joryio_submit_in_app_form")
public func joryio_submit_in_app_form(
    _ campaignId: UnsafePointer<CChar>?,
    _ valuesJson: UnsafePointer<CChar>?,
    _ requestId: Int32
) {
    let id = str(campaignId)
    let values = parseJson(str(valuesJson))
    Task {
        let r = await Joryio.shared.submitInAppForm(campaignId: id, values: values)
        var body: [String: Any] = ["ok": r.ok]
        if let m = r.message { body["message"] = m }
        if let redirect = r.redirect { body["redirect"] = redirect }
        if let errors = r.errors { body["errors"] = errors }
        if let reason = r.reason { body["reason"] = reason }
        reply(requestId, jsonString(body))
    }
}

/// Re-run targeting against the campaigns already synced. The SDK ignores it
/// unless enableDebug is on - a testing affordance, as on Android.
@_cdecl("joryio_evaluate_in_app_campaigns")
public func joryio_evaluate_in_app_campaigns() {
    Task { await Joryio.shared.evaluateInAppCampaigns() }
}

// MARK: - Identity and attributes

/// A labelled alias. Works for an anonymous player too (SDK contract 2); the
/// unlabelled alias(userId) is gone from every SDK (contract 1).
@_cdecl("joryio_add_alias")
public func joryio_add_alias(_ aliasLabel: UnsafePointer<CChar>?, _ aliasName: UnsafePointer<CChar>?) {
    Joryio.shared.addAlias(str(aliasLabel), name: str(aliasName))
}

/// Click-link identity: the URL that opened the app, passed through to the
/// native SDK unchanged. Native reads `jr_t` and applies the rules
/// (identified, opted out, consent, once per token).
@_cdecl("joryio_identify_from_link")
public func joryio_identify_from_link(_ url: UnsafePointer<CChar>?) {
    guard let parsed = URL(string: str(url)) else { return }
    Joryio.shared.identifyFromLink(parsed)
}

@_cdecl("joryio_set_attributes")
public func joryio_set_attributes(_ json: UnsafePointer<CChar>?) {
    Joryio.shared.setAttributes(parseJson(str(json)))
}

@_cdecl("joryio_set_attribute")
public func joryio_set_attribute(_ key: UnsafePointer<CChar>?, _ valueJson: UnsafePointer<CChar>?) {
    // The value crosses as a one-key JSON object rather than a raw string, so a
    // number stays a number. Passing everything as text is how an SDK ends up
    // with "42" where the segment builder expects 42, and the mismatch only
    // shows up as a segment that matches nobody.
    //
    // A null value UNSETS the attribute (contract addendum 19), as on Android.
    // C# already routes null to joryio_unset_attribute; this covers a JSON null
    // reaching here anyway, which JSONSerialization hands back as NSNull - and
    // which used to be stored as a value.
    let wrapper = parseJson(str(valueJson))
    let value = wrapper["v"]
    if value == nil || value is NSNull {
        Joryio.shared.unsetAttribute(str(key))
        return
    }
    Joryio.shared.setAttribute(str(key), value: value!)
}

@_cdecl("joryio_increment_attribute")
public func joryio_increment_attribute(_ key: UnsafePointer<CChar>?, _ by: Double) {
    Joryio.shared.incrementAttribute(str(key), by: by)
}

@_cdecl("joryio_unset_attribute")
public func joryio_unset_attribute(_ key: UnsafePointer<CChar>?) {
    Joryio.shared.unsetAttribute(str(key))
}

// MARK: - Consent
//
// Two separate switches (contract 24, consent model A): optIn/optOut never
// change consent and updateConsent never changes the opt-out. Joryio.shared
// exists before initialize, so each of these records a pre-init decision that
// initialize applies before anything is sent.

@_cdecl("joryio_opt_in")
public func joryio_opt_in() { Joryio.shared.optIn() }

@_cdecl("joryio_opt_out")
public func joryio_opt_out() { Joryio.shared.optOut() }

/// Swift's Bool is one byte; the C# side marshals it as such
/// ([return: MarshalAs(UnmanagedType.I1)]).
@_cdecl("joryio_is_user_opted_out")
public func joryio_is_user_opted_out() -> Bool { Joryio.shared.isUserOptedOut() }

/// The consent switch as its wire value: "granted" / "denied" / "pending", the
/// C# TrackingConsent wire values and JoryioConfig.TrackingConsent's raw
/// values. Safe before initialize(), where it answers from the stored decision.
/// C# releases the string with joryio_free_string.
@_cdecl("joryio_get_tracking_consent")
public func joryio_get_tracking_consent() -> UnsafeMutablePointer<CChar>? {
    cstr(Joryio.shared.getTrackingConsent().rawValue)
}

@_cdecl("joryio_update_consent")
public func joryio_update_consent(_ consent: UnsafePointer<CChar>?) {
    let raw = str(consent)
    guard let parsed = JoryioConfig.TrackingConsent(rawValue: raw) else {
        NSLog("[Joryio] updateConsent: unknown value '\(raw)' - ignored")
        return
    }
    Joryio.shared.updateConsent(parsed)
}

// MARK: - Marketing subscription
//
// A different consent from the block above: that is whether we may OBSERVE,
// this is whether we may MESSAGE.

@_cdecl("joryio_set_subscription")
public func joryio_set_subscription(_ channel: UnsafePointer<CChar>?, _ status: UnsafePointer<CChar>?) {
    guard let channel, let status else { return }
    Joryio.shared.setSubscription(channel: String(cString: channel), status: String(cString: status))
}

@_cdecl("joryio_add_to_subscription_group")
public func joryio_add_to_subscription_group(_ groupId: UnsafePointer<CChar>?, _ channel: UnsafePointer<CChar>?) {
    guard let groupId, let channel else { return }
    Joryio.shared.addToSubscriptionGroup(String(cString: groupId), channel: String(cString: channel))
}

@_cdecl("joryio_remove_from_subscription_group")
public func joryio_remove_from_subscription_group(_ groupId: UnsafePointer<CChar>?, _ channel: UnsafePointer<CChar>?) {
    guard let groupId, let channel else { return }
    Joryio.shared.removeFromSubscriptionGroup(String(cString: groupId), channel: String(cString: channel))
}

@_cdecl("joryio_wipe_data")
public func joryio_wipe_data() { Joryio.shared.wipeData() }

// MARK: - Push

@_cdecl("joryio_report_push_delivered")
public func joryio_report_push_delivered(_ trackingId: UnsafePointer<CChar>?) {
    Joryio.shared.reportPushDelivered(str(trackingId))
}

/// Answers through the request id with the native PushRegistrationResult as JSON.
@_cdecl("joryio_register_push_token")
public func joryio_register_push_token(_ token: UnsafePointer<CChar>?, _ requestId: Int32) {
    Joryio.shared.registerPushToken(str(token)) { r in
        reply(requestId, jsonString([
            "success": r.success,
            "message": r.message.map { $0 as Any } ?? NSNull(),
            "isUnauthorized": r.isUnauthorized,
            "isRetryable": r.isRetryable,
        ]))
    }
}

@_cdecl("joryio_unregister_push")
public func joryio_unregister_push() { Joryio.shared.unregisterPush() }

@_cdecl("joryio_get_device_token")
public func joryio_get_device_token() -> UnsafeMutablePointer<CChar>? {
    return cstr(Joryio.shared.getDeviceToken() ?? "")
}

@_cdecl("joryio_track_push_click")
public func joryio_track_push_click(_ trackingId: UnsafePointer<CChar>?) {
    Joryio.shared.trackPushClick(trackingId: str(trackingId))
}

//
// ASYNC ON iOS, SYNCHRONOUS ON ANDROID.
//
// isPushEnabled, getPushPermissionStatus and requestPushPermission are `async`
// here and plain functions on Android. The C# API cannot expose that difference
// without making every game write platform-specific code, so all three answer
// through a callback on BOTH platforms: iOS awaits and replies, Android replies
// immediately. Uniform surface, honest underneath.
//
// The request id is what lets two in-flight calls resolve to the right handler.
// Without it a second call would deliver its answer to the first one's callback,
// which is a bug that only appears under a double tap.
//

private func reply(_ requestId: Int32, _ value: String) {
    send("OnNativeResult", jsonString(["id": Int(requestId), "value": value]))
}

@_cdecl("joryio_request_push_permission")
public func joryio_request_push_permission(_ requestId: Int32) {
    Task {
        let granted = await Joryio.shared.requestPushPermission()
        reply(requestId, granted ? "true" : "false")
    }
}

@_cdecl("joryio_get_push_permission_status")
public func joryio_get_push_permission_status(_ requestId: Int32) {
    Task {
        let status = await Joryio.shared.getPushPermissionStatus()
        reply(requestId, status)
    }
}

@_cdecl("joryio_is_push_enabled")
public func joryio_is_push_enabled(_ requestId: Int32) {
    Task {
        let enabled = await Joryio.shared.isPushEnabled()
        reply(requestId, enabled ? "true" : "false")
    }
}

// The app icon badge - iOS only; the Android bridge exports the same two
// symbols as logged no-ops so the C# surface is one shape.

@_cdecl("joryio_update_badge_count")
public func joryio_update_badge_count(_ count: Int32) {
    Joryio.shared.updateBadgeCount(Int(count))
}

@_cdecl("joryio_clear_badge")
public func joryio_clear_badge() {
    Joryio.shared.clearBadge()
}

// MARK: - Session, queue, auth

@_cdecl("joryio_get_session_id")
public func joryio_get_session_id() -> UnsafeMutablePointer<CChar>? {
    return cstr(Joryio.shared.getSessionId())
}

/// { sessionId, sessionStartMs, durationMs } - the same JSON the Android bridge
/// builds; C# turns it into the (sessionId, sessionStart, duration) tuple.
@_cdecl("joryio_get_session_info")
public func joryio_get_session_info() -> UnsafeMutablePointer<CChar>? {
    let info = Joryio.shared.getSessionInfo()
    return cstr(jsonString([
        "sessionId": info.sessionId,
        "sessionStartMs": Int64(info.sessionStart.timeIntervalSince1970 * 1000),
        "durationMs": Int64(max(0, info.duration) * 1000),
    ]))
}

// getQueueSize is deliberately NOT exposed. It is a plain function on both
// natives now (Android's was `suspend` until 2026-09-26), so nothing stops it
// technically - it is left out because it is a diagnostic no game needs. If it
// is ever added, add it to BOTH bridges and the C# surface in one change: an
// API that exists on one platform is the drift this project's parity tripwire
// exists to prevent.

@_cdecl("joryio_set_sdk_authentication_token")
public func joryio_set_sdk_authentication_token(_ token: UnsafePointer<CChar>?) {
    Joryio.shared.setSdkAuthenticationToken(str(token))
}

/// Forward SDK authentication failures to C# as OnSdkAuthError, with the same
/// four fields as Android and React Native. `reason` is the enum's raw value,
/// which is the wire spelling (sub_mismatch, not subMismatch).
@_cdecl("joryio_enable_sdk_authentication_errors")
public func joryio_enable_sdk_authentication_errors() {
    Joryio.shared.setSdkAuthenticationErrorHandler { error in
        var body: [String: Any] = [
            "reason": error.reason.rawValue,
            "endpoint": error.endpoint,
            "refreshExhausted": error.refreshExhausted,
        ]
        if let raw = error.rawReason { body["rawReason"] = raw }
        send("OnSdkAuthError", jsonString(body))
    }
}

// MARK: - E-commerce

/// ONE entry point for every e-commerce event, as in React Native. The case
/// list MUST match the Android bridge's switch and JoryioEcommerce.cs - the
/// canonical event names live in the native tracker, not here.
@_cdecl("joryio_ecommerce")
public func joryio_ecommerce(_ method: UnsafePointer<CChar>?, _ payloadJson: UnsafePointer<CChar>?) {
    let p = parseJson(str(payloadJson))
    let name = str(method)
    // JoryioEcommerce.cs refuses a call missing a required argument; this is
    // the second line of defence, so the defaults below (an empty id, a zero
    // amount) can never turn a missing argument into a zero order.
    guard ecommerceArgumentsPresent(name, p) else { return }
    let tracker = Joryio.shared.ecommerce()

    switch name {
    case "viewProduct":
        tracker.viewProduct(product(p["product"]))
    case "viewCategory":
        tracker.viewCategory(
            categoryId: p["categoryId"] as? String ?? "",
            categoryName: p["categoryName"] as? String ?? "")
    case "search":
        tracker.search(query: p["query"] as? String ?? "", resultCount: p["resultCount"] as? Int)
    case "addToCart":
        tracker.addToCart(item: cartItem(p["item"]), cartValue: p["cartValue"] as? Double)
    case "removeFromCart":
        tracker.removeFromCart(product: product(p["product"]), quantity: p["quantity"] as? Int ?? 1)
    case "cartViewed":
        tracker.cartViewed(items: cartItems(p["items"]), cartValue: p["cartValue"] as? Double ?? 0)
    case "updateCart":
        tracker.updateCart(items: cartItems(p["items"]), cartValue: p["cartValue"] as? Double ?? 0)
    case "startCheckout":
        tracker.startCheckout(items: cartItems(p["items"]), cartValue: p["cartValue"] as? Double ?? 0)
    case "addPaymentInfo":
        tracker.addPaymentInfo(paymentMethod: p["paymentMethod"] as? String ?? "")
    case "purchase":
        tracker.purchase(order: order(p["order"]))
    case "orderFulfilled":
        tracker.orderFulfilled(
            orderId: p["orderId"] as? String ?? "",
            trackingNumber: p["trackingNumber"] as? String,
            carrier: p["carrier"] as? String)
    case "orderDelivered":
        tracker.orderDelivered(orderId: p["orderId"] as? String ?? "")
    case "orderCancelled":
        tracker.orderCancelled(orderId: p["orderId"] as? String ?? "", reason: p["reason"] as? String)
    case "orderRefunded":
        tracker.orderRefunded(
            orderId: p["orderId"] as? String ?? "",
            refundAmount: p["refundAmount"] as? Double,
            reason: p["reason"] as? String)
    case "addToWishlist":
        tracker.addToWishlist(product: product(p["product"]))
    case "removeFromWishlist":
        tracker.removeFromWishlist(product: product(p["product"]))
    case "shareProduct":
        tracker.shareProduct(product: product(p["product"]), shareMethod: p["shareMethod"] as? String ?? "")
    case "applyCoupon":
        tracker.applyCoupon(
            couponCode: p["couponCode"] as? String ?? "",
            discountAmount: p["discountAmount"] as? Double,
            discountType: p["discountType"] as? String)
    case "removeCoupon":
        tracker.removeCoupon(couponCode: p["couponCode"] as? String ?? "")
    case "submitReview":
        tracker.submitReview(
            productId: p["productId"] as? String ?? "",
            rating: p["rating"] as? Int ?? 0,
            reviewText: p["reviewText"] as? String)
    default:
        // Loudly, not silently: a typo'd method name would otherwise be a no-op
        // that looks exactly like the SDK working.
        NSLog("[Joryio] Unknown ecommerce method '\(name)' - event NOT sent")
    }
}

/// The required arguments of each e-commerce call (contract item 42), the same
/// list as JoryioEcommerce.cs and the Android bridge. False, with a log line,
/// when one is missing, empty or not a finite number.
private func ecommerceArgumentsPresent(_ method: String, _ p: [String: Any]) -> Bool {
    func text(_ o: Any?, _ key: String) -> Bool {
        // Blank counts as missing, as in the native trackers, C# and the
        // Android bridge (one guard set); it used to accept "   " here.
        if let s = (o as? [String: Any])?[key] as? String,
           !s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return true }
        NSLog("[Joryio] ecommerce.\(method)() needs a non-empty \(key) - event NOT sent")
        return false
    }
    func number(_ o: Any?, _ key: String) -> Bool {
        if let n = (o as? [String: Any])?[key] as? NSNumber, n.doubleValue.isFinite { return true }
        NSLog("[Joryio] ecommerce.\(method)() needs a number \(key) - event NOT sent")
        return false
    }
    func array(_ o: Any?, _ key: String) -> Bool {
        if (o as? [String: Any])?[key] is [Any] { return true }
        NSLog("[Joryio] ecommerce.\(method)() needs an array of \(key) - event NOT sent")
        return false
    }
    switch method {
    case "viewProduct", "removeFromCart", "addToWishlist", "removeFromWishlist":
        return text(p["product"], "productId")
    case "shareProduct":
        return text(p["product"], "productId") && text(p, "shareMethod")
    case "addToCart":
        return text(p["item"], "productId")
    case "viewCategory":
        return text(p, "categoryId") && text(p, "categoryName")
    case "search":
        return text(p, "query")
    case "cartViewed", "updateCart", "startCheckout":
        return array(p, "items") && number(p, "cartValue")
    case "addPaymentInfo":
        return text(p, "paymentMethod")
    case "purchase":
        return text(p["order"], "orderId") && array(p["order"], "items") && number(p["order"], "value")
    case "orderFulfilled", "orderDelivered", "orderCancelled", "orderRefunded":
        return text(p, "orderId")
    case "applyCoupon", "removeCoupon":
        return text(p, "couponCode")
    case "submitReview":
        return text(p, "productId")
    default:
        return true
    }
}

private func product(_ raw: Any?) -> EcommerceProduct {
    let m = raw as? [String: Any] ?? [:]
    return EcommerceProduct(
        productId: m["productId"] as? String ?? "",
        name: m["name"] as? String ?? "",
        price: m["price"] as? Double ?? 0,
        quantity: m["quantity"] as? Int,
        category: m["category"] as? String,
        brand: m["brand"] as? String,
        variant: m["variant"] as? String,
        variantId: m["variantId"] as? String,
        sku: m["sku"] as? String,
        imageUrl: m["imageUrl"] as? String,
        url: m["url"] as? String)
}

private func cartItem(_ raw: Any?) -> EcommerceCartItem {
    let m = raw as? [String: Any] ?? [:]
    return EcommerceCartItem(
        productId: m["productId"] as? String ?? "",
        name: m["name"] as? String ?? "",
        price: m["price"] as? Double ?? 0,
        // Defaults to 1, not 0: a zero-quantity line is a silently wrong
        // revenue number. Matches Android and React Native.
        quantity: m["quantity"] as? Int ?? 1,
        category: m["category"] as? String,
        brand: m["brand"] as? String,
        variant: m["variant"] as? String,
        variantId: m["variantId"] as? String,
        sku: m["sku"] as? String,
        imageUrl: m["imageUrl"] as? String,
        url: m["url"] as? String)
}

private func cartItems(_ raw: Any?) -> [EcommerceCartItem] {
    guard let arr = raw as? [Any] else { return [] }
    return arr.map { cartItem($0) }
}

private func order(_ raw: Any?) -> EcommerceOrder {
    let m = raw as? [String: Any] ?? [:]
    return EcommerceOrder(
        orderId: m["orderId"] as? String ?? "",
        items: cartItems(m["items"]),
        value: m["value"] as? Double ?? 0,
        currency: m["currency"] as? String,
        shipping: m["shipping"] as? Double,
        tax: m["tax"] as? Double,
        discount: m["discount"] as? Double,
        coupon: m["coupon"] as? String)
}

// MARK: - In-app campaign control

@_cdecl("joryio_sync_in_app_campaigns")
public func joryio_sync_in_app_campaigns() {
    Task { await Joryio.shared.syncInAppCampaigns() }
}

// MARK: - Placements (D444)
//
// The native SDK fetches, caches, rate-limits and reports. The bridge hands
// C# the data as JSON and routes C#'s Mark* calls to the native objects it
// last handed out - so every report passes the native identity check.

private var lastPlacements: [String: Placement] = [:]
private let placementsLock = NSLock()
private var placementsSubscription: JoryioSubscription?

private func placementsJson(_ map: [String: Placement]) -> String {
    placementsLock.lock()
    for (k, p) in map { lastPlacements[k] = p }
    placementsLock.unlock()
    return jsonString(map.mapValues { $0.toJson() })
}

@_cdecl("joryio_get_placements")
public func joryio_get_placements(_ keysJson: UnsafePointer<CChar>?, _ requestId: Int32) {
    let keys = parseJsonArray(str(keysJson))
    Joryio.shared.getPlacements(keys) { map in
        reply(requestId, placementsJson(map))
    }
}

@_cdecl("joryio_refresh_placements")
public func joryio_refresh_placements(_ keysJson: UnsafePointer<CChar>?) {
    let raw = str(keysJson)
    Joryio.shared.refreshPlacements(raw.isEmpty ? nil : parseJsonArray(raw))
}

@_cdecl("joryio_enable_placements_updates")
public func joryio_enable_placements_updates() {
    guard placementsSubscription == nil else { return }
    placementsSubscription = Joryio.shared.onPlacementsUpdated { map in
        send("OnPlacementsUpdated", placementsJson(map))
    }
}

@_cdecl("joryio_disable_placements_updates")
public func joryio_disable_placements_updates() {
    placementsSubscription?.cancel()
    placementsSubscription = nil
}

@_cdecl("joryio_mark_placement")
public func joryio_mark_placement(_ placementKey: UnsafePointer<CChar>?, _ itemId: UnsafePointer<CChar>?, _ action: UnsafePointer<CChar>?, _ elementId: UnsafePointer<CChar>?) {
    let key = str(placementKey), id = str(itemId), act = str(action), element = str(elementId)
    placementsLock.lock()
    let placement = lastPlacements[key]
    placementsLock.unlock()
    guard let placement else { return }
    if act == "control" {
        placement.markDisplayed()
        return
    }
    guard let item = placement.items.first(where: { $0.id == id }) else { return }
    switch act {
    case "displayed": item.markDisplayed()
    case "clicked": item.markClicked(elementId: element.isEmpty ? nil : element)
    case "dismissed": item.markDismissed()
    default: break
    }
}

// MARK: - Content cards (D444 Phase 3)
//
// The native SDK fetches, caches, rate-limits, keeps the anonymous visitor's
// device state and reports. The bridge hands C# the feed as JSON
// (CardFeed.toJson, no delivery tokens) and routes C#'s reports to the native
// Card values it last handed out - so a report carries the card's own token
// and passes the native identity check. An id the bridge never handed out
// goes to the id overload (the native SDK looks it up).

private var lastCards: [String: Card] = [:]
private let cardsLock = NSLock()
private var cardsSubscription: JoryioSubscription?

/// `page`: a cursor page (added to what was handed out) rather than the whole feed (which replaces it).
private func cardsJson(_ feed: CardFeed, page: Bool) -> String {
    cardsLock.lock()
    if !page { lastCards.removeAll() }
    for card in feed.cards { lastCards[card.id] = card }
    cardsLock.unlock()
    return jsonString(feed.toJson())
}

/// `cursor`: a feed's nextCursor for the next page, "" for the feed.
@_cdecl("joryio_get_cards")
public func joryio_get_cards(_ cursor: UnsafePointer<CChar>?, _ requestId: Int32) {
    let raw = str(cursor)
    let c: String? = raw.isEmpty ? nil : raw
    Joryio.shared.getCards(cursor: c) { feed in
        reply(requestId, cardsJson(feed, page: c != nil))
    }
}

/// From the native cache, never a network call.
@_cdecl("joryio_get_unread_card_count")
public func joryio_get_unread_card_count() -> Int32 {
    Int32(clamping: Joryio.shared.getUnreadCardCount())
}

/// `action`: viewed | read | clicked | dismissed (the wire values of POST /v1/in-app/track).
@_cdecl("joryio_mark_card")
public func joryio_mark_card(_ cardId: UnsafePointer<CChar>?, _ action: UnsafePointer<CChar>?) {
    let id = str(cardId), act = str(action)
    guard !id.isEmpty else { return }
    cardsLock.lock()
    let card = lastCards[id]
    cardsLock.unlock()
    switch act {
    case "viewed": if let card { Joryio.shared.markCardViewed(card) } else { Joryio.shared.markCardViewed(id) }
    case "read": if let card { Joryio.shared.markCardRead(card) } else { Joryio.shared.markCardRead(id) }
    case "clicked": if let card { Joryio.shared.markCardClicked(card) } else { Joryio.shared.markCardClicked(id) }
    case "dismissed": if let card { Joryio.shared.dismissCard(card) } else { Joryio.shared.dismissCard(id) }
    default: break
    }
}

@_cdecl("joryio_refresh_cards")
public func joryio_refresh_cards() {
    Joryio.shared.refreshCards()
}

@_cdecl("joryio_enable_cards_updates")
public func joryio_enable_cards_updates() {
    guard cardsSubscription == nil else { return }
    // nil before initialize(): Initialize switches the updates on again.
    cardsSubscription = Joryio.shared.attachCardFeedListener { feed in
        send("OnCardsUpdated", cardsJson(feed, page: false))
    }
}

@_cdecl("joryio_disable_cards_updates")
public func joryio_disable_cards_updates() {
    cardsSubscription?.cancel()
    cardsSubscription = nil
}

// MARK: - Links (SetLinkHandler)

/// Links waiting for the C# handler, by id; oldest dropped past the cap. A
/// link C# took stays here so its OpenDefault() can still run it later.
private var pendingLinks: [String: JoryioLink] = [:]
private var pendingLinkOrder: [String] = []
private var nextLinkId = 0
private let maxPendingLinks = 32

/// C# set a link handler: every link the native SDK is about to open goes to
/// C# first (OnLink) and waits for joryio_complete_link. The native handler
/// takes the link at once - UnitySendMessage cannot answer - and C# hands a
/// declined one back. App-wide in the native SDK, so it works before Initialize.
@_cdecl("joryio_enable_link_handler")
public func joryio_enable_link_handler() {
    Joryio.shared.setLinkHandler { link in
        nextLinkId += 1
        let id = "l\(nextLinkId)"
        pendingLinks[id] = link
        pendingLinkOrder.append(id)
        while pendingLinkOrder.count > maxPendingLinks {
            pendingLinks[pendingLinkOrder.removeFirst()] = nil
        }
        var body: [String: Any] = [
            "linkId": id,
            "url": link.url.absoluteString,
            "source": link.source.rawValue,
            "isAppDeepLink": link.isAppDeepLink,
        ]
        if let v = link.campaignId { body["campaignId"] = v }
        if let v = link.placementKey { body["placementKey"] = v }
        if let v = link.itemId { body["itemId"] = v }
        if let v = link.cardId { body["cardId"] = v }
        send("OnLink", jsonString(body))
        return true
    }
}

@_cdecl("joryio_disable_link_handler")
public func joryio_disable_link_handler() {
    Joryio.shared.clearLinkHandler()
    DispatchQueue.main.async {
        pendingLinks.removeAll()
        pendingLinkOrder.removeAll()
    }
}

/// C#'s answer: handled false opens the link as the SDK does without a handler.
@_cdecl("joryio_complete_link")
public func joryio_complete_link(_ linkId: UnsafePointer<CChar>?, _ handled: Bool) {
    let id = str(linkId)
    DispatchQueue.main.async {
        guard let link = pendingLinks.removeValue(forKey: id) else { return }
        pendingLinkOrder.removeAll { $0 == id }
        if !handled { link.openDefault() }
    }
}

@_cdecl("joryio_reset_displayed_campaigns")
public func joryio_reset_displayed_campaigns() {
    Joryio.shared.resetDisplayedCampaigns()
}

// MARK: - Feature flags (BACKLOG D446)
//
// Every getter passes the game's default to the native getter and returns its
// answer, so the native SDK decides (value, or default) and logs the exposure
// exactly as it does for a Swift caller. Values of any type cross as JSON
// text; the typed getters cross as Bool / Double / String.

private func parseJsonValue(_ raw: String) -> Any? {
    guard !raw.isEmpty, let data = raw.data(using: .utf8),
          let value = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) else { return nil }
    return value is NSNull ? nil : value
}

private func jsonValueString(_ value: Any?) -> String {
    guard let value, !(value is NSNull),
          let data = try? JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed]),
          let s = String(data: data, encoding: .utf8) else { return "null" }
    return s
}

@_cdecl("joryio_get_flag")
public func joryio_get_flag(_ key: UnsafePointer<CChar>?, _ defaultJson: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    cstr(jsonValueString(Joryio.shared.getFlag(str(key), default: parseJsonValue(str(defaultJson)))))
}

@_cdecl("joryio_get_bool_flag")
public func joryio_get_bool_flag(_ key: UnsafePointer<CChar>?, _ defaultValue: Bool) -> Bool {
    Joryio.shared.getBoolFlag(str(key), default: defaultValue)
}

@_cdecl("joryio_get_string_flag")
public func joryio_get_string_flag(_ key: UnsafePointer<CChar>?, _ defaultValue: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    cstr(Joryio.shared.getStringFlag(str(key), default: str(defaultValue)))
}

@_cdecl("joryio_get_number_flag")
public func joryio_get_number_flag(_ key: UnsafePointer<CChar>?, _ defaultValue: Double) -> Double {
    Joryio.shared.getNumberFlag(str(key), default: defaultValue)
}

@_cdecl("joryio_get_json_flag")
public func joryio_get_json_flag(_ key: UnsafePointer<CChar>?, _ defaultJson: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    cstr(jsonValueString(Joryio.shared.getJsonFlag(str(key), default: parseJsonValue(str(defaultJson)))))
}

/// Every flag as one JSON object. Logs no exposure.
@_cdecl("joryio_get_all_flags")
public func joryio_get_all_flags() -> UnsafeMutablePointer<CChar>? {
    let all = Joryio.shared.getAllFlags()
    return cstr(all.isEmpty ? "{}" : jsonValueString(all))
}

/// Answers "true" through the request id once the answer is in (or the request failed).
@_cdecl("joryio_refresh_flags")
public func joryio_refresh_flags(_ requestId: Int32) {
    Joryio.shared.refreshFlags { reply(requestId, "true") }
}

/// One native subscription for the process; C# fans it out to its listeners.
private var flagsSubscription: JoryioSubscription?

@_cdecl("joryio_enable_flags_updates")
public func joryio_enable_flags_updates() {
    guard flagsSubscription == nil else { return }
    flagsSubscription = Joryio.shared.onFlagsUpdated { keys in
        send("OnFlagsUpdated", jsonValueString(keys))
    }
}
