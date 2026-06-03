using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using WpfApp3.Models;
using WpfApp3.Utilities;

namespace WpfApp3.Services
{
    /// <summary>
    /// Service per gestione processi Windows
    /// </summary>
    public class ProcessService
    {
        /// <summary>
        /// Carica lista di processi in esecuzione
        /// </summary>
        public async Task<List<ProcessInfo>> LoadProcessesAsync()
        {
            return await Task.Run(() =>
            {
                var result = new List<ProcessInfo>();

                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        result.Add(new ProcessInfo
                        {
                            Name = p.ProcessName,
                            Id = p.Id,
                            RamMb = SystemHelper.SafeGetValue(() => p.WorkingSet64 / (double)AppConstants.BYTES_PER_MB),
                            Threads = SystemHelper.SafeGetValue(() => p.Threads.Count),
                            Priority = SystemHelper.SafeGet(() => p.PriorityClass.ToString()) ?? "?",
                            Path = SystemHelper.SafeGet(() => p.MainModule?.FileName ?? "") ?? ""
                        });
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                return result.OrderByDescending(x => x.RamMb).ToList();
            });
        }

        /// <summary>
        /// Termina processi selezionati
        /// </summary>
        public (int killed, int failed, string details) KillProcesses(IEnumerable<ProcessInfo> processes)
        {
            int ok = 0, fail = 0;
            var details = new StringBuilder();

            foreach (var p in processes)
            {
                try
                {
                    using var pr = Process.GetProcessById(p.Id);
                    pr.Kill();
                    ok++;
                    details.AppendLine($"OK   PID {p.Id,-6} {p.Name}");
                }
                catch (Exception ex)
                {
                    fail++;
                    details.AppendLine($"FAIL PID {p.Id,-6} {p.Name}  ({ex.Message})");
                }
            }

            return (ok, fail, details.ToString());
        }

        /// <summary>
        /// Sospende processi selezionati (freeze all threads)
        /// </summary>
        public (int suspended, string details) SuspendProcesses(IEnumerable<ProcessInfo> processes)
        {
            int ok = 0;
            var details = new StringBuilder();

            foreach (var p in processes)
            {
                try
                {
                    using var pr = Process.GetProcessById(p.Id);

                    foreach (ProcessThread th in pr.Threads)
                    {
                        var h = Interop.OpenThread(0x0002, false, (uint)th.Id);
                        if (h == IntPtr.Zero)
                            continue;

                        try
                        {
                            var res = Interop.SuspendThread(h);
                            if (res == uint.MaxValue)
                            {
                                details.AppendLine($"WARN suspend thread {th.Id} returned error");
                            }
                        }
                        finally
                        {
                            Interop.CloseHandle(h);
                        }
                    }

                    ok++;
                    details.AppendLine($"OK   PID {p.Id,-6} {p.Name}");
                }
                catch (Exception ex)
                {
                    details.AppendLine($"FAIL PID {p.Id,-6} {p.Name}  ({ex.Message})");
                }
            }

            return (ok, details.ToString());
        }

        /// <summary>
        /// Applica priorità a processi
        /// </summary>
        public (int ok, int failed, string details) SetProcessPriority(IEnumerable<ProcessInfo> processes, ProcessPriorityClass priority)
        {
            int ok = 0, fail = 0;
            var details = new StringBuilder();

            foreach (var p in processes)
            {
                try
                {
                    using var pr = Process.GetProcessById(p.Id);
                    pr.PriorityClass = priority;
                    ok++;
                    details.AppendLine($"OK   {p.Name} -> {priority}");
                }
                catch (Exception ex)
                {
                    fail++;
                    details.AppendLine($"FAIL {p.Name}  ({ex.Message})");
                }
            }

            return (ok, fail, details.ToString());
        }

        /// <summary>
        /// Uccide processi targeting (per Gaming Mode)
        /// </summary>
        public (int killed, string details) KillTargetProcesses(string[] targetNames)
        {
            int killed = 0;
            var details = new StringBuilder();

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName.ToLowerInvariant();
                    if (targetNames.Any(t => name.Contains(t)) &&
                        !AppConstants.CriticalProcesses.Contains(name))
                    {
                        details.AppendLine($"Kill {name} (PID {p.Id})");
                        p.Kill();
                        killed++;
                    }
                }
                catch (Exception ex)
                {
                    details.AppendLine($"FAIL {p.ProcessName}: {ex.Message}");
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            return (killed, details.ToString());
        }

        /// <summary>
        /// Svuota working set su tutti i processi
        /// </summary>
        public int FreeMemoryWorkingSet()
        {
            int count = 0;

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (Interop.EmptyWorkingSet(p.Handle))
                        count++;
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            return count;
        }

        /// <summary>
        /// Conta processi in esecuzione
        /// </summary>
        public int GetProcessCount()
        {
            try
            {
                return Process.GetProcesses().Length;
            }
            catch
            {
                return 0;
            }
        }
    }
}
