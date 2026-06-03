using System.ComponentModel;

namespace WpfApp3.Models
{
    /// <summary>
    /// Informazioni di un processo in esecuzione
    /// </summary>
    public class ProcessInfo : INotifyPropertyChanged
    {
        private bool _sel;
        public bool IsSelected
        {
            get => _sel;
            set { _sel = value; OnPC(nameof(IsSelected)); }
        }

        public string Name { get; set; } = "";
        public int Id { get; set; }
        public double RamMb { get; set; }
        public int Threads { get; set; }
        public string Path { get; set; } = "";

        private string _priority = "";
        public string Priority
        {
            get => _priority;
            set { _priority = value; OnPC(nameof(Priority)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Informazioni di un servizio Windows
    /// </summary>
    public class ServiceInfo : INotifyPropertyChanged
    {
        private bool _sel;
        public bool IsSelected
        {
            get => _sel;
            set { _sel = value; OnPC(nameof(IsSelected)); }
        }

        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public string StartMode { get; set; } = "";
        public string Description { get; set; } = "";
        public string Recommendation { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Informazioni di un'applicazione UWP
    /// </summary>
    public class AppInfo : INotifyPropertyChanged
    {
        private bool _sel;
        public bool IsSelected
        {
            get => _sel;
            set { _sel = value; OnPC(nameof(IsSelected)); }
        }

        public string Name { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string Recommendation { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Voce di avvio di Windows
    /// </summary>
    public class StartupEntry : INotifyPropertyChanged
    {
        private bool _sel;
        public bool IsSelected
        {
            get => _sel;
            set { _sel = value; OnPC(nameof(IsSelected)); }
        }

        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public string Location { get; set; } = "";
        public string RootHive { get; set; } = "";
        public string KeyPath { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Voce di log dell'applicazione
    /// </summary>
    public class LogEntry
    {
        public DateTime Time { get; set; }
        public string TimeText { get; set; } = "";
        public string LevelText { get; set; } = "";
        public string Category { get; set; } = "";
        public string Message { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// Livelli di log
    /// </summary>
    public enum LogLevel { Info, Ok, Warn, Error }

    /// <summary>
    /// Risultato di esecuzione comando
    /// </summary>
    public class CmdResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// Package di driver
    /// </summary>
    public class DriverPkg
    {
        public string OemInf { get; set; } = "";
        public string OriginalName { get; set; } = "";
        public string Provider { get; set; } = "";
        public string ClassName { get; set; } = "";
        public string Version { get; set; } = "";
        public string Date { get; set; } = "";
    }

    /// <summary>
    /// Ranking di un driver audio
    /// </summary>
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
}
