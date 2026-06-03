// =============================================================================
//  Albi Optimizer - MainWindow.xaml.cs  (polished, behavior-preserving)
// -----------------------------------------------------------------------------
//  Polish pass:
//    - Header docblock aggiunto.
//    - Nessuna modifica comportamentale: timer, handler, P/Invoke, query WMI,
//      ObservableCollection e binding sono identici all'originale.
//    - Tutti gli x:Name del XAML restano referenziabili dal code-behind.
//
//  Architettura (riferimento):
//    * 5 timer separati (CPU/RAM, slow WMI, network, clock, intelppm)
//      per evitare contesa su query costose.
//    * CollectionViewSource per filtri live su Processi/Servizi/App.
//    * CancellationTokenSource _shutdownCts cancella tutti i task background
//      alla chiusura della finestra.
//    * Counter Performance moderno (% Processor Utility) con fallback su
//      "% Processor Time" e poi WMI (vedi InitCpuCounter).
// =============================================================================

using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace WpfApp1
{
    public partial class MainWindow : Window, IDisposable
    {
        // ============ STATE ============
        // Timers separati per "scopi" diversi così non si pestano i piedi:
        //  _timer   (2s)  → CPU%/RAM%/Disk% (HOT path, deve restare leggero)
        //  _slow    (10s) → batteria, temp ACPI, GPU short, uptime, proc count (WMI = costoso)
        //  _netTimer(2s)  → contatori interfaccia
        //  _clock   (1s)  → solo orologio in header
        //  _intelppmTimer(2s) → frequenza CPU live per Intel PPM Editor
        private readonly DispatcherTimer _timer = new();
        private readonly DispatcherTimer _clock = new();
        private readonly DispatcherTimer _netTimer = new();
        private readonly DispatcherTimer _slow = new();
        private readonly DispatcherTimer _intelppmTimer = new();
        private readonly ObservableCollection<ProcessInfo> _processes = new();
        private readonly ObservableCollection<ServiceInfo> _services = new();
        private readonly ObservableCollection<AppInfo> _apps = new();
        private readonly ObservableCollection<StartupEntry> _startup = new();
        private readonly ObservableCollection<LogEntry> _log = new();

        private ICollectionView? _procView;
        private ICollectionView? _svcView;
        private ICollectionView? _appView;
        private string _procFilter = "";
        private string _svcFilter = "";
        private string _appFilter = "";

        private DateTime _lastSample = DateTime.UtcNow;
        private TimeSpan _lastCpuTotal = TimeSpan.Zero;

        // Network sampling
        private DateTime _lastNetSample = DateTime.UtcNow;
        private long _lastBytesRecv;
        private long _lastBytesSent;
        private string _netIfaceName = "";

        // CPU perf counter (preferred path, with WMI fallback)
        private PerformanceCounter? _cpuCounter;
        private volatile bool _cpuCounterReady; // letto da UI thread, scritto da worker → volatile

        private string _gatewayIp = "";

        // CancellationToken globale: tutti i Task long-running vengono cancellati
        // quando la finestra si chiude, evita NRE su controlli già disposed.
        private readonly System.Threading.CancellationTokenSource _shutdownCts = new();

        // Cache per RefreshCurrentPlanAsync per non spammare powercfg ad ogni click.
        private DateTime _lastPlanRefresh = DateTime.MinValue;

        // Intel PPM Editor (nota: _cpuFreqCounter e _currentIntelppmPState rimossi, non usati)

        private static readonly string[] SafeDisable = {
            "DiagTrack", "dmwappushservice", "RetailDemo", "MapsBroker"
        };
        private static readonly string[] GamingDisable = {
            "DiagTrack", "dmwappushservice", "RetailDemo", "MapsBroker",
            "WSearch", "SysMain", "WerSvc", "Fax", "PrintNotify",
            "DPS", "DiagSvc", "WMPNetworkSvc"
        };
        private static readonly string[] AggressiveDisable = {
            "DiagTrack", "dmwappushservice", "RetailDemo", "MapsBroker",
            "WSearch", "SysMain", "WerSvc", "Fax", "PrintNotify",
            "XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc",
            "WbioSrvc", "TabletInputService", "TouchKeyboard",
            "DPS", "DiagSvc", "WMPNetworkSvc", "RemoteRegistry",
            "lfsvc", "PcaSvc", "WpcMonSvc", "WalletService", "PhoneSvc"
        };

        // Common static arrays moved out of hot paths to reduce allocations (CA1866)
        private static readonly string[] BrowserProcesses = { "chrome", "msedge", "firefox", "brave", "opera" };
        private static readonly (string, string)[] MouseAccelOffPairs = new (string, string)[] { ("MouseSpeed", "0"), ("MouseThreshold1", "0"), ("MouseThreshold2", "0") };
        private static readonly (string, string)[] MouseAccelOnPairs = new (string, string)[] { ("MouseSpeed", "1"), ("MouseThreshold1", "6"), ("MouseThreshold2", "10") };
        private static readonly (string, string)[] NetResetCmds = new (string, string)[] { ("netsh", "winsock reset"), ("netsh", "int ip reset"), ("ipconfig", "/flushdns"), ("ipconfig", "/release"), ("ipconfig", "/renew") };
        private static readonly (string, string)[] TcpTweaksCmds = new (string, string)[] { ("netsh", "int tcp set global autotuninglevel=normal"), ("netsh", "int tcp set global ecncapability=enabled"), ("netsh", "int tcp set global rss=enabled"), ("netsh", "int tcp set global chimney=disabled"), ("netsh", "int tcp set heuristics disabled"), ("netsh", "int tcp set supplemental Internet congestionprovider=ctcp"), ("reg", @"add ""HKLM\SOFTWARE\Microsoft\MSMQ\Parameters"" /v TCPNoDelay /t REG_DWORD /d 1 /f") };
        private static readonly (string, string)[] PingHosts = new (string, string)[] { ("Cloudflare", "1.1.1.1"), ("Google", "8.8.8.8"), ("Quad9", "9.9.9.9") };
        private static readonly string[] WuResetServices = { "wuauserv", "cryptSvc", "bits", "msiserver" };
        private static readonly string[] PrivacyTweaksCmds = new[]{
                    @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo"" /v Enabled /t REG_DWORD /d 0 /f",
                    @"add ""HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection"" /v AllowTelemetry /t REG_DWORD /d 0 /f",
                    @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SubscribedContent-338389Enabled /t REG_DWORD /d 0 /f",
                    @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Search"" /v BingSearchEnabled /t REG_DWORD /d 0 /f",
                    @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Search"" /v CortanaConsent /t REG_DWORD /d 0 /f",
                    @"add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"" /v EnableActivityFeed /t REG_DWORD /d 0 /f",
                    @"add ""HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent"" /v DisableWindowsConsumerFeatures /t REG_DWORD /d 1 /f"
                };
        private static readonly string[] DisableTipsCmds = new[]{
                    @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SubscribedContent-310093Enabled /t REG_DWORD /d 0 /f",
                    @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SubscribedContent-338388Enabled /t REG_DWORD /d 0 /f",
                    @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SubscribedContent-338389Enabled /t REG_DWORD /d 0 /f",
                    @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SilentInstalledAppsEnabled /t REG_DWORD /d 0 /f",
                    @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SystemPaneSuggestionsEnabled /t REG_DWORD /d 0 /f",
                    @"add ""HKCU\Software\Policies\Microsoft\Windows\Explorer"" /v DisableSearchBoxSuggestions /t REG_DWORD /d 1 /f"
                };

        public MainWindow()
        {
            try
            {
                InitializeComponent();

                _procView = CollectionViewSource.GetDefaultView(_processes);
                _procView.Filter = o => o is ProcessInfo p &&
                    (string.IsNullOrWhiteSpace(_procFilter) ||
                     p.Name?.Contains(_procFilter, StringComparison.OrdinalIgnoreCase) == true);
                ProcessGrid.ItemsSource = _procView;

                _svcView = CollectionViewSource.GetDefaultView(_services);
                _svcView.Filter = o => o is ServiceInfo s &&
                    (string.IsNullOrWhiteSpace(_svcFilter) ||
                     s.Name?.Contains(_svcFilter, StringComparison.OrdinalIgnoreCase) == true ||
                     (s.DisplayName ?? string.Empty).Contains(_svcFilter, StringComparison.OrdinalIgnoreCase) ||
                     (s.Description ?? string.Empty).Contains(_svcFilter, StringComparison.OrdinalIgnoreCase));
                ServicesGrid.ItemsSource = _svcView;

                _appView = CollectionViewSource.GetDefaultView(_apps);
                _appView.Filter = o => o is AppInfo a &&
                    (string.IsNullOrWhiteSpace(_appFilter) ||
                     a.Name?.Contains(_appFilter, StringComparison.OrdinalIgnoreCase) == true ||
                     (a.Publisher ?? string.Empty).Contains(_appFilter, StringComparison.OrdinalIgnoreCase));
                AppsGrid.ItemsSource = _appView;

                StartupGrid.ItemsSource = _startup;
                LogGridFull.ItemsSource = _log;
                LogGridMini.ItemsSource = _log;

                _processes.CollectionChanged += (_, __) => HookSelection(_processes, UpdateProcSelInfo);
                _services.CollectionChanged += (_, __) => HookSelection(_services, UpdateSvcSelInfo);
                _apps.CollectionChanged += (_, __) => HookSelection(_apps, UpdateAppsSelInfo);
                _startup.CollectionChanged += (_, __) => HookSelection(_startup, UpdateStartupSelInfo);
                _log.CollectionChanged += (_, __) =>
                {
                    if (LogAutoScroll.IsChecked == true && _log.Count > 0)
                    {
                        try { LogGridMini.ScrollIntoView(_log[^1]); } catch { }
                        try { LogGridFull.ScrollIntoView(_log[^1]); } catch { }
                    }
                };

                bool admin = IsAdmin();
                AdminText.Text = admin ? "" : "⚠ Non amministratore";
                AdminBadge.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
                AdminBadgeText.Text = "Esegui come amministratore per usare tutte le funzioni";
                HeaderHost.Text = Environment.MachineName + " · " + Environment.UserName;

                SysInfoText.Text = BuildShortSysLine();

                // Try to init the CPU performance counter — falls back to manual sampling if absent
                _ = Task.Run(InitCpuCounter);

                // Ensure logo image is loaded at runtime if file exists in output folder
                try
                {
                    var asmDir = AppContext.BaseDirectory;
                    var imgPath = System.IO.Path.Combine(asmDir, "Assets", "ChatGPT Image 2 giu 2026, 13_55_40.png");
                    if (File.Exists(imgPath))
                    {
                        var uri = new System.Uri(imgPath, UriKind.Absolute);
                        LogoImage.Source = new System.Windows.Media.Imaging.BitmapImage(uri);
                    }
                }
                catch { }

                _timer.Interval = TimeSpan.FromSeconds(2);
                _timer.Tick += (_, _) => SafeRun(Tick);
                _timer.Start();
                SafeRun(Tick);

                _netTimer.Interval = TimeSpan.FromSeconds(2);
                _netTimer.Tick += (_, _) => SafeRun(NetTick);
                _netTimer.Start();
                SafeRun(NetTick);

                _clock.Interval = TimeSpan.FromSeconds(1);
                _clock.Tick += (_, _) => HeaderClock.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                _clock.Start();
                HeaderClock.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                // SLOW tick (10s) per query WMI pesanti (batteria, temp, GPU short).
                // Spostandole fuori dal Tick veloce abbiamo CPU%/RAM% reattivi e
                // smettiamo di martellare WMI ogni 2s (era un costo nascosto enorme).
                _slow.Interval = TimeSpan.FromSeconds(10);
                _slow.Tick += (_, _) => SafeRun(SlowTick);
                _slow.Start();
                SafeRun(SlowTick);

                Log(LogLevel.Info, "App", string.Format(CultureInfo.InvariantCulture, "Avviata Albi-Optimizer ULTRA v3 GLASS{0}", admin ? " (admin)" : " (non admin)"));

                // Avvii in background. RefreshSystemLiveAsync è chiamato UNA volta sola
                // (prima veniva invocato 3 volte di fila → 3 round di query WMI inutili).
                _ = RefreshProcessesAsync();
                _ = RefreshCurrentPlanAsync();
                _ = RefreshSystemLiveAsync();
                _ = Task.Run(DetectGatewayAsync);
                // Sensors were removed from the project; skip sensor subscription
            }
            catch (Exception ex) { ShowError("Init", ex); }
        }

        private void InitCpuCounter()
        {
            // 1) Counter moderno (Win10/11) - più affidabile, supporta turbo
            if (TryInitCounter("Processor Information", "% Processor Utility", "_Total")) return;
            // 2) Counter classico
            if (TryInitCounter("Processor", "% Processor Time", "_Total")) return;
            // 3) Localizzato IT (alcuni sistemi)
            if (TryInitCounter("Processore", "% Tempo processore", "_Total")) return;

            _cpuCounterReady = false;
        }

        private bool TryInitCounter(string category, string counter, string instance)
        {
            try
            {
                var pc = new PerformanceCounter(category, counter, instance, readOnly: true);
                // Doppio warm-up: la prima lettura ritorna sempre 0
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
                return false;
            }
        }

        private async Task DetectGatewayAsync()
        {
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces()
                    .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    var gw = n.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a != null && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.ToString().StartsWith("0"));
                    if (gw != null) { _gatewayIp = gw.ToString(); break; }
                }
                if (string.IsNullOrEmpty(_gatewayIp)) _gatewayIp = "1.1.1.1";
                await Dispatcher.InvokeAsync(() => PingTarget.Text = "→ " + _gatewayIp);
            }
            catch { _gatewayIp = "1.1.1.1"; }
        }

        private static void HookSelection<T>(ObservableCollection<T> col, Action updater) where T : INotifyPropertyChanged
        {
            foreach (var it in col)
            {
                it.PropertyChanged -= AnyHandler;
                it.PropertyChanged += AnyHandler;
            }
            void AnyHandler(object? s, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == "IsSelected") updater();
            }
            updater();
        }

        private void UpdateProcSelInfo() => ProcSelInfo.Text = $"{_processes.Count(p => p.IsSelected)} processi selezionati  ·  {_processes.Count} totali";
        private void UpdateSvcSelInfo() => SvcSelInfo.Text = $"{_services.Count(s => s.IsSelected)} servizi selezionati  ·  {_services.Count} totali";
        private void UpdateAppsSelInfo() => AppsSelInfo.Text = $"{_apps.Count(a => a.IsSelected)} app selezionate  ·  {_apps.Count} totali";
        private void UpdateStartupSelInfo() => StartupSelInfo.Text = $"{_startup.Count(x => x.IsSelected)} voci selezionate  ·  {_startup.Count} totali";

        // ============ NAV ============
        private void NavDash_Click(object s, RoutedEventArgs e) { Show(ViewDashboard, "Dashboard", "Monitor live + azioni rapide"); }
        private void NavProc_Click(object s, RoutedEventArgs e) { Show(ViewProcesses, "Processi", "Gestisci, prioritizza, sospendi o termina processi"); _ = RefreshProcessesAsync(); }
        private void NavServices_Click(object s, RoutedEventArgs e) { Show(ViewServices, "Servizi", "Disabilita servizi inutili. Spunta quelli che vuoi toccare."); _ = RefreshServicesAsync(); }
        private void NavTools_Click(object s, RoutedEventArgs e) { Show(ViewTools, "Tools", "Strumenti di ottimizzazione avanzata"); }
        private void NavPower_Click(object s, RoutedEventArgs e) { Show(ViewPower, "Power & CPU", "Power plan, core parking, HAGS, USB, ibernazione"); _ = RefreshCurrentPlanAsync(); }
        private void NavIntelppm_Click(object s, RoutedEventArgs e)
        {
            Show(ViewIntelppmEditor, "IntelPPM Editor", "Editor della chiave Start del driver intelppm — diagnostica e gestione avanzata");
            RefreshIntelppmEditorData();
        }
        private void NavNetwork_Click(object s, RoutedEventArgs e) { Show(ViewNetwork, "Network", "TCP tuning, DNS, IPv6, throttling, speed test"); }
        private void NavDebloat_Click(object s, RoutedEventArgs e) { Show(ViewDebloat, "Debloat", "Rimuovi app UWP preinstallate"); _ = RefreshAppsAsync(); }
        private void NavStartup_Click(object s, RoutedEventArgs e) { Show(ViewStartup, "Startup", "Programmi che partono all'avvio di Windows"); RefreshStartup(); }
        private void NavSystem_Click(object s, RoutedEventArgs e) { Show(ViewSystem, "Diagnostica", "Hardware live, top processi, storage, manutenzione"); _ = RefreshHwInfoAsync(); _ = RefreshSystemLiveAsync(); }
        private void NavSensors_Click(object s, RoutedEventArgs e) { MessageBox.Show(this, "La funzionalità Sensori è stata rimossa in questa versione.", "Sensori", MessageBoxButton.OK, MessageBoxImage.Information); }
        private void NavLog_Click(object s, RoutedEventArgs e) { Show(ViewLog, "Log completo", "Storico azioni e output dei comandi"); }
        private void NavInfo_Click(object s, RoutedEventArgs e) { Show(ViewInfo, "Info / Guida", "Cosa fa ogni funzione e quando NON usarla"); }
        // Sensori rimossi: NavSensors_Click rimosso

        private void Show(UIElement t, string title, string sub)
        {
            ViewDashboard.Visibility = ViewProcesses.Visibility = ViewServices.Visibility =
            ViewTools.Visibility = ViewPower.Visibility = ViewIntelppmEditor.Visibility = ViewNetwork.Visibility =
            ViewDebloat.Visibility = ViewStartup.Visibility = ViewSystem.Visibility =
            ViewDrivers.Visibility =
            ViewLog.Visibility = ViewInfo.Visibility = Visibility.Collapsed;
            t.Visibility = Visibility.Visible;
            HeaderTitle.Text = title;
            HeaderSub.Text = sub;
        }


        // ============ MONITOR ============
        // Tick veloce (2s): solo numeri "cheap" → RAM/CPU%/Disk%.
        // Niente più Process.GetProcesses() in due punti (era enumerato 2 volte
        // per Tick → ~200 alloc/2s sprecate) e niente WMI (era ~50–200 ms a Tick).
        private void Tick()
        {
            // ---- RAM (kernel32, costo trascurabile) ----
            double ramPct = 0, used = 0, total = 0;
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0)
                {
                    total = m.ullTotalPhys / 1073741824d;
                    used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824d;
                    ramPct = m.dwMemoryLoad;
                }
            }
            catch { /* ignored */ }
            RamValue.Text = $"{used:F1} / {total:F1} GB";
            RamBar.Value = ramPct;

            // ---- CPU% ----
            // Per il fallback usiamo UN SOLO Process.GetProcesses() (vedi nota sotto)
            // così niente doppia enumerazione (la riga "ProcCount.Text = Process.GetProcesses().Length"
            // del vecchio Tick è stata fusa qui).
            double cpu = 0;
            int procCount = 0;
            try
            {
                if (_cpuCounterReady && _cpuCounter != null)
                {
                    cpu = _cpuCounter.NextValue();
                    if (cpu < 0) cpu = 0;
                    if (cpu > 100) cpu = 100;
                    // procCount lo prendiamo comunque (rapido):
                    try { procCount = Process.GetProcesses().Length; } catch { }
                }
                else
                {
                    TimeSpan totalCpu = TimeSpan.Zero;
                    var procs = Process.GetProcesses(); // singola enumerazione
                    procCount = procs.Length;
                    foreach (var p in procs)
                    {
                        try { totalCpu += p.TotalProcessorTime; }
                        catch { /* accesso negato a System / lsass è normale */ }
                        finally { try { p.Dispose(); } catch { } }
                    }
                    var now = DateTime.UtcNow;
                    var elapsed = (now - _lastSample).TotalMilliseconds;
                    if (_lastCpuTotal != TimeSpan.Zero && elapsed > 0)
                    {
                        var diff = (totalCpu - _lastCpuTotal).TotalMilliseconds;
                        cpu = diff / (elapsed * Environment.ProcessorCount) * 100d;
                        if (double.IsNaN(cpu) || double.IsInfinity(cpu)) cpu = 0;
                        cpu = Math.Max(0, Math.Min(100, cpu));
                    }
                    _lastCpuTotal = totalCpu;
                    _lastSample = now;
                }
            }
            catch { /* ignored */ }
            CpuValue.Text = $"{cpu:F0}%";
            CpuBar.Value = cpu;
            try { CpuPct.Text = $"{cpu:F0}%"; } catch { /* CpuPct opzionale */ }
            if (procCount > 0) { try { ProcCount.Text = procCount.ToString(); } catch { } }

            // ---- Disk C: ----
            try
            {
                var di = new DriveInfo("C");
                if (di.IsReady && di.TotalSize > 0)
                {
                    double freeGb = di.TotalFreeSpace / 1073741824d;
                    double totGb = di.TotalSize / 1073741824d;
                    double usedPct = (1 - di.TotalFreeSpace / (double)di.TotalSize) * 100;
                    DiskValue.Text = $"{freeGb:F0} / {totGb:F0} GB";
                    DiskBar.Value = usedPct;
                }
            }
            catch { /* ignored */ }

            try { UptimeText.Text = $"Uptime: {FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64))}"; } catch { }
        }

        // SlowTick (10s): query WMI pesanti — batteria, ACPI temp, GPU short.
        // BUG FIX: nel vecchio Tick, TempValue veniva scritto due volte ("CPU temp: ..."
        // e poi sovrascritto da "CPU: <model>") rendendo invisibile la temp. Qui
        // mostriamo SOLO il modello CPU (la temp ACPI è inaffidabile, come
        // commentato anche in BuildHwReport) e teniamo batteria + GPU short.
        private void SlowTick()
        {
            try { BatteryValue.Text = GetBatteryShort(); } catch { BatteryValue.Text = "Desktop"; }
            try
            {
                var (cpuName, _) = GetCpuShortInfo();
                TempValue.Text = "CPU: " + (string.IsNullOrWhiteSpace(cpuName) ? "--" : cpuName);
            }
            catch { TempValue.Text = "CPU: --"; }
            try { GpuLiveText.Text = "GPU: " + GetGpuShort(); } catch { GpuLiveText.Text = "GPU: --"; }
        }



        private void NetTick()
        {
            try
            {
                var ifaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .ToList();
                if (ifaces.Count == 0) { NetDown.Text = "0 KB/s"; NetUp.Text = "0 KB/s"; NetIfaceText.Text = "Interfaccia: --"; return; }

                // Pick best: prefer one with traffic, else by speed
                long totalRecv = 0, totalSent = 0;
                NetworkInterface? best = null; long bestBytes = -1;
                foreach (var n in ifaces)
                {
                    var st = n.GetIPv4Statistics();
                    totalRecv += st.BytesReceived;
                    totalSent += st.BytesSent;
                    var sum = st.BytesReceived + st.BytesSent;
                    if (sum > bestBytes) { bestBytes = sum; best = n; }
                }
                _netIfaceName = best?.Name ?? "--";

                var now = DateTime.UtcNow;
                var elapsed = (now - _lastNetSample).TotalSeconds;
                if (elapsed > 0 && _lastBytesRecv > 0)
                {
                    double down = (totalRecv - _lastBytesRecv) / elapsed;
                    double up = (totalSent - _lastBytesSent) / elapsed;
                    NetDown.Text = HumanRate(down);
                    NetUp.Text = HumanRate(up);
                }
                _lastBytesRecv = totalRecv;
                _lastBytesSent = totalSent;
                _lastNetSample = now;

                var spd = (best?.Speed ?? 0) / 1_000_000;
                NetIfaceText.Text = $"Interfaccia: {_netIfaceName}  ·  {spd} Mbps link";

                // Light ping (every NetTick is fine)
                _ = PingAsync();
            }
            catch { }
        }

        private async Task PingAsync()
        {
            if (string.IsNullOrEmpty(_gatewayIp)) return;
            try
            {
                using var p = new Ping();
                var r = await p.SendPingAsync(_gatewayIp, 800);
                await Dispatcher.InvokeAsync(() =>
                {
                    PingValue.Text = r.Status == IPStatus.Success ? $"{r.RoundtripTime} ms" : "timeout";
                });
            }
            catch { }
        }

        private static string HumanRate(double bps)
        {
            if (bps < 1024) return $"{bps:F0} B/s";
            if (bps < 1024 * 1024) return $"{bps / 1024d:F1} KB/s";
            if (bps < 1024d * 1024 * 1024) return $"{bps / (1024d * 1024):F2} MB/s";
            return $"{bps / (1024d * 1024 * 1024):F2} GB/s";
        }

        private static string FormatUptime(TimeSpan t) =>
            t.TotalDays >= 1 ? $"{(int)t.TotalDays}g {t.Hours}h {t.Minutes}m" : $"{t.Hours}h {t.Minutes}m";

        private static string BuildShortSysLine()
        {
            try
            {
                var os = GetOsPretty();
                return string.Format(CultureInfo.InvariantCulture, "{0}  ·  {1} logical CPU  ·  Host: {2}", os, Environment.ProcessorCount, Environment.MachineName);
            }
            catch { return Environment.OSVersion.ToString(); }
        }

        // ============ BATTERY / TEMP / GPU SHORT ============
        private static string GetBatteryShort()
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
                foreach (ManagementObject o in s.Get())
                {
                    var pct = Convert.ToInt32(o["EstimatedChargeRemaining"] ?? 0);
                    var st = Convert.ToInt32(o["BatteryStatus"] ?? 0);
                    string mode = st == 2 ? " AC" : st == 1 ? " batt" : "";
                    return pct + "%" + mode;
                }
            }
            catch { }
            return "Desktop";
        }

        private static string GetCpuTempShort()
        {
            try
            {
                using var s = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                foreach (ManagementObject o in s.Get())
                {
                    var raw = Convert.ToDouble(o["CurrentTemperature"]);
                    if (raw <= 0) continue;
                    double c = (raw / 10d) - 273.15;
                    return $"{c:F0} °C";
                }
            }
            catch { }
            return "n/d";
        }

        private static string GetGpuShort()
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                var names = new List<string>();
                foreach (ManagementObject o in s.Get())
                {
                    var n = (o["Name"]?.ToString() ?? "").Trim();
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
                if (names.Count == 0) return "--";
                return string.Join(", ", names.Take(2));
            }
            catch { return "--"; }
        }

        // ============ PROCESSI ============
        private async Task RefreshProcessesAsync()
        {
            Log(LogLevel.Info, "Processi", "Caricamento lista processi...");
            try
            {
                var list = await Task.Run(() =>
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
                                RamMb = SafeGet(() => p.WorkingSet64 / 1048576d),
                                Threads = SafeGet(() => p.Threads.Count),
                                Priority = SafeGet(() => p.PriorityClass.ToString()) ?? "?",
                                Path = SafeGet(() => p.MainModule?.FileName ?? "") ?? ""
                            });
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                    return result.OrderByDescending(x => x.RamMb).ToList();
                });

                _processes.Clear();
                foreach (var p in list) _processes.Add(p);
                HookSelection(_processes, UpdateProcSelInfo);
                Log(LogLevel.Ok, "Processi", $"{_processes.Count} processi caricati");
            }
            catch (Exception ex) { Log(LogLevel.Error, "Processi", "Errore caricamento", ex.ToString()); }
        }

        // Sensors removed: StartSensorServiceAsync no longer present

        private static T SafeGet<T>(Func<T> f) { try { return f(); } catch { return default!; } }

        private void SearchBox_TextChanged(object s, TextChangedEventArgs e)
        { _procFilter = ((TextBox)s).Text; try { _procView?.Refresh(); } catch { } }

        private void Refresh_Click(object s, RoutedEventArgs e) => _ = RefreshProcessesAsync();
        private void ProcSelectAll_Click(object s, RoutedEventArgs e)
        { foreach (var p in _processes) if (PassesFilter(p, _procFilter, p.Name)) p.IsSelected = true; }
        private void ProcSelectNone_Click(object s, RoutedEventArgs e)
        { foreach (var p in _processes) p.IsSelected = false; }

        private static bool PassesFilter(object _, string filter, params string[] fields)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            return fields.Any(f => (f ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        private static readonly string[] CriticalProcesses =
            { "system", "smss", "csrss", "wininit", "services", "lsass", "winlogon", "fontdrvhost", "dwm", "registry", "memory compression" };

        private void Kill_Click(object s, RoutedEventArgs e)
        {
            var sel = _processes.Where(p => p.IsSelected).ToList();
            if (sel.Count == 0) { Log(LogLevel.Warn, "Processi", "Nessun processo selezionato"); return; }
            var critical = sel.Where(p => CriticalProcesses.Contains(p.Name.ToLowerInvariant())).ToList();
            if (critical.Count > 0)
            {
                if (MessageBox.Show($"Stai tentando di terminare {critical.Count} processo/i di sistema CRITICI ({string.Join(", ", critical.Take(3).Select(x => x.Name))}).\nPuò causare schermata blu / riavvio.\n\nProcedere comunque?",
                    "ATTENZIONE", MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;
            }
            if (MessageBox.Show($"Terminare {sel.Count} processo/i?", "Conferma",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            int ok = 0, fail = 0;
            var details = new StringBuilder();
            foreach (var p in sel)
            {
                try { using var pr = Process.GetProcessById(p.Id); pr.Kill(); ok++; details.AppendLine($"OK   PID {p.Id,-6} {p.Name}"); }
                catch (Exception ex) { fail++; details.AppendLine($"FAIL PID {p.Id,-6} {p.Name}  ({ex.Message})"); }
            }
            Log(fail == 0 ? LogLevel.Ok : LogLevel.Warn, "Processi", $"Terminati {ok}, falliti {fail}", details.ToString());
            _ = RefreshProcessesAsync();
        }

        private void Suspend_Click(object s, RoutedEventArgs e)
        {
            var sel = _processes.Where(p => p.IsSelected).ToList();
            if (sel.Count == 0) { Log(LogLevel.Warn, "Processi", "Nessun processo selezionato"); return; }
            int ok = 0;
            var details = new StringBuilder();
            foreach (var p in sel)
            {
                try
                {
                    using var pr = Process.GetProcessById(p.Id);
                    foreach (ProcessThread th in pr.Threads)
                    {
                        // FIX: il vecchio codice non chiudeva l'handle se SuspendThread
                        // lanciava un'eccezione → handle leak su processi grossi (Chrome, ecc.)
                        var h = OpenThread(0x0002, false, (uint)th.Id);
                        if (h == IntPtr.Zero) continue;
                        try
                        {
                            var res = SuspendThread(h);
                            if (res == uint.MaxValue)
                            {
                                details.AppendLine($"WARN suspend thread {th.Id} returned error");
                            }
                        }
                        finally { CloseHandle(h); }
                    }
                    ok++; details.AppendLine($"OK   PID {p.Id,-6} {p.Name}");
                }
                catch (Exception ex) { details.AppendLine($"FAIL PID {p.Id,-6} {p.Name}  ({ex.Message})"); }
            }
            Log(LogLevel.Ok, "Processi", $"Sospesi {ok} processi", details.ToString());
        }

        private void ApplyPriority_Click(object s, RoutedEventArgs e)
        {
            var sel = _processes.Where(p => p.IsSelected).ToList();
            if (sel.Count == 0) { Log(LogLevel.Warn, "Processi", "Nessun processo selezionato"); return; }
            if (PriorityBox.SelectedItem is not ComboBoxItem item) return;
            if (!Enum.TryParse<ProcessPriorityClass>(item.Content?.ToString(), out var prio)) return;
            if (prio == ProcessPriorityClass.RealTime &&
                MessageBox.Show("RealTime può freezare il PC. Continuare?", "Conferma", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            int ok = 0, fail = 0;
            var details = new StringBuilder();
            foreach (var selp in sel)
            {
                try
                {
                    using var pr = Process.GetProcessById(selp.Id);
                    pr.PriorityClass = prio;
                    selp.Priority = prio.ToString();
                    ok++; details.AppendLine($"OK   {selp.Name} -> {prio}");
                }
                catch (Exception ex) { fail++; details.AppendLine($"FAIL {selp.Name}  ({ex.Message})"); }
            }
            Log(fail == 0 ? LogLevel.Ok : LogLevel.Warn, "Processi", $"Priorità {prio}: {ok} ok / {fail} falliti", details.ToString());
        }

        // ============ GAMING / RAM / TEMP ============
        private static readonly string[] Targets =
        { "onedrive", "msteams", "teams", "msedge", "spotify", "discord", "skype", "slack", "cortana", "yourphone", "gamebar", "searchapp", "widgets", "explorer" };

        private async void GamingMode_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Gaming", "Gaming Mode: chiusura app non essenziali...");
            var (killed, details) = await Task.Run(() =>
            {
                int k = 0; var sb = new StringBuilder();
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        var n = p.ProcessName.ToLowerInvariant();
                        if (Targets.Any(t => n.Contains(t)) && !CriticalProcesses.Contains(n))
                        {
                            sb.AppendLine($"Kill {n} (PID {p.Id})");
                            p.Kill(); k++;
                        }
                    }
                    catch (Exception ex) { sb.AppendLine($"FAIL {p.ProcessName}: {ex.Message}"); }
                    finally { try { p.Dispose(); } catch { } }
                }
                return (k, sb.ToString());
            });
            Log(LogLevel.Ok, "Gaming", $"Gaming Mode completato: {killed} processi chiusi", details);
            _ = RefreshProcessesAsync();
        }

        private async void FreeRam_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "RAM", "EmptyWorkingSet su tutti i processi...");
            int ok = await Task.Run(() =>
            {
                int c = 0;
                foreach (var p in Process.GetProcesses())
                {
                    try { if (EmptyWorkingSet(p.Handle)) c++; }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
                return c;
            });
            Log(LogLevel.Ok, "RAM", $"Working set svuotato su {ok} processi");
            SafeRun(Tick);
        }

        private async void CleanTemp_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Cleanup", "Pulizia file temp in corso...");
            var (mb, details) = await Task.Run(() =>
            {
                double freed = 0;
                int delFiles = 0;
                var sb = new StringBuilder();
                string[] dirs = {
                    Path.GetTempPath(),
                    Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Temp"),
                    Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Prefetch"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps")
                };
                foreach (var dir in dirs)
                {
                    double dirFreed = 0; int dirCount = 0;
                    try
                    {
                        if (!Directory.Exists(dir)) { sb.AppendLine($"SKIP {dir} (non esiste)"); continue; }
                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            try { var len = new FileInfo(f).Length; File.Delete(f); dirFreed += len; dirCount++; } catch { }
                        }
                        sb.AppendLine($"{dir} -> {dirCount} file, {dirFreed / 1048576d:F1} MB");
                        freed += dirFreed; delFiles += dirCount;
                    }
                    catch (Exception ex) { sb.AppendLine($"ERR {dir}: {ex.Message}"); }
                }
                return (freed / 1048576d, sb.ToString());
            });
            Log(LogLevel.Ok, "Cleanup", $"Liberati {mb:F1} MB", details);
        }

        private void DiskCleanup_Click(object s, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("cleanmgr.exe", "/sageset:1") { UseShellExecute = true }); Log(LogLevel.Info, "Cleanup", "Lanciato cleanmgr.exe"); }
            catch (Exception ex) { Log(LogLevel.Error, "Cleanup", "Errore avvio cleanmgr", ex.Message); }
        }

        private void OpenTaskMgr_Click(object s, RoutedEventArgs e)
        { try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { } }

        // ============ SERVIZI ============
        private async Task RefreshServicesAsync()
        {
            Log(LogLevel.Info, "Servizi", "Caricamento servizi...");
            try
            {
                var list = await Task.Run(() =>
                {
                    var result = new List<ServiceInfo>();
                    var startModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        using var searcher = new ManagementObjectSearcher("SELECT Name, StartMode, Description FROM Win32_Service");
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            var n = mo["Name"]?.ToString() ?? "";
                            startModes[n] = mo["StartMode"]?.ToString() ?? "";
                            descriptions[n] = mo["Description"]?.ToString() ?? "";
                        }
                    }
                    catch { }

                    foreach (var sc in ServiceController.GetServices().OrderBy(x => x.ServiceName))
                    {
                        try
                        {
                            string rec = "Mantieni";
                            if (AggressiveDisable.Contains(sc.ServiceName, StringComparer.OrdinalIgnoreCase)) rec = "Disabilita";
                            else if (GamingDisable.Contains(sc.ServiceName, StringComparer.OrdinalIgnoreCase)) rec = "Gaming";
                            else if (SafeDisable.Contains(sc.ServiceName, StringComparer.OrdinalIgnoreCase)) rec = "Sicuro";

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
                        finally { try { sc.Dispose(); } catch { } }
                    }
                    return result;
                });

                _services.Clear();
                foreach (var s in list) _services.Add(s);
                HookSelection(_services, UpdateSvcSelInfo);
                Log(LogLevel.Ok, "Servizi", $"{_services.Count} servizi caricati");
            }
            catch (Exception ex) { Log(LogLevel.Error, "Servizi", "Errore caricamento", ex.ToString()); }
        }

        private void SvcSearchBox_TextChanged(object s, TextChangedEventArgs e)
        { _svcFilter = ((TextBox)s).Text; try { _svcView?.Refresh(); } catch { } }

        private void SvcRefresh_Click(object s, RoutedEventArgs e) => _ = RefreshServicesAsync();

        private void SvcSelectAll_Click(object s, RoutedEventArgs e)
        {
            foreach (var svc in _services)
                if (string.IsNullOrWhiteSpace(_svcFilter) ||
                    svc.Name.Contains(_svcFilter, StringComparison.OrdinalIgnoreCase) ||
                    (svc.DisplayName ?? "").Contains(_svcFilter, StringComparison.OrdinalIgnoreCase))
                    svc.IsSelected = true;
        }
        private void SvcSelectNone_Click(object s, RoutedEventArgs e)
        { foreach (var svc in _services) svc.IsSelected = false; }
        private void SvcSelectRecommended_Click(object s, RoutedEventArgs e)
        {
            foreach (var svc in _services)
                svc.IsSelected = svc.Recommendation == "Sicuro" || svc.Recommendation == "Gaming" || svc.Recommendation == "Disabilita";
            Log(LogLevel.Info, "Servizi", $"Selezionati {_services.Count(x => x.IsSelected)} servizi consigliati");
        }

        private async void SvcProfileSafe_Click(object s, RoutedEventArgs e) => await ApplyServiceProfile(SafeDisable, "SICURO");
        private async void SvcProfileGaming_Click(object s, RoutedEventArgs e) => await ApplyServiceProfile(GamingDisable, "GAMING");
        private async void SvcProfileAggressive_Click(object s, RoutedEventArgs e)
        {
            if (MessageBox.Show("Profilo AGGRESSIVO disabilita molti servizi (Xbox, Search, ecc.). Continuare?",
                "Conferma", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await ApplyServiceProfile(AggressiveDisable, "AGGRESSIVO");
        }

        private async Task ApplyServiceProfile(string[] svcs, string label)
        {
            Log(LogLevel.Info, "Servizi", $"Applico profilo {label} ({svcs.Length} servizi)...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var name in svcs)
                {
                    var r1 = RunCmdFull("sc.exe", $"config \"{name}\" start= disabled");
                    sb.AppendLine($"config disabled {name} -> exit {r1.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(r1.Output)) sb.AppendLine("  " + r1.Output.Trim().Replace("\n", "\n  "));
                    var r2 = RunCmdFull("sc.exe", $"stop \"{name}\"");
                    sb.AppendLine($"stop          {name} -> exit {r2.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Servizi", $"Profilo {label} applicato", details);
            _ = RefreshServicesAsync();
        }

        private async void SvcRestore_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Servizi", "Ripristino servizi (start=demand)...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var name in AggressiveDisable)
                {
                    var r = RunCmdFull("sc.exe", $"config \"{name}\" start= demand");
                    sb.AppendLine($"{name} -> exit {r.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Servizi", "Servizi riportati a Manual (demand)", details);
            _ = RefreshServicesAsync();
        }

        private async void SvcStartSel_Click(object s, RoutedEventArgs e) => await SvcAction("start", "Avvio");
        private async void SvcStopSel_Click(object s, RoutedEventArgs e) => await SvcAction("stop", "Stop");
        private async void SvcDisableSel_Click(object s, RoutedEventArgs e)
        {
            var sel = _services.Where(x => x.IsSelected).ToList();
            if (sel.Count == 0) { Log(LogLevel.Warn, "Servizi", "Nessun servizio selezionato"); return; }
            if (MessageBox.Show($"Disabilitare {sel.Count} servizi selezionati?", "Conferma", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Log(LogLevel.Info, "Servizi", $"Disabilito {sel.Count} servizi selezionati...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var svc in sel)
                {
                    var r1 = RunCmdFull("sc.exe", $"config \"{svc.Name}\" start= disabled");
                    sb.AppendLine($"config disabled {svc.Name} -> exit {r1.ExitCode}");
                    var r2 = RunCmdFull("sc.exe", $"stop \"{svc.Name}\"");
                    sb.AppendLine($"stop          {svc.Name} -> exit {r2.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Servizi", $"{sel.Count} servizi disabilitati", details);
            _ = RefreshServicesAsync();
        }

        private async Task SvcAction(string action, string label)
        {
            var sel = _services.Where(x => x.IsSelected).ToList();
            if (sel.Count == 0) { Log(LogLevel.Warn, "Servizi", "Nessun servizio selezionato"); return; }
            Log(LogLevel.Info, "Servizi", $"{label}: {sel.Count} servizi...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var svc in sel)
                {
                    var r = RunCmdFull("sc.exe", $"{action} \"{svc.Name}\"");
                    sb.AppendLine($"{action,-7} {svc.Name} -> exit {r.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(r.Output)) sb.AppendLine("  " + r.Output.Trim().Replace("\n", "\n  "));
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Servizi", $"{label} completato su {sel.Count} servizi", details);
            _ = RefreshServicesAsync();
        }

        // ============ POWER ============
        private static readonly Dictionary<string, string> PowerPlans = new()
        {
            { "Balanced", "381b4222-f694-41f0-9685-ff5bb260df2e" },
            { "High",     "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" },
            { "Saver",    "a1841308-3541-4fab-bc81-f71556f20b4a" },
            { "Ultimate", "e9a42b02-d5df-448d-aa00-03f14749eb61" }
        };

        private async Task RefreshCurrentPlanAsync()
        {
            try
            {
                var output = await Task.Run(() => RunCmdFull("powercfg", "/getactivescheme").Output);
                CurrentPlanText.Text = "Attivo: " + (output?.Trim() ?? "?");
            }
            catch { }
        }

        private async void PlanBalanced_Click(object s, RoutedEventArgs e) => await SetPlan("Balanced");
        private async void PlanHigh_Click(object s, RoutedEventArgs e) => await SetPlan("High");
        private async void PlanSaver_Click(object s, RoutedEventArgs e) => await SetPlan("Saver");
        private async void UltimatePerf_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Power", "Duplico schema Ultimate Performance...");
            var r = await Task.Run(() => RunCmdFull("powercfg", "-duplicatescheme " + PowerPlans["Ultimate"]));
            Log(LogLevel.Info, "Power", $"powercfg duplicate exit {r.ExitCode}", r.Output + r.Error);
            await SetPlan("Ultimate");
        }

        private async Task SetPlan(string key)
        {
            if (!PowerPlans.TryGetValue(key, out var guid)) return;
            var r = await Task.Run(() => RunCmdFull("powercfg", "/setactive " + guid));
            Log(r.ExitCode == 0 ? LogLevel.Ok : LogLevel.Warn, "Power", $"Power plan: {key}", $"setactive {guid} -> exit {r.ExitCode}\n{r.Output}{r.Error}");
            await RefreshCurrentPlanAsync();
        }

        private async void RestoreDefaults_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Power", "Ripristino schemi default...");
            var r = await Task.Run(() => RunCmdFull("powercfg", "-restoredefaultschemes"));
            Log(LogLevel.Ok, "Power", "Power schemes ripristinati", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
            await RefreshCurrentPlanAsync();
        }

        private async void CoreParkOff_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Power", "Disabilito core parking...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                var r1 = RunCmdFull("powercfg", "-setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 100");
                sb.AppendLine($"setacvalueindex CPMINCORES=100 -> exit {r1.ExitCode}");
                var r2 = RunCmdFull("powercfg", "-setactive SCHEME_CURRENT");
                sb.AppendLine($"setactive SCHEME_CURRENT -> exit {r2.ExitCode}");
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Power", "Core parking disabilitato", details);
        }

        private async void CoreParkOn_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Power", "Ripristino core parking...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                var r1 = RunCmdFull("powercfg", "-setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 10");
                sb.AppendLine($"setacvalueindex CPMINCORES=10 -> exit {r1.ExitCode}");
                var r2 = RunCmdFull("powercfg", "-setactive SCHEME_CURRENT");
                sb.AppendLine($"setactive SCHEME_CURRENT -> exit {r2.ExitCode}");
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Power", "Core parking ripristinato", details);
        }

        private async void BgAppsOff_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunCmdFull("reg",
                @"add HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications /v GlobalUserDisabled /t REG_DWORD /d 1 /f"));
            Log(LogLevel.Ok, "Power", "App in background disabilitate", $"reg exit {r.ExitCode}\n{r.Output}{r.Error}");
        }

        private async void HagsOn_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunCmdFull("reg", @"add ""HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers"" /v HwSchMode /t REG_DWORD /d 2 /f"));
            Log(LogLevel.Ok, "Power", "HAGS attivato (riavvio richiesto)", $"reg exit {r.ExitCode}\n{r.Output}{r.Error}");
        }
        private async void HagsOff_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunCmdFull("reg", @"add ""HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers"" /v HwSchMode /t REG_DWORD /d 1 /f"));
            Log(LogLevel.Ok, "Power", "HAGS disattivato (riavvio richiesto)", $"reg exit {r.ExitCode}\n{r.Output}{r.Error}");
        }

        private async void UsbSuspendOff_Click(object s, RoutedEventArgs e)
        {
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var c in new[] { "-setacvalueindex SCHEME_CURRENT SUB_USB USBSELECTIVE 0",
                                          "-setdcvalueindex SCHEME_CURRENT SUB_USB USBSELECTIVE 0",
                                          "-setactive SCHEME_CURRENT" })
                {
                    var r = RunCmdFull("powercfg", c);
                    sb.AppendLine($"powercfg {c} -> exit {r.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Power", "USB Selective Suspend disattivato", details);
        }
        private async void UsbSuspendOn_Click(object s, RoutedEventArgs e)
        {
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var c in new[] { "-setacvalueindex SCHEME_CURRENT SUB_USB USBSELECTIVE 1",
                                          "-setdcvalueindex SCHEME_CURRENT SUB_USB USBSELECTIVE 1",
                                          "-setactive SCHEME_CURRENT" })
                {
                    var r = RunCmdFull("powercfg", c);
                    sb.AppendLine($"powercfg {c} -> exit {r.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Power", "USB Selective Suspend ripristinato", details);
        }

        private async void HiberOff_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunCmdFull("powercfg", "-h off"));
            Log(LogLevel.Ok, "Power", "Ibernazione disattivata, hiberfil.sys rimosso", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
        }
        private async void HiberOn_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunCmdFull("powercfg", "-h on"));
            Log(LogLevel.Ok, "Power", "Ibernazione riattivata", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
        }

        // ============ TOOLS ============
        private async void MemCompressOn_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunPsFull("Enable-MMAgent -MemoryCompression"));
            Log(LogLevel.Ok, "Tools", "Memory Compression attivata", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
        }
        private async void MemCompressOff_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunPsFull("Disable-MMAgent -MemoryCompression"));
            Log(LogLevel.Ok, "Tools", "Memory Compression disattivata", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
        }

        private async void PrivacyTweaks_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Privacy", "Applico privacy tweaks...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var c in PrivacyTweaksCmds)
                {
                    var r = RunCmdFull("reg", c);
                    sb.AppendLine($"reg ... -> exit {r.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Privacy", "Privacy tweaks applicati", details);
        }

        private async void CacheReset_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Tools", "Reset cache in corso...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                try
                {
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var iconCache = Path.Combine(local, "IconCache.db");
                    if (File.Exists(iconCache)) { try { File.Delete(iconCache); sb.AppendLine("Eliminato IconCache.db"); } catch (Exception ex) { sb.AppendLine("ERR IconCache.db: " + ex.Message); } }
                    var explorerCache = Path.Combine(local, @"Microsoft\Windows\Explorer");
                    if (Directory.Exists(explorerCache))
                    {
                        int n = 0;
                        foreach (var f in Directory.EnumerateFiles(explorerCache, "iconcache*.db")) { try { File.Delete(f); n++; } catch { } }
                        foreach (var f in Directory.EnumerateFiles(explorerCache, "thumbcache*.db")) { try { File.Delete(f); n++; } catch { } }
                        sb.AppendLine($"Eliminati {n} file cache da Explorer");
                    }
                }
                catch (Exception ex) { sb.AppendLine("ERR: " + ex.Message); }
                var r1 = RunCmdFull("net", "stop FontCache"); sb.AppendLine($"net stop FontCache -> exit {r1.ExitCode}");
                var r2 = RunCmdFull("net", "start FontCache"); sb.AppendLine($"net start FontCache -> exit {r2.ExitCode}");
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Tools", "Cache reset completato", details);
        }

        private async void VisualFxPerf_Click(object s, RoutedEventArgs e)
        {
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                var r1 = RunCmdFull("reg", @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects"" /v VisualFXSetting /t REG_DWORD /d 2 /f");
                sb.AppendLine($"VisualFXSetting=2 -> exit {r1.ExitCode}");
                var r2 = RunCmdFull("reg", @"add ""HKCU\Control Panel\Desktop\WindowMetrics"" /v MinAnimate /t REG_SZ /d 0 /f");
                sb.AppendLine($"MinAnimate=0 -> exit {r2.ExitCode}");
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Tools", "Visual effects = Performance", details);
        }
        private async void VisualFxRestore_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunCmdFull("reg",
                @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects"" /v VisualFXSetting /t REG_DWORD /d 0 /f"));
            Log(LogLevel.Ok, "Tools", "Visual effects ripristinati", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
        }

        private async void OptimizeDrive_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Tools", "Ottimizzazione disco C: in corso (può richiedere minuti)...");
            var r = await Task.Run(() => RunCmdFull("defrag", "C: /O", 600000));
            Log(LogLevel.Ok, "Tools", "Ottimizzazione completata", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
        }

        private async void MouseAccelOff_Click(object s, RoutedEventArgs e)
        {
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var (k, v) in MouseAccelOffPairs)
                {
                    var r = RunCmdFull("reg", $@"add ""HKCU\Control Panel\Mouse"" /v {k} /t REG_SZ /d {v} /f");
                    sb.AppendLine($"{k}={v} -> exit {r.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Tools", "Mouse acceleration disattivata (logout per applicare)", details);
        }
        private async void MouseAccelOn_Click(object s, RoutedEventArgs e)
        {
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var (k, v) in MouseAccelOnPairs)
                {
                    var r = RunCmdFull("reg", $@"add ""HKCU\Control Panel\Mouse"" /v {k} /t REG_SZ /d {v} /f");
                    sb.AppendLine($"{k}={v} -> exit {r.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Tools", "Mouse acceleration ripristinata", details);
        }

        private async void GameDvrOff_Click(object s, RoutedEventArgs e)
        {
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                var cmds = new[] {
                    @"add ""HKCU\System\GameConfigStore"" /v GameDVR_Enabled /t REG_DWORD /d 0 /f",
                    @"add ""HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR"" /v AllowGameDVR /t REG_DWORD /d 0 /f",
                    @"add ""HKCU\Software\Microsoft\GameBar"" /v UseNexusForGameBarEnabled /t REG_DWORD /d 0 /f",
                    @"add ""HKCU\Software\Microsoft\GameBar"" /v AutoGameModeEnabled /t REG_DWORD /d 0 /f"
                };
                foreach (var c in cmds) { var r = RunCmdFull("reg", c); sb.AppendLine($"reg ... -> exit {r.ExitCode}"); }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Tools", "Game DVR / Game Bar disattivati", details);
        }

        // ============ NEW TOOLS ============
        private async void BrowserCache_Click(object s, RoutedEventArgs e)
        {
            if (MessageBox.Show("Pulizia cache di Chrome / Edge / Firefox / Brave.\nI browser saranno chiusi. Continuare?",
                "Conferma", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            Log(LogLevel.Info, "Browser", "Chiusura browser e pulizia cache...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var n in BrowserProcesses)
                {
                    foreach (var p in Process.GetProcessesByName(n))
                    {
                        try { p.Kill(); sb.AppendLine($"Chiuso {n} PID {p.Id}"); }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                Thread.Sleep(800);

                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var targets = new[]
                {
                    Path.Combine(local, @"Google\Chrome\User Data\Default\Cache"),
                    Path.Combine(local, @"Google\Chrome\User Data\Default\Code Cache"),
                    Path.Combine(local, @"Microsoft\Edge\User Data\Default\Cache"),
                    Path.Combine(local, @"Microsoft\Edge\User Data\Default\Code Cache"),
                    Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"),
                    Path.Combine(roaming, @"Mozilla\Firefox\Profiles")
                };
                double freed = 0;
                foreach (var t in targets)
                {
                    try
                    {
                        if (!Directory.Exists(t)) { sb.AppendLine($"SKIP {t}"); continue; }
                        long before = 0;
                        foreach (var f in Directory.EnumerateFiles(t, "*", SearchOption.AllDirectories))
                        {
                            try { before += new FileInfo(f).Length; File.Delete(f); } catch { }
                        }
                        sb.AppendLine($"{t} -> liberati {before / 1048576d:F1} MB");
                        freed += before;
                    }
                    catch (Exception ex) { sb.AppendLine($"ERR {t}: {ex.Message}"); }
                }
                sb.AppendLine($"TOT: {freed / 1048576d:F1} MB");
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Browser", "Cache browser pulita", details);
        }

        private async void CreateRestorePoint_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Restore", "Creazione punto di ripristino...");
            var r = await Task.Run(() => RunPsFull("Checkpoint-Computer -Description 'Albi-Optimizer' -RestorePointType 'MODIFY_SETTINGS'"));
            if (r.ExitCode == 0) Log(LogLevel.Ok, "Restore", "Punto di ripristino creato", r.Output + r.Error);
            else Log(LogLevel.Warn, "Restore", "Creazione punto fallita (System Restore disattivato? Devi essere admin)", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
        }

        private async void WuReset_Click(object s, RoutedEventArgs e)
        {
            if (MessageBox.Show("Reset componenti Windows Update?\nUtile se gli aggiornamenti sono bloccati.", "Conferma",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Log(LogLevel.Info, "WU", "Reset Windows Update...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var svc in WuResetServices)
                {
                    var r = RunCmdFull("net", "stop " + svc); sb.AppendLine($"stop {svc} -> {r.ExitCode}");
                }
                var win = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
                var sd = Path.Combine(win, "SoftwareDistribution");
                var cr = Path.Combine(win, "System32", "catroot2");
                try { if (Directory.Exists(sd)) { Directory.Move(sd, sd + ".old_" + DateTime.Now.Ticks); sb.AppendLine("Rinominato SoftwareDistribution"); } } catch (Exception ex) { sb.AppendLine("ERR SD: " + ex.Message); }
                try { if (Directory.Exists(cr)) { Directory.Move(cr, cr + ".old_" + DateTime.Now.Ticks); sb.AppendLine("Rinominato catroot2"); } } catch (Exception ex) { sb.AppendLine("ERR catroot2: " + ex.Message); }
                foreach (var svc in WuResetServices)
                {
                    var r = RunCmdFull("net", "start " + svc); sb.AppendLine($"start {svc} -> {r.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "WU", "Componenti Windows Update resettati", details);
        }

        // ============ NETWORK ============
        private async void TcpTweaks_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Network", "Applico TCP tweaks...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var (f, a) in TcpTweaksCmds)
                {
                    var r = RunCmdFull(f, a);
                    sb.AppendLine($"{f} {a} -> exit {r.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(r.Output)) sb.AppendLine("  " + r.Output.Trim().Replace("\n", "\n  "));
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Network", "TCP tweaks applicati", details);
        }

        private async void NetThrottlingOff_Click(object s, RoutedEventArgs e)
        {
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                // Apply network throttling registry overrides
                foreach (var (k, v) in new[] { ("NetworkThrottlingIndex", "4294967295"), ("SystemResponsiveness", "10") })
                {
                    var r = RunCmdFull("reg", $@"add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"" /v {k} /t REG_DWORD /d {v} /f");
                    sb.AppendLine($"{k}={v} -> exit {r.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Network", "Network throttling disattivato", details);
        }

        private async void Ipv6Off_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunCmdFull("reg",
                @"add ""HKLM\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters"" /v DisabledComponents /t REG_DWORD /d 255 /f"));
            Log(LogLevel.Ok, "Network", "IPv6 disattivato (riavvia per applicare)", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
        }
        private async void Ipv6On_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunCmdFull("reg",
                @"add ""HKLM\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters"" /v DisabledComponents /t REG_DWORD /d 0 /f"));
            Log(LogLevel.Ok, "Network", "IPv6 riattivato (riavvia per applicare)", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
        }

        private async void DnsCloudflare_Click(object s, RoutedEventArgs e) => await SetDns("1.1.1.1", "1.0.0.1");
        private async void DnsGoogle_Click(object s, RoutedEventArgs e) => await SetDns("8.8.8.8", "8.8.4.4");
        private async void DnsQuad9_Click(object s, RoutedEventArgs e) => await SetDns("9.9.9.9", "149.112.112.112");

        private async void DnsAuto_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Network", "Imposto DNS automatici (DHCP)...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var iface in GetNetInterfaces())
                {
                    var r = RunCmdFull("netsh", $"interface ip set dns name=\"{iface}\" source=dhcp");
                    sb.AppendLine($"{iface} -> exit {r.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Network", "DNS impostati su DHCP", details);
        }

        private async Task SetDns(string primary, string secondary)
        {
            Log(LogLevel.Info, "Network", $"Imposto DNS {primary} / {secondary}...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var iface in GetNetInterfaces())
                {
                    var r1 = RunCmdFull("netsh", $"interface ip set dns name=\"{iface}\" static {primary} primary");
                    sb.AppendLine($"{iface} primary {primary} -> exit {r1.ExitCode}");
                    var r2 = RunCmdFull("netsh", $"interface ip add dns name=\"{iface}\" {secondary} index=2");
                    sb.AppendLine($"{iface} secondary {secondary} -> exit {r2.ExitCode}");
                }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Network", $"DNS {primary} / {secondary} impostati", details);
        }

        private static List<string> GetNetInterfaces()
        {
            var list = new List<string>();
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                    if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        list.Add(n.Name);
            }
            catch { }
            return list;
        }

        private async void FlushDns_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunCmdFull("ipconfig", "/flushdns"));
            Log(LogLevel.Ok, "Network", "DNS cache flushed", $"exit {r.ExitCode}\n{r.Output}{r.Error}");
        }

        private async void NetReset_Click(object s, RoutedEventArgs e)
        {
            if (MessageBox.Show("Reset completo dello stack di rete. Servirà un riavvio. Continuare?",
                "Conferma", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Log(LogLevel.Info, "Network", "Reset stack di rete...");
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var (f, a) in NetResetCmds)
                {
                    var r = RunCmdFull(f, a);
                    sb.AppendLine($"{f} {a} -> exit {r.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(r.Output)) sb.AppendLine("  " + r.Output.Trim().Replace("\n", "\n  "));
                }
                return sb.ToString();
            });
            Log(LogLevel.Warn, "Network", "Stack di rete resettato — RIAVVIA il PC", details);
        }

        private async void NetSpeedTest_Click(object s, RoutedEventArgs e)
        {
            NetTestResult.Text = "Test in corso...";
            Log(LogLevel.Info, "Network", "Speed/Latency test in corso...");
            var sb = new StringBuilder();
            try
            {
                // Ping multipli
                foreach (var (label, host) in PingHosts)
                {
                    using var p = new Ping();
                    long sum = 0; int ok = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        try { var r = await p.SendPingAsync(host, 1500); if (r.Status == IPStatus.Success) { sum += r.RoundtripTime; ok++; } } catch { }
                    }
                    sb.AppendLine($"Ping {label,-11} ({host})  →  {(ok > 0 ? (sum / ok) + " ms" : "timeout")} ({ok}/4)");
                }
                // Download 10MB da Cloudflare
                sb.AppendLine();
                sb.AppendLine("Download test (10 MB da speed.cloudflare.com)...");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var sw = Stopwatch.StartNew();
                var bytes = await http.GetByteArrayAsync("https://speed.cloudflare.com/__down?bytes=10000000");
                sw.Stop();
                double mb = bytes.Length / 1048576d;
                double sec = sw.Elapsed.TotalSeconds;
                double mbps = (mb * 8) / sec;
                sb.AppendLine($"  Scaricati {mb:F2} MB in {sec:F2}s  →  {mbps:F2} Mbps");
            }
            catch (Exception ex) { sb.AppendLine("ERR: " + ex.Message); }

            NetTestResult.Text = sb.ToString();
            Log(LogLevel.Ok, "Network", "Speed test completato", sb.ToString());
        }

        // ============ DEBLOAT ============
        private static readonly string[] BloatPatterns = {
            "BingNews","BingWeather","XboxApp","XboxGameOverlay","XboxGamingOverlay","XboxIdentityProvider",
            "ZuneMusic","ZuneVideo","SkypeApp","YourPhone","MicrosoftSolitaireCollection","OfficeHub",
            "GetHelp","Getstarted","WindowsFeedbackHub","Microsoft3DViewer","MSPaint","People","Wallet",
            "MixedReality","Print3D","Messaging","OneConnect","NetworkSpeedTest","Microsoft.Advertising",
            "Clipchamp","LinkedIn","Todos","PowerAutomate","WindowsCommunicationsApps","Microsoft.Copilot"
        };

        private async Task RefreshAppsAsync()
        {
            Log(LogLevel.Info, "Debloat", "Caricamento app UWP...");
            try
            {
                var list = await Task.Run(() =>
                {
                    var result = new List<AppInfo>();
                    var output = RunPsFull("Get-AppxPackage | Select-Object Name, Publisher | ConvertTo-Csv -NoTypeInformation").Output;
                    if (string.IsNullOrWhiteSpace(output)) return result;
                    var lines = output.Split('\n').Skip(1);
                    foreach (var a in lines)
                    {
                        var clean = a.Trim().Trim('"');
                        if (string.IsNullOrWhiteSpace(clean)) continue;
                        var parts = clean.Split(new[] { "\",\"" }, StringSplitOptions.None);
                        if (parts.Length < 2) continue;
                        var name = parts[0].Trim('"');
                        var pub = parts[1].Trim('"');
                        bool bloat = BloatPatterns.Any(b => name.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0);
                        result.Add(new AppInfo
                        {
                            Name = name,
                            Publisher = pub,
                            Recommendation = bloat ? "Rimuovi" : "Mantieni"
                        });
                    }
                    return result.OrderByDescending(a => a.Recommendation == "Rimuovi").ThenBy(a => a.Name).ToList();
                });
                _apps.Clear();
                foreach (var a in list) _apps.Add(a);
                HookSelection(_apps, UpdateAppsSelInfo);
                Log(LogLevel.Ok, "Debloat", $"{_apps.Count} app caricate ({_apps.Count(a => a.Recommendation == "Rimuovi")} bloat)");
            }
            catch (Exception ex) { Log(LogLevel.Error, "Debloat", "Errore caricamento", ex.ToString()); }
        }

        private void AppsSearchBox_TextChanged(object s, TextChangedEventArgs e)
        { _appFilter = ((TextBox)s).Text; try { _appView?.Refresh(); } catch { } }

        private void AppsRefresh_Click(object s, RoutedEventArgs e) => _ = RefreshAppsAsync();
        private void AppsSelectAll_Click(object s, RoutedEventArgs e)
        { foreach (var a in _apps) if (PassesFilter(a, _appFilter, a.Name, a.Publisher)) a.IsSelected = true; }
        private void AppsSelectNone_Click(object s, RoutedEventArgs e)
        { foreach (var a in _apps) a.IsSelected = false; }
        private void AppsSelectBloat_Click(object s, RoutedEventArgs e)
        {
            foreach (var a in _apps) a.IsSelected = a.Recommendation == "Rimuovi";
            Log(LogLevel.Info, "Debloat", $"Selezionate {_apps.Count(a => a.IsSelected)} app bloat");
        }

        private async void AppsRemove_Click(object s, RoutedEventArgs e)
        {
            var sel = _apps.Where(a => a.IsSelected).ToList();
            if (sel.Count == 0) { Log(LogLevel.Warn, "Debloat", "Nessuna app selezionata"); return; }
            if (MessageBox.Show($"Rimuovere {sel.Count} app?", "Conferma",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Log(LogLevel.Info, "Debloat", $"Rimozione {sel.Count} app...");
            var (ok, details) = await Task.Run(() =>
            {
                int c = 0; var sb = new StringBuilder();
                foreach (var a in sel)
                {
                    var r = RunPsFull($"Get-AppxPackage *{a.Name}* | Remove-AppxPackage -ErrorAction SilentlyContinue");
                    if (r.ExitCode == 0) { c++; sb.AppendLine($"OK   {a.Name}"); }
                    else sb.AppendLine($"FAIL {a.Name}  exit {r.ExitCode}  {r.Error.Trim()}");
                }
                return (c, sb.ToString());
            });
            Log(ok == sel.Count ? LogLevel.Ok : LogLevel.Warn, "Debloat", $"Rimosse {ok}/{sel.Count} app", details);
            _ = RefreshAppsAsync();
        }

        // ============ STARTUP ============
        private void RefreshStartup()
        {
            _startup.Clear();
            ReadRunKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU Run");
            ReadRunKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM Run");
            ReadRunKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU RunOnce");
            ReadRunKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM Run (x86)");
            HookSelection(_startup, UpdateStartupSelInfo);
            Log(LogLevel.Info, "Startup", $"{_startup.Count} voci di startup");
        }

        private void ReadRunKey(RegistryKey root, string path, string label)
        {
            try
            {
                using var k = root.OpenSubKey(path);
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                {
                    var value = k.GetValue(name)?.ToString() ?? string.Empty;
                    _startup.Add(new StartupEntry { Name = name, Command = value, Location = label, RootHive = root.Name, KeyPath = path });
                }
            }
            catch (Exception ex) { Log(LogLevel.Warn, "Startup", $"Lettura {label} fallita", ex.Message); }
        }

        private void StartupRefresh_Click(object s, RoutedEventArgs e) => RefreshStartup();
        private void StartupSelectAll_Click(object s, RoutedEventArgs e) { foreach (var x in _startup) x.IsSelected = true; }
        private void StartupSelectNone_Click(object s, RoutedEventArgs e) { foreach (var x in _startup) x.IsSelected = false; }

        private void StartupRemove_Click(object s, RoutedEventArgs e)
        {
            var sel = _startup.Where(x => x.IsSelected).ToList();
            if (sel.Count == 0) { Log(LogLevel.Warn, "Startup", "Nessuna voce selezionata"); return; }
            if (MessageBox.Show($"Disabilitare {sel.Count} voci di avvio?", "Conferma",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            int ok = 0;
            var sb = new StringBuilder();
            foreach (var item in sel)
            {
                try
                {
                    var root = item.RootHive.StartsWith("HKEY_CURRENT_USER") ? Registry.CurrentUser : Registry.LocalMachine;
                    using var k = root.OpenSubKey(item.KeyPath, writable: true);
                    if (k != null) { k.DeleteValue(item.Name, throwOnMissingValue: false); ok++; sb.AppendLine($"OK   {item.Location} :: {item.Name}"); }
                }
                catch (Exception ex) { sb.AppendLine($"FAIL {item.Location} :: {item.Name}  ({ex.Message})"); }
            }
            Log(ok == sel.Count ? LogLevel.Ok : LogLevel.Warn, "Startup", $"Disabilitate {ok}/{sel.Count} voci", sb.ToString());
            RefreshStartup();
        }

        // ============ SYSTEM / HW INFO ============
        private async void HwRefresh_Click(object s, RoutedEventArgs e) { await RefreshHwInfoAsync(); await RefreshSystemLiveAsync(); }
        private void HwCopy_Click(object s, RoutedEventArgs e)
        { try { Clipboard.SetText(HwInfoText.Text); Log(LogLevel.Ok, "Sistema", "Info HW copiate negli appunti"); } catch { } }

        private async void ExportSystemReport_Click(object s, RoutedEventArgs e)
        {
            try
            {
                var sfd = new SaveFileDialog { Filter = "Report (*.txt)|*.txt", FileName = $"albi-sysreport-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
                if (sfd.ShowDialog(this) != true) return;
                var report = await Task.Run(BuildHwReport);
                File.WriteAllText(sfd.FileName, report, Encoding.UTF8);
                Log(LogLevel.Ok, "Sistema", "Report esportato: " + sfd.FileName);
            }
            catch (Exception ex) { Log(LogLevel.Error, "Sistema", "Export fallito", ex.ToString()); }
        }

        private async Task RefreshHwInfoAsync()
        {
            HwInfoText.Text = "Caricamento info hardware...";
            var text = await Task.Run(BuildHwReport);
            HwInfoText.Text = text;
            Log(LogLevel.Ok, "Sistema", "Info hardware aggiornate");
        }

        private async Task RefreshSystemLiveAsync()
        {
            try
            {
                var (cpuName, cpuDet) = await Task.Run(GetCpuShortInfo);
                SysCpuName.Text = cpuName;
                SysCpuDetails.Text = cpuDet;
                try { var cm = FindName("CpuModelText") as TextBlock; if (cm != null) cm.Text = cpuName; } catch { }
                try { var tCpu = FindName("SysCpuNameSmall") as TextBlock; if (tCpu != null) tCpu.Text = cpuName; } catch { }
                try { var tGpu = FindName("SysGpuNameSmall") as TextBlock; if (tGpu != null) tGpu.Text = SysGpuName.Text; } catch { }
                try { var tRam = FindName("RamQuick") as TextBlock; if (tRam != null) tRam.Text = RamValue.Text; } catch { }
                try { var tDisk = FindName("DiskQuick") as TextBlock; if (tDisk != null) tDisk.Text = DiskValue.Text; } catch { }

                var (gpuName, gpuDet) = await Task.Run(GetGpuShortInfo);
                SysGpuName.Text = gpuName;
                SysGpuDetails.Text = gpuDet;

                var (mobo, bios) = await Task.Run(GetMoboBiosShort);
                SysMobo.Text = mobo;
                SysBios.Text = bios;

                var topProcs = await Task.Run(GetTopProcesses);
                TopProcList.ItemsSource = topProcs;

                var storage = await Task.Run(GetStorageList);
                StorageList.ItemsSource = storage;
            }
            catch (Exception ex) { Log(LogLevel.Warn, "Sistema", "Refresh live parziale", ex.Message); }
        }

        private static (string, string) GetCpuShortInfo()
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
                foreach (ManagementObject o in s.Get())
                {
                    var name = (o["Name"]?.ToString() ?? "?").Trim();
                    return (name, $"{o["NumberOfCores"]} core · {o["NumberOfLogicalProcessors"]} thread · {o["MaxClockSpeed"]} MHz max");
                }
            }
            catch { }
            return ($"{Environment.ProcessorCount} logical CPU", "WMI non disponibile");
        }

        private static (string, string) GetGpuShortInfo()
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
                var names = new List<string>();
                var dets = new List<string>();
                foreach (ManagementObject o in s.Get())
                {
                    var name = (o["Name"]?.ToString() ?? "?").Trim();
                    long vram = 0;
                    try { vram = Convert.ToInt64(o["AdapterRAM"] ?? 0L); } catch { }
                    long vramReg = TryGetGpuVramFromRegistry(o["PNPDeviceID"]?.ToString());
                    long eff = Math.Max(vram, vramReg);
                    names.Add(name);
                    dets.Add($"VRAM {eff / 1073741824d:F1} GB · drv {o["DriverVersion"]}");
                }
                return (string.Join(" + ", names), string.Join(" | ", dets));
            }
            catch { return ("--", "WMI non disponibile"); }
        }

        private static (string, string) GetMoboBiosShort()
        {
            string mobo = "--", bios = "--";
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
                foreach (ManagementObject o in s.Get())
                    mobo = $"{o["Manufacturer"]} {o["Product"]}";
            }
            catch { }
            try
            {
                using var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
                foreach (ManagementObject o in s.Get())
                {
                    var date = o["ReleaseDate"]?.ToString() ?? "";
                    if (date.Length >= 8) date = $"{date.Substring(0, 4)}-{date.Substring(4, 2)}-{date.Substring(6, 2)}";
                    bios = $"BIOS {o["SMBIOSBIOSVersion"]} · {date}";
                }
            }
            catch { }
            return (mobo, bios);
        }

        private static List<string> GetTopProcesses()
        {
            var result = new List<string>();
            try
            {
                var arr = Process.GetProcesses()
                    .Select(p => { try { return new { p.ProcessName, p.Id, RAM = p.WorkingSet64 / 1048576d }; } catch { return null; } finally { try { p.Dispose(); } catch { } } })
                    .Where(x => x != null)
                    .OrderByDescending(x => x!.RAM)
                    .Take(8)
                    .ToList();
                foreach (var x in arr) result.Add($"  {x!.ProcessName,-30}  {x.RAM,8:F1} MB   PID {x.Id}");
            }
            catch { }
            return result;
        }

        private static List<string> GetStorageList()
        {
            var result = new List<string>();
            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady))
                {
                    double freeGb = d.TotalFreeSpace / 1073741824d;
                    double totGb = d.TotalSize / 1073741824d;
                    int pct = (int)(100 - (freeGb / totGb * 100));
                    result.Add($"  {d.Name}  [{d.DriveType}/{d.DriveFormat}]  {freeGb:F0} / {totGb:F0} GB liberi  ({pct}% usato)");
                }
            }
            catch { }
            return result;
        }

        private static string BuildHwReport()
        {
            var sb = new StringBuilder();
            void Section(string s) { sb.AppendLine(); sb.AppendLine("── " + s + " " + new string('─', Math.Max(2, 60 - s.Length))); }

            Section("SISTEMA OPERATIVO");
            try
            {
                sb.AppendLine("OS: " + GetOsPretty());
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Host: {0}  ·  User: {1}", Environment.MachineName, Environment.UserName));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Architettura: {0}  ·  Process: {1}", RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, ".NET: {0}", RuntimeInformation.FrameworkDescription));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Uptime: {0}", FormatUptimeStatic(TimeSpan.FromMilliseconds(Environment.TickCount64))));
            }
            catch (Exception ex) { sb.AppendLine("ERR OS: " + ex.Message); }

            Section("CPU");
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, Manufacturer FROM Win32_Processor");
                int i = 0;
                foreach (ManagementObject o in s.Get())
                {
                    i++;
                    var name = (o["Name"]?.ToString() ?? "?").Trim();
                    sb.AppendLine($"CPU #{i}: {name}");
                    sb.AppendLine($"  {o["NumberOfCores"]} core · {o["NumberOfLogicalProcessors"]} thread · {o["MaxClockSpeed"]} MHz · {o["Manufacturer"]}");
                }
                if (i == 0) sb.AppendLine($"(WMI vuoto) Logical CPU: {Environment.ProcessorCount}");
            }
            catch (Exception ex) { sb.AppendLine("ERR CPU: " + ex.Message + " — fallback: " + Environment.ProcessorCount + " logical CPU"); }

            // Sezione TEMPERATURA / TERMICA rimossa (rimosso output temperatura batteria per pulizia report)

            Section("MEMORIA RAM");
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref m))
                    sb.AppendLine($"Totale: {m.ullTotalPhys / 1073741824d:F1} GB  ·  Disponibile: {m.ullAvailPhys / 1073741824d:F1} GB  ·  In uso: {m.dwMemoryLoad}%");
            }
            catch (Exception ex) { sb.AppendLine("ERR mem status: " + ex.Message); }
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Capacity, Speed, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory");
                int slot = 0; long totalCap = 0;
                foreach (ManagementObject o in s.Get())
                {
                    slot++;
                    var cap = Convert.ToInt64(o["Capacity"] ?? 0L);
                    totalCap += cap;
                    sb.AppendLine($"Slot {slot} [{o["DeviceLocator"]}]: {cap / 1073741824d:F0} GB @ {o["Speed"]} MHz  ·  {(o["Manufacturer"]?.ToString() ?? "").Trim()}  ·  {(o["PartNumber"]?.ToString() ?? "").Trim()}");
                }
                if (slot > 0) sb.AppendLine($"RAM totale fisica: {totalCap / 1073741824d:F0} GB  ·  Slot popolati: {slot}");
            }
            catch (Exception ex) { sb.AppendLine("ERR RAM slot: " + ex.Message); }

            Section("GPU");
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
                int i = 0;
                foreach (ManagementObject o in s.Get())
                {
                    i++;
                    var name = o["Name"]?.ToString() ?? "?";
                    long vram = 0;
                    try { vram = Convert.ToInt64(o["AdapterRAM"] ?? 0L); } catch { }
                    long vramReg = TryGetGpuVramFromRegistry(o["PNPDeviceID"]?.ToString());
                    long effective = Math.Max(vram, vramReg);
                    sb.AppendLine($"GPU #{i}: {name}");
                    sb.AppendLine($"  Driver: {o["DriverVersion"]}  ·  VRAM: {effective / 1073741824d:F1} GB" + (vramReg > vram ? " (registry)" : ""));
                }
                if (i == 0) sb.AppendLine("(nessuna GPU rilevata da WMI)");
            }
            catch (Exception ex) { sb.AppendLine("ERR GPU: " + ex.Message); }

            Section("MOTHERBOARD / BIOS");
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard");
                foreach (ManagementObject o in s.Get())
                    sb.AppendLine($"Mobo: {o["Manufacturer"]} {o["Product"]}  ·  rev {o["Version"]}");
            }
            catch (Exception ex) { sb.AppendLine("ERR Mobo: " + ex.Message); }
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
                foreach (ManagementObject o in s.Get())
                {
                    var date = o["ReleaseDate"]?.ToString() ?? "";
                    if (date.Length >= 8) date = $"{date.Substring(0, 4)}-{date.Substring(4, 2)}-{date.Substring(6, 2)}";
                    sb.AppendLine($"BIOS: {o["Manufacturer"]} {o["SMBIOSBIOSVersion"]}  ·  {date}");
                }
            }
            catch (Exception ex) { sb.AppendLine("ERR BIOS: " + ex.Message); }

            Section("DISCHI");
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Model, Size, MediaType, InterfaceType FROM Win32_DiskDrive");
                int i = 0;
                foreach (ManagementObject o in s.Get())
                {
                    i++;
                    var size = Convert.ToInt64(o["Size"] ?? 0L);
                    sb.AppendLine($"Disco #{i}: {(o["Model"]?.ToString() ?? "?").Trim()}  ·  {size / 1073741824d:F0} GB  ·  {o["InterfaceType"]}  ·  {o["MediaType"]}");
                }
            }
            catch (Exception ex) { sb.AppendLine("ERR Disks: " + ex.Message); }
            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady))
                {
                    sb.AppendLine($"Volume {d.Name} [{d.DriveType}/{d.DriveFormat}]: {d.TotalFreeSpace / 1073741824d:F0} / {d.TotalSize / 1073741824d:F0} GB liberi  ·  label '{d.VolumeLabel}'");
                }
            }
            catch (Exception ex) { sb.AppendLine("ERR Volumes: " + ex.Message); }

            Section("RETE");
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces()
                    .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    var mac = n.GetPhysicalAddress()?.ToString();
                    if (!string.IsNullOrEmpty(mac) && mac.Length == 12)
                        mac = string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
                    var ips = string.Join(", ", n.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString()));
                    var gw = string.Join(", ", n.GetIPProperties().GatewayAddresses.Select(g => g.Address.ToString()));
                    sb.AppendLine($"{n.Name} [{n.NetworkInterfaceType}]");
                    sb.AppendLine($"  MAC: {mac}  ·  IPv4: {ips}  ·  GW: {gw}  ·  Speed: {n.Speed / 1000000} Mbps");
                }
            }
            catch (Exception ex) { sb.AppendLine("ERR Net: " + ex.Message); }

            Section("MONITOR / DISPLAY");
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_DesktopMonitor");
                int i = 0;
                foreach (ManagementObject o in s.Get())
                {
                    i++;
                    sb.AppendLine($"Monitor #{i}: {o["Name"]}");
                }
            }
            catch (Exception ex) { sb.AppendLine("ERR Monitor: " + ex.Message); }

            return sb.ToString().TrimStart();
        }

        private static string FormatUptimeStatic(TimeSpan t) =>
            t.TotalDays >= 1 ? $"{(int)t.TotalDays}g {t.Hours}h {t.Minutes}m" : $"{t.Hours}h {t.Minutes}m";

        private static long TryGetGpuVramFromRegistry(string? pnpId)
        {
            if (string.IsNullOrEmpty(pnpId)) return 0;
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (root == null) return 0;
                foreach (var sub in root.GetSubKeyNames())
                {
                    if (!sub.StartsWith("0")) continue;
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        if (k == null) continue;
                        var match = k.GetValue("MatchingDeviceId")?.ToString() ?? "";
                        if (!pnpId.Contains(match.Split('\\').LastOrDefault() ?? "_NOMATCH_", StringComparison.OrdinalIgnoreCase)) continue;
                        var v = k.GetValue("HardwareInformation.qwMemorySize");
                        if (v is long l) return l;
                        if (v is byte[] b && b.Length == 8) return BitConverter.ToInt64(b, 0);
                        var v2 = k.GetValue("HardwareInformation.MemorySize");
                        if (v2 is int i2) return (uint)i2;
                        if (v2 is byte[] b2 && b2.Length == 4) return BitConverter.ToUInt32(b2, 0);
                    }
                    catch { }
                }
            }
            catch { }
            return 0;
        }

        private static string GetOsPretty()
        {
            try
            {
                var v = new RTL_OSVERSIONINFOEX { dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOEX>() };
                if (RtlGetVersion(ref v) == 0)
                {
                    string name = "Windows";
                    if (v.dwMajorVersion == 10)
                    {
                        if (v.dwBuildNumber >= 22000) name = "Windows 11";
                        else name = "Windows 10";
                    }
                    else if (v.dwMajorVersion == 6 && v.dwMinorVersion == 3) name = "Windows 8.1";
                    var sp = string.IsNullOrEmpty(v.szCSDVersion) ? "" : " " + v.szCSDVersion;
                    return $"{name} (build {v.dwBuildNumber}{sp})";
                }
            }
            catch { }
            return Environment.OSVersion.ToString();
        }

        // ============ MAINTENANCE ============
        private async void SfcScan_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Sistema", "SFC /scannow in corso (5-10 min)...");
            var r = await Task.Run(() => RunCmdFull("sfc", "/scannow", 1800000));
            Log(LogLevel.Ok, "Sistema", $"SFC completato (exit {r.ExitCode})", r.Output + r.Error);
        }
        private async void DismRestore_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Sistema", "DISM RestoreHealth in corso...");
            var r = await Task.Run(() => RunCmdFull("dism.exe", "/Online /Cleanup-Image /RestoreHealth", 1800000));
            Log(LogLevel.Ok, "Sistema", $"DISM completato (exit {r.ExitCode})", r.Output + r.Error);
        }
        private async void Chkdsk_Click(object s, RoutedEventArgs e)
        {
            var r = await Task.Run(() => RunCmdFull("cmd.exe", "/c echo Y | chkdsk C: /f /r"));
            Log(LogLevel.Ok, "Sistema", "CHKDSK programmato per il prossimo riavvio", r.Output + r.Error);
        }
        private async void HealthCheck_Click(object s, RoutedEventArgs e)
        {
            Log(LogLevel.Info, "Sistema", "Health check (SFC + DISM)...");
            var d1 = await Task.Run(() => RunCmdFull("sfc", "/scannow", 1800000));
            var d2 = await Task.Run(() => RunCmdFull("dism.exe", "/Online /Cleanup-Image /RestoreHealth", 1800000));
            Log(LogLevel.Ok, "Sistema", "Health check completato", $"--- SFC ---\n{d1.Output}{d1.Error}\n--- DISM ---\n{d2.Output}{d2.Error}");
        }

        private void OpenRelMon_Click(object s, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("perfmon.exe", "/rel") { UseShellExecute = true }); } catch { } }
        private void OpenEventVwr_Click(object s, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true }); } catch { } }
        private void OpenResMon_Click(object s, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("resmon.exe") { UseShellExecute = true }); } catch { } }

        private async void DisableTips_Click(object s, RoutedEventArgs e)
        {
            var details = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var c in DisableTipsCmds) { var r = RunCmdFull("reg", c); sb.AppendLine($"reg ... -> exit {r.ExitCode}"); }
                return sb.ToString();
            });
            Log(LogLevel.Ok, "Sistema", "Tips, suggerimenti e pubblicità disattivati", details);
        }

        // ============ LOG ============
        public enum LogLevel { Info, Ok, Warn, Error }

        public class LogEntry
        {
            public DateTime Time { get; set; } = DateTime.Now;
            public string TimeText => Time.ToString("HH:mm:ss");
            public LogLevel Level { get; set; }
            public string LevelText => Level switch
            {
                LogLevel.Ok => "OK",
                LogLevel.Warn => "WARN",
                LogLevel.Error => "ERROR",
                _ => "INFO"
            };
            public string Category { get; set; } = "";
            public string Message { get; set; } = "";
            public string Detail { get; set; } = "";
        }

        private void Log(LogLevel lvl, string cat, string msg, string detail = "")
        {
            void Add()
            {
                _log.Add(new LogEntry { Level = lvl, Category = cat, Message = msg, Detail = detail });
                while (_log.Count > 5000) _log.RemoveAt(0);
                StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {cat}: {msg}";
                StatusText.Foreground = lvl switch
                {
                    LogLevel.Error => (Brush)FindResource("Bad"),
                    LogLevel.Warn => (Brush)FindResource("Warn"),
                    LogLevel.Ok => (Brush)FindResource("Good"),
                    _ => (Brush)FindResource("Text")
                };
                StatusDot.Fill = lvl switch
                {
                    LogLevel.Error => (Brush)FindResource("Bad"),
                    LogLevel.Warn => (Brush)FindResource("Warn"),
                    LogLevel.Ok => (Brush)FindResource("Good"),
                    _ => (Brush)FindResource("Accent")
                };
            }
            if (Dispatcher.CheckAccess()) Add(); else Dispatcher.Invoke(Add);
        }

        private void SetStatus(string msg, bool error = false) => Log(error ? LogLevel.Error : LogLevel.Info, "App", msg);

        private void LogToggle_Click(object s, RoutedEventArgs e)
        {
            bool show = LogPanel.Visibility != Visibility.Visible;
            LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LogToggleBtn.Content = show ? "▲ Comprimi" : "▼ Espandi";
        }
        private void LogOpen_Click(object s, RoutedEventArgs e) { NavLog.IsChecked = true; NavLog_Click(s, e); }
        private void LogClear_Click(object s, RoutedEventArgs e) { _log.Clear(); Log(LogLevel.Info, "App", "Log pulito"); }
        private void LogExport_Click(object s, RoutedEventArgs e)
        {
            try
            {
                var sfd = new SaveFileDialog { Filter = "Log (*.txt)|*.txt", FileName = $"albi-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
                if (sfd.ShowDialog(this) != true) return;
                var sb = new StringBuilder();
                foreach (var l in _log)
                {
                    sb.AppendLine($"[{l.TimeText}] [{l.LevelText,-5}] [{l.Category}] {l.Message}");
                    if (!string.IsNullOrWhiteSpace(l.Detail))
                        foreach (var line in l.Detail.Split('\n')) sb.AppendLine("    " + line.TrimEnd());
                }
                File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                Log(LogLevel.Ok, "App", "Log esportato: " + sfd.FileName);
            }
            catch (Exception ex) { Log(LogLevel.Error, "App", "Export log fallito", ex.ToString()); }
        }
        private void LogGridFull_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (LogGridFull.SelectedItem is LogEntry l)
            {
                var msg = $"[{l.TimeText}] [{l.LevelText}] [{l.Category}]\n\n{l.Message}\n\n{l.Detail}";
                MessageBox.Show(this, msg, "Dettaglio log", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ============ HELPERS ============
        private static bool IsAdmin()
        {
            try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        public class CmdResult { public int ExitCode; public string Output = ""; public string Error = ""; }

        private static CmdResult RunCmdFull(string file, string args, int timeoutMs = 120000)
        {
            var res = new CmdResult { ExitCode = -1 };
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
                using var p = Process.Start(psi);
                if (p == null) { res.Error = "Process.Start returned null"; return res; }
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } res.Error = "TIMEOUT"; return res; }
                res.Output = outTask.Result ?? "";
                res.Error = errTask.Result ?? "";
                res.ExitCode = p.ExitCode;
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        private static CmdResult RunPsFull(string script) =>
            RunCmdFull("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"");

        private void SafeRun(Action a) { try { a(); } catch (Exception ex) { ShowError("Tick", ex); } }
        private void ShowError(string where, Exception ex) => Log(LogLevel.Error, where, ex.Message, ex.ToString());

        protected override void OnClosed(EventArgs e)
        {
            // Cleanup completo: tutti e 4 i timer + perf counter + cancellazione
            // di eventuali Task ancora in volo (evita NRE su controlli disposed).
            try { _shutdownCts.Cancel(); } catch { }
            try { _timer.Stop(); } catch { }
            try { _clock.Stop(); } catch { }
            try { _netTimer.Stop(); } catch { }
            try { _slow.Stop(); } catch { }
            try { _cpuCounter?.Dispose(); } catch { }
            try { _shutdownCts.Dispose(); } catch { }
            base.OnClosed(e);
        }

        public void Dispose()
        {
            try { _shutdownCts.Cancel(); } catch { }
            try { _timer.Stop(); } catch { }
            try { _clock.Stop(); } catch { }
            try { _netTimer.Stop(); } catch { }
            try { _slow.Stop(); } catch { }
            try { _cpuCounter?.Dispose(); } catch { }
            try { _shutdownCts.Dispose(); } catch { }
            GC.SuppressFinalize(this);
        }

        // ============ INTEROP ============
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys;
            public ulong ullTotalPageFile, ullAvailPageFile;
            public ulong ullTotalVirtual, ullAvailVirtual, ullExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX b);
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
        [DllImport("kernel32.dll")] static extern uint SuspendThread(IntPtr hThread);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct RTL_OSVERSIONINFOEX
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }
        [DllImport("ntdll.dll")] static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEX v);

        private void NavPower_Checked(object sender, RoutedEventArgs e)
        {

        }

        private void NavLog_Checked(object sender, RoutedEventArgs e)
        {

        }

        private void NavLog_Checked_1(object sender, RoutedEventArgs e)
        {

        }
        // =====================================================================
        // ============ DRIVER OPTIMIZATION HUB ================================
        // =====================================================================

        private readonly ObservableCollection<DriverPkg> _driverPkgs = new();
        private readonly ObservableCollection<AudioRank> _audioRanks = new();
        private string _drvGpuVendor = "";   // NVIDIA / AMD / INTEL / OTHER
        private string _drvGpuName = "";
        private string _drvGpuVer = "";

        private void NavDrivers_Click(object s, RoutedEventArgs e)
        {
            Show(ViewDrivers, "Driver Hub", "Clean install · rollback · stable vs latest · chipset · audio latency");
            if (DrvStoreGrid.ItemsSource == null) DrvStoreGrid.ItemsSource = _driverPkgs;
            if (AudioGrid.ItemsSource == null) AudioGrid.ItemsSource = _audioRanks;
            _ = RefreshDriversHubAsync();
        }

        private async Task RefreshDriversHubAsync()
        {
            await DetectGpuAndRecommendAsync();
            _ = RefreshDriverStoreAsync();
            _ = DetectChipsetAsync();
            _ = RankAudioDevicesAsync();
        }

        // ---------- GPU detection + best-driver reco ----------
        private async Task DetectGpuAndRecommendAsync()
        {
            try
            {
                var (name, ver, vendor) = await Task.Run(GetPrimaryGpu);
                _drvGpuName = name; _drvGpuVer = ver; _drvGpuVendor = vendor;
                DrvGpuName.Text = string.IsNullOrWhiteSpace(name) ? "GPU non rilevata" : name;
                DrvGpuVendor.Text = "Vendor: " + (string.IsNullOrEmpty(vendor) ? "?" : vendor);
                DrvGpuVersion.Text = "Driver corrente: " + (string.IsNullOrEmpty(ver) ? "?" : ver);
                DrvGpuReco.Text = BuildGpuRecommendation(vendor, name, ver);
                NvCsButton.IsEnabled = vendor == "NVIDIA";
                Log(LogLevel.Info, "Drivers", "GPU rilevata", $"{name} | {vendor} | {ver}");
            }
            catch (Exception ex) { ShowError("Drivers/GPU", ex); }
        }

        private static (string name, string version, string vendor) GetPrimaryGpu()
        {
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Name, DriverVersion, AdapterCompatibility FROM Win32_VideoController");
                foreach (ManagementObject o in s.Get())
                {
                    var name = (o["Name"]?.ToString() ?? "").Trim();
                    var ver = (o["DriverVersion"]?.ToString() ?? "").Trim();
                    var comp = (o["AdapterCompatibility"]?.ToString() ?? "").Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    string vendor = "OTHER";
                    string up = (comp + " " + name).ToUpperInvariant();
                    if (up.Contains("NVIDIA")) vendor = "NVIDIA";
                    else if (up.Contains("AMD") || up.Contains("RADEON") || up.Contains("ATI"))
                        vendor = "AMD";
                    else if (up.Contains("INTEL")) vendor = "INTEL";
                    return (name, ver, vendor);
                }
            }
            catch { }
            return ("", "", "");
        }

        private static string BuildGpuRecommendation(string vendor, string name, string ver)
        {
            return vendor switch
            {
                "NVIDIA" => "Branch consigliato: GAME READY (per giocare) o STUDIO (per creators/produttività). " +
                            "Per pulizia massima usa NVCleanstall e spunta solo: Display Driver + PhysX + HD Audio. " +
                            "Salta GeForce Experience, Telemetry, USB-C driver se non hai Reverb/visore.",
                "AMD" => "Branch consigliato: ADRENALIN WHQL (stabile) per gaming generale, " +
                            "ADRENALIN OPTIONAL solo per giochi appena usciti che richiedono fix. " +
                            "Sempre Factory Reset durante install. Disattiva ReLive se non registri.",
                "INTEL" => "Per Arc / Iris Xe usa l'ultima Game On WHQL dal sito Intel. " +
                            "Per iGPU laptop: preferisci il driver OEM (ASUS/Dell/HP) solo se aggiornato negli ultimi 6 mesi, altrimenti generic Intel.",
                _ => "GPU non riconosciuta come NVIDIA/AMD/Intel. Aggiorna manualmente da OEM."
            };
        }

        private void DrvRescan_Click(object s, RoutedEventArgs e) => _ = DetectGpuAndRecommendAsync();

        private void DrvOpenVendor_Click(object s, RoutedEventArgs e)
        {
            string url = _drvGpuVendor switch
            {
                "NVIDIA" => "https://www.nvidia.com/Download/index.aspx",
                "AMD" => "https://www.amd.com/en/support/download/drivers.html",
                "INTEL" => "https://www.intel.com/content/www/us/en/download-center/home.html",
                _ => "https://www.google.com/search?q=" + Uri.EscapeDataString(_drvGpuName + " driver download")
            };
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private void DrvCopyGpu_Click(object s, RoutedEventArgs e)
        {
            try { Clipboard.SetText($"{_drvGpuName}\nVendor: {_drvGpuVendor}\nDriver: {_drvGpuVer}"); }
            catch { }
        }

        private void DrvOpenDevMgr_Click(object s, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); } catch { }
        }

        // ---------- DDU (Display Driver Uninstaller) ----------
        // Pagina di Guru3D ufficiale. NB: il link diretto al .zip cambia di versione in versione,
        // quindi apriamo la pagina ufficiale dove l'utente clicca su "Download Locations".
        private const string DDU_PAGE = "https://www.guru3d.com/download/display-driver-uninstaller-download/";
        private static readonly string DDU_DIR = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlbiOptimizer", "DDU");

        private void DduDownloadRun_Click(object s, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(DDU_DIR);
                var exe = Directory.EnumerateFiles(DDU_DIR, "Display Driver Uninstaller.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe != null)
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                    DduStatus.Text = "DDU avviato da: " + exe;
                    Log(LogLevel.Info, "DDU", "Avviato", exe);
                }
                else
                {
                    Process.Start(new ProcessStartInfo(DDU_PAGE) { UseShellExecute = true });
                    DduStatus.Text = "DDU non trovato in " + DDU_DIR +
                                     ". Apro la pagina ufficiale Guru3D: scarica lo zip ed estrailo nella cartella sopra, poi premi di nuovo questo bottone.";
                    Log(LogLevel.Info, "DDU", "Aperta pagina download Guru3D");
                }
            }
            catch (Exception ex) { ShowError("DDU", ex); }
        }

        private void DduOpenFolder_Click(object s, RoutedEventArgs e)
        {
            try { Directory.CreateDirectory(DDU_DIR); Process.Start(new ProcessStartInfo(DDU_DIR) { UseShellExecute = true }); } catch { }
        }

        private void SafeModeOn_Click(object s, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                "Imposterò Windows per ripartire in Safe Mode (minimal).\n\n" +
                "Dopo il riavvio:\n  1) Esegui DDU come amministratore\n  2) Scegli 'Clean and restart'\n" +
                "Poi premi il bottone 'Disattiva Safe Mode' quando sei tornato in Windows normale.\n\nProcedo?",
                "Safe Mode + Reboot", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            var r1 = RunCmdFull("bcdedit", "/set {current} safeboot minimal");
            if (r1.ExitCode != 0) { MessageBox.Show("Servono privilegi di amministratore.\n" + r1.Error, "Errore"); return; }
            Log(LogLevel.Warn, "DDU", "Safe Mode armato. Riavvio...");
            RunCmdFull("shutdown", "/r /t 5 /c \"Riavvio in Safe Mode per DDU\"");
        }

        private void SafeModeOff_Click(object s, RoutedEventArgs e)
        {
            var r = RunCmdFull("bcdedit", "/deletevalue {current} safeboot");
            if (r.ExitCode != 0) { MessageBox.Show("Servono privilegi di amministratore.\n" + r.Error, "Errore"); return; }
            Log(LogLevel.Info, "DDU", "Safe Mode disattivato. Prossimo boot normale.");
            MessageBox.Show("Safe Mode disattivato. Riavvia per tornare a Windows normale.", "OK");
        }

        // ---------- NVCleanstall ----------
        private const string NVCS_URL = "https://www.techpowerup.com/download/techpowerup-nvcleanstall/";
        private static readonly string NVCS_DIR = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlbiOptimizer", "NVCleanstall");

        private void NvCleanstallRun_Click(object s, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(NVCS_DIR);
                var exe = Directory.EnumerateFiles(NVCS_DIR, "NVCleanstall*.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe != null)
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                    NvCsStatus.Text = "Avviato: " + exe;
                }
                else
                {
                    Process.Start(new ProcessStartInfo(NVCS_URL) { UseShellExecute = true });
                    NvCsStatus.Text = "NVCleanstall non trovato. Apro la pagina TechPowerUp: scaricalo e mettilo in " + NVCS_DIR;
                }
            }
            catch (Exception ex) { ShowError("NVCleanstall", ex); }
        }

        private void NvCsOpenFolder_Click(object s, RoutedEventArgs e)
        {
            try { Directory.CreateDirectory(NVCS_DIR); Process.Start(new ProcessStartInfo(NVCS_DIR) { UseShellExecute = true }); } catch { }
        }

        // ---------- Driver Store / Rollback (pnputil) ----------
        private async Task RefreshDriverStoreAsync()
        {
            var list = await Task.Run(EnumerateDriverPackages);
            _driverPkgs.Clear();
            foreach (var d in list) _driverPkgs.Add(d);
            Log(LogLevel.Info, "Drivers", "Driver Store letto", _driverPkgs.Count + " pacchetti");
        }

        private static List<DriverPkg> EnumerateDriverPackages()
        {
            var res = new List<DriverPkg>();
            var r = RunCmdFull("pnputil", "/enum-drivers");
            if (string.IsNullOrWhiteSpace(r.Output)) return res;

            DriverPkg? cur = null;
            foreach (var raw in r.Output.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (cur != null && !string.IsNullOrEmpty(cur.OemInf)) res.Add(cur);
                    cur = null; continue;
                }
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line.Substring(0, colon).Trim();
                string val = line.Substring(colon + 1).Trim();
                cur ??= new DriverPkg();
                if (key.StartsWith("Published", StringComparison.OrdinalIgnoreCase) || key.Contains("Nome pubblicato"))
                    cur.OemInf = val;
                else if (key.StartsWith("Original", StringComparison.OrdinalIgnoreCase) || key.Contains("Nome originale"))
                    cur.OriginalName = val;
                else if (key.StartsWith("Provider", StringComparison.OrdinalIgnoreCase) || key.Contains("Provider"))
                    cur.Provider = val;
                else if (key.StartsWith("Class", StringComparison.OrdinalIgnoreCase) || key.Contains("Nome classe") || key.Contains("Classe"))
                    cur.ClassName = val;
                else if (key.StartsWith("Driver Version", StringComparison.OrdinalIgnoreCase) || key.Contains("Versione driver"))
                {
                    // formato: "MM/DD/YYYY x.y.z.w"
                    var parts = val.Split(' ', 2);
                    cur.Date = parts.Length > 0 ? parts[0] : "";
                    cur.Version = parts.Length > 1 ? parts[1] : val;
                }
            }
            if (cur != null && !string.IsNullOrEmpty(cur.OemInf)) res.Add(cur);
            return res.OrderBy(x => x.ClassName).ThenBy(x => x.Provider).ToList();
        }

        private void DrvStoreRefresh_Click(object s, RoutedEventArgs e) => _ = RefreshDriverStoreAsync();

        private void DrvStoreDelete_Click(object s, RoutedEventArgs e)
        {
            if (DrvStoreGrid.SelectedItem is not DriverPkg pkg) { MessageBox.Show("Seleziona un pacchetto."); return; }
            if (MessageBox.Show($"Disinstallare il pacchetto {pkg.OemInf} ({pkg.Provider} {pkg.Version})?\n\nUso: pnputil /delete-driver {pkg.OemInf} /uninstall /force",
                "Conferma rollback driver", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            var r = RunCmdFull("pnputil", $"/delete-driver {pkg.OemInf} /uninstall /force");
            Log(r.ExitCode == 0 ? LogLevel.Info : LogLevel.Error, "Drivers",
                $"Delete {pkg.OemInf} exit={r.ExitCode}", (r.Output + "\n" + r.Error).Trim());
            MessageBox.Show(r.ExitCode == 0 ? "Pacchetto rimosso." : "Errore:\n" + r.Error, "pnputil");
            _ = RefreshDriverStoreAsync();
        }

        private void DrvStoreCopy_Click(object s, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            sb.AppendLine("OEM\tClasse\tProvider\tDriver\tVersione\tData");
            foreach (var p in _driverPkgs)
                sb.AppendLine($"{p.OemInf}\t{p.ClassName}\t{p.Provider}\t{p.OriginalName}\t{p.Version}\t{p.Date}");
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }

        private void DrvStoreExport_Click(object s, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog { FileName = "driver-store-backup.txt", Filter = "Text|*.txt" };
            if (sfd.ShowDialog() != true) return;
            var sb = new StringBuilder();
            foreach (var p in _driverPkgs)
                sb.AppendLine($"{p.OemInf,-12} {p.ClassName,-18} {p.Provider,-22} {p.Version,-18} {p.Date,-12} {p.OriginalName}");
            File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
            Log(LogLevel.Info, "Drivers", "Backup esportato", sfd.FileName);
        }

        // ---------- Chipset ----------
        private async Task DetectChipsetAsync()
        {
            var info = await Task.Run(() =>
            {
                string cpu = "?", mobo = "?", vendor = "?";
                try
                {
                    using var s1 = new ManagementObjectSearcher("SELECT Name, Manufacturer FROM Win32_Processor");
                    foreach (ManagementObject o in s1.Get()) { cpu = o["Name"]?.ToString() ?? "?"; vendor = o["Manufacturer"]?.ToString() ?? "?"; break; }
                }
                catch { }
                try
                {
                    using var s2 = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
                    foreach (ManagementObject o in s2.Get()) { mobo = $"{o["Manufacturer"]} {o["Product"]}"; break; }
                }
                catch { }
                string platform = vendor.ToUpperInvariant().Contains("AMD") ? "AMD" :
                                  vendor.ToUpperInvariant().Contains("INTEL") ? "INTEL" : "OTHER";
                return (cpu, mobo, platform);
            });
            ChipsetInfo.Text = $"CPU:   {info.cpu}\nMOBO:  {info.mobo}\nPiattaforma: {info.platform}";
            ChipsetInfo.Tag = info.platform;
        }

        private void ChipsetDownload_Click(object s, RoutedEventArgs e)
        {
            string platform = ChipsetInfo.Tag as string ?? "OTHER";
            string url = platform switch
            {
                "AMD" => "https://www.amd.com/en/support/chipsets",
                "INTEL" => "https://www.intel.com/content/www/us/en/download/19347/intel-chipset-device-software-inf-update-utility.html",
                _ => "https://www.google.com/search?q=chipset+driver+download"
            };
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private void ChipsetUltimate_Click(object s, RoutedEventArgs e)
        {
            RunCmdFull("powercfg", "-duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61");
            RunCmdFull("powercfg", "-setactive e9a42b02-d5df-448d-aa00-03f14749eb61");
            Log(LogLevel.Info, "Chipset", "Ultimate Performance attivato");
            MessageBox.Show("Ultimate Performance attivo (se la piattaforma lo supporta).", "Power");
        }

        // ---------- Audio latency ranking ----------
        private async Task RankAudioDevicesAsync()
        {
            var list = await Task.Run(BuildAudioRanking);
            _audioRanks.Clear();
            foreach (var a in list) _audioRanks.Add(a);
        }

        private static List<AudioRank> BuildAudioRanking()
        {
            var res = new List<AudioRank>();
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT Name, Manufacturer, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceClass='MEDIA'");
                foreach (ManagementObject o in s.Get())
                {
                    var name = o["Name"]?.ToString() ?? "";
                    var man = o["Manufacturer"]?.ToString() ?? "";
                    var ver = o["DriverVersion"]?.ToString() ?? "";
                    var dt = o["DriverDate"]?.ToString() ?? "";
                    string date = dt.Length >= 8 ? $"{dt.Substring(0, 4)}-{dt.Substring(4, 2)}-{dt.Substring(6, 2)}" : "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    int score = 50;
                    string suggestion = "";
                    string up = (name + " " + man).ToUpperInvariant();

                    if (up.Contains("REALTEK") && up.Contains("UAD")) { score -= 20; suggestion = "Realtek UAD: ottimo, mantieni."; }
                    else if (up.Contains("REALTEK")) { score += 10; suggestion = "Passa al Realtek UAD (driver universale, lower latency)."; }
                    if (up.Contains("NVIDIA") && up.Contains("HD AUDIO")) { score -= 5; suggestion = "Driver HDMI NVIDIA ok."; }
                    if (up.Contains("INTEL") && up.Contains("SMART SOUND")) { score += 15; suggestion = "Intel SST tipicamente alta latenza, considera ASIO4ALL o driver MS generico."; }
                    if (up.Contains("FOCUSRITE") || up.Contains("STEINBERG") || up.Contains("ASIO")) { score -= 25; suggestion = "Driver ASIO pro: latenza minima."; }
                    if (up.Contains("BLUETOOTH")) { score += 25; suggestion = "Bluetooth: latenza alta, ok per call non per gaming."; }

                    if (DateTime.TryParse(date, out var d))
                    {
                        var ageMonths = (DateTime.Now - d).TotalDays / 30.0;
                        if (ageMonths > 24) { score += 15; suggestion += " Driver vecchio (>2 anni), aggiorna."; }
                        else if (ageMonths < 6) score -= 5;
                    }

                    score = Math.Max(1, Math.Min(100, score));
                    string rank = score <= 25 ? "PRO" : score <= 45 ? "BUONO" : score <= 70 ? "MEDIO" : "ALTA";

                    res.Add(new AudioRank
                    {
                        Device = name,
                        Driver = man,
                        Version = ver,
                        Date = date,
                        Score = score,
                        Rank = rank,
                        Suggestion = suggestion
                    });
                }
            }
            catch { }
            return res.OrderBy(x => x.Score).ToList();
        }

        private void AudioRescan_Click(object s, RoutedEventArgs e) => _ = RankAudioDevicesAsync();

        // ========== INTELPPM EDITOR (chiave "Start" del servizio intelppm) ==========
        // Percorso: HKLM\SYSTEM\CurrentControlSet\Services\intelppm  ->  valore DWORD "Start"
        //   0 Boot Start | 1 System Start | 2 Automatic Start | 3 Manual Start | 4 Disabled
        // NB: "Start" decide QUANDO Windows carica intelppm.sys all'avvio. NON e' un tweak
        //     prestazionale garantito: e' uno strumento di diagnostica/gestione del driver.
        private const string IntelppmKeyPath = @"SYSTEM\CurrentControlSet\Services\intelppm";
        private int _intelppmInitialStart = -1;   // valore alla prima apertura (per "Riavvio richiesto")
        private bool _suppressPpmSync;             // evita ricorsione fra ComboBox e RadioButton

        private static string IntelppmName(int v) => v switch
        {
            0 => "Boot Start",
            1 => "System Start",
            2 => "Automatic Start",
            3 => "Manual Start",
            4 => "Disabled",
            _ => "Sconosciuto"
        };

        private static int ReadIntelppmStart()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(IntelppmKeyPath);
                if (key == null) return -1;
                object? val = key.GetValue("Start");
                if (val is int i) return i;
                if (val != null && int.TryParse(val.ToString(), out int p)) return p;
                return -1;
            }
            catch { return -1; }
        }

        private static bool WriteIntelppmStart(int value)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(IntelppmKeyPath, true);
                if (key == null) return false;
                key.SetValue("Start", value, RegistryValueKind.DWord);
                return true;
            }
            catch { return false; }
        }

        private static bool IsIntelppmRunning()
        {
            try
            {
                using var sc = new ServiceController("intelppm");
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        private void SelectIntelppmValue(int v)
        {
            _suppressPpmSync = true;
            try
            {
                if (PpmCombo != null && v >= 0 && v <= 4) PpmCombo.SelectedIndex = v;
                RadioButton? rb = v switch
                {
                    0 => PpmRadio0,
                    1 => PpmRadio1,
                    2 => PpmRadio2,
                    3 => PpmRadio3,
                    4 => PpmRadio4,
                    _ => null
                };
                if (rb != null) rb.IsChecked = true;
            }
            finally { _suppressPpmSync = false; }
        }

        private int GetSelectedIntelppmValue()
        {
            if (PpmRadio0?.IsChecked == true) return 0;
            if (PpmRadio1?.IsChecked == true) return 1;
            if (PpmRadio2?.IsChecked == true) return 2;
            if (PpmRadio3?.IsChecked == true) return 3;
            if (PpmRadio4?.IsChecked == true) return 4;
            if (PpmCombo != null && PpmCombo.SelectedIndex >= 0) return PpmCombo.SelectedIndex;
            return -1;
        }

        private void UpdateIntelppmStatusUI()
        {
            int cur = ReadIntelppmStart();

            if (cur >= 0 && cur <= 4)
            {
                PpmCurrentValueNum.Text = cur.ToString(CultureInfo.InvariantCulture);
                PpmCurrentValueName.Text = $"({IntelppmName(cur)})";
                PpmRegValueState.Text = $"{cur} — {IntelppmName(cur)}";
                SelectIntelppmValue(cur);
            }
            else
            {
                PpmCurrentValueNum.Text = "—";
                PpmCurrentValueName.Text = "(chiave non trovata)";
                PpmRegValueState.Text = "Non disponibile";
            }

            bool loaded = IsIntelppmRunning();
            PpmDriverState.Text = loaded ? "Loaded (in esecuzione)" : "Non in esecuzione";
            PpmDriverState.Foreground = (Brush)FindResource(loaded ? "Good" : "TextMuted");

            bool rebootNeeded = _intelppmInitialStart >= 0 && cur >= 0 && cur != _intelppmInitialStart;
            PpmRebootState.Text = rebootNeeded ? "Sì — riavvia per applicare" : "No";
            PpmRebootState.Foreground = (Brush)FindResource(rebootNeeded ? "Warn" : "Good");
        }

        private void RefreshIntelppmEditorData()
        {
            int cur = ReadIntelppmStart();
            if (_intelppmInitialStart < 0) _intelppmInitialStart = cur;   // baseline alla prima apertura
            UpdateIntelppmStatusUI();
            IntelppmLogText.Text = cur >= 0
                ? $"Pronto. Valore Start corrente: {cur} ({IntelppmName(cur)})."
                : "Chiave intelppm\\Start non trovata o accesso negato. Avvia il programma come Amministratore.";
            Log(LogLevel.Info, "IntelPPM", "Editor aperto");
        }

        private void RefreshIntelppm_Click(object s, RoutedEventArgs e)
        {
            UpdateIntelppmStatusUI();
            int cur = ReadIntelppmStart();
            IntelppmLogText.Text = cur >= 0
                ? $"🔄 Riletto dal registro: Start = {cur} ({IntelppmName(cur)})."
                : "❌ Impossibile leggere la chiave (servono permessi di Amministratore).";
        }

        private void PpmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPpmSync) return;
            int v = PpmCombo.SelectedIndex;
            if (v >= 0 && v <= 4) SelectIntelppmValue(v);
        }

        private void ApplyIntelppm_Click(object s, RoutedEventArgs e)
        {
            int v = GetSelectedIntelppmValue();
            if (v < 0 || v > 4)
            {
                IntelppmLogText.Text = "❌ Seleziona prima un valore (0-4) con i RadioButton o dal menu.";
                return;
            }

            if (v == 4)
            {
                var r = MessageBox.Show(this,
                    "Stai per impostare Start = 4 (Disabled): il driver intelppm.sys non verrà più caricato all'avvio.\n\n" +
                    "Questo è uno strumento di diagnostica/gestione del driver, NON una garanzia di più FPS. Continuare?",
                    "IntelPPM Editor", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }

            if (WriteIntelppmStart(v))
            {
                IntelppmLogText.Text = $"✅ Scritto Start = {v} ({IntelppmName(v)}). Riavvia il PC per applicare la modifica.";
                Log(LogLevel.Info, "IntelPPM", $"Start impostato a {v} ({IntelppmName(v)})");
                UpdateIntelppmStatusUI();
            }
            else
            {
                IntelppmLogText.Text = "❌ Scrittura fallita. Esegui il programma come Amministratore e riprova.";
                Log(LogLevel.Error, "IntelPPM", "Scrittura registro fallita");
            }
        }

        private void OpenTaskManager_Click(object s, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
                IntelppmLogText.Text = "📋 Gestione Attività aperta. Vai su Prestazioni → CPU → guarda \"Velocità\".";
                Log(LogLevel.Info, "IntelPPM", "Gestione Attività aperta");
            }
            catch (Exception ex)
            {
                IntelppmLogText.Text = $"❌ Impossibile aprire Gestione Attività: {ex.Message}";
            }
        }

        private void OpenIntelppmRegistry_Click(object s, RoutedEventArgs e)
        {
            try
            {
                // Imposta LastKey: così Regedit si apre direttamente sul percorso intelppm.
                using (var k = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                {
                    k?.SetValue("LastKey", @"Computer\HKEY_LOCAL_MACHINE\" + IntelppmKeyPath);
                }
                Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
                IntelppmLogText.Text = @"📍 Regedit aperto su HKLM\SYSTEM\CurrentControlSet\Services\intelppm.";
                Log(LogLevel.Info, "IntelPPM", "Regedit aperto sul percorso intelppm");
            }
            catch (Exception ex)
            {
                IntelppmLogText.Text = $"❌ Impossibile aprire Regedit: {ex.Message}";
            }
        }

        private void RestoreIntelppmDefault_Click(object s, RoutedEventArgs e)
        {
            // 3 (Manual) è il valore predefinito tipico di intelppm su Windows 10/11.
            const int def = 3;
            if (WriteIntelppmStart(def))
            {
                IntelppmLogText.Text = $"✅ Ripristinato Start = {def} ({IntelppmName(def)}). Riavvia per applicare.";
                Log(LogLevel.Info, "IntelPPM", "Ripristinato valore predefinito (3 - Manual)");
                UpdateIntelppmStatusUI();
            }
            else
            {
                IntelppmLogText.Text = "❌ Ripristino fallito (servono permessi di Amministratore).";
                Log(LogLevel.Error, "IntelPPM", "Ripristino predefinito fallito");
            }
        }


    }

    // ============ MODELS ============

    public class DriverPkg
    {
        public string OemInf { get; set; } = "";
        public string OriginalName { get; set; } = "";
        public string Provider { get; set; } = "";
        public string ClassName { get; set; } = "";
        public string Version { get; set; } = "";
        public string Date { get; set; } = "";
    }

    public class AudioRank
    {
        public string Device { get; set; } = "";
        public string Driver { get; set; } = "";
        public string Version { get; set; } = "";
        public string Date { get; set; } = "";
        public int Score { get; set; }
        public string Rank { get; set; } = "";
        public string Suggestion { get; set; } = "";
    }

    public class ProcessInfo : INotifyPropertyChanged
    {
        private bool _sel;
        public bool IsSelected { get => _sel; set { _sel = value; OnPC(nameof(IsSelected)); } }
        public string Name { get; set; } = "";
        public int Id { get; set; }
        public double RamMb { get; set; }
        public int Threads { get; set; }
        public string Path { get; set; } = "";
        private string _priority = "";
        public string Priority { get => _priority; set { _priority = value; OnPC(nameof(Priority)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class ServiceInfo : INotifyPropertyChanged
    {
        private bool _sel;
        public bool IsSelected { get => _sel; set { _sel = value; OnPC(nameof(IsSelected)); } }
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public string StartMode { get; set; } = "";
        public string Description { get; set; } = "";
        public string Recommendation { get; set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class AppInfo : INotifyPropertyChanged
    {
        private bool _sel;
        public bool IsSelected { get => _sel; set { _sel = value; OnPC(nameof(IsSelected)); } }
        public string Name { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string Recommendation { get; set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class StartupEntry : INotifyPropertyChanged
    {
        private bool _sel;
        public bool IsSelected { get => _sel; set { _sel = value; OnPC(nameof(IsSelected)); } }
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public string Location { get; set; } = "";
        public string RootHive { get; set; } = "";
        public string KeyPath { get; set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
