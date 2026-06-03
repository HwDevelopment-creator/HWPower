using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using WpfApp3.Utilities;

namespace WpfApp3.Services
{
    /// <summary>
    /// Service per strumenti e utility di sistema
    /// </summary>
    public class ToolsService
    {
        /// <summary>
        /// Applica profilo privacy
        /// </summary>
        public async Task<string> ApplyPrivacyTweaks()
        {
            var sb = new StringBuilder();

            await Task.Run(() =>
            {
                foreach (var cmd in AppConstants.PrivacyTweaksCmds)
                {
                    var result = CommandHelper.RunCmdFull("reg", cmd);
                    sb.AppendLine($"reg {cmd.Substring(0, Math.Min(50, cmd.Length))}... -> {result.ExitCode}");
                }
            });

            return sb.ToString();
        }

        /// <summary>
        /// Disabilita Windows Tips/Suggestions
        /// </summary>
        public async Task<string> DisableTips()
        {
            var sb = new StringBuilder();

            await Task.Run(() =>
            {
                foreach (var cmd in AppConstants.DisableTipsCmds)
                {
                    var result = CommandHelper.RunCmdFull("reg", cmd);
                    sb.AppendLine($"reg {cmd.Substring(0, Math.Min(50, cmd.Length))}... -> {result.ExitCode}");
                }
            });

            return sb.ToString();
        }

        /// <summary>
        /// Effettua pulizia file temporanei
        /// </summary>
        public async Task<(double mbFreed, string details)> CleanTempFilesAsync()
        {
            return await Task.Run(() =>
            {
                double freed = 0;
                int delFiles = 0;
                var sb = new StringBuilder();

                string[] dirs = {
                    Path.GetTempPath(),
                    Path.Combine(
                        Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
                        "Temp"),
                    Path.Combine(
                        Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
                        "Prefetch"),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CrashDumps")
                };

                foreach (var dir in dirs)
                {
                    double dirFreed = 0;
                    int dirCount = 0;

                    try
                    {
                        if (!Directory.Exists(dir))
                        {
                            sb.AppendLine($"SKIP {dir} (non esiste)");
                            continue;
                        }

                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var len = new FileInfo(f).Length;
                                File.Delete(f);
                                dirFreed += len;
                                dirCount++;
                            }
                            catch { }
                        }

                        sb.AppendLine(
                            $"{dir} -> {dirCount} file, " +
                            $"{dirFreed / (double)AppConstants.BYTES_PER_MB:F1} MB");

                        freed += dirFreed;
                        delFiles += dirCount;
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"ERR {dir}: {ex.Message}");
                    }
                }

                return (freed / (double)AppConstants.BYTES_PER_MB, sb.ToString());
            });
        }

        /// <summary>
        /// Avvia Disk Cleanup utility
        /// </summary>
        public bool StartDiskCleanup()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "cleanmgr.exe", "/sageset:1")
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Avvia Task Manager
        /// </summary>
        public bool StartTaskManager()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "taskmgr.exe")
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resetta rete (WINSOCK, IP, DNS)
        /// </summary>
        public async Task<string> ResetNetworkAsync()
        {
            var sb = new StringBuilder();

            await Task.Run(() =>
            {
                foreach (var (cmd, args) in AppConstants.NetResetCmds)
                {
                    var result = CommandHelper.RunCmdFull(cmd, args);
                    sb.AppendLine($"{cmd} {args} -> {result.ExitCode}");
                }
            });

            return sb.ToString();
        }

        /// <summary>
        /// Applica TCP Tweaks
        /// </summary>
        public async Task<string> ApplyTcpTweaksAsync()
        {
            var sb = new StringBuilder();

            await Task.Run(() =>
            {
                foreach (var (cmd, args) in AppConstants.TcpTweaksCmds)
                {
                    var result = CommandHelper.RunCmdFull(cmd, args);
                    sb.AppendLine($"{cmd} {args.Substring(0, Math.Min(50, args.Length))}... -> {result.ExitCode}");
                }
            });

            return sb.ToString();
        }

        /// <summary>
        /// Resetta Windows Update
        /// </summary>
        public async Task<string> ResetWindowsUpdateAsync()
        {
            var sb = new StringBuilder();

            await Task.Run(() =>
            {
                foreach (var service in AppConstants.WuResetServices)
                {
                    var stopCmd = CommandHelper.RunCmdFull("sc.exe", $"stop {service}");
                    sb.AppendLine($"sc stop {service} -> {stopCmd.ExitCode}");

                    var delCmd = CommandHelper.RunCmdFull("sc.exe", $"delete {service}");
                    sb.AppendLine($"sc delete {service} -> {delCmd.ExitCode}");
                }
            });

            return sb.ToString();
        }

        /// <summary>
        /// Ping to hosts per test connettività
        /// </summary>
        public async Task<(string host, long pingMs, bool success)[]> PingHostsAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<(string, long, bool)>();

                foreach (var (hostName, hostIp) in AppConstants.PingHosts)
                {
                    try
                    {
                        using var ping = new System.Net.NetworkInformation.Ping();
                        var reply = ping.Send(hostIp, 800);

                        results.Add((hostName,
                            reply?.RoundtripTime ?? 0,
                            reply?.Status == System.Net.NetworkInformation.IPStatus.Success));
                    }
                    catch
                    {
                        results.Add((hostName, 0, false));
                    }
                }

                return results.ToArray();
            });
        }
    }
}
