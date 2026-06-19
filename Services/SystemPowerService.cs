using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassroomControl
{
    public class SystemPowerService
    {
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";

        public void Shutdown()
        {
            ExecuteShutdownCommand("-s -t 0");
        }

        public void Restart()
        {
            ExecuteShutdownCommand("-r -t 0");
        }

        public void Sleep()
        {
            SetSuspendState(false, true, false);
        }

        public void Hibernate()
        {
            SetSuspendState(true, true, false);
        }

        public void LogOff()
        {
            ExitWindowsEx(0x00000000, 0x00000000);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ExitWindowsEx(uint uFlags, uint dwReason);

        private void ExecuteShutdownCommand(string arguments)
        {
            try
            {
                EnableShutdownPrivilege();
                
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false
                } ?? throw new InvalidOperationException("Failed to create ProcessStartInfo");

                Process process = new Process
                {
                    StartInfo = startInfo
                };
                process.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"执行关机命令失败: {ex.Message}");
            }
        }

        private void EnableShutdownPrivilege()
        {
            try
            {
                if (OpenProcessToken(Process.GetCurrentProcess().Handle, 
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tokenHandle))
                {
                    if (LookupPrivilegeValue(string.Empty, SE_SHUTDOWN_NAME, out LUID luid))
                    {
                        TOKEN_PRIVILEGES tokenPrivileges = new TOKEN_PRIVILEGES
                        {
                            PrivilegeCount = 1,
                            Luid = luid,
                            Attributes = SE_PRIVILEGE_ENABLED
                        };

                        AdjustTokenPrivileges(tokenHandle, false, ref tokenPrivileges, 
                            0, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启用关机权限失败: {ex.Message}");
            }
        }
    }
}