using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using WinRT;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Management.Deployment;
using Windows.Storage;

namespace projectFrameCut.InstanceSelector
{
    public sealed partial class MainWindow : Window
    {
        private readonly ProjectLaunchService _launchService;
        private readonly SelectorSettingsStore _settingsStore;
        private readonly IReadOnlyList<ProjectFrameCutInstance> _instances;
        private readonly string? _projectPath;
        private readonly string? _launchPayload;
        private readonly IDataTransferManagerInterop _dataTransferManagerInterop;
        private readonly DataTransferManager _dataTransferManager;
        private StorageFile? _diagnosticArchive;

        private static readonly Guid DataTransferManagerId =
            new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

        internal MainWindow(
            ProjectLaunchService launchService,
            SelectorSettingsStore settingsStore,
            string? projectPath,
            string message,
            IReadOnlyList<ProjectFrameCutInstance> instances,
            bool allowInstanceSelection,
            string? launchPayload = null)
        {
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            Title = UiStrings.Get("WindowTitle");
            AppTitleTextBlock.Text = Title;
            if (Content is FrameworkElement root)
            {
                root.FlowDirection = CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
            }

            _launchService = launchService;
            _settingsStore = settingsStore;
            _instances = instances;
            _projectPath = projectPath;
            _launchPayload = launchPayload;
            _dataTransferManagerInterop = DataTransferManager.As<IDataTransferManagerInterop>();
            Guid dataTransferManagerId = DataTransferManagerId;
            nint dataTransferManager = _dataTransferManagerInterop.GetForWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(this),
                ref dataTransferManagerId);
            _dataTransferManager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(dataTransferManager);
            _dataTransferManager.DataRequested += DataTransferManager_DataRequested;
            StatusTextBlock.Text = message;
            ProjectPathTextBlock.Text = string.IsNullOrWhiteSpace(projectPath)
                ? string.Empty
                : UiStrings.Format("Ui_ProjectPath", projectPath);
            InstancesList.ItemsSource = instances;
            DefaultCliInstanceComboBox.ItemsSource = instances;
            DiagnosticInstanceComboBox.ItemsSource = instances;

            bool hasInstances = allowInstanceSelection && instances.Count > 0;
            bool showSettingsActions =
                string.IsNullOrWhiteSpace(projectPath) &&
                string.IsNullOrWhiteSpace(launchPayload);
            TitleTextBlock.Text = allowInstanceSelection
                ? UiStrings.Get("Ui_Title_SelectInstance")
                : UiStrings.Get("Ui_Title_Selector");
            InstancesHeaderTextBlock.Visibility = allowInstanceSelection
                ? Visibility.Visible
                : Visibility.Collapsed;
            InstancesList.Visibility = hasInstances ? Visibility.Visible : Visibility.Collapsed;
            NoInstancesTextBlock.Visibility = allowInstanceSelection && !hasInstances
                ? Visibility.Visible
                : Visibility.Collapsed;
            OpenButton.Visibility = allowInstanceSelection ? Visibility.Visible : Visibility.Collapsed;
            SettingsActionsPanel.Visibility = showSettingsActions
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!allowInstanceSelection)
            {
                Grid.SetColumnSpan(CloseButton, 2);
            }

            if (showSettingsActions)
            {
                UpdateDirectoryClassControls();
                UpdateDefaultCliControls();
                DiagnosticInstanceComboBox.SelectedItem =
                    DefaultCliInstanceComboBox.SelectedItem ??
                    (instances.Count > 0 ? instances[0] : null);
            }
        }

        private void DiagnosticInstanceComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            StartDiagnosticCaptureButton.IsEnabled =
                DiagnosticInstanceComboBox.SelectedItem is ProjectFrameCutInstance;
        }

        private async void StartDiagnosticCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (DiagnosticInstanceComboBox.SelectedItem is not ProjectFrameCutInstance instance)
            {
                return;
            }

            SetDiagnosticCaptureBusy(true);
            try
            {
                DiagnosticCaptureStateTextBlock.Text = UiStrings.Get("Ui_Diagnostic_Running");
                DiagnosticCaptureResult result = await DiagnosticCaptureService.CaptureAsync(instance);
                _diagnosticArchive = await StorageFile.GetFileFromPathAsync(result.ArchivePath);
                DiagnosticCaptureStateTextBlock.Text = UiStrings.Format(
                    "Ui_Diagnostic_Complete",
                    result.ExitCode,
                    result.DumpCount,
                    result.ArchivePath);
                _dataTransferManagerInterop.ShowShareUIForWindow(
                    WinRT.Interop.WindowNative.GetWindowHandle(this));
            }
            catch (Exception ex)
            {
                DiagnosticCaptureStateTextBlock.Text =
                    UiStrings.Format("Ui_Diagnostic_Error", ex.Message);
            }
            finally
            {
                SetDiagnosticCaptureBusy(false);
            }
        }

        private void DataTransferManager_DataRequested(
            DataTransferManager sender,
            DataRequestedEventArgs args)
        {
            StorageFile? archive = _diagnosticArchive;
            if (archive is null)
            {
                args.Request.FailWithDisplayText(UiStrings.Get("Ui_Diagnostic_NoArchive"));
                return;
            }

            args.Request.Data.Properties.Title = UiStrings.Get("Ui_Diagnostic_ShareTitle");
            args.Request.Data.SetStorageItems([archive]);
        }

        private void DefaultCliInstanceComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            SaveDefaultCliInstanceButton.IsEnabled =
                DefaultCliInstanceComboBox.SelectedItem is ProjectFrameCutInstance;
        }

        private void SaveDefaultCliInstanceButton_Click(object sender, RoutedEventArgs e)
        {
            if (DefaultCliInstanceComboBox.SelectedItem is not ProjectFrameCutInstance selectedInstance)
            {
                return;
            }

            SetDefaultCliBusy(true);
            try
            {
                ProjectFrameCutInstance? currentInstance = null;
                foreach (ProjectFrameCutInstance instance in _instances)
                {
                    if (string.Equals(
                            instance.PackageIdentifier,
                            selectedInstance.PackageIdentifier,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        currentInstance = instance;
                        break;
                    }
                }

                if (currentInstance is null)
                {
                    throw new InvalidOperationException(
                        UiStrings.Get("Ui_Error_SelectedInstanceUnavailable"));
                }

                _settingsStore.SetDefaultInstance(currentInstance.PackageIdentifier);
                DefaultCliInstanceStateTextBlock.Text =
                    UiStrings.Format(
                        "Ui_Default_Current",
                        currentInstance.PackageIdentifier,
                        currentInstance.Version);
            }
            catch (Exception ex)
            {
                DefaultCliInstanceStateTextBlock.Text =
                    UiStrings.Format("Ui_Error_SaveDefault", ex.Message);
            }
            finally
            {
                SetDefaultCliBusy(false);
            }
        }

        private void ClearDefaultCliInstanceButton_Click(object sender, RoutedEventArgs e)
        {
            SetDefaultCliBusy(true);
            try
            {
                _settingsStore.ClearDefaultInstance();
                DefaultCliInstanceComboBox.SelectedItem = null;
                DefaultCliInstanceStateTextBlock.Text = UiStrings.Get("Ui_Default_NotSet");
            }
            catch (Exception ex)
            {
                DefaultCliInstanceStateTextBlock.Text =
                    UiStrings.Format("Ui_Error_ClearDefault", ex.Message);
            }
            finally
            {
                SetDefaultCliBusy(false);
            }
        }

        private void InstallDirectoryClassButton_Click(object sender, RoutedEventArgs e)
        {
            SetDirectoryClassBusy(true);
            try
            {
                ProjectDirectoryRegistration.Install();
                StatusTextBlock.Text = UiStrings.Get("Ui_Status_DirectoryInstalled");
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = UiStrings.Format("Ui_Error_InstallDirectory", ex.Message);
            }
            finally
            {
                SetDirectoryClassBusy(false);
                UpdateDirectoryClassControls();
            }
        }

        private void UninstallDirectoryClassButton_Click(object sender, RoutedEventArgs e)
        {
            SetDirectoryClassBusy(true);
            try
            {
                ProjectDirectoryRegistration.Uninstall();
                StatusTextBlock.Text = UiStrings.Get("Ui_Status_DirectoryUninstalled");
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = UiStrings.Format("Ui_Error_UninstallDirectory", ex.Message);
            }
            finally
            {
                SetDirectoryClassBusy(false);
                UpdateDirectoryClassControls();
            }
        }

        private async void UninstallInstanceManagerButton_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog dialog = new()
            {
                Title = UiStrings.Get("Ui_Dialog_UninstallTitle"),
                Content = UiStrings.Get("Ui_Dialog_UninstallContent"),
                PrimaryButtonText = UiStrings.Get("Ui_Dialog_ConfirmUninstall"),
                CloseButtonText = UiStrings.Get("Ui_Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = UninstallInstanceManagerButton.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            SetUninstallInstanceManagerBusy(true);
            try
            {
                string packageFullName = Package.Current.Id.FullName;
                ProjectDirectoryRegistration.Uninstall();
                _settingsStore.ClearDefaultInstance();
                StatusTextBlock.Text = UiStrings.Get("Ui_Status_Uninstalling");

                DeploymentResult result = await new PackageManager()
                    .RemovePackageAsync(packageFullName);
                if (!string.IsNullOrWhiteSpace(result.ErrorText))
                {
                    throw new InvalidOperationException(result.ErrorText);
                }

                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = UiStrings.Format("Ui_Error_UninstallManager", ex.Message);
                SetUninstallInstanceManagerBusy(false);
            }
        }

        private void InstancesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            OpenButton.IsEnabled =
                InstancesList.SelectedItem is ProjectFrameCutInstance &&
                (!string.IsNullOrWhiteSpace(_projectPath) ||
                 !string.IsNullOrWhiteSpace(_launchPayload));
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            if (InstancesList.SelectedItem is not ProjectFrameCutInstance instance ||
                (string.IsNullOrWhiteSpace(_projectPath) &&
                 string.IsNullOrWhiteSpace(_launchPayload)))
            {
                return;
            }

            SetBusy(true);
            try
            {
                if (!string.IsNullOrWhiteSpace(_launchPayload))
                {
                    _launchService.LaunchProtocol(instance, _launchPayload);
                }
                else
                {
                    _launchService.Launch(instance, _projectPath!);
                }

                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = UiStrings.Format("Ui_Error_Launch", ex.Message);
                SetBusy(false);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SetBusy(bool isBusy)
        {
            InstancesList.IsEnabled = !isBusy;
            CloseButton.IsEnabled = !isBusy;
            OpenButton.IsEnabled =
                !isBusy &&
                InstancesList.SelectedItem is ProjectFrameCutInstance &&
                (!string.IsNullOrWhiteSpace(_projectPath) ||
                 !string.IsNullOrWhiteSpace(_launchPayload));
        }

        private void SetDirectoryClassBusy(bool isBusy)
        {
            InstallDirectoryClassButton.IsEnabled = !isBusy;
            UninstallDirectoryClassButton.IsEnabled = !isBusy;
        }

        private void SetDefaultCliBusy(bool isBusy)
        {
            DefaultCliInstanceComboBox.IsEnabled = !isBusy && _instances.Count > 0;
            SaveDefaultCliInstanceButton.IsEnabled =
                !isBusy && DefaultCliInstanceComboBox.SelectedItem is ProjectFrameCutInstance;
            ClearDefaultCliInstanceButton.IsEnabled = !isBusy && _settingsStore.SettingsExist;
        }

        private void SetUninstallInstanceManagerBusy(bool isBusy)
        {
            UninstallInstanceManagerButton.IsEnabled = !isBusy;
        }

        private void SetDiagnosticCaptureBusy(bool isBusy)
        {
            DiagnosticInstanceComboBox.IsEnabled = !isBusy && _instances.Count > 0;
            StartDiagnosticCaptureButton.IsEnabled =
                !isBusy && DiagnosticInstanceComboBox.SelectedItem is ProjectFrameCutInstance;
        }

        private void UpdateDefaultCliControls()
        {
            DefaultCliInstanceComboBox.IsEnabled = _instances.Count > 0;
            try
            {
                string? defaultIdentifier = _settingsStore.GetDefaultInstance();
                ProjectFrameCutInstance? configuredInstance = null;
                if (!string.IsNullOrWhiteSpace(defaultIdentifier))
                {
                    foreach (ProjectFrameCutInstance instance in _instances)
                    {
                        if (string.Equals(
                                instance.PackageIdentifier,
                                defaultIdentifier,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            configuredInstance = instance;
                            break;
                        }
                    }
                }

                DefaultCliInstanceComboBox.SelectedItem = configuredInstance;
                DefaultCliInstanceStateTextBlock.Text = defaultIdentifier switch
                {
                    null => UiStrings.Get("Ui_Default_NotSet"),
                    _ when configuredInstance is not null =>
                        UiStrings.Format(
                            "Ui_Default_Current",
                            configuredInstance.PackageIdentifier,
                            configuredInstance.Version),
                    _ => UiStrings.Format("Ui_Default_Unavailable", defaultIdentifier)
                };
            }
            catch (Exception ex)
            {
                DefaultCliInstanceComboBox.SelectedItem = null;
                DefaultCliInstanceStateTextBlock.Text = ex.Message;
            }

            SaveDefaultCliInstanceButton.IsEnabled =
                DefaultCliInstanceComboBox.SelectedItem is ProjectFrameCutInstance;
            ClearDefaultCliInstanceButton.IsEnabled = _settingsStore.SettingsExist;
        }

        private void UpdateDirectoryClassControls()
        {
            bool isInstalled = ProjectDirectoryRegistration.IsInstalled();
            DirectoryClassStateTextBlock.Text = isInstalled
                ? UiStrings.Get("Ui_Directory_Installed")
                : UiStrings.Get("Ui_Directory_NotInstalled");
            UninstallDirectoryClassButton.IsEnabled = isInstalled;
        }
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDataTransferManagerInterop
    {
        nint GetForWindow([In] nint appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(nint appWindow);
    }
}
