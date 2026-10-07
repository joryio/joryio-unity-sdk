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

            // Swift needs to find the Joryio module, which CocoaPods puts here.
            project.AddBuildProperty(frameworkTarget, "SWIFT_INCLUDE_PATHS", "$(PODS_CONFIGURATION_BUILD_DIR)/Joryio");

            // Read BEFORE writing: the capability manager below sets this
            // property to whatever file it is given.
            var existingEntitlements = project.GetBuildPropertyForAnyConfig(mainTarget, "CODE_SIGN_ENTITLEMENTS");

            project.WriteToFile(pbxPath);

            EnableRemoteNotificationCallbacks(buildPath);
            AddPushCapability(buildPath, pbxPath, existingEntitlements);
            Debug.Log("[Joryio] Configured Xcode project for the Swift bridge and push notifications.");
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
