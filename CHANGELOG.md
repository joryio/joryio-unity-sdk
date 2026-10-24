# Changelog

## 1.3.1 - 2026-10-24

Changed: on iOS the native SDK is now a Swift package, not a CocoaPods pod. The iOS post-build step adds `https://github.com/joryio/joryio-ios-sdk.git` at exactly this version to the exported Xcode project and links the `Joryio` product into `UnityFramework` (and `JoryioUI` as well when the `JORYIO_IOS_UI` scripting define is set). `Editor/JoryioDependencies.xml` now lists only the Android artifact; External Dependency Manager is no longer involved on iOS. Upgrading from 1.3.0, which used the `Joryio/Core` pod: build the iOS player once with **Replace** rather than **Append**, and delete any `<iosPod name="Joryio/...">` line from your own External Dependency Manager files.

Later **Append** builds keep the package in step: the Swift package reference is moved to the version of the installed Unity package, and `JoryioUI` is linked when `JORYIO_IOS_UI` is set after the first export. Removing the define needs a Replace build to unlink `JoryioUI`; the build log says so.

No C# API changes. Android is unchanged.

## 1.3.0 - 2026-10-06

First public release.
