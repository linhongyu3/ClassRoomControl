using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace ClassroomControl.Services
{
    public class UsbDeviceInfo
    {
        public string DeviceID { get; set; } = string.Empty;
        public string DriveLetter { get; set; } = string.Empty;
        public string VolumeName { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;
    }

    public class UsbDetectionService : IDisposable
    {
        private ManagementEventWatcher? _insertWatcher;
        private ManagementEventWatcher? _removeWatcher;
        private List<UsbDeviceInfo> _usbDevices = new();
        private bool _disposed;

        public event EventHandler<List<UsbDeviceInfo>>? UsbDevicesChanged;

        public List<UsbDeviceInfo> GetUsbDevices()
        {
            _usbDevices.Clear();
            
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_LogicalDisk WHERE DriveType=2");
                
                foreach (var device in searcher.Get())
                {
                    _usbDevices.Add(new UsbDeviceInfo
                    {
                        DeviceID = device["DeviceID"]?.ToString() ?? string.Empty,
                        DriveLetter = device["DeviceID"]?.ToString() ?? string.Empty,
                        VolumeName = device["VolumeName"]?.ToString() ?? "可移动磁盘",
                        FriendlyName = GetUsbDeviceName(device["DeviceID"]?.ToString() ?? string.Empty) ?? "可移动磁盘"
                    });
                }
            }
            catch { }
            
            return _usbDevices;
        }

        public void StartMonitoring()
        {
            try
            {
                // 监听USB插入
                _insertWatcher = new ManagementEventWatcher(
                    new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2"));
                _insertWatcher.EventArrived += (s, e) => OnUsbChanged();
                _insertWatcher.Start();

                // 监听USB移除
                _removeWatcher = new ManagementEventWatcher(
                    new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 3"));
                _removeWatcher.EventArrived += (s, e) => OnUsbChanged();
                _removeWatcher.Start();
            }
            catch { }
        }

        public void StopMonitoring()
        {
            _insertWatcher?.Stop();
            _removeWatcher?.Stop();
        }

        private void OnUsbChanged()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app != null && app.Dispatcher != null)
                {
                    app.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            UsbDevicesChanged?.Invoke(this, GetUsbDevices());
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"USB设备变化事件触发失败: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"USB检测回调失败: {ex.Message}");
            }
        }

        public bool EjectUsb(string driveLetter)
        {
            return EjectUsbWithForce(driveLetter, true);
        }

        public bool EjectUsbWithForce(string driveLetter, bool forceCloseProcesses = true)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_LogicalDisk WHERE DeviceID='{driveLetter}'");
                
                foreach (var disk in searcher.Get())
                {
                    string volumePath = $"Win32_Volume.DeviceID='\\\\?\\Volume{disk["VolumeSerialNumber"]?.ToString()}\\\\'";
                    var volume = new ManagementObject(volumePath);
                    
                    try
                    {
                        // 首先尝试正常弹出
                        volume.InvokeMethod("Dismount", new object[] { true, true });
                        return true;
                    }
                    catch (Exception ex)
                    {
                        // 如果弹出失败，且允许强制关闭进程
                        if (forceCloseProcesses)
                        {
                            // 查找并关闭占用该驱动器的进程
                            var closedProcesses = CloseProcessesUsingDrive(driveLetter);
                            
                            if (closedProcesses > 0)
                            {
                                // 再次尝试弹出
                                try
                                {
                                    volume.InvokeMethod("Dismount", new object[] { true, true });
                                    return true;
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }
            
            return false;
        }

        /// <summary>
        /// 查找并关闭占用指定驱动器的进程
        /// </summary>
        /// <param name="driveLetter">驱动器盘符，如 "D:"</param>
        /// <returns>关闭的进程数量</returns>
        public int CloseProcessesUsingDrive(string driveLetter)
        {
            int closedCount = 0;
            string lowerDrive = driveLetter.ToLower();
            
            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        // 检查进程的主模块路径
                        if (!string.IsNullOrEmpty(process.MainModule?.FileName))
                        {
                            if (process.MainModule.FileName.ToLower().StartsWith(lowerDrive))
                            {
                                process.Kill();
                                process.WaitForExit(1000);
                                closedCount++;
                                continue;
                            }
                        }
                        
                        // 检查进程的工作目录
                        if (!string.IsNullOrEmpty(process.StartInfo.WorkingDirectory))
                        {
                            if (process.StartInfo.WorkingDirectory.ToLower().StartsWith(lowerDrive))
                            {
                                process.Kill();
                                process.WaitForExit(1000);
                                closedCount++;
                                continue;
                            }
                        }
                    }
                    catch
                    {
                        // 忽略访问被拒绝等异常
                    }
                }
            }
            catch { }
            
            return closedCount;
        }

        // 使用 Windows Shell API 弹出U盘
        public bool EjectUsbWithExplorerApi(string driveLetter)
        {
            try
            {
                // 获取驱动器号（确保是大写）
                string drive = driveLetter.ToUpper();
                if (!drive.EndsWith(":"))
                    drive += ":";

                // 使用 ShellExecute 调用系统弹出功能
                return ShellExecuteEject(drive);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"使用Explorer API弹出失败: {ex.Message}");
                return false;
            }
        }

        private bool ShellExecuteEject(string driveLetter)
        {
            try
            {
                // 使用 ShellExecute 调用 "Eject" 动词
                IntPtr result = ShellExecute(
                    IntPtr.Zero,
                    "Eject",
                    driveLetter + @"\\",
                    null,
                    null,
                    SW_SHOWNORMAL);

                // ShellExecute 返回值大于32表示成功
                return result.ToInt32() > 32;
            }
            catch { }
            return false;
        }

        // Windows API 声明
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr ShellExecute(
            IntPtr hwnd,
            string lpOperation,
            string lpFile,
            string lpParameters,
            string lpDirectory,
            int nShowCmd);

        // 常量
        private const int SW_SHOWNORMAL = 1;

        private string GetUsbDeviceName(string driveLetter)
        {
            try
            {
                using var diskSearcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_LogicalDisk WHERE DeviceID='{driveLetter}'");
                
                foreach (var disk in diskSearcher.Get())
                {
                    string volumeName = disk["VolumeName"]?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(volumeName) && volumeName != "NTFS")
                    {
                        return volumeName;
                    }
                }

                using var volumeSearcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Volume WHERE DriveLetter='{driveLetter}'");
                
                foreach (var volume in volumeSearcher.Get())
                {
                    string serialNumber = volume["SerialNumber"]?.ToString() ?? string.Empty;
                    string deviceID = volume["DeviceID"]?.ToString() ?? string.Empty;

                    using var driveSearcher = new ManagementObjectSearcher(
                        "SELECT * FROM Win32_DiskDrive WHERE InterfaceType='USB'");
                    
                    foreach (var drive in driveSearcher.Get())
                    {
                        string driveDeviceID = drive["DeviceID"]?.ToString() ?? string.Empty;
                        string model = drive["Model"]?.ToString() ?? string.Empty;

                        using var partitionSearcher = new ManagementObjectSearcher(
                            "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" + 
                            driveDeviceID.Replace("\\", "\\\\") + 
                            "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                        
                        foreach (var partition in partitionSearcher.Get())
                        {
                            using var logicalSearcher = new ManagementObjectSearcher(
                                "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + 
                                partition["DeviceID"]?.ToString()?.Replace("\\", "\\\\") + 
                                "'} WHERE AssocClass=Win32_LogicalDiskToPartition");
                            
                            foreach (var logical in logicalSearcher.Get())
                            {
                                string logicalDriveLetter = logical["DeviceID"]?.ToString() ?? string.Empty;
                                if (logicalDriveLetter.Equals(driveLetter, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!string.IsNullOrEmpty(model) && model != "USB Mass Storage Device")
                                    {
                                        return model;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("获取USB设备名称失败: " + ex.Message);
            }
            
            return string.Empty;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopMonitoring();
            _insertWatcher?.Dispose();
            _removeWatcher?.Dispose();
            _usbDevices.Clear();
        }
    }
}