using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

class Program {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(
        string lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    const uint INFINITE = 0xFFFFFFFF;

    static int Main(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: launch-desktop.exe <workingDir> <exePath> [args...]");
            return 1;
        }

        string cwd = args[0];
        string exe = args[1];

        StringBuilder cmdLine = new StringBuilder();
        cmdLine.Append("\"").Append(exe).Append("\"");
        for (int i = 2; i < args.Length; i++) {
            cmdLine.Append(" ");
            string arg = args[i];
            if (arg.Contains(" ") || arg.Contains("\t")) {
                cmdLine.Append("\"").Append(arg.Replace("\"", "\\\"")).Append("\"");
            } else {
                cmdLine.Append(arg);
            }
        }

        STARTUPINFO si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(si);
        si.lpDesktop = @"WinSta0\Default";

        PROCESS_INFORMATION pi;
        bool success = CreateProcess(
            null,
            cmdLine.ToString(),
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            0,
            IntPtr.Zero,
            cwd,
            ref si,
            out pi
        );

        if (!success) {
            int err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine("Failed to launch process on interactive desktop: error " + err);
            return err;
        }

        Console.CancelKeyPress += (s, e) => {
            try {
                Process p = Process.GetProcessById(pi.dwProcessId);
                if (!p.HasExited) {
                    p.Kill();
                }
            } catch {}
        };

        CloseHandle(pi.hThread);
        WaitForSingleObject(pi.hProcess, INFINITE);

        uint exitCode = 0;
        GetExitCodeProcess(pi.hProcess, out exitCode);
        CloseHandle(pi.hProcess);

        return (int)exitCode;
    }
}
