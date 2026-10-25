using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Joryio.Editor
{
    /// <summary>
    /// Reads and pins the iOS SDK's Swift package reference in an exported
    /// project.pbxproj - the part of the iOS post-build step an "Append" build
    /// depends on.
    ///
    /// Unity's PBXProject can ADD a remote package but has no API to find or
    /// change one already in the project, and an Append build keeps the Xcode
    /// project from the previous export. Without this, an Append build:
    ///
    ///  - kept the package at the version the FIRST export pinned. The bridge
    ///    source Unity copies in is always the current package's, so after an
    ///    upgrade it compiled against an older native SDK (or failed to);
    ///  - never linked JoryioUI when JORYIO_IOS_UI was set after the first
    ///    export, so native in-app rendering was silently missing.
    ///
    /// Plain string work with no UnityEditor dependency, so the managed tests in
    /// Tools~/Tests run it under Mono without an Editor.
    /// </summary>
    internal static class JoryioXcodePackage
    {
        // One XCRemoteSwiftPackageReference object. Its only nested dictionary
        // is `requirement = { ... };`, so one level of braces is enough.
        private static readonly Regex PackageReference = new Regex(
            @"(?<guid>[0-9A-Fa-f]{24})[^=\n]*=\s*\{\s*isa\s*=\s*XCRemoteSwiftPackageReference;(?<body>(?:[^{}]|\{[^{}]*\})*?)\};",
            RegexOptions.Compiled);

        private static readonly Regex Requirement = new Regex(
            @"requirement\s*=\s*\{[^{}]*\};",
            RegexOptions.Compiled);

        /// <summary>The object id of the package reference for <paramref name="url"/>, or null.</summary>
        internal static string FindPackageGuid(string pbx, string url)
        {
            if (string.IsNullOrEmpty(pbx)) return null;
            foreach (Match m in PackageReference.Matches(pbx))
            {
                if (NamesUrl(m.Groups["body"].Value, url)) return m.Groups["guid"].Value;
            }
            return null;
        }

        /// <summary>
        /// The project text with the package reference for <paramref name="url"/>
        /// required at exactly <paramref name="version"/>. Unchanged when there is
        /// no such reference or it already says so.
        /// </summary>
        internal static string PinExactVersion(string pbx, string url, string version)
        {
            if (string.IsNullOrEmpty(pbx)) return pbx;
            var pinned = "requirement = {\n\t\t\t\tkind = exactVersion;\n\t\t\t\tversion = \"" + version + "\";\n\t\t\t};";
            return PackageReference.Replace(pbx, m =>
            {
                var body = m.Groups["body"].Value;
                if (!NamesUrl(body, url)) return m.Value;
                var current = Requirement.Match(body);
                if (current.Success && IsExactly(current.Value, version)) return m.Value;
                var newBody = current.Success
                    ? body.Substring(0, current.Index) + pinned + body.Substring(current.Index + current.Length)
                    : body.TrimEnd() + "\n\t\t\t" + pinned + "\n\t\t";
                return m.Value.Substring(0, m.Groups["body"].Index - m.Index) + newBody + "};";
            });
        }

        /// <summary>The product names already taken from the package with id <paramref name="packageGuid"/>.</summary>
        internal static HashSet<string> LinkedProducts(string pbx, string packageGuid)
        {
            var names = new HashSet<string>();
            if (string.IsNullOrEmpty(pbx) || string.IsNullOrEmpty(packageGuid)) return names;
            var dependency = new Regex(
                @"isa\s*=\s*XCSwiftPackageProductDependency;\s*package\s*=\s*" + Regex.Escape(packageGuid) +
                @"[^;]*;\s*productName\s*=\s*""?(?<name>[^;""]+)""?;");
            foreach (Match m in dependency.Matches(pbx)) names.Add(m.Groups["name"].Value.Trim());
            return names;
        }

        private static bool NamesUrl(string body, string url) =>
            Regex.IsMatch(body, @"repositoryURL\s*=\s*""?" + Regex.Escape(url) + @"""?;");

        private static bool IsExactly(string requirement, string version) =>
            Regex.IsMatch(requirement, @"kind\s*=\s*exactVersion;") &&
            Regex.IsMatch(requirement, @"version\s*=\s*""?" + Regex.Escape(version) + @"""?;");
    }
}
