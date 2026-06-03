using System.Runtime.InteropServices;

namespace WpfApp3.Utilities
{
    /// <summary>
    /// P/Invoke declarations per accesso alle API Windows
    /// </summary>
    public static class Interop
    {
        // ========== MEMORY & WORKING SET ==========
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EmptyWorkingSet(IntPtr hwProc);

        // ========== THREAD MANAGEMENT ==========
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        // ========== OS VERSION ==========
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct RTL_OSVERSIONINFOEX
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        [DllImport("ntdll.dll", SetLastError = true)]
        public static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEX lpVersionInformation);

        // ========== REGISTRY ==========
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegOpenKeyEx(
            UIntPtr hKey,
            string lpSubKey,
            int ulOptions,
            int samDesired,
            out UIntPtr phkResult);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegCloseKey(UIntPtr hKey);

        // Registry predefined keys
        public static readonly UIntPtr HKEY_LOCAL_MACHINE = new(0x80000002u);
        public static readonly UIntPtr HKEY_CURRENT_USER = new(0x80000001u);
    }
}
