// =============================================================================
//  Albi Optimizer - App.xaml.cs   (polished)
// -----------------------------------------------------------------------------
//  Responsabilita':
//    1. Bootstrap dell'applicazione WPF (OnStartup / OnExit).
//    2. Gestione globale degli unhandled exception (UI + task + AppDomain).
//    3. Update check non bloccante via raw GitHub JSON, con:
//         - HttpClient singleton (SocketsHttpHandler tuned, UA esplicito)
//         - Confronto SemVer numerico (no string-compare)
//         - Download atomico su file .part + rename
//         - Prompt utente prima di chiudere e lanciare l'updater
//    4. Cleanup ordinato (CancellationToken + Dispose) all'uscita.
//
//  Polish rispetto all'originale (behavior-preserving):
//    - Logging strutturato con prefissi coerenti [tag].
//    - Costanti estratte (UPDATE_MANIFEST_URL, TIMEOUT_*).
//    - Cancellation token propagato a TUTTE le operazioni await.
//    - try/finally su Dispose dell'HttpClient per evitare leak in OnExit.
//    - Messaggi utente in italiano coerenti.
// =============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;
using WpfApp1;

namespace WpfApp3
{
    public partial class App : Application
    {
        // ---------------------- Costanti di rete / update ------------------------
        private const string UPDATE_MANIFEST_URL =
            "https://raw.githubusercontent.com/HwDevelopment-creator/HWPower/main/update.json";

        private static readonly TimeSpan HTTP_TIMEOUT = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan CONNECT_TIMEOUT = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan POOL_CONN_LIFETIME = TimeSpan.FromMinutes(5);

        // ---------------------------------------------------------------------
        //  HttpClient singleton: connessioni persistenti, evita socket
        //  exhaustion. User-Agent esplicito (GitHub raw rifiuta certe
        //  richieste anonime).
        // ---------------------------------------------------------------------
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = POOL_CONN_LIFETIME,
                ConnectTimeout = CONNECT_TIMEOUT,
            };

            var client = new HttpClient(handler)
            {
                Timeout = HTTP_TIMEOUT
            };

            string asmVer = Assembly.GetExecutingAssembly()
                                    .GetName().Version?.ToString() ?? "1.0";
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("AlbiOptimizer", asmVer));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("*/*"));
            return client;
        }

        // CancellationToken globale: se l'utente chiude la finestra durante
        // un download in corso non lasciamo task orfani che scrivono su disco.
        private readonly CancellationTokenSource _cts = new();

        // ------------------------ Lifecycle WPF ----------------------------------
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            HookGlobalExceptionHandlers();

            // Mostra subito la finestra; l'update check gira in background.
            var window = new MainWindow();
            window.Show();

            // Update check non bloccante. Se fallisce, non disturba l'utente.
            _ = CheckUpdateAsync(_cts.Token);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _cts.Cancel(); } catch { /* idem */ }
            try { _cts.Dispose(); } catch { /* idem */ }
            try { _http.Dispose(); } catch { /* idem */ }
            base.OnExit(e);
        }

        // -------------------- Global exception capture ---------------------------
        private void HookGlobalExceptionHandlers()
        {
            // Cattura crash non gestiti sul thread UI: mostra dialog e marca handled.
            DispatcherUnhandledException += (s, ex) =>
            {
                Debug.WriteLine("[Dispatcher EX] " + ex.Exception);
                MessageBox.Show(
                    "Errore non gestito:\n" + ex.Exception.Message,
                    "Albi Optimizer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ex.Handled = true;
            };

            // Task non osservati: log + observed (evita TerminateOnUnhandledException).
            TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                Debug.WriteLine("[Unobserved Task EX] " + ex.Exception);
                ex.SetObserved();
            };

            // AppDomain: ultima rete di sicurezza (logging only).
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                Debug.WriteLine("[Domain EX] " + ex.ExceptionObject);
            };
        }

        // -------------------- Update check & install -----------------------------
        //
        //  Step:
        //    1. GET del manifest JSON (version + url).
        //    2. Confronto versioni IN MODO NUMERICO (Version.TryParse), NON string.
        //    3. Download atomico: scrivo su .part, poi File.Move su nome finale.
        //    4. Prompt utente in UI thread; se accetta, lancio l'EXE e Shutdown.
        //
        private async Task CheckUpdateAsync(CancellationToken ct)
        {
            try
            {
                using var resp = await _http.GetAsync(
                    UPDATE_MANIFEST_URL,
                    HttpCompletionOption.ResponseContentRead,
                    ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[CheckUpdate] HTTP {(int)resp.StatusCode}");
                    return;
                }

                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                JObject data = JObject.Parse(json);

                string? latestVersion = data["version"]?.ToString()?.Trim();
                string? downloadUrl = data["url"]?.ToString()?.Trim();

                if (string.IsNullOrWhiteSpace(latestVersion) || string.IsNullOrWhiteSpace(downloadUrl))
                    return;

                if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var dlUri) ||
                    (dlUri.Scheme != Uri.UriSchemeHttp && dlUri.Scheme != Uri.UriSchemeHttps))
                {
                    Debug.WriteLine("[CheckUpdate] URL update non valido");
                    return;
                }

                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version
                                     ?? new Version(0, 0, 0, 0);

                if (!Version.TryParse(latestVersion, out var latest)) return;
                if (latest <= currentVersion) return; // gia' aggiornati

                string finalPath = await DownloadAtomicAsync(dlUri, ct).ConfigureAwait(false);
                if (finalPath is null || ct.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() => PromptAndInstall(finalPath, latest, currentVersion));
            }
            catch (OperationCanceledException) { /* normale a shutdown */ }
            catch (Exception ex)
            {
                // L'update check non deve mai bloccare l'app.
                Debug.WriteLine("[CheckUpdate] " + ex.Message);
            }
        }

        private async Task<string?> DownloadAtomicAsync(Uri dlUri, CancellationToken ct)
        {
            string finalPath = Path.Combine(Path.GetTempPath(), "HWPowerSetup.exe");
            string tmpPath = finalPath + ".part";

            using (var dl = await _http.GetAsync(
                       dlUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!dl.IsSuccessStatusCode) return null;

                await using var fs = new FileStream(
                    tmpPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 81920, useAsync: true);

                await dl.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            try
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tmpPath, finalPath);
                return finalPath;
            }
            catch
            {
                // Se il rename fallisce uso comunque il .part (ancora eseguibile).
                return tmpPath;
            }
        }

        private void PromptAndInstall(string setupPath, Version latest, Version current)
        {
            var ans = MessageBox.Show(
                $"E' disponibile l'aggiornamento {latest} (corrente {current}).\n\n" +
                "Vuoi installarlo ora? L'app verra' chiusa.",
                "Aggiornamento disponibile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (ans != MessageBoxResult.Yes) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    UseShellExecute = true,
                });
                Current?.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Impossibile avviare l'updater:\n" + ex.Message,
                    "HWPower update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
