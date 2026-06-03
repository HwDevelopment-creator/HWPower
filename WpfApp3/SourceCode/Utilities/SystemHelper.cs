using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace WpfApp3.Utilities
{
    /// <summary>
    /// Helper per operazioni di sistema
    /// </summary>
    public static class SystemHelper
    {
        /// <summary>
        /// Formatta una velocità in byte/s a formato leggibile
        /// </summary>
        public static string HumanRate(double bps)
        {
            if (bps < 1024) return $"{bps:F0} B/s";
            if (bps < 1024 * 1024) return $"{bps / 1024d:F1} KB/s";
            if (bps < 1024d * 1024 * 1024) return $"{bps / (1024d * 1024):F2} MB/s";
            return $"{bps / (1024d * 1024 * 1024):F2} GB/s";
        }

        /// <summary>
        /// Formatta un TimeSpan come stringa leggibile di uptime
        /// </summary>
        public static string FormatUptime(TimeSpan t)
        {
            return t.TotalDays >= 1
                ? $"{(int)t.TotalDays}g {t.Hours}h {t.Minutes}m"
                : $"{t.Hours}h {t.Minutes}m";
        }

        /// <summary>
        /// Ottiene una descrizione breve del SO
        /// </summary>
        public static string BuildShortSysLine()
        {
            try
            {
                var os = GetOsPretty();
                return string.Format(CultureInfo.InvariantCulture,
                    "{0}  ·  {1} logical CPU  ·  Host: {2}",
                    os, Environment.ProcessorCount, Environment.MachineName);
            }
            catch
            {
                return Environment.OSVersion.ToString();
            }
        }

        /// <summary>
        /// Ottiene il nome leggibile del sistema operativo
        /// </summary>
        public static string GetOsPretty()
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
                foreach (ManagementObject o in s.Get())
                {
                    var caption = o["Caption"]?.ToString();
                    if (!string.IsNullOrEmpty(caption))
                    {
                        return caption.Replace("Microsoft ", "").Trim();
                    }
                }
            }
            catch { }
            return Environment.OSVersion.ToString();
        }

        /// <summary>
        /// Esegue un'azione in modo sicuro con gestione eccezioni
        /// </summary>
        public static void SafeRun(Action action, string context = "")
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{context}] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Esegue un'azione in modo sicuro con return type (reference type)
        /// </summary>
        public static T SafeGet<T>(Func<T> action, T defaultValue = default) where T : class
        {
            try
            {
                var result = action?.Invoke();
                return result ?? defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Esegue un'azione in modo sicuro con return type (value type)
        /// </summary>
        public static T SafeGetValue<T>(Func<T> action) where T : struct
        {
            try
            {
                return action?.Invoke() ?? default;
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// Controlla se un testo passa il filtro
        /// </summary>
        public static bool PassesFilter(string filter, params string[] fields)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            return fields.Any(f =>
                (f ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Converte byte a GB
        /// </summary>
        public static double BytesToGb(long bytes)
        {
            return bytes / (double)AppConstants.BYTES_PER_GB;
        }

        /// <summary>
        /// Converte byte a MB
        /// </summary>
        public static double BytesToMb(long bytes)
        {
            return bytes / (double)AppConstants.BYTES_PER_MB;
        }

        /// <summary>
        /// Converte byte a KB
        /// </summary>
        public static double BytesToKb(long bytes)
        {
            return bytes / (double)AppConstants.BYTES_PER_KB;
        }

        /// <summary>
        /// Ottiene la memoria disponibile del sistema
        /// </summary>
        public static (double usedGb, double totalGb, uint memoryLoadPercent) GetMemoryInfo()
        {
            try
            {
                var mem = new Interop.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Interop.MEMORYSTATUSEX>() };
                if (!Interop.GlobalMemoryStatusEx(ref mem))
                    return (0, 0, 0);

                double total = BytesToGb(((long)mem.ullTotalPhys));
                double available = BytesToGb(((long)mem.ullAvailPhys));
                double used = total - available;

                return (used, total, mem.dwMemoryLoad);
            }
            catch
            {
                return (0, 0, 0);
            }
        }

        /// <summary>
        /// Esegue una query WMI in modo sicuro
        /// </summary>
        public static T? SafeQueryWmi<T>(string query, Func<ManagementObject, T> selector, string scope = AppConstants.WMI_ROOT)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, query);
                using var results = searcher.Get();

                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        return selector(obj);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WMI Query Error] {query}: {ex.Message}");
            }

            return default;
        }

        /// <summary>
        /// Esegue una query WMI e colleziona tutti i risultati
        /// </summary>
        public static List<T> SafeQueryWmiMultiple<T>(string query, Func<ManagementObject, T> selector, string scope = AppConstants.WMI_ROOT)
        {
            var results = new List<T>();

            try
            {
                using var searcher = new ManagementObjectSearcher(scope, query);
                using var queryResults = searcher.Get();

                foreach (ManagementObject obj in queryResults)
                {
                    using (obj)
                    {
                        try
                        {
                            results.Add(selector(obj));
                        }
                        catch { /* Skip problematic entries */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WMI Query Error] {query}: {ex.Message}");
            }

            return results;
        }
    }
}
