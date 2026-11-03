# Changelog

## 1.3.3 - 2026-11-03

Fixed (from the iOS and Android SDKs this package uses): an in-app message the native SDK draws counts as displayed only once someone could have seen it. It is shown only on a screen that is in the foreground and not mid-transition or closing, and `in_app.displayed` is reported - and the campaign's frequency cap spent - only after it has been on screen for `JoryioSDK.IN_APP_DISPLAY_DWELL_MS` (1000 ms) with the app in the foreground. A tap or a close before then counts as seen (displayed first, then the click or dismissal); a message that leaves the screen sooner is not counted and stays eligible.

Added: `JoryioSDK.IN_APP_DISPLAY_DWELL_MS`. If the game draws in-app messages itself (`SetInAppMessageHandler`), call `MarkDisplayed()` by the same rule: after the dwell, or right before `MarkClicked` / `MarkDismissed` if the player acts sooner - not as the message is first drawn. No bridge or wire changes.

## 1.3.2 - 2026-10-25

Added: `JoryioConfig.HoldInAppMessagesForHandler` (off by default; wire key `holdInAppMessagesForHandler`). `SetInAppMessageHandler` can only be called after `Initialize`, and the first sync could finish before it, so the native SDK drew that message itself when its UI was in the build. With the option on, a message that is ready before the handler is set is held - not drawn, not reported, nothing spent - and handed to the handler when it is set; `ClearInAppMessageHandler()` instead ends the hold and the native SDK draws the held message. The same option, by the same name, on iOS, Android and React Native.

## 1.3.1 - 2026-10-24

Changed: on iOS the native SDK is now a Swift package, not a CocoaPods pod. The iOS post-build step adds `https://github.com/joryio/joryio-ios-sdk.git` at exactly this version to the exported Xcode project and links the `Joryio` product into `UnityFramework` (and `JoryioUI` as well when the `JORYIO_IOS_UI` scripting define is set). `Editor/JoryioDependencies.xml` now lists only the Android artifact; External Dependency Manager is no longer involved on iOS. Upgrading from 1.3.0, which used the `Joryio/Core` pod: build the iOS player once with **Replace** rather than **Append**, and delete any `<iosPod name="Joryio/...">` line from your own External Dependency Manager files.

Later **Append** builds keep the package in step: the Swift package reference is moved to the version of the installed Unity package, and `JoryioUI` is linked when `JORYIO_IOS_UI` is set after the first export. Removing the define needs a Replace build to unlink `JoryioUI`; the build log says so.

Added: `JoryioInAppMessage.MaxWidth` for HTML modals and slide-ups (null for every other message), forwarded by both native bridges. When you draw messages yourself, give your web view min(MaxWidth, screen width - your margins) before the content lays out and measure the height from `document.body` (with `body{display:flow-root}`), not the root element, whose height never drops below the web view's own.

Fixed (when the native SDK draws messages: `joryio-android-ui` on Android, the `JORYIO_IOS_UI` define on iOS): HTML in-app messages did not work on Android since 1.3.0, and native HTML modals and slide-ups could grow but never shrink. See the iOS and Android release notes.

No other C# API changes.

## 1.3.0 - 2026-10-06

First public release.
