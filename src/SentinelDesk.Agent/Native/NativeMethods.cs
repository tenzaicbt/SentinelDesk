using System.Runtime.InteropServices;

namespace SentinelDesk.Agent.Native;

internal static partial class NativeMethods
{
    // ── CPU / memory
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME { public uint Low; public uint High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [LibraryImport("kernel32.dll")]
    public static partial ulong GetTickCount64();

    // ── Terminal services / sessions
    public const int WTS_CURRENT_SERVER_HANDLE = 0;

    public enum WTS_CONNECTSTATE_CLASS { Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }
    public enum WTS_INFO_CLASS { WTSUserName = 5, WTSDomainName = 7, WTSClientName = 10, WTSSessionInfoEx = 25 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, WTS_INFO_CLASS infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("kernel32.dll")]
    public static extern int WTSGetActiveConsoleSessionId();

    // ── Process creation in user session
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NO_WINDOW = 0x08000000;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUser(IntPtr hToken, string? appName, string cmdLine, IntPtr procAttrs, IntPtr threadAttrs,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    /// <summary>The user signed in to a session and their group names ("DOMAIN\Group"). Needs SYSTEM.</summary>
    public static List<string> SessionGroupNames(int sessionId)
    {
        var names = new List<string>();
        if (sessionId < 0 || !WTSQueryUserToken(sessionId, out var token)) return names;
        try
        {
            using var id = new System.Security.Principal.WindowsIdentity(token);
            if (id.Name is { Length: > 0 } n) names.Add(n);
            foreach (var g in id.Groups ?? new System.Security.Principal.IdentityReferenceCollection())
            {
                try { if (g.Translate(typeof(System.Security.Principal.NTAccount)) is { } a) names.Add(a.Value); }
                catch { /* unresolvable SID */ }
            }
        }
        finally { CloseHandle(token); }
        return names;
    }

    /// <summary>True when the process runs elevated as a member of Administrators (a UAC-filtered token is not).</summary>
    public static bool IsElevatedAdministrator(int pid)
    {
        const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008, TokenDuplicate = 0x0002;
        var proc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (proc == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(proc, TokenQuery | TokenDuplicate, out var token)) return false;
            try
            {
                using var id = new System.Security.Principal.WindowsIdentity(token);
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(proc); }
    }

    // ── Named pipe client identification
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    public static List<(int SessionId, string? User, string? Domain, WTS_CONNECTSTATE_CLASS State)> EnumerateSessions()
    {
        var result = new List<(int, string?, string?, WTS_CONNECTSTATE_CLASS)>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var ptr, out var count)) return result;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(ptr + i * size);
                if (info.SessionId == 0) continue; // services session
                var user = QueryString(info.SessionId, WTS_INFO_CLASS.WTSUserName);
                if (string.IsNullOrEmpty(user)) continue;
                result.Add((info.SessionId, user, QueryString(info.SessionId, WTS_INFO_CLASS.WTSDomainName), info.State));
            }
        }
        finally { WTSFreeMemory(ptr); }
        return result;
    }

    public static string? QueryString(int sessionId, WTS_INFO_CLASS cls)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, cls, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }

    /// <summary>Launches a process inside an interactive user session (requires LocalSystem / SE_TCB_NAME).</summary>
    public static bool StartInSession(int sessionId, string exePath, out string? error)
    {
        error = null;
        if (!WTSQueryUserToken(sessionId, out var token)) { error = $"WTSQueryUserToken failed ({Marshal.GetLastWin32Error()})"; return false; }
        var env = IntPtr.Zero;
        try
        {
            CreateEnvironmentBlock(out env, token, false);
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            if (!CreateProcessAsUser(token, exePath, $"\"{exePath}\"", IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(exePath), ref si, out var pi))
            {
                error = $"CreateProcessAsUser failed ({Marshal.GetLastWin32Error()})";
                return false;
            }
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            CloseHandle(token);
        }
    }
}
