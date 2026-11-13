using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.InstanceSelector
{
    internal static class DiagnosticCaptureService
    {
        private const string TargetExecutableName = "pjfc-cli.exe";

        public static async Task<DiagnosticCaptureResult> CaptureAsync(
            ProjectFrameCutInstance instance)
        {
            DateTimeOffset startedAt = DateTimeOffset.Now;
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "projectFrameCut",
                "Diagnostics");
            string sessionName = $"{startedAt:yyyyMMdd-HHmmssfff}-{Sanitize(instance.PackageIdentifier)}";
            string sessionPath = Path.Combine(root, sessionName);
            Directory.CreateDirectory(sessionPath);

            await ConfigureWerAsync(sessionPath);

            string stdoutPath = Path.Combine(sessionPath, "stdout.log");
            string stderrPath = Path.Combine(sessionPath, "stderr.log");
            ProcessStartInfo startInfo = new(instance.CliExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add("gui");
            startInfo.ArgumentList.Add("--consoleLog");
            startInfo.ArgumentList.Add("--logDiagnostic");
            startInfo.ArgumentList.Add("--internal_diag_mode");

            int exitCode;
            await using (FileStream stdout = new(
                stdoutPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous))
            await using (FileStream stderr = new(
                stderrPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous))
            using (Process process = Process.Start(startInfo) ??
                throw new InvalidOperationException("The diagnostic process could not be started."))
            {
                Task stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout);
                Task stderrTask = process.StandardError.BaseStream.CopyToAsync(stderr);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdoutTask, stderrTask);
                exitCode = process.ExitCode;
            }

            await WaitForDumpAsync(sessionPath, exitCode);
            await File.WriteAllTextAsync(
                Path.Combine(sessionPath, "capture.txt"),
                $"Instance: {instance.DisplayName}{Environment.NewLine}" +
                $"Package: {instance.PackageIdentifier}{Environment.NewLine}" +
                $"Version: {instance.Version}{Environment.NewLine}" +
                $"Executable: {instance.CliExecutablePath}{Environment.NewLine}" +
                "Arguments: gui --consoleLog --logDiagnostic --internal_diag_mode" + Environment.NewLine +
                $"Started: {startedAt:O}{Environment.NewLine}" +
                $"Exited: {DateTimeOffset.Now:O}{Environment.NewLine}" +
                $"Exit code: {exitCode} (0x{exitCode:X8}){Environment.NewLine}" +
                $"WER image: {TargetExecutableName}{Environment.NewLine}" +
                $"WER dump type: 2 (full dump){Environment.NewLine}",
                Encoding.UTF8);

            string archivePath = Path.Combine(root, sessionName + ".zip");
            await Task.Run(() => ZipFile.CreateFromDirectory(
                sessionPath,
                archivePath,
                CompressionLevel.Fastest,
                false));
            return new DiagnosticCaptureResult(
                archivePath,
                exitCode,
                Directory.EnumerateFiles(sessionPath, "*.dmp", SearchOption.TopDirectoryOnly).Count());
        }

        private static async Task ConfigureWerAsync(string dumpFolder)
        {
            string regPath = Path.Combine(Path.GetTempPath(), $"pjfc-wer-{Guid.NewGuid():N}.reg");
            try
            {
                string escapedFolder = string.Join(
                    ",",
                    Encoding.Unicode.GetBytes(dumpFolder + '\0').Select(b => b.ToString("x2")));
                await File.WriteAllTextAsync(
                    regPath,
                    "Windows Registry Editor Version 5.00\r\n\r\n" +
                    $"[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\Windows Error Reporting\\LocalDumps\\{TargetExecutableName}]\r\n" +
                    $"\"DumpFolder\"=hex(2):{escapedFolder}\r\n" +
                    "\"DumpType\"=dword:00000002\r\n" +
                    "\"DumpCount\"=dword:0000000a\r\n",
                    Encoding.Unicode);

                ProcessStartInfo startInfo = new(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "reg.exe"))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.ArgumentList.Add("import");
                startInfo.ArgumentList.Add(regPath);
                using Process process = Process.Start(startInfo) ??
                    throw new InvalidOperationException("Windows Registry Editor could not be started.");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Windows Registry Editor exited with code {process.ExitCode}.");
                }
            }
            finally
            {
                File.Delete(regPath);
            }
        }

        private static async Task WaitForDumpAsync(string sessionPath, int exitCode)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(exitCode == 0 ? 2 : 15);
            long[]? previousSizes = null;
            int stableSamples = 0;
            do
            {
                await Task.Delay(500);
                long[] sizes = Directory
                    .EnumerateFiles(sessionPath, "*.dmp", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => new FileInfo(path).Length)
                    .ToArray();
                if (sizes.Length > 0 && previousSizes is not null && sizes.SequenceEqual(previousSizes))
                {
                    if (++stableSamples >= 2)
                    {
                        return;
                    }
                }
                else
                {
                    stableSamples = 0;
                }

                previousSizes = sizes;
            }
            while (DateTime.UtcNow < deadline);
        }

        private static string Sanitize(string value)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        }
    }

    internal sealed record DiagnosticCaptureResult(string ArchivePath, int ExitCode, int DumpCount);
}
