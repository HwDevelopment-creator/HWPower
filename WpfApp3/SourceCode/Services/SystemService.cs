using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Threading;
using WpfApp3.Utilities;

namespace WpfApp3.Services
{
    /// <summary>
    /// Service per accesso a informazioni di sistema WMI, performance counter, etc.
    /// </summary>
    public class SystemService
    {
        private PerformanceCounter? _cpuCounter;
        private bool _cpuCounterReady;

        public bool IsCpuCounterReady => _cpuCounterReady;

        /// <summary>
        /// Inizializza il performance counter per CPU%
        /// </summary>
        public void InitializeCpuCounter()
        {
            // Prova ordinatamente diverse combinazioni
            if (TryInitCounter("Processor Information", "% Processor Utility", "_Total"))
                return;
            if (TryInitCounter("Processor", "% Processor Time", "_Total"))
                return;
            if (TryInitCounter("Processore", "% Tempo processore", "_Total"))
                return;

            _cpuCounterReady = false;
        }

        private bool TryInitCounter(string category, string counter, string instance)
        {
            try
            {
                _cpuCounter?.Dispose();
                _cpuCounter = null;

                var pc = new PerformanceCounter(category, counter, instance, readOnly: true);
                // Doppio warm-up: prima lettura ritorna sempre 0
                pc.NextValue();
                Thread.Sleep(200);
                pc.NextValue();
                Thread.Sleep(150);

                _cpuCounter = pc;
                _cpuCounterReady = true;
                return true;
            }
            catch
            {
                _cpuCounter?.Dispose();
                _cpuCounter = null;
                return false;
            }
        }

        /// <summary>
        /// Legge il CPU% dal counter o WMI fallback
        /// </summary>
        public double GetCpuPercentage()
        {
            try
            {
                if (_cpuCounterReady && _cpuCounter != null)
                {
                    float val = _cpuCounter.NextValue();
                    return Math.Max(0, Math.Min(100, val));
                }
            }
            catch { }

            // Fallback: calcolo manuale da Process.TotalProcessorTime
            return CalculateCpuManual();
        }

        /// <summary>
        /// Calcolo manuale della CPU% da processi (costoso, ma fallback)
        /// </summary>
        private double CalculateCpuManual()
        {
            try
            {
                var totalCpu = TimeSpan.Zero;
                var procs = Process.GetProcesses();

                foreach (var p in procs)
                {
                    try
                    {
                        totalCpu += p.TotalProcessorTime;
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                return 0; // Placeholder - il valore reale richiede campionamento
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Ottiene informazioni batteria
        /// </summary>
        public string GetBatteryInfo()
        {
            return SystemHelper.SafeQueryWmi(
                "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery",
                obj =>
                {
                    try
                    {
                        var pct = Convert.ToInt32(obj["EstimatedChargeRemaining"] ?? 0);
                        var st = Convert.ToInt32(obj["BatteryStatus"] ?? 0);
                        string mode = st == 2 ? " AC" : st == 1 ? " batt" : "";
                        return pct + "%" + mode;
                    }
                    catch
                    {
                        return "?";
                    }
                }) ?? "Desktop";
        }

        /// <summary>
        /// Ottiene temperatura CPU (da ACPI, non affidabile)
        /// </summary>
        public string GetCpuTemperature()
        {
            return SystemHelper.SafeQueryWmi(
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature",
                obj =>
                {
                    try
                    {
                        var raw = Convert.ToDouble(obj["CurrentTemperature"]);
                        if (raw <= 0) return null;
                        double celsius = (raw / 10d) - 273.15;
                        return $"{celsius:F0} °C";
                    }
                    catch
                    {
                        return null;
                    }
                },
                AppConstants.WMI_ROOT_WMI) ?? "n/d";
        }

        /// <summary>
        /// Ottiene nome e informazioni della GPU
        /// </summary>
        public string GetGpuInfo()
        {
            var names = SystemHelper.SafeQueryWmiMultiple(
                "SELECT Name FROM Win32_VideoController",
                obj =>
                {
                    try
                    {
                        return (obj["Name"]?.ToString() ?? "").Trim();
                    }
                    catch
                    {
                        return "";
                    }
                });

            if (names.Count == 0)
                return "--";

            return string.Join(", ", names
                .Where(n => !string.IsNullOrEmpty(n))
                .Take(2));
        }

        /// <summary>
        /// Ottiene nome della CPU
        /// </summary>
        public string GetCpuName()
        {
            return SystemHelper.SafeQueryWmi(
                "SELECT Name FROM Win32_Processor",
                obj => (obj["Name"]?.ToString() ?? "").Trim()) ?? "--";
        }

        /// <summary>
        /// Ottiene informazioni OS
        /// </summary>
        public string GetOsCaption()
        {
            return SystemHelper.SafeQueryWmi(
                "SELECT Caption FROM Win32_OperatingSystem",
                obj => (obj["Caption"]?.ToString() ?? "").Trim()) ?? Environment.OSVersion.ToString();
        }

        /// <summary>
        /// Pulisce le risorse
        /// </summary>
        public void Dispose()
        {
            _cpuCounter?.Dispose();
            _cpuCounter = null;
            _cpuCounterReady = false;
        }
    }
}
