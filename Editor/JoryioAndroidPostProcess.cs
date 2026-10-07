using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace Joryio.Editor
{
    /// <summary>
    /// Keep the Java bridge through R8.
    ///
    /// C# reaches io.joryio.unity.JoryioUnityBridge BY NAME, through JNI
    /// (AndroidJavaClass + CallStatic). Nothing in the Java/Kotlin code calls
    /// it, so a release build with minification on - the default "Minify:
    /// Release" setting - renames or deletes every method, and the first call
    /// from C# dies with a ClassNotFoundException or NoSuchMethodError. Debug
    /// builds are not minified, so it works throughout QA and fails in the
    /// build players install.
    ///
    /// WHY A POST-PROCESSOR AND NOT A proguard-user.txt. Unity reads
    /// proguard-user.txt only from Assets/Plugins/Android, only when "Custom
    /// Proguard File" is ticked, and never from a package. What it does do, in
    /// every version from 2021.3 to 6000.x, is generate unityLibrary with
    /// `consumerProguardFiles 'proguard-unity.txt'` - so rules appended to that
    /// file apply to the whole app, from a package, with no setting to tick.
    /// Checked against the 6000.4 templates (mainTemplate.gradle line 32).
    /// </summary>
    public sealed class JoryioAndroidPostProcess : IPostGenerateGradleAndroidProject
    {
        // After Unity's own generation, before EDM4U-style processors that
        // might append to the same file; order does not matter for the rules.
        public int callbackOrder => 100;

        internal const string Marker = "# Joryio Unity SDK - keep rules";

        internal const string Rules =
            Marker + "\n" +
            // The bridge: every public static is an entry point C# calls by name.
            "-keep class io.joryio.unity.** { *; }\n";
        // The native SDK's own reflective needs (Gson wire models, the
        // Retrofit interface, TypeToken signatures) ship INSIDE its AAR as
        // consumer rules since 1.3.0 (sdk-android joryio/consumer-rules.pro),
        // and the Android Gradle plugin applies them automatically - so a
        // blanket keep of io.joryio.sdk.** here would only stop R8 shrinking
        // the SDK in every game.

        /// <param name="path">The generated unityLibrary module.</param>
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            AppendRules(path);
        }

        /// <summary>Idempotent: an incremental export reuses the file.</summary>
        internal static bool AppendRules(string unityLibraryPath)
        {
            var file = Path.Combine(unityLibraryPath, "proguard-unity.txt");
            if (!File.Exists(file))
            {
                Debug.LogWarning(
                    "[Joryio] No proguard-unity.txt in the generated Gradle project, so the SDK's keep rules were " +
                    "not added. If this build is minified, add to your own ProGuard file:\n" + Rules);
                return false;
            }
            var existing = File.ReadAllText(file);
            if (existing.Contains(Marker)) return true;
            File.AppendAllText(file, (existing.EndsWith("\n") ? "" : "\n") + Rules);
            return true;
        }
    }
}
