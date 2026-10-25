#if UNITY_IOS
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace Joryio.Editor
{
    /// <summary>
    /// Configure the exported Xcode project.
    ///
    /// Unity copies plugin files into the Xcode project but does not configure
    /// the project for them, and our bridge needs things Unity does not set by
    /// default. Each one fails in a way that reads as somebody else's problem:
    ///
    ///  - SWIFT_VERSION unset: "Swift is not supported" or a compile failure on
    ///    a file the developer did not write.
    ///  - ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES off: builds fine, then crashes
    ///    on launch on a device with no Swift runtime, with a dyld error that
    ///    names a library nobody added.
    ///  - Push capability absent: the app runs, registers for notifications,
    ///    and simply never receives one. No error anywhere.
    ///  - UNITY_USES_REMOTE_NOTIFICATIONS 0: Unity's app controller compiles
    ///    out application:didRegisterForRemoteNotificationsWithDeviceToken:,
    ///    so the APNs token is never seen by anyone and the device is never
    ///    registered for push - again with no error.
    ///
    /// The bridge is Swift and not Objective-C because the iOS SDK exposes no
    /// @objc API at all, so this is not optional configuration for a nicety - it
    /// is what makes the plugin build.
    /// </summary>
    public sealed class JoryioPostProcessBuild : IPostprocessBuildWithReport
    {
        public int callbackOrder => 100;

        private const string DefaultEntitlements = "Joryio.entitlements";

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;
            ConfigureXcode(report.summary.outputPath);
        }

        private static void ConfigureXcode(string buildPath)
        {
            var pbxPath = PBXProject.GetPBXProjectPath(buildPath);

            // An Append build keeps the previous export's package reference;
            // pin it to this package's version before PBXProject loads the
            // file, since PBXProject cannot change a reference it did not add.
            var pbxText = File.ReadAllText(pbxPath);
            var pinnedText = JoryioXcodePackage.PinExactVersion(pbxText, IosSdkPackageUrl, IosSdkVersion);
            if (pinnedText != pbxText)
            {
                File.WriteAllText(pbxPath, pinnedText);
                Debug.Log("[Joryio] Pinned the existing " + IosSdkPackageUrl + " reference to " + IosSdkVersion + ".");
            }

            var project = new PBXProject();
            project.ReadFromFile(pbxPath);

            var frameworkTarget = project.GetUnityFrameworkTargetGuid();
            var mainTarget = project.GetUnityMainTargetGuid();

            // The bridge lives in UnityFramework, which is where Unity puts
            // plugin sources - setting this on the main target only compiles
            // nothing and is a common way to lose an afternoon.
            project.SetBuildProperty(frameworkTarget, "SWIFT_VERSION", "5.9");
            project.SetBuildProperty(mainTarget, "SWIFT_VERSION", "5.9");
            project.SetBuildProperty(mainTarget, "ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES", "YES");

            // The native SDK, as a Swift package linked into UnityFramework -
            // the target the bridge compiles in, so `import Joryio` resolves.
            AddIosSdkPackage(project, frameworkTarget, pinnedText);

            // Read BEFORE writing: the capability manager below sets this
            // property to whatever file it is given.
            var existingEntitlements = project.GetBuildPropertyForAnyConfig(mainTarget, "CODE_SIGN_ENTITLEMENTS");

            project.WriteToFile(pbxPath);

            EnableRemoteNotificationCallbacks(buildPath);
            AddPushCapability(buildPath, pbxPath, existingEntitlements);
            Debug.Log("[Joryio] Configured Xcode project for the Swift bridge and push notifications.");
        }

        /// <summary>The native iOS SDK's Swift package (the public mirror of packages/sdk-ios).</summary>
        internal const string IosSdkPackageUrl = "https://github.com/joryio/joryio-ios-sdk.git";

        /// <summary>
        /// The native iOS SDK version this package is built against. Pinned
        /// EXACTLY, like the Android artifact in JoryioDependencies.xml: the SDKs
        /// release in lockstep and the bridge calls API of that exact release.
        /// sdk-version-parity.spec.ts pins it with every other SDK version, and
        /// the release tool (sdk_release.py set-version) moves it.
        /// </summary>
        internal const string IosSdkVersion = "1.3.1-beta.2";

        /// <summary>
        /// The package products to link. `Joryio` is tracking, identity and push;
        /// `JoryioUI` adds the SDK's own in-app message views and WebKit, so it is
        /// linked only when the game asks for it with the scripting define symbol
        /// JORYIO_IOS_UI (Player Settings, iOS). A game that renders in-engine
        /// through SetInAppMessageHandler leaves it out.
        /// </summary>
        internal static string[] IosSdkProducts(bool withUI) =>
            withUI ? new[] { "Joryio", "JoryioUI" } : new[] { "Joryio" };

        private static bool LinkIosUI =>
#if JORYIO_IOS_UI
            true;
#else
            false;
#endif

        /// <summary>
        /// Add the native SDK as a remote Swift package and link its products into
        /// UnityFramework. Xcode resolves the package when the project is opened
        /// or built - no CocoaPods and no External Dependency Manager on iOS.
        ///
        /// This replaced the `Joryio/Core` pod in JoryioDependencies.xml because
        /// CocoaPods trunk goes read-only on 2026-12-02: no SDK release after that
        /// can reach a game through a pod.
        ///
        /// An "Append" build re-runs this on a project that already has the
        /// package. Adding it again would leave two package references, so the
        /// existing reference is reused (ConfigureXcode has already pinned it to
        /// IosSdkVersion) and only the products it does not link yet are added:
        /// setting JORYIO_IOS_UI after the first export used to leave JoryioUI
        /// out of an Append build without a word. Unlinking is not possible
        /// through PBXProject, so a JoryioUI left over after the define was
        /// removed is reported, not removed.
        /// </summary>
        internal static bool AddIosSdkPackage(PBXProject project, string targetGuid, string existingProjectText)
        {
            var packageGuid = JoryioXcodePackage.FindPackageGuid(existingProjectText, IosSdkPackageUrl);
            var added = packageGuid == null;
            var linked = JoryioXcodePackage.LinkedProducts(existingProjectText, packageGuid);
            if (added)
            {
                packageGuid = project.AddRemotePackageReferenceAtVersion(IosSdkPackageUrl, IosSdkVersion);
            }
            else
            {
                Debug.Log("[Joryio] The Xcode project already references " + IosSdkPackageUrl + "; reusing it.");
            }
            foreach (var product in IosSdkProducts(LinkIosUI))
            {
                if (linked.Contains(product)) continue;
                project.AddRemotePackageFrameworkToProject(targetGuid, product, packageGuid, false);
            }
            if (!LinkIosUI && linked.Contains("JoryioUI"))
            {
                Debug.LogWarning(
                    "[Joryio] JoryioUI is still linked from an earlier export although JORYIO_IOS_UI is not set. " +
                    "Make a Replace build (not Append) to remove it.");
            }
            return added;
        }

        /// <summary>
        /// Flip Unity's UNITY_USES_REMOTE_NOTIFICATIONS to 1.
        ///
        /// Unity sets it from its own scan of the game's scripts, and that scan
        /// cannot see a native plugin, so it writes 0 - which compiles the
        /// app-delegate token callbacks out of UnityAppController.mm entirely.
        /// With it at 1 the controller re-posts the token as
        /// kUnityDidRegisterForRemoteNotificationsWithDeviceToken, which the
        /// Swift bridge observes and forwards to the native SDK. Unity rewrites
        /// the header on every build, including an append, and this runs after,
        /// so it holds. Unity's own Mobile Notifications package does the same.
        /// </summary>
        internal static bool EnableRemoteNotificationCallbacks(string buildPath)
        {
            var header = Path.Combine(buildPath, "Classes", "Preprocessor.h");
            if (!File.Exists(header))
            {
                Debug.LogWarning(
                    "[Joryio] Classes/Preprocessor.h not found, so UNITY_USES_REMOTE_NOTIFICATIONS was not set. " +
                    "Without it the APNs device token never reaches the SDK - set it to 1 in that file.");
                return false;
            }
            var text = File.ReadAllText(header);
            var updated = Regex.Replace(
                text,
                @"#define\s+UNITY_USES_REMOTE_NOTIFICATIONS\s+0",
                "#define UNITY_USES_REMOTE_NOTIFICATIONS 1");
            if (updated != text) File.WriteAllText(header, updated);
            return true;
        }

        /// <summary>
        /// The entitlements file to write into, relative to the build folder:
        /// the target's EXISTING one when it has one, so another plugin's
        /// entitlements (Sign in with Apple, associated domains, iCloud, ...)
        /// survive. This used to write a fresh Joryio.entitlements and point the
        /// target at it, which silently dropped every other plugin's
        /// capabilities from the signed app.
        /// </summary>
        internal static string EntitlementsPath(string buildPath, string existing)
        {
            if (string.IsNullOrEmpty(existing)) return DefaultEntitlements;
            var relative = existing.Replace("$(SRCROOT)/", "").Replace("$(PROJECT_DIR)/", "").Trim('"');
            // A path still holding a build variable cannot be resolved here;
            // fall back rather than write to a literal "$(...)" folder.
            if (relative.Contains("$(")) return DefaultEntitlements;
            if (Path.IsPathRooted(relative))
            {
                var root = Path.GetFullPath(buildPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!relative.StartsWith(root)) return DefaultEntitlements;
                relative = relative.Substring(root.Length);
            }
            return relative;
        }

        private static void AddPushCapability(string buildPath, string pbxPath, string existingEntitlements)
        {
            var project = new PBXProject();
            project.ReadFromFile(pbxPath);

            // ProjectCapabilityManager READS an existing file at this path and
            // adds to it (verified in UnityEditor.iOS.Extensions.Xcode:
            // GetOrCreateEntitlementDoc), so reusing the path keeps its keys.
            var entitlementsPath = EntitlementsPath(buildPath, existingEntitlements);
            var manager = new ProjectCapabilityManager(
                pbxPath,
                entitlementsPath,
                targetGuid: project.GetUnityMainTargetGuid());

            manager.AddPushNotifications(development: EditorUserBuildSettings.development);
            manager.AddBackgroundModes(BackgroundModesOptions.RemoteNotifications);
            manager.WriteToFile();

            // Sanity-check rather than trust: a capability that silently failed
            // to write is indistinguishable at runtime from a device that simply
            // received no notifications.
            if (!File.Exists(Path.Combine(buildPath, entitlementsPath)))
            {
                Debug.LogWarning(
                    "[Joryio] Push entitlements were not written. Push registration will succeed and no " +
                    "notification will ever arrive - add the Push Notifications capability in Xcode manually.");
            }
        }
    }
}
#endif
