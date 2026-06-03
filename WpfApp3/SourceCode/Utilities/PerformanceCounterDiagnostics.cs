// =============================================================================
//  Albi Optimizer - PerformanceCounterDiagnostics.cs   (polished)
// -----------------------------------------------------------------------------
//  Strumento di diagnostica per individuare quale combinazione
//  (Category, Counter, Instance) di PerformanceCounter funziona sul sistema
//  corrente, utile per orientare il fallback in MainWindow.InitCpuCounter
//  quando le letture di CPU% o frequenza non sono affidabili.
//
//  Esegue tre test set:
//    * Enumerazione completa istanze/contatori delle categorie chiave.
//    * Frequenza CPU (3 strategie: modern PC counter, per-core, WMI).
//    * Utilizzo CPU% (3 strategie: % Processor Utility, % Processor Time, WMI).
//
//  Polish vs originale (behavior-preserving):
//    - Helper interni 'SafeTest' per eliminare boilerplate try/catch.
//    - Output formattato in modo coerente (header sezione + indent).
//    - using statement / Dispose garantito tramite 'using var'.
//    - CultureInfo.InvariantCulture nelle stampe numeriche.
//    - Sleep ridotti senza compromettere il sampling (warm-up + 100ms).
// =============================================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Threading;

namespace WpfApp3
{
    public static class PerformanceCounterDiagnostics
    {
        // ----------- Entry point ------------------------------------------------
        public static void ListAllCounters()
        {
            Console.WriteLine("===== CONTATORI DISPONIBILI =====\n");

            string[] categories = { "Processor", "Processor Information", "System", "Process" };
            foreach (var catName in categories) DumpCategory(catName);

            Console.WriteLine("\n\n===== TEST LETTURA FREQUENZA =====\n");
            TestFrequencyCounter();

            Console.WriteLine("\n\n===== TEST LETTURA CPU % =====\n");
            TestCpuUsageCounter();
        }

        // ----------- Enumerazione categoria -------------------------------------
        private static void DumpCategory(string catName)
        {
            try
            {
                var cat = new PerformanceCounterCategory(catName);

                Console.WriteLine($"\n[{catName}] Istanze:");
                foreach (var inst in cat.GetInstanceNames())
                    Console.WriteLine($"  - {inst}");

                Console.WriteLine("  Contatori:");
                foreach (var ctr in cat.GetCounters())
                    Console.WriteLine($"    - {ctr.CounterName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[{catName}] ERRORE: {ex.Message}");
            }
        }

        // ----------- Frequenza CPU ----------------------------------------------
        private static void TestFrequencyCounter()
        {
            SafeTestCounter(
                label: "Test 1: Processor Information -> Processor Frequency -> _Total",
                category: "Processor Information",
                counter: "Processor Frequency",
                instance: "_Total",
                unit: "MHz");

            SafeTestCounter(
                label: "Test 2: Processor Information -> Processor Frequency -> [core 0]",
                category: "Processor Information",
                counter: "Processor Frequency",
                instance: "0,0",
                unit: "MHz");

            SafeTestWmi(
                label: "Test 3: WMI -> Win32_Processor -> CurrentClockSpeed",
                query: "SELECT CurrentClockSpeed FROM Win32_Processor",
                key: "CurrentClockSpeed",
                unit: "MHz");
        }

        // ----------- Utilizzo CPU % --------------------------------------------
        private static void TestCpuUsageCounter()
        {
            SafeTestCounter(
                label: "Test 1: Processor Information -> % Processor Utility -> _Total",
                category: "Processor Information",
                counter: "% Processor Utility",
                instance: "_Total",
                unit: "%");

            SafeTestCounter(
                label: "Test 2: Processor -> % Processor Time -> _Total",
                category: "Processor",
                counter: "% Processor Time",
                instance: "_Total",
                unit: "%");

            SafeTestWmi(
                label: "Test 3: WMI -> Win32_Processor -> LoadPercentage",
                query: "SELECT LoadPercentage FROM Win32_Processor",
                key: "LoadPercentage",
                unit: "%");
        }

        // ----------- Helper testing --------------------------------------------
        private static void SafeTestCounter(
            string label, string category, string counter, string instance, string unit)
        {
            Console.WriteLine(label);
            try
            {
                using var pc = new PerformanceCounter(category, counter, instance, readOnly: true);
                // Doppio warm-up: la prima lettura ritorna sempre 0.
                pc.NextValue();
                Thread.Sleep(100);
                float val = pc.NextValue();
                Console.WriteLine(
                    $"  Risultato: {val.ToString("F2", CultureInfo.InvariantCulture)} {unit}\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERRORE: {ex.Message}\n");
            }
        }

        private static void SafeTestWmi(string label, string query, string key, string unit)
        {
            Console.WriteLine(label);
            try
            {
                using var s = new ManagementObjectSearcher(query);
                foreach (var o in s.Get())
                {
                    string? raw = o[key]?.ToString();
                    if (uint.TryParse(raw, NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out uint n))
                    {
                        Console.WriteLine($"  Risultato: {n} {unit}\n");
                    }
                    else
                    {
                        Console.WriteLine($"  Risultato non parsable: '{raw}'\n");
                    }
                    o.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERRORE: {ex.Message}\n");
            }
        }
    }
}
