using System.Collections.ObjectModel;
using System.Management;
using System.ServiceProcess;
using System.Text;
using WpfApp3.Models;
using WpfApp3.Utilities;

namespace WpfApp3.Services
{
    /// <summary>
    /// Service per gestione servizi Windows
    /// </summary>
    public class ServiceService
    {
        /// <summary>
        /// Carica lista di servizi Windows
        /// </summary>
        public async Task<List<ServiceInfo>> LoadServicesAsync()
        {
            return await Task.Run(() =>
            {
                var result = new List<ServiceInfo>();

                // Prima query WMI per StartMode e Description
                var startModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT Name, StartMode, Description FROM Win32_Service");

                    foreach (ManagementObject mo in searcher.Get())
                    {
                        using (mo)
                        {
                            var name = mo["Name"]?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(name))
                            {
                                startModes[name] = mo["StartMode"]?.ToString() ?? "";
                                descriptions[name] = mo["Description"]?.ToString() ?? "";
                            }
                        }
                    }
                }
                catch { }

                // Enumera ServiceController
                foreach (var sc in ServiceController.GetServices().OrderBy(x => x.ServiceName))
                {
                    try
                    {
                        var rec = GetRecommendation(sc.ServiceName);

                        result.Add(new ServiceInfo
                        {
                            Name = sc.ServiceName,
                            DisplayName = sc.DisplayName,
                            Status = sc.Status.ToString(),
                            StartMode = startModes.TryGetValue(sc.ServiceName, out var sm) ? sm : "",
                            Description = descriptions.TryGetValue(sc.ServiceName, out var d) ? d : "",
                            Recommendation = rec
                        });
                    }
                    catch { }
                    finally
                    {
                        try { sc.Dispose(); } catch { }
                    }
                }

                return result;
            });
        }

        /// <summary>
        /// Ottiene la raccomandazione per un servizio
        /// </summary>
        private string GetRecommendation(string serviceName)
        {
            var cmp = StringComparer.OrdinalIgnoreCase;

            if (AppConstants.AggressiveDisable.Contains(serviceName, cmp))
                return "Disabilita";
            if (AppConstants.GamingDisable.Contains(serviceName, cmp))
                return "Gaming";
            if (AppConstants.SafeDisable.Contains(serviceName, cmp))
                return "Sicuro";

            return "Mantieni";
        }

        /// <summary>
        /// Applica un profilo di servizi
        /// </summary>
        public (string details, int success, int failed) ApplyServiceProfile(
            string[] serviceNames, string profileLabel)
        {
            return Task.Run(() =>
            {
                var sb = new StringBuilder();
                int success = 0, failed = 0;

                foreach (var name in serviceNames)
                {
                    try
                    {
                        // Disabilita il servizio
                        var r1 = CommandHelper.RunCmdFull("sc.exe", $"config \"{name}\" start= disabled");
                        sb.AppendLine($"config disabled {name} -> exit {r1.ExitCode}");

                        if (!string.IsNullOrWhiteSpace(r1.Output))
                            sb.AppendLine("  " + r1.Output.Trim().Replace("\n", "\n  "));

                        // Lo ferma
                        var r2 = CommandHelper.RunCmdFull("sc.exe", $"stop \"{name}\"");
                        sb.AppendLine($"stop          {name} -> exit {r2.ExitCode}");

                        if (r1.ExitCode == 0 && r2.ExitCode == 0)
                            success++;
                        else
                            failed++;
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"FAIL {name}: {ex.Message}");
                        failed++;
                    }
                }

                return (sb.ToString(), success, failed);
            }).Result;
        }

        /// <summary>
        /// Ripristina i servizi (start=demand = Manual)
        /// </summary>
        public string RestoreServices()
        {
            var sb = new StringBuilder();

            foreach (var name in AppConstants.AggressiveDisable)
            {
                var r = CommandHelper.RunCmdFull("sc.exe", $"config \"{name}\" start= demand");
                sb.AppendLine($"{name} -> exit {r.ExitCode}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Gestisce un'azione su servizi (start/stop/restart)
        /// </summary>
        public string PerformServiceAction(IEnumerable<ServiceInfo> services, string action)
        {
            var sb = new StringBuilder();

            foreach (var svc in services)
            {
                try
                {
                    var r = CommandHelper.RunCmdFull("sc.exe", $"{action} \"{svc.Name}\"");
                    sb.AppendLine($"{action,-7} {svc.Name} -> exit {r.ExitCode}");

                    if (!string.IsNullOrWhiteSpace(r.Output))
                        sb.AppendLine("  " + r.Output.Trim().Replace("\n", "\n  "));
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"FAIL {svc.Name}: {ex.Message}");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Disabilita servizi selezionati
        /// </summary>
        public string DisableServices(IEnumerable<ServiceInfo> services)
        {
            var sb = new StringBuilder();

            foreach (var svc in services)
            {
                try
                {
                    var r1 = CommandHelper.RunCmdFull("sc.exe", $"config \"{svc.Name}\" start= disabled");
                    sb.AppendLine($"config disabled {svc.Name} -> exit {r1.ExitCode}");

                    var r2 = CommandHelper.RunCmdFull("sc.exe", $"stop \"{svc.Name}\"");
                    sb.AppendLine($"stop          {svc.Name} -> exit {r2.ExitCode}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"FAIL {svc.Name}: {ex.Message}");
                }
            }

            return sb.ToString();
        }
    }
}
