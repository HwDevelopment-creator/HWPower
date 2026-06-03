using System.Diagnostics;
using System.Text;
using WpfApp3.Models;

namespace WpfApp3.Utilities
{
    /// <summary>
    /// Helper per esecuzione di comandi e script
    /// </summary>
    public static class CommandHelper
    {
        /// <summary>
        /// Esegue un comando e ne cattura output
        /// </summary>
        public static CmdResult RunCmdFull(string file, string args, int timeoutMs = AppConstants.CMD_TIMEOUT_MS)
        {
            var result = new CmdResult { ExitCode = -1 };

            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    result.Error = "Process.Start returned null";
                    return result;
                }

                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    result.Error = "TIMEOUT";
                    return result;
                }

                result.Output = outTask.Result ?? "";
                result.Error = errTask.Result ?? "";
                result.ExitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Esegue uno script PowerShell
        /// </summary>
        public static CmdResult RunPsFull(string script)
        {
            return RunCmdFull("powershell.exe", 
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"");
        }

        /// <summary>
        /// Esegue un comando con elevazione (richiede UAC)
        /// </summary>
        public static CmdResult RunCmdElevated(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = false
                };

                using var process = Process.Start(psi);
                process?.WaitForExit();
                return new CmdResult { ExitCode = process?.ExitCode ?? -1 };
            }
            catch (Exception ex)
            {
                return new CmdResult { ExitCode = -1, Error = ex.Message };
            }
        }
    }
}
