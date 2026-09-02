using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;

namespace projectFrameCut.InstanceSelector
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private readonly ProjectLaunchService _launchService = new();
        private readonly SelectorSettingsStore _settingsStore = new();

        public App()
        {
            InitializeComponent();
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                ProjectDirectoryRegistration.EnsureRegisteredAtStartup();

                ActivationProjectPath activation = GetActivationProjectPath();
                if (!string.IsNullOrWhiteSpace(activation.ErrorMessage))
                {
                    ShowWindow(activation.ErrorMessage, null, []);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(activation.ProtocolUri))
                {
                    await LaunchProtocolAsync(activation.ProtocolUri);
                    return;
                }

                if (string.IsNullOrWhiteSpace(activation.ProjectPath))
                {
                    IReadOnlyList<ProjectFrameCutInstance> settingsInstances;
                    string settingsMessage;
                    try
                    {
                        settingsInstances = await _launchService.DiscoverInstancesAsync();
                        settingsMessage = UiStrings.Get("App_SettingsMessage");
                    }
                    catch (Exception ex)
                    {
                        settingsInstances = [];
                        settingsMessage = UiStrings.Format("App_Error_DiscoverInstances", ex.Message);
                    }

                    ShowWindow(settingsMessage, null, settingsInstances);
                    return;
                }

                ProjectFileInspection inspection = await _launchService.InspectProjectAsync(activation.ProjectPath);
                if (!inspection.IsValid)
                {
                    ShowWindow(inspection.Message, inspection.ProjectPath, []);
                    return;
                }

                IReadOnlyList<ProjectFrameCutInstance> instances;
                try
                {
                    instances = await _launchService.DiscoverInstancesAsync();
                }
                catch (Exception ex)
                {
                    ShowWindow(
                        UiStrings.Format("App_Error_DiscoverInstances", ex.Message),
                        inspection.ProjectPath,
                        []);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(inspection.AppIdentifier))
                {
                    ProjectFrameCutInstance? target = instances.FirstOrDefault(instance =>
                        string.Equals(instance.PackageIdentifier, inspection.AppIdentifier, StringComparison.OrdinalIgnoreCase));

                    if (target is not null)
                    {
                        try
                        {
                            _launchService.Launch(target, inspection.ProjectPath);
                            Exit();
                            return;
                        }
                        catch (Exception ex)
                        {
                            ShowWindow(
                                UiStrings.Format(
                                    "App_Error_LastLaunch",
                                    target.DisplayName,
                                    ex.Message),
                                inspection.ProjectPath,
                                instances,
                                true);
                            return;
                        }
                    }

                    ShowWindow(
                        UiStrings.Format("App_Error_InstanceNotFound", inspection.AppIdentifier),
                        inspection.ProjectPath,
                        instances,
                        true);
                    return;
                }

                ShowWindow(
                    UiStrings.Get("App_Error_MissingLastOpen"),
                    inspection.ProjectPath,
                    instances,
                    true);
            }
            catch (Exception ex)
            {
                ShowWindow(UiStrings.Format("App_Error_Startup", ex.Message), null, []);
            }
        }

        private async Task LaunchProtocolAsync(string protocolUri)
        {
            IReadOnlyList<ProjectFrameCutInstance> instances;
            try
            {
                instances = await _launchService.DiscoverInstancesAsync();
            }
            catch (Exception ex)
            {
                ShowWindow(
                    UiStrings.Format("App_Error_DiscoverInstances", ex.Message),
                    null,
                    []);
                return;
            }

            bool hasInstanceParameter = TryGetQueryParameter(
                protocolUri,
                "instance",
                out string? requestedIdentifier);
            if (!hasInstanceParameter)
            {
                try
                {
                    requestedIdentifier = _settingsStore.GetDefaultInstance();
                }
                catch (Exception ex)
                {
                    ShowWindow(UiStrings.Format("App_Error_Startup", ex.Message), null, instances);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(requestedIdentifier))
            {
                ShowWindow(
                    UiStrings.Get("App_SettingsMessage"),
                    null,
                    instances,
                    true,
                    protocolUri);
                return;
            }

            ProjectFrameCutInstance? target = instances.FirstOrDefault(instance =>
                string.Equals(
                    instance.PackageIdentifier,
                    requestedIdentifier,
                    StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                ShowWindow(
                    UiStrings.Format("App_Error_InstanceNotFound", requestedIdentifier),
                    null,
                    instances);
                return;
            }

            try
            {
                _launchService.LaunchProtocol(target, protocolUri);
                Exit();
            }
            catch (Exception ex)
            {
                ShowWindow(
                    UiStrings.Format("App_Error_LastLaunch", target.DisplayName, ex.Message),
                    null,
                    instances);
            }
        }

        private void ShowWindow(
            string message,
            string? projectPath,
            IReadOnlyList<ProjectFrameCutInstance> instances,
            bool allowInstanceSelection = false,
            string? launchPayload = null)
        {
            _window = new MainWindow(
                _launchService,
                _settingsStore,
                projectPath,
                message,
                instances,
                allowInstanceSelection,
                launchPayload);
            _window.Activate();
        }

        private static ActivationProjectPath GetActivationProjectPath()
        {
            try
            {
                AppActivationArguments? activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (activationArguments?.Kind == ExtendedActivationKind.File &&
                    activationArguments.Data is IFileActivatedEventArgs fileArguments)
                {
                    string[] filePaths = fileArguments.Files
                        .Select(file => file.Path)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .ToArray();

                    return filePaths.Length switch
                    {
                        0 => new(null, null, UiStrings.Get("App_Error_ActivationNoPath")),
                        1 => new(filePaths[0], null, null),
                        _ => new(null, null, UiStrings.Get("App_Error_ActivationMultiple"))
                    };
                }

                if (activationArguments?.Kind == ExtendedActivationKind.Protocol &&
                    activationArguments.Data is IProtocolActivatedEventArgs protocolArguments)
                {
                    string protocolUri = protocolArguments.Uri.OriginalString;
                    return string.IsNullOrWhiteSpace(protocolUri)
                        ? new(null, null, UiStrings.Get("App_Error_ActivationNoPath"))
                        : new(null, protocolUri, null);
                }
            }
            catch (Exception ex)
            {
                return new(null, null, UiStrings.Format("App_Error_ActivationRead", ex.Message));
            }

            string[] commandLinePaths = Environment.GetCommandLineArgs()
                .Skip(1)
                .Where(argument => !string.IsNullOrWhiteSpace(argument) && !argument.StartsWith('-'))
                .ToArray();

            if (commandLinePaths.Length == 1 && IsProjectFrameCutProtocol(commandLinePaths[0]))
            {
                return new(null, commandLinePaths[0], null);
            }

            return commandLinePaths.Length switch
            {
                0 => new(null, null, null),
                1 => new(commandLinePaths[0], null, null),
                _ => new(null, null, UiStrings.Get("App_Error_ActivationMultiple"))
            };
        }

        private static bool IsProjectFrameCutProtocol(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            string.Equals(uri.Scheme, "pjfc", StringComparison.OrdinalIgnoreCase);

        private static bool TryGetQueryParameter(
            string value,
            string parameterName,
            out string? parameterValue)
        {
            parameterValue = null;
            int queryStart = value.IndexOf('?');
            if (queryStart < 0)
            {
                return false;
            }

            int fragmentStart = value.IndexOf('#', queryStart + 1);
            string query = fragmentStart < 0
                ? value[(queryStart + 1)..]
                : value[(queryStart + 1)..fragmentStart];
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = pair.IndexOf('=');
                string key = separator < 0 ? pair : pair[..separator];
                if (!string.Equals(
                        Uri.UnescapeDataString(key.Replace('+', ' ')),
                        parameterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parameterValue = separator < 0
                    ? string.Empty
                    : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
                return true;
            }

            return false;
        }

        private sealed record ActivationProjectPath(
            string? ProjectPath,
            string? ProtocolUri,
            string? ErrorMessage);
    }
}
