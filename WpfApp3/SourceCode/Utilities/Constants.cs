namespace WpfApp3.Utilities
{
    /// <summary>
    /// Costanti globali dell'applicazione
    /// </summary>
    public static class AppConstants
    {
        // ========== UPDATE CHECK ==========
        public const string UPDATE_MANIFEST_URL = "https://raw.githubusercontent.com/HwDevelopment-creator/HWPower/main/update.json";
        public static readonly TimeSpan HTTP_TIMEOUT = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan CONNECT_TIMEOUT = TimeSpan.FromSeconds(8);
        public static readonly TimeSpan POOL_CONN_LIFETIME = TimeSpan.FromMinutes(5);

        // ========== SERVICE PROFILES ==========
        public static readonly string[] SafeDisable = {
            "DiagTrack", "dmwappushservice", "RetailDemo", "MapsBroker"
        };

        public static readonly string[] GamingDisable = {
            "DiagTrack", "dmwappushservice", "RetailDemo", "MapsBroker",
            "WSearch", "SysMain", "WerSvc", "Fax", "PrintNotify",
            "DPS", "DiagSvc", "WMPNetworkSvc"
        };

        public static readonly string[] AggressiveDisable = {
            "DiagTrack", "dmwappushservice", "RetailDemo", "MapsBroker",
            "WSearch", "SysMain", "WerSvc", "Fax", "PrintNotify",
            "XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc",
            "WbioSrvc", "TabletInputService", "TouchKeyboard",
            "DPS", "DiagSvc", "WMPNetworkSvc", "RemoteRegistry",
            "lfsvc", "PcaSvc", "WpcMonSvc", "WalletService", "PhoneSvc"
        };

        // ========== PROCESS & BROWSER ==========
        public static readonly string[] BrowserProcesses = { "chrome", "msedge", "firefox", "brave", "opera" };

        public static readonly string[] CriticalProcesses = {
            "system", "smss", "csrss", "wininit", "services", "lsass", "winlogon", "fontdrvhost", "dwm", "registry", "memory compression"
        };

        public static readonly string[] GamingTargets = {
            "onedrive", "msteams", "teams", "msedge", "spotify", "discord", "skype", "slack", "cortana", "yourphone", "gamebar", "searchapp", "widgets", "explorer"
        };

        // ========== MOUSE ACCELERATION ==========
        public static readonly (string, string)[] MouseAccelOffPairs = {
            ("MouseSpeed", "0"), ("MouseThreshold1", "0"), ("MouseThreshold2", "0")
        };

        public static readonly (string, string)[] MouseAccelOnPairs = {
            ("MouseSpeed", "1"), ("MouseThreshold1", "6"), ("MouseThreshold2", "10")
        };

        // ========== NETWORK COMMANDS ==========
        public static readonly (string, string)[] NetResetCmds = {
            ("netsh", "winsock reset"), 
            ("netsh", "int ip reset"), 
            ("ipconfig", "/flushdns"), 
            ("ipconfig", "/release"), 
            ("ipconfig", "/renew")
        };

        public static readonly (string, string)[] TcpTweaksCmds = {
            ("netsh", "int tcp set global autotuninglevel=normal"),
            ("netsh", "int tcp set global ecncapability=enabled"),
            ("netsh", "int tcp set global rss=enabled"),
            ("netsh", "int tcp set global chimney=disabled"),
            ("netsh", "int tcp set heuristics disabled"),
            ("netsh", "int tcp set supplemental Internet congestionprovider=ctcp"),
            ("reg", @"add ""HKLM\SOFTWARE\Microsoft\MSMQ\Parameters"" /v TCPNoDelay /t REG_DWORD /d 1 /f")
        };

        public static readonly (string, string)[] PingHosts = {
            ("Cloudflare", "1.1.1.1"),
            ("Google", "8.8.8.8"),
            ("Quad9", "9.9.9.9")
        };

        // ========== WINDOWS UPDATE ==========
        public static readonly string[] WuResetServices = { "wuauserv", "cryptSvc", "bits", "msiserver" };

        // ========== PRIVACY TWEAKS ==========
        public static readonly string[] PrivacyTweaksCmds = {
            @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo"" /v Enabled /t REG_DWORD /d 0 /f",
            @"add ""HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection"" /v AllowTelemetry /t REG_DWORD /d 0 /f",
            @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SubscribedContent-338389Enabled /t REG_DWORD /d 0 /f",
            @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Search"" /v BingSearchEnabled /t REG_DWORD /d 0 /f",
            @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Search"" /v CortanaConsent /t REG_DWORD /d 0 /f",
            @"add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"" /v EnableActivityFeed /t REG_DWORD /d 0 /f",
            @"add ""HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent"" /v DisableWindowsConsumerFeatures /t REG_DWORD /d 1 /f"
        };

        public static readonly string[] DisableTipsCmds = {
            @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SubscribedContent-310093Enabled /t REG_DWORD /d 0 /f",
            @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SubscribedContent-338388Enabled /t REG_DWORD /d 0 /f",
            @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SubscribedContent-338389Enabled /t REG_DWORD /d 0 /f",
            @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SilentInstalledAppsEnabled /t REG_DWORD /d 0 /f",
            @"add ""HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"" /v SystemPaneSuggestionsEnabled /t REG_DWORD /d 0 /f",
            @"add ""HKCU\Software\Policies\Microsoft\Windows\Explorer"" /v DisableSearchBoxSuggestions /t REG_DWORD /d 1 /f"
        };

        // ========== POWER PLANS ==========
        public static readonly Dictionary<string, string> PowerPlans = new()
        {
            { "Balanced", "381b4222-f694-41f0-9685-ff5bb260df2e" },
            { "High",     "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" },
            { "Saver",    "a1841308-3541-4fab-bc81-f71556f20b4a" },
            { "Ultimate", "e9a42b02-d5df-448d-aa00-03f14749eb61" }
        };

        // ========== TIMER INTERVALS ==========
        public const int TIMER_FAST_INTERVAL_MS = 2000;      // CPU/RAM/Disk
        public const int TIMER_NETWORK_INTERVAL_MS = 2000;   // Network counters
        public const int TIMER_CLOCK_INTERVAL_MS = 1000;     // Clock updates
        public const int TIMER_SLOW_INTERVAL_MS = 10000;     // WMI queries (expensive)
        public const int TIMER_INTELPPM_INTERVAL_MS = 2000;  // Intel PPM Editor

        // ========== WMI NAMESPACES ==========
        public const string WMI_ROOT = "root\\cimv2";
        public const string WMI_ROOT_WMI = "root\\WMI";

        // ========== PATHS & LOCATIONS ==========
        public const string INTELPPM_KEY_PATH = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\intelppm";
        public const string INTELPPM_REG_PATH = @"SYSTEM\CurrentControlSet\Services\intelppm";

        // ========== FILE OPERATIONS ==========
        public const int FILE_BUFFER_SIZE = 81920;
        public const int CMD_TIMEOUT_MS = 120000;

        // ========== MEMORY ==========
        public const long BYTES_PER_GB = 1073741824;
        public const long BYTES_PER_MB = 1048576;
        public const long BYTES_PER_KB = 1024;
    }
}
