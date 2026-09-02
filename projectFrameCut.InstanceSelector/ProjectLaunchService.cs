using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;

namespace projectFrameCut.InstanceSelector
{
    internal sealed class ProjectLaunchService
    {
        public async Task<ProjectFileInspection> InspectProjectAsync(string projectPath)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(projectPath);
            }
            catch (Exception ex)
            {
                return ProjectFileInspection.Invalid(projectPath, $"项目路径无效：{ex.Message}");
            }

            if (Directory.Exists(fullPath))
            {
                fullPath = Path.Combine(fullPath, "project.pjfc");
                if (!File.Exists(fullPath))
                {
                    return ProjectFileInspection.Invalid(
                        fullPath,
                        "项目目录内找不到 project.pjfc 文件。");
                }
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".pjfc", StringComparison.OrdinalIgnoreCase))
            {
                return ProjectFileInspection.Invalid(
                    fullPath,
                    "只能打开 projectFrameCut 项目目录或 project.pjfc 文件。");
            }

            if (!File.Exists(fullPath))
            {
                return ProjectFileInspection.Invalid(fullPath, "找不到指定的 project.pjfc 文件。");
            }

            try
            {
                await using FileStream stream = File.OpenRead(fullPath);
                using JsonDocument document = await JsonDocument.ParseAsync(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return ProjectFileInspection.Invalid(fullPath, "project.pjfc 的根节点必须是 JSON 对象。");
                }

                JsonElement? identifierElement = null;
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(
                        property.Name,
                        "LastOpenAppIdentifier",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        identifierElement = property.Value;
                        break;
                    }
                }

                if (!identifierElement.HasValue)
                {
                    return ProjectFileInspection.Valid(fullPath, null);
                }

                if (identifierElement.Value.ValueKind != JsonValueKind.String)
                {
                    return ProjectFileInspection.Invalid(fullPath, "LastOpenAppIdentifier 必须是字符串。");
                }

                string? identifier = identifierElement.Value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(identifier) ||
                    string.Equals(identifier, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    identifier = null;
                }

                return ProjectFileInspection.Valid(fullPath, identifier);
            }
            catch (JsonException ex)
            {
                return ProjectFileInspection.Invalid(fullPath, $"project.pjfc 不是有效的 JSON：{ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ProjectFileInspection.Invalid(fullPath, $"无法读取 project.pjfc：{ex.Message}");
            }
        }

        public async Task<IReadOnlyList<ProjectFrameCutInstance>> DiscoverInstancesAsync()
        {
            IReadOnlyList<CompatibleInstanceAlias> compatibleAliases =
                CompatibleInstanceCatalog.DiscoverAliases();
            if (compatibleAliases.Count == 0)
            {
                return [];
            }

            PackageManager packageManager = new();
            List<ProjectFrameCutInstance> instances = [];

            foreach (Package package in packageManager.FindPackagesForUser(string.Empty))
            {
                if (package.IsFramework || package.IsResourcePackage)
                {
                    continue;
                }

                PackageVersion version = package.Id.Version;
                string versionText = FormatPackageVersion(version);
                CompatibleInstanceAlias? compatibleAlias = compatibleAliases.FirstOrDefault(alias =>
                    string.Equals(
                        alias.PackageIdentifier,
                        package.Id.Name,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        alias.VersionText,
                        versionText,
                        StringComparison.OrdinalIgnoreCase));
                if (compatibleAlias is null)
                {
                    continue;
                }

                IReadOnlyList<AppListEntry> appEntries;
                try
                {
                    appEntries = await package.GetAppListEntriesAsync();
                }
                catch
                {
                    continue;
                }

                AppListEntry? mainApp = appEntries.FirstOrDefault(entry =>
                    entry.AppUserModelId.EndsWith("!App", StringComparison.OrdinalIgnoreCase));
                if (mainApp is null)
                {
                    continue;
                }

                string displayName;
                try
                {
                    displayName = mainApp.DisplayInfo.DisplayName;
                }
                catch
                {
                    displayName = "projectFrameCut";
                }

                instances.Add(new ProjectFrameCutInstance(
                    string.IsNullOrWhiteSpace(displayName) ? "projectFrameCut" : displayName,
                    versionText,
                    package.Id.Name,
                    mainApp.AppUserModelId,
                    compatibleAlias.ExecutablePath));
            }

            return instances
                .GroupBy(instance => instance.PackageIdentifier, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(instance => Version.Parse(instance.Version))
                    .First())
                .OrderBy(instance => instance.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(instance => instance.PackageIdentifier, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string FormatPackageVersion(PackageVersion version) =>
            $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";

        public void Launch(ProjectFrameCutInstance instance, string projectPath)
        {
            if (!File.Exists(projectPath))
            {
                throw new FileNotFoundException("项目文件不存在。", projectPath);
            }

            LaunchWithArguments(instance, $"\"{projectPath}\"");
        }

        public void LaunchProtocol(ProjectFrameCutInstance instance, string protocolUri)
        {
            if (string.IsNullOrWhiteSpace(protocolUri))
            {
                throw new ArgumentException("协议 URI 不能为空。", nameof(protocolUri));
            }

            LaunchWithArguments(instance, protocolUri);
        }

        private static void LaunchWithArguments(ProjectFrameCutInstance instance, string arguments)
        {
            IApplicationActivationManager activationManager =
                (IApplicationActivationManager)new ApplicationActivationManager();
            int result = activationManager.ActivateApplication(
                instance.AppUserModelId,
                arguments,
                ActivateOptions.None,
                out _);
            Marshal.ThrowExceptionForHR(result);
        }
    }

    internal sealed record ProjectFileInspection(
        bool IsValid,
        string ProjectPath,
        string? AppIdentifier,
        string Message)
    {
        public static ProjectFileInspection Valid(string projectPath, string? appIdentifier) =>
            new(true, projectPath, appIdentifier, string.Empty);

        public static ProjectFileInspection Invalid(string projectPath, string message) =>
            new(false, projectPath, null, message);
    }

    internal sealed record ProjectFrameCutInstance(
        string DisplayName,
        string Version,
        string PackageIdentifier,
        string AppUserModelId,
        string CliExecutablePath)
    {
        public string Details => UiStrings.Format("Ui_InstanceDetails", Version, PackageIdentifier);
    }

    [Flags]
    internal enum ActivateOptions
    {
        None = 0
    }

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    internal class ApplicationActivationManager
    {
    }
}
