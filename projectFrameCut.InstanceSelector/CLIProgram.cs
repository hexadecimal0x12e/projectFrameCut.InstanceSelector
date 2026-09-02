using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using LocalizedResources;

namespace projectFrameCut.InstanceSelector
{
    internal static class CLIProgram
    {
        private const string InstanceEnvironmentVariable = "PJFC_INSTANCE";
        private const int RuntimeErrorExitCode = 1;
        private const int UsageErrorExitCode = 2;

        private static ISimpleLocalizerBase Localized =>
            SimpleLocalizerBaseGeneratedHelper.Localized ??= SimpleLocalizer.Init();

        public static async Task<int> RunAsync(string[] args)
        {
            if (args.Length > 0 &&
                string.Equals(args[0], "stdio_mcp", StringComparison.OrdinalIgnoreCase))
            {
                return await StdioMcpRouter.RunAsync(args.Skip(1).ToArray());
            }

            if (args.Length == 1 &&
                string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase))
            {
                WriteHelp(Console.Out);
                return 0;
            }

            if (args.Length == 1 &&
                string.Equals(args[0], "instance", StringComparison.OrdinalIgnoreCase))
            {
                WriteInstanceHelp(Console.Out);
                return 0;
            }

            if (args.Length == 2 &&
                string.Equals(args[0], "instance", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(args[1], "--help", StringComparison.OrdinalIgnoreCase))
            {
                WriteInstanceHelp(Console.Out);
                return 0;
            }

            if (!TryParseArguments(args, out CliInvocation invocation, out string? parseError))
            {
                Console.Error.WriteLine(parseError);
                if (args.Length > 0 &&
                    string.Equals(args[0], "instance", StringComparison.OrdinalIgnoreCase))
                {
                    WriteInstanceUsage(Console.Error);
                }
                else
                {
                    WriteUsage(Console.Error);
                }

                return UsageErrorExitCode;
            }

            SelectorSettingsStore settingsStore = new();
            if (invocation.ManagementAction is
                CliManagementAction.ClearDefault or
                CliManagementAction.InstallSelectorExtension or
                CliManagementAction.UninstallSelectorExtension or
                CliManagementAction.UninstallInstanceManager)
            {
                try
                {
                    switch (invocation.ManagementAction)
                    {
                        case CliManagementAction.ClearDefault:
                            settingsStore.ClearDefaultInstance();
                            Console.WriteLine(Localized.CLI_Output_DefaultCleared);
                            break;
                        case CliManagementAction.InstallSelectorExtension:
                            ProjectDirectoryRegistration.Install();
                            Console.WriteLine(Localized.CLI_Output_ExtensionInstalled);
                            break;
                        case CliManagementAction.UninstallSelectorExtension:
                            ProjectDirectoryRegistration.Uninstall();
                            Console.WriteLine(Localized.CLI_Output_ExtensionUninstalled);
                            break;
                        case CliManagementAction.UninstallInstanceManager:
                            await UninstallInstanceManagerAsync(settingsStore);
                            break;
                    }

                    return 0;
                }
                catch (Exception ex)
                {
                    string operation = invocation.ManagementAction switch
                    {
                        CliManagementAction.ClearDefault => Localized.CLI_Operation_ClearDefault,
                        CliManagementAction.InstallSelectorExtension => Localized.CLI_Operation_InstallExtension,
                        CliManagementAction.UninstallSelectorExtension => Localized.CLI_Operation_UninstallExtension,
                        CliManagementAction.UninstallInstanceManager => Localized.CLI_Operation_UninstallManager,
                        _ => Localized.CLI_Operation_Default
                    };
                    Console.Error.WriteLine(Localized.CLI_Error_Operation(operation, ex.Message));
                    return RuntimeErrorExitCode;
                }
            }

            IReadOnlyList<ProjectFrameCutInstance> instances;
            try
            {
                instances = await new ProjectLaunchService().DiscoverInstancesAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(Localized.CLI_Error_DiscoverInstances(ex.Message));
                return RuntimeErrorExitCode;
            }

            if (invocation.ManagementAction == CliManagementAction.ListInstances)
            {
                string? configuredDefault;
                try
                {
                    configuredDefault = settingsStore.GetDefaultInstance();
                }
                catch (InvalidDataException ex)
                {
                    Console.Error.WriteLine(Localized.CLI_Error_Settings(ex.Message));
                    return RuntimeErrorExitCode;
                }

                WriteInstances(Console.Out, instances, configuredDefault);
                return 0;
            }

            if (invocation.ManagementAction == CliManagementAction.SetDefault)
            {
                ProjectFrameCutInstance? defaultInstance = FindInstance(
                    instances,
                    invocation.ManagementValue!);
                if (defaultInstance is null)
                {
                    Console.Error.WriteLine(
                        Localized.CLI_Error_InstanceNotFound(invocation.ManagementValue!));
                    WriteInstances(Console.Error, instances, null);
                    return RuntimeErrorExitCode;
                }

                try
                {
                    settingsStore.SetDefaultInstance(defaultInstance.PackageIdentifier);
                    Console.WriteLine(
                        Localized.CLI_Output_DefaultSet(
                            defaultInstance.PackageIdentifier,
                            defaultInstance.Version));
                    return 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(Localized.CLI_Error_SaveDefault(ex.Message));
                    return RuntimeErrorExitCode;
                }
            }

            string? requestedIdentifier = invocation.InstanceIdentifier;
            if (string.IsNullOrWhiteSpace(requestedIdentifier))
            {
                requestedIdentifier = Environment.GetEnvironmentVariable(InstanceEnvironmentVariable)?.Trim();
            }

            if (string.IsNullOrWhiteSpace(requestedIdentifier))
            {
                try
                {
                    requestedIdentifier = settingsStore.GetDefaultInstance();
                }
                catch (InvalidDataException ex)
                {
                    Console.Error.WriteLine(Localized.CLI_Error_Settings(ex.Message));
                    return RuntimeErrorExitCode;
                }
            }

            if (string.IsNullOrWhiteSpace(requestedIdentifier))
            {
                Console.Error.WriteLine(Localized.CLI_Error_NoInstance);
                if (invocation.IsManagementMode)
                {
                    WriteInstances(Console.Error, instances, null);
                }

                return RuntimeErrorExitCode;
            }

            ProjectFrameCutInstance? target = FindInstance(instances, requestedIdentifier);
            if (target is null)
            {
                Console.Error.WriteLine(Localized.CLI_Error_InstanceNotFound(requestedIdentifier));
                if (invocation.IsManagementMode)
                {
                    WriteInstances(Console.Error, instances, null);
                }

                return RuntimeErrorExitCode;
            }

            try
            {
                ProcessStartInfo startInfo = new(target.CliExecutablePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory
                };
                foreach (string argument in invocation.TargetArguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using Process process = Process.Start(startInfo) ??
                    throw new InvalidOperationException(Localized.CLI_Error_TargetProcessStart);
                await process.WaitForExitAsync();
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    Localized.CLI_Error_TargetStart(target.PackageIdentifier, ex.Message));
                return RuntimeErrorExitCode;
            }
        }

        private static async Task UninstallInstanceManagerAsync(
            SelectorSettingsStore settingsStore)
        {
            string packageFullName;
            try
            {
                packageFullName = Package.Current.Id.FullName;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    Localized.CLI_Error_NoPackageIdentity,
                    ex);
            }

            Console.Write(
                Localized.CLI_Prompt_Uninstall);
            string? confirmation = Console.ReadLine()?.Trim();
            if (!string.Equals(confirmation, "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(confirmation, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(Localized.CLI_Output_UninstallCancelled);
                return;
            }

            ProjectDirectoryRegistration.Uninstall();
            settingsStore.ClearDefaultInstance();

            Console.WriteLine(Localized.CLI_Output_Uninstalling);
            DeploymentResult result = await new PackageManager()
                .RemovePackageAsync(packageFullName);
            if (!string.IsNullOrWhiteSpace(result.ErrorText))
            {
                throw new InvalidOperationException(result.ErrorText);
            }

            Console.WriteLine(Localized.CLI_Output_Uninstalled);
        }

        private static ProjectFrameCutInstance? FindInstance(
            IReadOnlyList<ProjectFrameCutInstance> instances,
            string packageIdentifier) =>
            instances.FirstOrDefault(instance => string.Equals(
                instance.PackageIdentifier,
                packageIdentifier.Trim(),
                StringComparison.OrdinalIgnoreCase));

        private static void WriteInstances(
            TextWriter writer,
            IReadOnlyList<ProjectFrameCutInstance> instances,
            string? defaultIdentifier)
        {
            if (instances.Count == 0)
            {
                writer.WriteLine(Localized.CLI_Output_NoInstances);
                return;
            }

            writer.WriteLine(Localized.CLI_Output_Instances);
            foreach (ProjectFrameCutInstance instance in instances)
            {
                string marker = string.Equals(
                    instance.PackageIdentifier,
                    defaultIdentifier,
                    StringComparison.OrdinalIgnoreCase)
                    ? "*"
                    : " ";
                writer.WriteLine(
                    $"{marker} {instance.DisplayName} | {instance.PackageIdentifier} | {instance.Version}");
            }
        }

        private static bool TryParseArguments(
            IReadOnlyList<string> args,
            out CliInvocation invocation,
            out string? errorMessage)
        {
            string? instanceIdentifier = null;
            CliManagementAction managementAction = CliManagementAction.None;
            string? managementValue = null;
            List<string> targetArguments = [];
            bool parseSelectorArguments = true;
            bool isManagementMode = args.Count > 0 &&
                string.Equals(args[0], "instance", StringComparison.OrdinalIgnoreCase);
            int firstArgumentIndex = isManagementMode ? 1 : 0;

            for (int index = firstArgumentIndex; index < args.Count; index++)
            {
                string argument = args[index];
                if (parseSelectorArguments && argument == "--")
                {
                    parseSelectorArguments = false;
                    continue;
                }

                if (!parseSelectorArguments)
                {
                    targetArguments.Add(argument);
                    continue;
                }

                if (string.Equals(argument, "--instance", StringComparison.OrdinalIgnoreCase))
                {
                    if (instanceIdentifier is not null)
                    {
                        return Fail(Localized.CLI_Error_InstanceOnlyOnce, out invocation, out errorMessage);
                    }

                    if (!TryTakeValue(args, ref index, out instanceIdentifier))
                    {
                        return Fail(Localized.CLI_Error_InstanceValueRequired, out invocation, out errorMessage);
                    }

                    continue;
                }

                if (argument.StartsWith("--instance=", StringComparison.OrdinalIgnoreCase))
                {
                    if (instanceIdentifier is not null)
                    {
                        return Fail(Localized.CLI_Error_InstanceOnlyOnce, out invocation, out errorMessage);
                    }

                    instanceIdentifier = argument["--instance=".Length..].Trim();
                    if (instanceIdentifier.Length == 0)
                    {
                        return Fail(Localized.CLI_Error_InstanceValueRequired, out invocation, out errorMessage);
                    }

                    continue;
                }

                if (isManagementMode &&
                    string.Equals(argument, "--list-instances", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TrySetManagementAction(
                            CliManagementAction.ListInstances,
                            null,
                            ref managementAction,
                            ref managementValue))
                    {
                        return Fail(Localized.CLI_Error_OneManagementAction, out invocation, out errorMessage);
                    }

                    continue;
                }

                if (isManagementMode &&
                    string.Equals(argument, "--clear-default", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TrySetManagementAction(
                            CliManagementAction.ClearDefault,
                            null,
                            ref managementAction,
                            ref managementValue))
                    {
                        return Fail(Localized.CLI_Error_OneManagementAction, out invocation, out errorMessage);
                    }

                    continue;
                }

                if (isManagementMode &&
                    string.Equals(argument, "--set-default", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryTakeValue(args, ref index, out string? value))
                    {
                        return Fail(Localized.CLI_Error_SetDefaultValueRequired, out invocation, out errorMessage);
                    }

                    if (!TrySetManagementAction(
                            CliManagementAction.SetDefault,
                            value,
                            ref managementAction,
                            ref managementValue))
                    {
                        return Fail(Localized.CLI_Error_OneManagementAction, out invocation, out errorMessage);
                    }

                    continue;
                }

                if (isManagementMode &&
                    argument.StartsWith("--set-default=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = argument["--set-default=".Length..].Trim();
                    if (value.Length == 0)
                    {
                        return Fail(Localized.CLI_Error_SetDefaultValueRequired, out invocation, out errorMessage);
                    }

                    if (!TrySetManagementAction(
                            CliManagementAction.SetDefault,
                            value,
                            ref managementAction,
                            ref managementValue))
                    {
                        return Fail(Localized.CLI_Error_OneManagementAction, out invocation, out errorMessage);
                    }

                    continue;
                }

                if (isManagementMode &&
                    string.Equals(argument, "--install-selector-extension", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TrySetManagementAction(
                            CliManagementAction.InstallSelectorExtension,
                            null,
                            ref managementAction,
                            ref managementValue))
                    {
                        return Fail(Localized.CLI_Error_OneManagementAction, out invocation, out errorMessage);
                    }

                    continue;
                }

                if (isManagementMode &&
                    string.Equals(argument, "--uninstall-selector-extension", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TrySetManagementAction(
                            CliManagementAction.UninstallSelectorExtension,
                            null,
                            ref managementAction,
                            ref managementValue))
                    {
                        return Fail(Localized.CLI_Error_OneManagementAction, out invocation, out errorMessage);
                    }

                    continue;
                }

                if (isManagementMode &&
                     string.Equals(argument, "--uninstall-instance-manager", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TrySetManagementAction(
                            CliManagementAction.UninstallInstanceManager,
                            null,
                            ref managementAction,
                            ref managementValue))
                    {
                        return Fail(Localized.CLI_Error_OneManagementAction, out invocation, out errorMessage);
                    }

                    continue;
                }

                targetArguments.Add(argument);
            }

            if (managementAction != CliManagementAction.None &&
                (instanceIdentifier is not null || targetArguments.Count > 0))
            {
                return Fail(
                    Localized.CLI_Error_ManagementArgumentsConflict,
                    out invocation,
                    out errorMessage);
            }

            invocation = new CliInvocation(
                isManagementMode,
                instanceIdentifier,
                managementAction,
                managementValue,
                targetArguments);
            errorMessage = null;
            return true;
        }

        private static bool TryTakeValue(
            IReadOnlyList<string> args,
            ref int index,
            out string? value)
        {
            if (index + 1 >= args.Count ||
                args[index + 1] == "--" ||
                args[index + 1].StartsWith('-'))
            {
                value = null;
                return false;
            }

            value = args[++index].Trim();
            return value.Length > 0;
        }

        private static bool TrySetManagementAction(
            CliManagementAction action,
            string? value,
            ref CliManagementAction currentAction,
            ref string? currentValue)
        {
            if (currentAction != CliManagementAction.None)
            {
                return false;
            }

            currentAction = action;
            currentValue = value;
            return true;
        }

        private static bool Fail(
            string message,
            out CliInvocation invocation,
            out string? errorMessage)
        {
            invocation = new CliInvocation(false, null, CliManagementAction.None, null, []);
            errorMessage = message;
            return false;
        }

        private static void WriteUsage(TextWriter writer)
        {
            writer.WriteLine(Localized.CLI_Usage_Title);
            writer.WriteLine(Localized.CLI_Usage_Main);
            writer.WriteLine(Localized.CLI_Usage_Instance);
            writer.WriteLine(Localized.CLI_Usage_Stdio);
            writer.WriteLine(Localized.CLI_Usage_Help);
        }

        private static void WriteInstanceUsage(TextWriter writer)
        {
            writer.WriteLine(Localized.CLI_Usage_InstanceTitle);
            writer.WriteLine(Localized.CLI_Usage_InstanceList);
            writer.WriteLine(Localized.CLI_Usage_InstanceSetDefault);
            writer.WriteLine(Localized.CLI_Usage_InstanceClearDefault);
            writer.WriteLine(Localized.CLI_Usage_InstanceInstallExtension);
            writer.WriteLine(Localized.CLI_Usage_InstanceUninstallExtension);
            writer.WriteLine(Localized.CLI_Usage_InstanceUninstallManager);
        }

        private static void WriteHelp(TextWriter writer)
        {
            writer.WriteLine(Localized.CLI_Help_Title);
            writer.WriteLine();
            WriteUsage(writer);
            writer.WriteLine();
            writer.WriteLine(Localized.CLI_Help_Options);
            writer.WriteLine(Localized.CLI_Help_InstanceOption);
            writer.WriteLine(Localized.CLI_Help_HelpOption);
            writer.WriteLine(Localized.CLI_Help_DelimiterOption);
            writer.WriteLine();
            writer.WriteLine(Localized.CLI_Help_ManagementParsing);
            writer.WriteLine(Localized.CLI_Help_Env);
            writer.WriteLine(Localized.CLI_Help_Stdio);
        }

        private static void WriteInstanceHelp(TextWriter writer)
        {
            writer.WriteLine(Localized.CLI_Help_Title);
            writer.WriteLine();
            WriteInstanceUsage(writer);
            writer.WriteLine();
            writer.WriteLine(Localized.CLI_Help_Options);
            writer.WriteLine(Localized.CLI_InstanceHelp_List);
            writer.WriteLine(Localized.CLI_InstanceHelp_Set);
            writer.WriteLine(Localized.CLI_InstanceHelp_SetDescription);
            writer.WriteLine(Localized.CLI_InstanceHelp_Clear);
            writer.WriteLine(Localized.CLI_InstanceHelp_Install);
            writer.WriteLine(Localized.CLI_InstanceHelp_Uninstall);
            writer.WriteLine(Localized.CLI_InstanceHelp_Manager);
        }

        private sealed record CliInvocation(
            bool IsManagementMode,
            string? InstanceIdentifier,
            CliManagementAction ManagementAction,
            string? ManagementValue,
            IReadOnlyList<string> TargetArguments);

        private enum CliManagementAction
        {
            None,
            ListInstances,
            SetDefault,
            ClearDefault,
            InstallSelectorExtension,
            UninstallSelectorExtension,
            UninstallInstanceManager
        }
    }
}
