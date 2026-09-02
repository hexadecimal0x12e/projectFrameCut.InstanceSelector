using System;
using System.Diagnostics;

namespace projectFrameCut.InstanceSelector
{
    internal static class Program
    {
        private const string CliHostProcessName = "pjfc";

        [STAThread]
        public static int Main(string[] args)
        {
            using Process currentProcess = Process.GetCurrentProcess();
            if (string.Equals(
                    currentProcess.ProcessName,
                    CliHostProcessName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return CLIProgram.RunAsync(args).GetAwaiter().GetResult();
            }

            XamlGeneratedProgram.XamlGeneratedMain();
            return 0;
        }
    }
}
