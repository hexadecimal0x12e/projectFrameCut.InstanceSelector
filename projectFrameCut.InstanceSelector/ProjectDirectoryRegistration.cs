using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace projectFrameCut.InstanceSelector
{
    internal static class ProjectDirectoryRegistration
    {
        // Project folders opt in with:
        // [.ShellClassInfo]
        // DirectoryClass=projectFrameCut.ProjectDirectory
        public const string DirectoryClass = "projectFrameCut.ProjectDirectory";

        private const string RegistryPath = @"Software\Classes\" + DirectoryClass;
        private const string SelectorAlias = "projectFrameCut.InstanceSelector.exe";
        private const string PackagedIconRelativePath = @"Assets\appicon.ico";
        private const string IconFileName = "appicon.ico";
        private const string DisabledMarkerFileName = "project-directory-class.disabled";
        private const string ExploreCommand = "\"%SystemRoot%\\explorer.exe\" \"%1\"";
        private const int RegistrationVersion = 2;

        public static void EnsureRegisteredAtStartup()
        {
            if (File.Exists(GetDisabledMarkerPath()))
            {
                return;
            }

            EnsureRegisteredCore();
        }

        public static void Install()
        {
            EnsureRegisteredCore();

            string disabledMarkerPath = GetDisabledMarkerPath();
            if (File.Exists(disabledMarkerPath))
            {
                File.Delete(disabledMarkerPath);
            }
        }

        public static void Uninstall()
        {
            string disabledMarkerPath = GetDisabledMarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(disabledMarkerPath)!);
            File.WriteAllText(disabledMarkerPath, string.Empty);

            Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false);
            SHChangeNotify(ShellChangeNotifyEventId.AssociationChanged, 0, 0, 0);
        }

        public static bool IsInstalled()
        {
            using RegistryKey? directoryClassKey =
                Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            return directoryClassKey is not null;
        }

        private static void EnsureRegisteredCore()
        {
            string command = $"\"{GetSelectorExecutablePath()}\" \"%1\"";
            string defaultIcon = $"\"{EnsureIconExtracted(out bool iconChanged)}\",0";
            if (IsRegistrationCurrent(command, defaultIcon))
            {
                if (iconChanged)
                {
                    SHChangeNotify(ShellChangeNotifyEventId.AssociationChanged, 0, 0, 0);
                }

                return;
            }

            using RegistryKey directoryClassKey =
                Registry.CurrentUser.CreateSubKey(RegistryPath, true) ??
                throw new InvalidOperationException("无法创建项目目录的注册表项。");
            directoryClassKey.SetValue(null, "projectFrameCut Project Directory", RegistryValueKind.String);
            directoryClassKey.SetValue("CanUseForDirectory", string.Empty, RegistryValueKind.String);

            using RegistryKey defaultIconKey = directoryClassKey.CreateSubKey("DefaultIcon", true) ??
                throw new InvalidOperationException("无法创建项目目录的 DefaultIcon 注册表项。");
            defaultIconKey.SetValue(null, defaultIcon, RegistryValueKind.String);

            using RegistryKey shellKey = directoryClassKey.CreateSubKey("shell", true) ??
                throw new InvalidOperationException("无法创建项目目录的 shell 注册表项。");
            shellKey.SetValue(null, "open", RegistryValueKind.String);

            using RegistryKey openKey = shellKey.CreateSubKey("open", true) ??
                throw new InvalidOperationException("无法创建项目目录的 open 注册表项。");
            openKey.SetValue(null, "使用 projectFrameCut 打开", RegistryValueKind.String);

            using RegistryKey commandKey = openKey.CreateSubKey("command", true) ??
                throw new InvalidOperationException("无法创建项目目录的 command 注册表项。");
            commandKey.SetValue(null, command, RegistryValueKind.String);

            using RegistryKey exploreKey = shellKey.CreateSubKey("explore", true) ??
                throw new InvalidOperationException("无法创建项目目录的 explore 注册表项。");
            exploreKey.SetValue(null, "浏览项目资产", RegistryValueKind.String);

            using RegistryKey exploreCommandKey = exploreKey.CreateSubKey("command", true) ??
                throw new InvalidOperationException("无法创建项目目录的 explore command 注册表项。");
            exploreCommandKey.SetValue(
                null,
                ExploreCommand,
                RegistryValueKind.ExpandString);

            directoryClassKey.SetValue(
                "RegistrationVersion",
                RegistrationVersion,
                RegistryValueKind.DWord);

            SHChangeNotify(ShellChangeNotifyEventId.AssociationChanged, 0, 0, 0);
        }

        private static bool IsRegistrationCurrent(string command, string defaultIcon)
        {
            using RegistryKey? directoryClassKey =
                Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            if (directoryClassKey is null ||
                !Equals(directoryClassKey.GetValue("RegistrationVersion"), RegistrationVersion))
            {
                return false;
            }

            if (!Array.Exists(
                    directoryClassKey.GetValueNames(),
                    name => string.Equals(
                        name,
                        "CanUseForDirectory",
                        StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            using RegistryKey? shellKey = directoryClassKey.OpenSubKey("shell", false);
            if (!string.Equals(
                shellKey?.GetValue(null) as string,
                "open",
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using RegistryKey? commandKey =
                directoryClassKey.OpenSubKey(@"shell\open\command", false);
            if (!string.Equals(
                commandKey?.GetValue(null) as string,
                command,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using RegistryKey? defaultIconKey =
                directoryClassKey.OpenSubKey("DefaultIcon", false);
            if (!string.Equals(
                defaultIconKey?.GetValue(null) as string,
                defaultIcon,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using RegistryKey? exploreCommandKey =
                directoryClassKey.OpenSubKey(@"shell\explore\command", false);
            return string.Equals(
                exploreCommandKey?.GetValue(
                    null,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                ExploreCommand,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureIconExtracted(out bool iconChanged)
        {
            string sourcePath = Path.Combine(AppContext.BaseDirectory, PackagedIconRelativePath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("应用包内找不到项目目录图标。", sourcePath);
            }

            string localStatePath = GetLocalStatePath();
            Directory.CreateDirectory(localStatePath);
            string destinationPath = Path.Combine(localStatePath, IconFileName);

            iconChanged = !File.Exists(destinationPath) ||
                !File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(
                    File.ReadAllBytes(destinationPath));
            if (iconChanged)
            {
                File.Copy(sourcePath, destinationPath, true);
            }

            return destinationPath;
        }

        public static string GetLocalStatePath()
        {
            try
            {
                return ApplicationData.Current.LocalFolder.Path;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "projectFrameCut.InstanceSelector",
                    "LocalState");
            }
        }

        private static string GetDisabledMarkerPath() =>
            Path.Combine(GetLocalStatePath(), DisabledMarkerFileName);

        private static string GetSelectorExecutablePath()
        {
            string aliasPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps",
                SelectorAlias);

            if (File.Exists(aliasPath))
            {
                return aliasPath;
            }

            return Environment.ProcessPath ??
                throw new InvalidOperationException("无法确定 Selector 的可执行文件路径。");
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(
            ShellChangeNotifyEventId eventId,
            uint flags,
            nint item1,
            nint item2);

        private enum ShellChangeNotifyEventId : uint
        {
            AssociationChanged = 0x08000000
        }
    }
}
