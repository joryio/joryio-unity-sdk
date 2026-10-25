# Joryio Unity SDK

The Joryio SDK for Unity games: identify players, track events, receive push notifications and show in-app messages, all through your Joryio workspace.

The Unity SDK is a thin layer over the native Joryio [iOS](https://github.com/joryio/joryio-ios-sdk) and [Android](https://github.com/joryio/joryio-android-sdk) SDKs. Every call forwards to the native SDK, so batching, offline queueing and retries behave exactly as in a native app.

## Features

- **Event tracking** and screen views, with values that keep their type (`7` stays a number, nested dictionaries and lists stay objects and arrays)
- **E-commerce events** with the platform's canonical names (`JoryioSDK.Ecommerce.Purchase(...)`)
- **Identity** - anonymous ID out of the box, `Identify`, labelled aliases with `AddAlias`
- **Player attributes** - set, increment and unset
- **Push notifications** - permission prompt, automatic APNs token on iOS, token registration, delivery receipts and click tracking
- **In-app messages** rendered in-engine with your own art, or drawn by the native SDK; forms inside HTML messages
- **Subscription preferences** per channel and subscription group
- **Privacy controls** - tracking consent (`granted` / `pending` / `denied`), opt-out from launch, on-device data wipe
- **SDK authentication** with a token-refresh callback
- **No platform `#if`s needed** - on the Editor, desktop and consoles every call exists and does nothing (one warning per session)
- **Never throws into your game** - calls before `Initialize`, null arguments and native failures are logged and dropped

## Requirements

- Unity 2022.3 LTS or newer (Unity 2021.3 and 2022.1 build Android with JDK 8 and cannot compile against the native Android SDK, which is Java 11 bytecode)
- iOS 14.0+ and Android 6.0 (API level 23)+
- Android: Target API Level 34 or higher (Player Settings). The native SDK's AndroidX libraries require compileSdk 34, and Unity's Android build fails below it
- Android: [External Dependency Manager for Unity](https://github.com/googlesamples/unity-jar-resolver) (EDM4U). iOS does not use it
- iOS: Xcode 15 or newer, with network access to `github.com` the first time the exported project builds (Xcode downloads the native SDK as a Swift package)
- A Joryio workspace with an **iOS app** and an **Android app**, each with its own SDK key (`jry_sdk_ios_...`, `jry_sdk_android_...`) from **Settings → Apps** in the dashboard
- For Android push: Firebase Cloud Messaging in the game - the Firebase Unity SDK's Messaging package and your `google-services.json`. The native Android SDK compiles against `firebase-messaging` but does not bring it; without it analytics and in-app messaging work and push stays off

## Installation

### 1. Add the package

**From Git** (recommended): in the Package Manager choose **+ → Add package from git URL** and enter the URL with the release tag:

```
https://github.com/joryio/joryio-unity-sdk.git#1.3.2
```

Or add it to `Packages/manifest.json` yourself:

```json
{
  "dependencies": {
    "com.joryio.sdk": "https://github.com/joryio/joryio-unity-sdk.git#1.3.2"
  }
}
```

To update, change the tag to the new version. Without a tag the Package Manager follows the newest commit, so always pin one.

**From a `.unitypackage`**: download `joryio-unity-<version>.unitypackage` from the [GitHub releases page](https://github.com/joryio/joryio-unity-sdk/releases), then in Unity choose **Assets → Import Package → Custom Package** and import everything. The SDK lands under `Assets/JoryioSDK`. To update, delete that folder and import the new version.

Use one install method, not both. The Git URL package and `Assets/JoryioSDK` carry the same assemblies (`Joryio.Runtime`, `Joryio.Editor`) and the same asset GUIDs, so a project with both does not compile. To switch, remove the one you have first: delete `Assets/JoryioSDK`, or remove the Joryio package in the Package Manager. The `.unitypackage` brings the licence terms (`LICENSE`), this README and `CHANGELOG.md` into `Assets/JoryioSDK` as well.

The package is not on OpenUPM: OpenUPM lists open-source packages only, and the Joryio SDKs are licensed commercially.

### 2. The native SDKs

The package ships no prebuilt native binaries. The native SDKs are resolved by coordinate when you build:

| Platform | Resolved dependency | Through |
|----------|--------------------|---------|
| Android | `io.joryio:joryio-android:1.3.2` and `org.jetbrains.kotlin:kotlin-stdlib` (Maven Central), from `Editor/JoryioDependencies.xml` | EDM4U and Gradle |
| iOS | Swift package `https://github.com/joryio/joryio-ios-sdk` at exactly 1.3.2, product `Joryio`, linked into `UnityFramework` | the package's iOS post-build step, then Xcode |

Resolving by coordinate lets Gradle and Swift Package Manager reconcile libraries the SDK shares with your other plugins (OkHttp, Kotlin, SQLite and so on) instead of colliding at build time. `kotlin-stdlib` is listed because the Java bridge compiles against Kotlin types; Gradle picks the newest one any plugin asks for.

Install EDM4U if your project does not have it yet, then on Android run **Assets → External Dependency Manager → Android Resolver → Resolve**. On iOS there is nothing to resolve in Unity: the post-build step adds the Swift package to the exported Xcode project, and Xcode downloads it the first time it opens or builds the project. CocoaPods is not involved, so the SDK is unaffected by CocoaPods trunk going read-only on 2 December 2026. Upgrading from a version that used the `Joryio/Core` pod: build the iOS player once with **Replace** rather than **Append**, and delete any `<iosPod name="Joryio/...">` line from your own EDM4U files.

### 3. What the package configures for you

- **Xcode**: the native SDK as a Swift package (`joryio-ios-sdk`, linked into `UnityFramework`), Swift version, Swift runtime embedding, the Push Notifications capability, the remote-notification background mode, and `UNITY_USES_REMOTE_NOTIFICATIONS` (without it Unity compiles out the callback that delivers the APNs token). The capability is added to your target's **existing** entitlements file when it has one, so other plugins' entitlements are kept.
- **Android**: ProGuard/R8 keep rules for the Java bridge, appended to the generated `proguard-unity.txt`, so a minified release build keeps the methods C# calls by name.

## Quick start

```csharp
using System.Collections.Generic;
using UnityEngine;
using Joryio.Runtime;

public class Bootstrap : MonoBehaviour
{
    void Awake()
    {
#if UNITY_IOS
        const string sdkKey = "jry_sdk_ios_YOUR_IOS_KEY";
#else
        const string sdkKey = "jry_sdk_android_YOUR_ANDROID_KEY";
#endif
        JoryioSDK.Initialize(sdkKey, "https://api-eu1.joryio.com", new JoryioConfig {
            Debug = UnityEngine.Debug.isDebugBuild,
        });
    }
}
```

Use the API host shown for your workspace in the dashboard. Calls made before `Initialize` are ignored with a warning, except:

- `OptOut()`, `OptIn()`, `UpdateConsent()` and `WipeData()`, which are held and applied when the SDK starts, before anything is sent, and `IsUserOptedOut()` and `GetTrackingConsent()` (see [Privacy controls](#privacy-controls)).
- `SetSdkAuthenticationToken()` and `SetSdkAuthenticationErrorHandler()`: the native SDK keeps the token and starts with it, ahead of its first request (see [SDK authentication](#sdk-authentication)).
- Subscription changes - `SetSubscription`, `AddToSubscriptionGroup`, `RemoveFromSubscriptionGroup` - and `UnregisterPush()`. They are held, kept on the device across a restart, and sent once the SDK starts. A newer change of the same channel or group replaces an older one, so the player's last choice is the one sent. A withdrawal - an unsubscribe, leaving a group, `UnregisterPush()` - is always sent: an `OptOut()`, a `Denied` consent or `WipeData()` before `Initialize` discards held subscribes, never withdrawals. A subscribe, an opt-in or a group join made before `Initialize` while the player is opted out or consent is `Denied` (stored from an earlier launch or set in this one) is not held at all: it is dropped with a warning, and a later `OptIn()` or `Granted` does not send it. A withdrawal made before a `WipeData()` still goes out for the player who made it, not for the fresh identity the wipe creates. A `RegisterPushToken` before `Initialize` is not sent, and it does not cancel a held `UnregisterPush()`.

A second `Initialize` is ignored. A key for another platform (iOS keys start with `jry_sdk_ios_`, Android keys with `jry_sdk_android_`) is refused with an error and the SDK stays off, so a later `Initialize` with the right key works. An iOS launch before the device's first unlock since it started is not a failure: `IsInitialized` is true, the native SDK finishes starting at the unlock, and calls made meanwhile wait for it. If calls held by an earlier launch are still on the device, iOS keeps them unreadable until that unlock too; the SDK then collects nothing and every call waits until they can be read, and sends them first and the calls made since after them, in the order they were made. Meanwhile `GetTrackingConsent()` and `IsUserOptedOut()` report the decisions this launch made (calls and `JoryioConfig`). Without one, `GetTrackingConsent()` reports `Pending`, because the stored consent cannot be read yet, and `IsUserOptedOut()` answers from the native SDK, which may not see an opt-out stored by an earlier launch until the unlock. When the calls are sent, the consent applied is the one this launch set explicitly (`UpdateConsent` before `Initialize`, else `JoryioConfig.TrackingConsent`); without one, the decision saved on the device applies, read after the unlock, the same as the native SDKs: a stored `Denied` stays `Denied`, and with none stored the SDK is `Granted`, as on any other launch. Other getters, such as `GetUserId()`, `GetAnonymousId()` and `GetSessionId()`, still answer from the native SDK and do not yet reflect the calls that are waiting.

### Track events

```csharp
JoryioSDK.Track("level_complete", new Dictionary<string, object> {
    { "level", 7 },
    { "duration_seconds", 143.5 },
    { "loadout", new List<string> { "sword", "shield" } },
    { "finished_at", System.DateTime.UtcNow },   // sent as ISO-8601 UTC
});

JoryioSDK.TrackScreen("Shop");
```

Every numeric type keeps its type, nested dictionaries and lists stay JSON objects and arrays, `DateTime` becomes ISO-8601 in UTC, and inside event properties `NaN` or infinity becomes `null` rather than breaking the event. As an **attribute** value (`SetAttribute`, `SetAttributes`, `IncrementAttribute`) `NaN` or infinity is ignored with a warning and the attribute is left unchanged - it is not unset.

### Identify players

```csharp
JoryioSDK.Identify("player-1234");
JoryioSDK.SetAttributes(new Dictionary<string, object> { { "favourite_class", "mage" } });
JoryioSDK.SetAttribute("vip", true);
JoryioSDK.IncrementAttribute("games_played");
JoryioSDK.UnsetAttribute("temporary_flag");
JoryioSDK.SetAttribute("clan", null);   // a null value unsets, the same as UnsetAttribute("clan")

// While the player is opted out or consent is Denied, Identify only remembers
// the ID on the device - nothing is sent - so an unsubscribe made meanwhile
// reaches the right player. Reset() and WipeData() forget it.

// Another identifier for the same player - works before Identify too
JoryioSDK.AddAlias("steam_id", "76561198000000000");

// On logout: a new anonymous ID, no user ID
JoryioSDK.Reset();
```

## Configuration

Every `JoryioConfig` property is optional. Durations are in seconds, except `RetryBackoffMs`, which keeps the native SDKs' name:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Debug` | `bool` | `false` | Verbose native logging on both platforms; also unlocks the testing calls (`ResetDisplayedCampaigns`, `EvaluateInAppCampaigns`) |
| `BatchSize` | `int` | `50` | Events per batch |
| `FlushIntervalSeconds` | `double` | `5` | Seconds between automatic flushes |
| `SendImmediately` | `bool` | `false` | Send each event immediately |
| `MaxQueueSize` | `int` | `1000` | Most events held while sends fail |
| `RequestTimeoutSeconds` | `double` | `10` | Per-request timeout |
| `MaxRetries` | `int` | `3` | Retries per request before it waits for the next flush |
| `RetryBackoffMs` | `int` | `1000` | Base delay between retries, in milliseconds |
| `SessionTimeoutSeconds` | `double` | `1800` | Inactivity before a new session |
| `TrackSessionStart` | `bool` | `true` | Track "Session Start" automatically |
| `AnonymousId` | `string` | generated | Your own anonymous ID for this device |
| `RequestPushPermissionAtLaunch` | `bool` | `false` | Ask for notification permission during `Initialize` |
| `TrackingConsent` | `TrackingConsent?` | `null` (not set) | Your consent screen's answer at launch. Not set keeps the answer the SDK remembered (a stored `Denied` stays denied) and otherwise tracks normally. `Granted` tracks and replaces a stored denial; `Pending` queues events on the device and sends nothing until `UpdateConsent(TrackingConsent.Granted)`, for this launch only; `Denied` collects nothing and is remembered. See [Privacy controls](#privacy-controls) |
| `OptOut` | `bool` | `false` | Start opted out. An opt-out is stored, so `false` here does not undo an earlier `OptOut()` - call `OptIn()` |
| `NotificationChannelName` | `string` | `null` (`"Notifications"`) | **Android only.** The notification channel's name in the system's notification settings (Android 8+); pass it localised. iOS has no channels and ignores it |
| `EnableSdkAuthentication` | `bool` | `false` | Send a signed token with every request (see [SDK authentication](#sdk-authentication)) |
| `SdkAuthenticationToken` | `string` | none | The token to start with, if you already have one |
| `AllowHtmlJsInAppMessages` | `bool` | `false` | Let the **native** UI draw HTML in-app messages. Scripts in them do not run; the native SDK's bridge handles buttons and forms. No effect on messages your own handler renders |
| `HoldInAppMessagesForHandler` | `bool` | `false` | Your game draws in-app messages with `SetInAppMessageHandler`, which can only come after `Initialize`: hold messages that are ready before it (or until `ClearInAppMessageHandler()`), so none is drawn natively first |

## Push notifications

Upload your push credentials in the dashboard first: [APNs for iOS](https://docs.joryio.com/developers/apps/apns-credentials), [Firebase for Android](https://docs.joryio.com/developers/apps/fcm-credentials).

Ask when the player has a reason to say yes - the system prompt is one-shot:

```csharp
JoryioSDK.GetPushPermissionStatus(status => {
    // "granted", "denied", "notDetermined" or "unavailable"
    if (status == "notDetermined") {
        JoryioSDK.RequestPushPermission(granted => {
            Debug.Log(granted ? "Push enabled" : "Player declined");
        });
    }
});
```

The callback runs on the main thread, exactly once.

**Who registers the device.** On **iOS** the SDK registers by itself: it receives the APNs device token from Unity's app controller once permission is granted - nothing to wire in an app delegate. On **Android** it registers by itself only when **Firebase Cloud Messaging is in the game** (the Firebase Unity SDK's Messaging package and `google-services.json`): the native SDK compiles against `firebase-messaging` but does not bring it, so without Firebase there is no FCM token, nothing is registered, and push stays off while analytics and in-app messaging work normally.

**Android activity.** The prompt is shown from your game's activity. Unity 6 builds use `GameActivity` by default; earlier versions (and Unity 6 projects that select it) use the classic `UnityPlayerActivity`. Both are supported by the native SDK 1.3.2 that this package resolves: the callback reports the player's answer after the system dialog closes. Below Android 13 there is no runtime prompt and the callback answers at once with whether notifications are enabled.

### If the game already has a push stack

Keep it and hand the SDK the token - the APNs device token on iOS, the FCM registration token on Android. On Android that token comes from Firebase Messaging (or your own FCM wrapper): **Unity Mobile Notifications does not produce an FCM token on Android** - it schedules local notifications there - so it can supply the token on iOS only.

```csharp
JoryioSDK.RegisterPushToken(token, result => {
    if (!result.success && result.isRetryable) {
        // transient failure (offline, DNS, 5xx): register again later
    }
});

// With the trackingId from the message's data payload:
JoryioSDK.ReportPushDelivered(trackingId);   // when a push arrives
JoryioSDK.TrackPushClick(trackingId);        // when the player opens it
```

What the SDK still does on its own, so nothing surprises you:

- **iOS**: `Initialize` makes the SDK the `UNUserNotificationCenter` delegate, and forwards every callback to the delegate that was installed before it. Initialize Joryio **after** your other push plugin has set its delegate (Unity Mobile Notifications does so at launch, so `Awake` is fine); a plugin that sets the delegate later replaces Joryio's, and then Joryio's open tracking relies on your `TrackPushClick` call. The SDK also receives the APNs token from Unity's app controller itself - reporting the same token again through `RegisterPushToken` is harmless - unless the player has turned push off, because `RegisterPushToken` lifts an `UnregisterPush()` (see below).
- **Android**: the SDK does not touch the FCM service when you register the token yourself.

`RegisterPushToken` turns push back on for this device: it lifts an earlier `UnregisterPush()`. Firebase's `OnTokenReceived` fires by itself at every start, so if you forward it, skip it while the player has turned push off in your settings, and call `RegisterPushToken` when they turn it back on. Otherwise a token refresh, not the player, undoes their choice.

A Joryio push carries the same data keys on every platform: `joryio` (always `"true"`), `trackingId`, `jry_campaign_id` (campaign or journey id), `jry_deep_link` (what a tap opens) and `jry_image_url`. On Android the native SDK also records the tap itself when the game is opened from a notification; reporting it as well is harmless, a click is counted once per notification.

Also available: `IsPushEnabled(callback)`, `GetDeviceToken()`, `UnregisterPush()`, and on iOS `UpdateBadgeCount(n)` / `ClearBadge()` (no-ops on Android, where launchers badge from notifications themselves).

## In-app messages

### Render them in-engine

Install a handler to draw messages with your own UI:

```csharp
JoryioSDK.SetInAppMessageHandler(message => {
    // Title / Body / ImageUrl / Buttons / Style are the authored content.
    // Name is the campaign's dashboard label - never show it to a player.
    myCanvas.Show(message.Title, message.Body, message.ImageUrl);
    foreach (var button in message.Buttons)
        myCanvas.AddButton(button.Text, () => {
            message.MarkClicked(button);
            if (button.Action == "url" || button.Action == "deep_link")
                Application.OpenURL(button.Url);
            else if (button.Action == "request_push_permission")
                JoryioSDK.RequestPushPermission(granted => { });
            myCanvas.Hide();
            message.MarkDismissed();   // the tap closed the message
        });
    message.MarkDisplayed();
}, canRenderHtml: false);
```

Installing a handler means **you** report what happened: call `MarkDisplayed()` when the message appears, `MarkClicked(button)` when a button is tapped, and `MarkDismissed()` when the message closes - a tap that closes it is reported as both, clicked then dismissed, as the native SDKs do when they draw it. A message you never report counts as never shown: reporting shows no displays, revenue attribution has no display to join on, and frequency caps do not count it. `MarkDisplayed()` reports once per message however often you call it; `MarkClicked(button)` sends the button's `Id` as the click's `elementId` so the campaign's click map can tell buttons apart (a button without an id sends none). The SDK cannot act on a button you draw: open the URL yourself, and for a `request_push_permission` button call `RequestPushPermission`.

`canRenderHtml` describes your renderer. A Unity canvas cannot display HTML messages (they need a web view), so leave it `false` unless your game hosts one.

To hand rendering back to the native SDK - when your renderer is destroyed, say - call `JoryioSDK.ClearInAppMessageHandler()` (or `SetInAppMessageHandler(null)`, which does the same). The native SDK then draws messages with its own views again, if its UI artifacts are in the build (see below). From a component that may not be the only renderer, pass the handler you installed - `ClearInAppMessageHandler(myHandler)` - and it is removed only if it is still the installed one, never a handler another script installed after it. `ClearInAppMessageHandler(null)` clears whatever is installed.

### Forms inside HTML messages

If your web view renders an HTML message with a `<form data-jry-form>`, submit it through the SDK so the values are written to this player's profile:

```csharp
JoryioSDK.SubmitInAppForm(message.Id, new Dictionary<string, object> {
    { "email", "player@example.com" },
    { "topics", new List<string> { "events", "offers" } },   // a checkbox group
}, result => {
    if (!result.ok) Debug.Log(result.message);
});
```

The callback always runs once; a failure (including no network) is `ok = false`.

### Or let the native SDK draw them

The package resolves the UI-free native artifacts. For the native SDK to draw messages itself (as an OS-styled overlay), add the UI artifacts:

- **Android**: an EDM4U dependencies file of your own, for example `Assets/Editor/JoryioUIDependencies.xml`:

  ```xml
  <dependencies>
    <androidPackages>
      <androidPackage spec="io.joryio:joryio-android-ui:1.3.2" />
    </androidPackages>
  </dependencies>
  ```

- **iOS**: the scripting define symbol `JORYIO_IOS_UI` (**Player Settings → iOS → Other Settings → Scripting Define Symbols**). The iOS build then links the Swift package's `JoryioUI` product as well.

Other in-app calls: `SyncInAppCampaigns()`, `TrackInAppImpression(campaignId, action, elementId)` (prefer the `Mark...` methods on the message), and two testing helpers that only run with `Debug` on: `ResetDisplayedCampaigns()` and `EvaluateInAppCampaigns()`.

## E-commerce

Use the typed tracker rather than `Track` for commerce: it applies the canonical event names that revenue reporting and attribution read. A hand-typed name such as "Added to Cart" is stored as a different event and matches no report.

```csharp
var item = new EcommerceCartItem { ProductId = "gems_500", Name = "500 gems", Price = 4.99, Quantity = 1 };
JoryioSDK.Ecommerce.AddToCart(item, cartValue: 4.99);
JoryioSDK.Ecommerce.Purchase(new EcommerceOrder {
    OrderId = "order-1042",
    Items = new List<EcommerceCartItem> { item },
    Value = 4.99,
    Currency = "USD",
});
```

Also: `ViewProduct`, `ViewCategory`, `Search`, `RemoveFromCart`, `CartViewed`, `UpdateCart`, `StartCheckout`, `AddPaymentInfo`, `OrderFulfilled`, `OrderDelivered`, `OrderCancelled`, `OrderRefunded`, `AddToWishlist`, `RemoveFromWishlist`, `ShareProduct`, `ApplyCoupon`, `RemoveCoupon` and `SubmitReview` - the same list as React Native.

A call missing a required argument logs a warning and sends nothing - never an empty or zero event: a product without a `ProductId`, an order without an `OrderId` or `Items`, an empty order or coupon id, a cart call without its item list. An amount that is `NaN` or infinite (`Value`, `Price`, `cartValue`, `Shipping`, `Tax`, `Discount`, `refundAmount`, `discountAmount`) is refused the same way, rather than recorded as `0`.

## Samples

Import from the Package Manager (select **Joryio**, open **Samples**, click **Import**):

| Sample | What it is |
|--------|-----------|
| **In-app renderer** | A scene, a restylable panel prefab and the script that wires them together - draw in-app messages in-engine with your own art. Requires `com.unity.ugui` |

Installed from the `.unitypackage`? There is no Package Manager entry to import from: download `joryio-unity-<version>-sample.unitypackage` from the same [releases page](https://github.com/joryio/joryio-unity-sdk/releases) and import it the same way. It lands in `Assets/Samples/Joryio/<version>/In-app renderer`, where the Package Manager puts the sample.

The renderer draws `JoryioInAppPanel.prefab`, assigned to its **Panel Prefab** field: restyle the prefab, or point the field at your own with children named `Title`, `Body`, `PrimaryButton` and `SecondaryButton`. It creates an EventSystem if the scene has none, with the Input System's UI module when that is your active input handling. Messages arrive only in an iOS or Android build: in the Editor the SDK is a no-op, so pressing Play shows nothing.

## Subscription preferences

Whether a player may be **messaged** on a channel - separate from `OptOut()`, which is whether they may be **tracked**.

```csharp
JoryioSDK.SetSubscription("email", "unsubscribed");
JoryioSDK.AddToSubscriptionGroup("list_abc123", "email");
JoryioSDK.RemoveFromSubscriptionGroup("list_abc123", "email");
```

Channels: `email`, `sms`, `whatsapp`, `push`, `viber`. Statuses: `optedIn`, `subscribed`, `unsubscribed`.

## Privacy controls

Two independent switches, the same on every Joryio SDK:

- **Tracking consent** - your consent screen's answer: `Granted`, `Denied` or `Pending`.
- **Opt-out** - the player's own choice: `OptOut()` / `OptIn()`.

Data is collected only when consent is granted **and** the player has not opted out; either "no" wins. Neither switch changes the other: `UpdateConsent(TrackingConsent.Granted)` does not lift an opt-out (only `OptIn()` does), and `OptOut()` does not change the consent.

```csharp
// Before the player has answered your consent screen
JoryioSDK.Initialize(sdkKey, apiHost, new JoryioConfig { TrackingConsent = TrackingConsent.Pending });
// ...once they answer
JoryioSDK.UpdateConsent(TrackingConsent.Granted);   // or TrackingConsent.Denied

JoryioSDK.OptOut();          // stop collecting; queued events and pending attribute writes are discarded
JoryioSDK.OptIn();           // resume
JoryioSDK.IsUserOptedOut();  // the opt-out switch only
JoryioSDK.GetTrackingConsent();   // the consent switch only: Granted, Denied or Pending
JoryioSDK.WipeData();        // erase everything stored on the device (the opt-out is kept)
```

- `Denied` is **remembered** across restarts. It is lifted only by `UpdateConsent(TrackingConsent.Granted)` or by an explicit `TrackingConsent = TrackingConsent.Granted` in the config. Leaving `JoryioConfig.TrackingConsent` unset (the default) keeps a stored denial; with nothing stored the SDK tracks normally, so a game without a consent screen needs no setting.
- `Pending` applies to the current launch only and never overrides a stored `Denied`. While it holds, nothing is sent - no session start, no in-app sync, no push registration - and events wait on the device (up to `MaxQueueSize`); `Granted` releases them, `Denied` discards them.
- An opt-out survives restarts; to start a session already opted out, set `JoryioConfig.OptOut`.
- `IsUserOptedOut()` reflects the opt-out switch only; `GetTrackingConsent()` returns the consent switch (`Granted`, `Denied` or `Pending`). Both answer before `Initialize` from what is stored on the device: an opt-out or a `Denied` remembered from an earlier launch is reported as such, not as a default, and an `OptOut()` / `OptIn()` made earlier in the same launch wins over the stored opt-out. `GetTrackingConsent()` returns `Granted` only when nothing has been decided, which is also how the SDK behaves then.
- An `UpdateConsent` value outside the enum (a bad cast) is ignored with a warning; the same value in the config is treated as not set.

## SDK authentication

For workspaces with SDK authentication turned on, every request carries a token your server signs:

```csharp
JoryioSDK.Initialize(sdkKey, apiHost, new JoryioConfig { EnableSdkAuthentication = true });
JoryioSDK.SetSdkAuthenticationToken(tokenFromYourServer);   // after login

JoryioSDK.SetSdkAuthenticationErrorHandler(error => {
    // error.reason: missing, invalid, expired, sub_mismatch or unknown
    // error.refreshExhausted: the SDK has stopped retrying this request
    StartCoroutine(FetchFreshToken(token => JoryioSDK.SetSdkAuthenticationToken(token)));
});
```

`SetSdkAuthenticationToken` and `SetSdkAuthenticationErrorHandler` work before `Initialize` too. The native SDK starts with that token, ahead of its first request, and keeps it through a `WipeData()` made before `Initialize`, so an unsubscribe held for the player being erased still carries their token. You can also pass the first token as `JoryioConfig.SdkAuthenticationToken`.

The handler runs on the main thread. It is called for an authentication `401` on the current player's requests, including before any token was set (reason `missing`) - never for the leftovers of a player who has logged out, so it never makes you sign out the player who is playing now. The current player's refused events and writes are kept and retried once you set a fresh token. Events go out in batches of one player at a time, and events and writes of a player who has logged out are dropped on any authentication `401` (`missing`, `invalid`, `expired` or `sub_mismatch`), so they never hold back the next player's. `Reset()` takes the token out of the session; it stays with the outgoing player's leftovers, and the next player's requests never carry it - set their token with `SetSdkAuthenticationToken` once they sign in. Events and writes the outgoing player had already queued still go out with that player's token, so a normal logout loses nothing.

## Utilities

```csharp
JoryioSDK.Flush();
JoryioSDK.GetUserId();
JoryioSDK.GetAnonymousId();
var (userId, anonymousId) = JoryioSDK.GetIdentity();          // userId is null when anonymous
var (sessionId, sessionStart, duration) = JoryioSDK.GetSessionInfo();   // duration in seconds
JoryioSDK.GetSessionId();
```

## Parity with the native SDKs

The C# API uses the native SDKs' names, one for one. What is deliberately different:

| Native API | Unity | Why |
|------------|-------|-----|
| `alias(userId)` | not available | Removed from every Joryio SDK in 1.3.0; use `AddAlias(label, name)` |
| `setInAppMessageCallback` / `clearInAppMessageCallback` | `SetInAppMessageHandler` / `ClearInAppMessageHandler` | The handler receives a `JoryioInAppMessage` whose `Mark...` methods do the reporting |
| `getQueueSize()` | not exposed | A diagnostic no game needs |
| `updateBadgeCount` / `clearBadge` | iOS only | Android has no app badge API; launchers badge from notifications |
| `ecommerce(config)` with a custom `EcommerceConfig` | `JoryioSDK.Ecommerce` uses the default config | Matches React Native; `Purchase` takes the order's own `Currency` |
| Extra `properties` on some e-commerce calls | not exposed | Matches React Native; use `Track` for custom events |
| `inAppPresenter`, `InAppConfig` display options | not exposed | They style the native UI; a Unity game draws its own or uses the native defaults |
| Callbacks / `async` | `Action<T>` callbacks | Async on iOS, synchronous on Android; one callback shape on both, always on the main thread |

## Documentation

- [Unity SDK guide](https://docs.joryio.com/developers/sdk/unity-sdk)
- [Tracking events](https://docs.joryio.com/developers/sdk/tracking-events)
- [Changelog](./CHANGELOG.md)

## Support

- Issues: https://github.com/joryio/joryio-unity-sdk/issues
- Email: support@joryio.com

## License

Commercial - see [LICENSE](./LICENSE). Use of the SDK requires an active Joryio subscription or agreement.
