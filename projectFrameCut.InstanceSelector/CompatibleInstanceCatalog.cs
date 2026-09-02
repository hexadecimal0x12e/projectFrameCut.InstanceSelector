using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace projectFrameCut.InstanceSelector
{
    internal static class CompatibleInstanceCatalog
    {
        private const string CompatibleAliasPrefix = "projectFrameCutCompatible_";
        private const string ExecutableExtension = ".exe";

        public static IReadOnlyList<CompatibleInstanceAlias> DiscoverAliases()
        {
            string windowsAppsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps");

            if (!Directory.Exists(windowsAppsPath))
            {
                return [];
            }

            List<CompatibleInstanceAlias> aliases = [];
            foreach (string aliasPath in Directory.EnumerateFiles(
                         windowsAppsPath,
                         $"{CompatibleAliasPrefix}*{ExecutableExtension}",
                         SearchOption.TopDirectoryOnly))
            {
                if (TryParseAlias(
                        Path.GetFileName(aliasPath),
                        out string packageIdentifier,
                        out Version version))
                {
                    aliases.Add(new CompatibleInstanceAlias(
                        packageIdentifier,
                        version,
                        aliasPath));
                }
            }

            return aliases
                .OrderBy(alias => alias.PackageIdentifier, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(alias => alias.Version)
                .ToArray();
        }

        internal static bool TryParseAlias(
            string fileName,
            out string packageIdentifier,
            out Version version)
        {
            packageIdentifier = string.Empty;
            version = new Version();

            if (!fileName.StartsWith(CompatibleAliasPrefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(ExecutableExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string aliasIdentity = fileName[
                CompatibleAliasPrefix.Length..
                ^ExecutableExtension.Length];
            int versionSeparatorIndex = aliasIdentity.LastIndexOf('_');
            if (versionSeparatorIndex <= 0 || versionSeparatorIndex == aliasIdentity.Length - 1)
            {
                return false;
            }

            string parsedPackageIdentifier = aliasIdentity[..versionSeparatorIndex];
            string parsedVersionText = aliasIdentity[(versionSeparatorIndex + 1)..];
            if (!Version.TryParse(parsedVersionText, out Version? parsedVersion) ||
                parsedVersion.Build < 0 ||
                parsedVersion.Revision < 0)
            {
                return false;
            }

            packageIdentifier = parsedPackageIdentifier;
            version = parsedVersion;
            return true;
        }
    }

    internal sealed record CompatibleInstanceAlias(
        string PackageIdentifier,
        Version Version,
        string ExecutablePath)
    {
        public string VersionText =>
            $"{Version.Major}.{Version.Minor}.{Version.Build}.{Version.Revision}";
    }
}
