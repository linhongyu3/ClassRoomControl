using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfBrushes = System.Windows.Media.Brushes;

namespace ClassroomControl
{
    public partial class MainWindow : Window
    {
        private SystemPowerService _powerService = null!;
        private TrayIconService _trayIconService = null!;
        private Services.SchedulerService _schedulerService = null!;
        private Services.DailyTaskService _dailyTaskService = null!;
        private Services.DesktopOrganizerService _desktopOrganizerService = null!;
        private Services.ActivityLogService _logService = null!;
        private Services.UsbDetectionService _usbDetectionService = null!;
        private Services.AppUsageService? _appUsageService;
        private Services.AppUsageRule? _editingRule = null;
        private DispatcherTimer? _infoUpdateTimer;
        private List<CustomAppEntry> _customApps = new();
        private StackPanel _usbButtonsPanel = new();
        private static string _appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConstants.AppNameEnglish);
        private List<ProcessInfo> _processList = new();
        private Dictionary<string, System.Windows.Media.ImageSource> _iconCache = new();
        private static string CustomAppsFile => Path.Combine(_appDataFolder, "custom_apps.json");
        private const string AutoStartRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoStartRegName = AppConstants.AppNameEnglish;
        private static string DevModeSettingsFile => Path.Combine(_appDataFolder, "dev_mode.json");
        private static string HighPriorityProcessesFile => Path.Combine(_appDataFolder, "high_priority_processes.json");

        public static bool IsDevModeEnabled { get; private set; }
        public static bool UseBeautifulUI { get; private set; } = true;
        private HashSet<int> _highPriorityProcessIds = new();

        private static readonly string[] AppColors = { "#7E57C2", "#26A69A", "#EC407A", "#5C6BC0", "#FFA726", "#66BB6A", "#AB47BC", "#29B6F6" };

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                
                _powerService = new SystemPowerService();
                _logService = new Services.ActivityLogService();
                _schedulerService = new Services.SchedulerService();
                _dailyTaskService = new Services.DailyTaskService();
                _desktopOrganizerService = new Services.DesktopOrganizerService();
                _usbDetectionService = new Services.UsbDetectionService();
                _appUsageService = new Services.AppUsageService(_appDataFolder);
                _appUsageService.StartMonitoring();
                _appUsageService.AppBlocked += (s, rule) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!string.IsNullOrEmpty(_logClearPassword))
                        {
                            ShowAppBlockedDialog(rule.AppName, rule.AppPath);
                        }
                        else
                        {
                            System.Windows.MessageBox.Show($"当前时间段禁止使用 {rule.AppName}", 
                                "应用使用限制", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    });
                };
                
                _appUsageService.AppBlockedRequest += (s, data) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!string.IsNullOrEmpty(_logClearPassword))
                        {
                            ShowAppBlockedDialog(data.appName, data.appPath);
                        }
                    });
                };
                _appUsageService.RulesChanged += (s, rules) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        LstAppRules.ItemsSource = null;
                        LstAppRules.ItemsSource = rules;
                    });
                };
                LstAppRules.ItemsSource = _appUsageService.GetRules();
                _trayIconService = new TrayIconService(this);
                
                StateChanged += MainWindow_StateChanged;
                IsVisibleChanged += MainWindow_IsVisibleChanged;
                Deactivated += MainWindow_Deactivated;
                Activated += MainWindow_Activated;
                InitializeUsbDetection();
                LoadSystemInfo();
                LoadDevModeStateStatic();
                LoadHighPriorityProcesses();
                UpdateDevPanelVisibility();
                StartInfoUpdateTimer();
                InitializeScheduler();
                InitializeDailyTasks();
                InitializeOrganizer();
                LoadCustomApps();
                RebuildCustomAppButtons();
                
                // 默认启用美观UI
                EnableBeautifulUI();
                _logService.Load();
                LoadLogPassword();
                LstActivityLog.ItemsSource = _logService.Logs;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"主窗口初始化失败: {ex.Message}\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                _trayIconService.MinimizeToTray();
                _usbDetectionService?.StopMonitoring();
            }
        }

        private void MainWindow_IsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible)
            {
                OptimizeForBackground();
            }
            else
            {
                RestoreFromBackground();
            }
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            OptimizeForBackground();
        }

        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            RestoreFromBackground();
        }

        private bool _isInBackground = false;

        private void OptimizeForBackground()
        {
            if (_isInBackground) return;
            _isInBackground = true;
            
            _infoUpdateTimer?.Stop();
            
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private void RestoreFromBackground()
        {
            if (!_isInBackground) return;
            _isInBackground = false;
            
            _infoUpdateTimer?.Start();
            
            LoadSystemInfo();
            UpdateBatteryStatus();
        }

        private void InitializeDailyTasks()
        {
            // 绑定任务列表
            LstScheduledTasks.ItemsSource = _dailyTaskService.Tasks;

            // 监听任务状态变化
            _dailyTaskService.StatusChanged += (s, msg) =>
            {
                Dispatcher.Invoke(() =>
                {
                    TxtTaskStatus.Text = msg;
                });
            };

            // 监听任务执行
            _dailyTaskService.TaskExecuting += (s, task) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _logService.Log("任务执行", $"定时任务已触发：{task.Description}");
                    if (task.Type == Services.TaskType.Shutdown || task.Type == Services.TaskType.Restart)
                    {
                        ShowShutdownReminder(task.ReminderMinutes);
                    }
                });
            };
        }

        private void InitializeScheduler()
        {
            _schedulerService.StatusChanged += (s, msg) =>
            {
                Dispatcher.Invoke(() =>
                {
                    TxtTaskStatus.Text = msg;
                });
            };

            _schedulerService.ShutdownImminent += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ShowShutdownReminder();
                });
            };
        }

        private void ShowShutdownReminder(int reminderMinutes = 5)
        {
            var reminderWindow = new ShutdownReminderWindow(reminderMinutes);
            reminderWindow.CancelRequested += (s, e) =>
            {
                _schedulerService.CancelSchedule();
                _logService.Log("定时关机", "用户取消了定时关机");
            };
            reminderWindow.ShutdownNowRequested += (s, e) =>
            {
                _schedulerService.CancelSchedule();
                _logService.Log("定时关机", "用户立即关机");
            };
            reminderWindow.Show();
        }

        private void LoadSystemInfo()
        {
            TxtComputerName.Text = Environment.MachineName;
            TxtOSVersion.Text = GetFriendlyOSName();
            UpdateBatteryStatus();
            UpdateAdminStatus();
        }

        private string GetFriendlyOSName()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        var productName = key.GetValue("ProductName") as string ?? "";
                        var currentBuildStr = key.GetValue("CurrentBuild") as string;
                        var majorVer = key.GetValue("CurrentMajorVersionNumber") as int?;
                        var minorVer = key.GetValue("CurrentMinorVersionNumber") as int?;

                        // Windows 11: 构建号 >= 22000，注册表可能仍显示 Windows 10
                        if (int.TryParse(currentBuildStr, out int buildNumber) && buildNumber >= 22000)
                        {
                            if (productName.Contains("Windows 10"))
                            {
                                productName = productName.Replace("Windows 10", "Windows 11");
                            }
                            else if (!productName.Contains("Windows 11"))
                            {
                                var edition = "";
                                if (productName.Contains("Pro")) edition = "Pro";
                                else if (productName.Contains("Home")) edition = "Home";
                                else if (productName.Contains("Enterprise")) edition = "Enterprise";
                                else if (productName.Contains("Education")) edition = "Education";
                                productName = $"Windows 11 {edition}".Trim();
                            }
                        }

                        // Windows 7: MajorVersion=6, MinorVersion=1
                        // 注册表可能显示 "Windows 7" 或 "Windows 7 xxx"
                        if (majorVer == 6 && minorVer == 1)
                        {
                            if (!productName.Contains("Windows 7"))
                            {
                                var sp = key.GetValue("CSDVersion") as string ?? "";
                                productName = $"Windows 7{(string.IsNullOrEmpty(sp) ? "" : " " + sp)}";
                            }
                        }

                        // Windows Vista: MajorVersion=6, MinorVersion=0
                        if (majorVer == 6 && minorVer == 0)
                        {
                            if (!productName.Contains("Vista"))
                            {
                                productName = "Windows Vista";
                            }
                        }

                        // Windows XP: MajorVersion=5, MinorVersion=1 (注册表路径不同，ProductName 通常可用)
                        if (majorVer == 5 && minorVer == 1)
                        {
                            if (!productName.Contains("XP"))
                            {
                                var sp = key.GetValue("CSDVersion") as string ?? "";
                                productName = $"Windows XP{(string.IsNullOrEmpty(sp) ? "" : " " + sp)}";
                            }
                        }

                        // Windows XP 64-bit: MajorVersion=5, MinorVersion=2 (也用于 Server 2003)
                        if (majorVer == 5 && minorVer == 2)
                        {
                            // 区分 XP x64 和 Server 2003
                            var systemType = key.GetValue("SystemBiosVersion") as string ?? "";
                            // 简单判断：如果 ProductName 不含 Server，视为 XP x64
                            if (!productName.Contains("Server") && !productName.Contains("XP"))
                            {
                                productName = "Windows XP x64";
                            }
                        }

                        // Windows 8: MajorVersion=6, MinorVersion=2
                        if (majorVer == 6 && minorVer == 2)
                        {
                            if (!productName.Contains("Windows 8"))
                            {
                                productName = "Windows 8";
                            }
                        }

                        // Windows 8.1: MajorVersion=6, MinorVersion=3
                        if (majorVer == 6 && minorVer == 3)
                        {
                            if (!productName.Contains("Windows 8.1"))
                            {
                                productName = "Windows 8.1";
                            }
                        }

                        return string.IsNullOrEmpty(productName) ? Environment.OSVersion.ToString() : productName;
                    }
                }
            }
            catch
            {
                // 读取注册表失败，回退到版本号判断
                var ver = Environment.OSVersion.Version;
                if (ver.Major == 5 && ver.Minor == 1) return "Windows XP";
                if (ver.Major == 5 && ver.Minor == 2) return "Windows XP x64";
                if (ver.Major == 6 && ver.Minor == 0) return "Windows Vista";
                if (ver.Major == 6 && ver.Minor == 1) return "Windows 7";
                if (ver.Major == 6 && ver.Minor == 2) return "Windows 8";
                if (ver.Major == 6 && ver.Minor == 3) return "Windows 8.1";
            }
            return Environment.OSVersion.ToString();
        }

        private void UpdateBatteryStatus()
        {
            if (_isInBackground) return;
            
            // 在简约UI模式下减少WMI查询频率
            if (!UseBeautifulUI)
            {
                return;
            }
            
            try
            {
                var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
                var batteries = searcher.Get();
                
                if (batteries.Count == 0)
                {
                    TxtBattery.Text = "未检测到电池";
                }
                else
                {
                    foreach (ManagementObject battery in batteries)
                    {
                        var status = battery["BatteryStatus"]?.ToString();
                        var estimatedCharge = battery["EstimatedChargeRemaining"]?.ToString();
                        TxtBattery.Text = $"{estimatedCharge}% ({(status == "2" ? "充电中" : "放电中")})";
                        break;
                    }
                }
            }
            catch
            {
                TxtBattery.Text = "无法获取电池信息";
            }
        }

        private void StartInfoUpdateTimer()
        {
            _infoUpdateTimer = new DispatcherTimer();
            // 根据UI模式调整更新频率
            _infoUpdateTimer.Interval = UseBeautifulUI ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
            _infoUpdateTimer.Tick += (s, e) =>
            {
                UpdateUptime();
                UpdateBatteryStatus();
            };
            _infoUpdateTimer.Start();
        }

        private void UpdateUptime()
        {
            if (_isInBackground) return;
            
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            TxtUptime.Text = $"{uptime.Days}天 {uptime.Hours}小时 {uptime.Minutes}分钟";
        }

        // 电源控制按钮事件
        private void BtnShutdown_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show("确定要关闭计算机吗？", "确认关机", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _logService.Log("关机", "执行了立即关机操作");
                _powerService.Shutdown();
            }
        }

        private void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show("确定要重启计算机吗？", "确认重启", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _logService.Log("重启", "执行了立即重启操作");
                _powerService.Restart();
            }
        }

        private void BtnSleep_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show("确定要让计算机进入睡眠状态吗？", "确认睡眠", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _logService.Log("睡眠", "执行了睡眠操作");
                _powerService.Sleep();
            }
        }

        private void BtnHibernate_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show("确定要让计算机进入休眠状态吗？", "确认休眠", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _logService.Log("休眠", "执行了休眠操作");
                _powerService.Hibernate();
            }
        }

        private bool IsRunAsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private void UpdateAdminStatus()
        {
            if (IsRunAsAdmin())
            {
                TxtAdminStatus.Text = "已拥有管理员权限";
                TxtAdminStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#43A047"));
                BtnRunAsAdmin.IsEnabled = false;
                BtnRunAsAdmin.Content = "当前已是管理员";
                BtnRunAsAdmin.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#B0BEC5"));
            }
            else
            {
                TxtAdminStatus.Text = "当前为普通用户权限";
                TxtAdminStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E53935"));
                BtnRunAsAdmin.IsEnabled = true;
                BtnRunAsAdmin.Content = "以管理员身份运行";
                BtnRunAsAdmin.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF8F00"));
            }
        }

        private void BtnRunAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (IsRunAsAdmin())
            {
                System.Windows.MessageBox.Show("当前已经以管理员身份运行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    System.Windows.MessageBox.Show("无法获取程序路径。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Verb = "runas",
                    UseShellExecute = true
                };

                Process.Start(startInfo);

                _trayIconService.Dispose();
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"提升管理员权限失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 快速启动按钮事件
        private void BtnOpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "msedge.exe",
                    UseShellExecute = true
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.bing.com",
                    UseShellExecute = true
                });
            }
        }

        private void BtnOpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("explorer.exe");
        }

        private void BtnOpenTaskManager_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("taskmgr.exe");
        }

        private void BtnOpenCmd_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("cmd.exe");
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:",
                UseShellExecute = true
            });
        }

        private void BtnAddCustomApp_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                Title = "选择要添加的程序"
            };

            if (openFileDialog.ShowDialog() != true) return;

            var appPath = openFileDialog.FileName;
            var appName = Path.GetFileNameWithoutExtension(appPath);

            // 自动提取程序图标作为默认
            string defaultIconType = "default";
            string defaultIconData = "";
            try
            {
                var extractedIcon = ExtractIconFromExe(appPath);
                if (extractedIcon != null)
                {
                    defaultIconType = "exe";
                    defaultIconData = BitmapSourceToBase64(extractedIcon);
                }
            }
            catch { }

            var inputDialog = new Window
            {
                Title = "添加快速启动应用",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(WpfColor.FromArgb(255, 236, 239, 241))
            };

            var panel = new StackPanel { Margin = new Thickness(24) };

            var nameLabel = new TextBlock { Text = "应用名称", FontSize = 13, Margin = new Thickness(0, 0, 0, 6) };
            var nameBox = new System.Windows.Controls.TextBox { Text = appName, Height = 32, FontSize = 13, Padding = new Thickness(10, 0, 10, 0) };

            var iconTitle = new TextBlock { Text = "图标来源", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8) };

            var rbExe = new System.Windows.Controls.RadioButton { Content = "使用程序自身图标", FontSize = 13, Margin = new Thickness(0, 0, 0, 8), IsChecked = defaultIconType == "exe" };
            var rbDefault = new System.Windows.Controls.RadioButton { Content = "默认（显示首字母）", FontSize = 13, Margin = new Thickness(0, 0, 0, 8), IsChecked = defaultIconType == "default" };
            var rbCustom = new System.Windows.Controls.RadioButton { Content = "自定义图片", FontSize = 13, Margin = new Thickness(0, 0, 0, 8) };

            var customPathPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 10) };
            var customPathBox = new System.Windows.Controls.TextBox { Text = "", Width = 250, Height = 30, FontSize = 12, IsEnabled = false, Padding = new Thickness(8, 0, 8, 0) };
            var browseImgBtn = new System.Windows.Controls.Button { Content = "浏览", Width = 60, Height = 30, Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#546E7A")), Foreground = WpfBrushes.White, FontSize = 12 };
            browseImgBtn.Click += (s, args) =>
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.ico)|*.png;*.jpg;*.jpeg;*.bmp;*.ico|所有文件 (*.*)|*.*",
                    Title = "选择图标文件"
                };
                if (ofd.ShowDialog() == true)
                    customPathBox.Text = ofd.FileName;
            };
            rbCustom.Checked += (s, args) => customPathBox.IsEnabled = true;
            rbCustom.Unchecked += (s, args) => customPathBox.IsEnabled = false;
            customPathPanel.Children.Add(customPathBox);
            customPathPanel.Children.Add(browseImgBtn);

            // 图标预览
            var previewBorder = new Border { Background = WpfBrushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(20), Margin = new Thickness(0, 6, 0, 14), HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            var previewImage = new System.Windows.Controls.Image { Width = 48, Height = 48 };
            previewBorder.Child = previewImage;

            var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var btnOk = CreateDialogButton("确定", (WpfColor)WpfColorConverter.ConvertFromString("#1E88E5"));
            var btnCancel = CreateDialogButton("取消", (WpfColor)WpfColorConverter.ConvertFromString("#B0BEC5"));
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);

            panel.Children.Add(nameLabel);
            panel.Children.Add(nameBox);
            panel.Children.Add(iconTitle);
            panel.Children.Add(rbExe);
            panel.Children.Add(rbDefault);
            panel.Children.Add(rbCustom);
            panel.Children.Add(customPathPanel);
            panel.Children.Add(previewBorder);
            panel.Children.Add(btnPanel);
            inputDialog.Content = panel;

            // 预览更新
            void UpdatePreview()
            {
                previewImage.Source = null;
                if (rbExe.IsChecked == true)
                {
                    try
                    {
                        var bmpSource = ExtractIconFromExe(appPath);
                        if (bmpSource != null) previewImage.Source = bmpSource;
                    }
                    catch { }
                }
                else if (rbCustom.IsChecked == true && System.IO.File.Exists(customPathBox.Text))
                {
                    try
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(customPathBox.Text, UriKind.Absolute);
                        bmp.DecodePixelWidth = 48;
                        bmp.DecodePixelHeight = 48;
                        bmp.EndInit();
                        bmp.Freeze();
                        previewImage.Source = bmp;
                    }
                    catch { }
                }
            }

            rbDefault.Checked += (s, args) => UpdatePreview();
            rbExe.Checked += (s, args) => UpdatePreview();
            rbCustom.Checked += (s, args) => UpdatePreview();
            customPathBox.TextChanged += (s, args) => UpdatePreview();

            btnOk.Click += (s, args) =>
            {
                var name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    System.Windows.MessageBox.Show("请输入应用名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string iconType = "default";
                string iconData = "";

                if (rbExe.IsChecked == true)
                {
                    var bmpSource = ExtractIconFromExe(appPath);
                    if (bmpSource != null)
                    {
                        iconType = "exe";
                        iconData = BitmapSourceToBase64(bmpSource);
                    }
                }
                else if (rbCustom.IsChecked == true)
                {
                    var imgPath = customPathBox.Text.Trim();
                    if (!System.IO.File.Exists(imgPath))
                    {
                        System.Windows.MessageBox.Show("请选择有效的图片文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    try
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(imgPath, UriKind.Absolute);
                        bmp.DecodePixelWidth = 32;
                        bmp.DecodePixelHeight = 32;
                        bmp.EndInit();
                        bmp.Freeze();
                        iconType = "custom";
                        iconData = BitmapSourceToBase64(bmp);
                    }
                    catch
                    {
                        System.Windows.MessageBox.Show("无法读取该图片文件。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }

                _customApps.Add(new CustomAppEntry
                {
                    Name = name,
                    Path = appPath,
                    ColorIndex = _customApps.Count % AppColors.Length,
                    IconType = iconType,
                    IconData = iconData
                });
                _logService.Log("添加应用", $"添加了快速启动应用：{name}");
                SaveCustomApps();
                RebuildCustomAppButtons();
            _logService.Load();
                inputDialog.DialogResult = true;
            };

            btnCancel.Click += (s, args) => { inputDialog.DialogResult = false; };

            UpdatePreview();
            nameBox.SelectAll();
            nameBox.Focus();
            inputDialog.ShowDialog();
        }

        private void LoadCustomApps()
        {
            try
            {
                if (File.Exists(CustomAppsFile))
                {
                    var json = File.ReadAllText(CustomAppsFile);
                    _customApps = JsonSerializer.Deserialize<List<CustomAppEntry>>(json) ?? new();
                }
            }
            catch { }
        }

        private void SaveCustomApps()
        {
            try
            {
                var json = JsonSerializer.Serialize(_customApps);
                File.WriteAllText(CustomAppsFile, json);
            }
            catch { }
        }

    private void RebuildCustomAppButtons()
    {
        var toRemove = new List<UIElement>();
        foreach (UIElement child in QuickLaunchPanel.Children)
        {
            if (child is FrameworkElement fe && fe.Tag as string == "CustomApp")
                toRemove.Add(child);
        }
        foreach (var item in toRemove)
            QuickLaunchPanel.Children.Remove(item);

        // 确保"添加应用"按钮在最后
        if (QuickLaunchPanel.Children.Contains(BtnAddCustomApp))
            QuickLaunchPanel.Children.Remove(BtnAddCustomApp);

        foreach (var app in _customApps)
        {
            var color = AppColors[app.ColorIndex % AppColors.Length];
            var btn = new System.Windows.Controls.Button { Style = (Style)FindResource("QuickButton"), Tag = "CustomApp" };

            FrameworkElement iconElement;
            if (!string.IsNullOrEmpty(app.IconData))
            {
                try
                {
                    var imageBytes = Convert.FromBase64String(app.IconData);
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    using (var ms = new System.IO.MemoryStream(imageBytes))
                    {
                        image.BeginInit();
                        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        image.StreamSource = ms;
                        image.DecodePixelWidth = 32;
                        image.DecodePixelHeight = 32;
                        image.EndInit();
                    }
                    image.Freeze();
                    iconElement = new System.Windows.Controls.Image
                    {
                        Source = image,
                        Width = 32,
                        Height = 32,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                    };
                }
                catch
                {
                    iconElement = CreateDefaultIconElement(app, color);
                }
            }
            else
            {
                iconElement = CreateDefaultIconElement(app, color);
            }

            btn.Content = new StackPanel
            {
                Children =
                {
                    iconElement,
                    new TextBlock
                    {
                        Text = app.Name,
                        FontSize = 14,
                        Margin = new Thickness(0, 6, 0, 0),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 90
                    }
                }
            };

            btn.Click += (s, args) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = app.Path,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(app.Path) ?? ""
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"无法启动程序：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            btn.ContextMenu = new System.Windows.Controls.ContextMenu();
            var removeItem = new System.Windows.Controls.MenuItem { Header = "移除此应用" };
            removeItem.Click += (s, args) =>
            {
                if (System.Windows.MessageBox.Show($"确定要移除 \"{app.Name}\" 吗？", "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _customApps.Remove(app);
                    _logService.Log("移除应用", $"移除了快速启动应用：{app.Name}");
                    SaveCustomApps();
                    RebuildCustomAppButtons();
                    _logService.Load();
                }
            };
            var changeIconItem = new System.Windows.Controls.MenuItem { Header = "更换图标..." };
            changeIconItem.Click += (s, args) => ShowIconPicker(app);
            btn.ContextMenu.Items.Add(changeIconItem);
            btn.ContextMenu.Items.Add(removeItem);

            QuickLaunchPanel.Children.Add(btn);
        }

        QuickLaunchPanel.Children.Add(BtnAddCustomApp);
    }

    private FrameworkElement CreateDefaultIconElement(CustomAppEntry app, string color)
    {
        return new TextBlock
        {
            Text = app.Name.Length > 0 ? app.Name.Substring(0, 1) : "?",
            FontSize = 26,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(color))
        };
    }

    private System.Windows.Controls.Button CreateDialogButton(string content, WpfColor bgColor, double width = 80)
    {
        var color = bgColor;
        var hoverColor = WpfColor.FromArgb(
            (byte)Math.Max(0, color.A - 40),
            (byte)Math.Max(0, color.R - 30),
            (byte)Math.Max(0, color.G - 30),
            (byte)Math.Max(0, color.B - 30));
        var pressedColor = WpfColor.FromArgb(
            (byte)Math.Max(0, color.A - 80),
            (byte)Math.Max(0, color.R - 60),
            (byte)Math.Max(0, color.G - 60),
            (byte)Math.Max(0, color.B - 60));

        var template = ControlTemplate(content, color, hoverColor, pressedColor);
        return new System.Windows.Controls.Button
        {
            Content = content,
            Width = width,
            Height = 34,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Foreground = WpfBrushes.White,
            Template = template,
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }

    private ControlTemplate ControlTemplate(string _, WpfColor color, WpfColor hoverColor, WpfColor pressedColor)
    {
        var xaml = $@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                TargetType='Button'>
  <Border x:Name='bd' CornerRadius='6' Padding='12,0'
          Background='#FF{color.R:X2}{color.G:X2}{color.B:X2}'>
    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'
                      TextBlock.Foreground='White' TextBlock.FontSize='14' TextBlock.FontWeight='SemiBold'/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'>
      <Setter TargetName='bd' Property='Background'
              Value='#FF{hoverColor.R:X2}{hoverColor.G:X2}{hoverColor.B:X2}'/>
    </Trigger>
    <Trigger Property='IsPressed' Value='True'>
      <Setter TargetName='bd' Property='Background'
              Value='#FF{pressedColor.R:X2}{pressedColor.G:X2}{pressedColor.B:X2}'/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private void ShowIconPicker(CustomAppEntry app)
    {
        var dialog = new Window
        {
            Title = $"更换图标 - {app.Name}",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromArgb(255, 236, 239, 241))
        };

        var panel = new StackPanel { Margin = new Thickness(24) };

        var title = new TextBlock { Text = "选择图标来源", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) };

        var rbDefault = new System.Windows.Controls.RadioButton { Content = "默认（显示首字母）", FontSize = 14, Margin = new Thickness(0, 0, 0, 10), IsChecked = app.IconType == "default" || string.IsNullOrEmpty(app.IconType) };
        var rbExe = new System.Windows.Controls.RadioButton { Content = "使用程序自身图标", FontSize = 14, Margin = new Thickness(0, 0, 0, 10), IsChecked = app.IconType == "exe" };
        var rbCustom = new System.Windows.Controls.RadioButton { Content = "自定义图片", FontSize = 14, Margin = new Thickness(0, 0, 0, 10), IsChecked = app.IconType == "custom" };

        // 自定义图片文件路径行
        var customPathPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 10) };
        var customPathBox = new System.Windows.Controls.TextBox { Text = "", Width = 250, Height = 30, FontSize = 12, IsEnabled = rbCustom.IsChecked == true, Padding = new Thickness(8, 0, 8, 0) };
        var browseBtn = new System.Windows.Controls.Button { Content = "浏览", Width = 60, Height = 30, Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#546E7A")), Foreground = WpfBrushes.White, FontSize = 12 };
        browseBtn.Click += (s, args) =>
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.ico)|*.png;*.jpg;*.jpeg;*.bmp;*.ico|所有文件 (*.*)|*.*",
                Title = "选择图标文件"
            };
            if (ofd.ShowDialog() == true)
                customPathBox.Text = ofd.FileName;
        };
        rbCustom.Checked += (s, args) => customPathBox.IsEnabled = true;
        rbCustom.Unchecked += (s, args) => customPathBox.IsEnabled = false;
        customPathPanel.Children.Add(customPathBox);
        customPathPanel.Children.Add(browseBtn);

        // 图标预览
        var previewBorder = new Border { Background = WpfBrushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(20), Margin = new Thickness(0, 10, 0, 16), HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        var previewImage = new System.Windows.Controls.Image { Width = 48, Height = 48 };
        previewBorder.Child = previewImage;

        // 按钮行
        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var btnOk = CreateDialogButton("确定", (WpfColor)WpfColorConverter.ConvertFromString("#1E88E5"));
        btnOk.Margin = new Thickness(0, 0, 10, 0);
        var btnCancel = CreateDialogButton("取消", (WpfColor)WpfColorConverter.ConvertFromString("#B0BEC5"));
        btnPanel.Children.Add(btnOk);
        btnPanel.Children.Add(btnCancel);

        panel.Children.Add(title);
        panel.Children.Add(rbDefault);
        panel.Children.Add(rbExe);
        panel.Children.Add(rbCustom);
        panel.Children.Add(customPathPanel);
        panel.Children.Add(previewBorder);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;

        // 预览更新逻辑
        void UpdatePreview()
        {
            previewImage.Source = null;
            if (rbExe.IsChecked == true)
            {
                try
                {
                    var bmpSource = ExtractIconFromExe(app.Path);
                    if (bmpSource != null) previewImage.Source = bmpSource;
                }
                catch { }
            }
            else if (rbCustom.IsChecked == true && System.IO.File.Exists(customPathBox.Text))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(customPathBox.Text, UriKind.Absolute);
                    bmp.DecodePixelWidth = 48;
                    bmp.DecodePixelHeight = 48;
                    bmp.EndInit();
                    bmp.Freeze();
                    previewImage.Source = bmp;
                }
                catch { }
            }
        }

        rbDefault.Checked += (s, args) => UpdatePreview();
        rbExe.Checked += (s, args) => UpdatePreview();
        rbCustom.Checked += (s, args) => UpdatePreview();
        customPathBox.TextChanged += (s, args) => UpdatePreview();

        btnOk.Click += (s, args) =>
        {
            if (rbDefault.IsChecked == true)
            {
                app.IconType = "default";
                app.IconData = "";
            }
            else if (rbExe.IsChecked == true)
            {
                var bmpSource = ExtractIconFromExe(app.Path);
                if (bmpSource != null)
                {
                    app.IconType = "exe";
                    app.IconData = BitmapSourceToBase64(bmpSource);
                }
                else
                {
                    System.Windows.MessageBox.Show("无法提取该程序的图标，将使用默认图标。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    app.IconType = "default";
                    app.IconData = "";
                }
            }
            else if (rbCustom.IsChecked == true)
            {
                var imgPath = customPathBox.Text.Trim();
                if (!System.IO.File.Exists(imgPath))
                {
                    System.Windows.MessageBox.Show("请选择有效的图片文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(imgPath, UriKind.Absolute);
                    bmp.DecodePixelWidth = 32;
                    bmp.DecodePixelHeight = 32;
                    bmp.EndInit();
                    bmp.Freeze();
                    app.IconType = "custom";
                    app.IconData = BitmapSourceToBase64(bmp);
                }
                catch
                {
                    System.Windows.MessageBox.Show("无法读取该图片文件。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            SaveCustomApps();
            RebuildCustomAppButtons();
            _logService.Load();
            dialog.DialogResult = true;
        };

        btnCancel.Click += (s, args) => { dialog.DialogResult = false; };

        UpdatePreview();
        dialog.ShowDialog();
    }

    private System.Windows.Media.Imaging.BitmapSource? ExtractIconFromExe(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            var bmp = icon.ToBitmap();
            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var bitmapImage = new System.Windows.Media.Imaging.BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmapImage.StreamSource = ms;
            bitmapImage.DecodePixelWidth = 32;
            bitmapImage.DecodePixelHeight = 32;
            bitmapImage.EndInit();
            bitmapImage.Freeze();
            return bitmapImage;
        }
        catch
        {
            return null;
        }
    }

    private string BitmapSourceToBase64(System.Windows.Media.Imaging.BitmapSource bitmapSource)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapSource));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

        private void BtnScheduleShutdown_Click(object sender, RoutedEventArgs e)
        {
            if (_schedulerService.IsScheduled)
            {
                System.Windows.MessageBox.Show("已有一个定时关机任务，请先取消当前的任务。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!TryParseTaskTime(out int hour, out int minute))
                return;

            DateTime scheduledTime = DateTime.Today.AddHours(hour).AddMinutes(minute);

            // 如果时间已过，则设置为明天
            if (scheduledTime <= DateTime.Now)
            {
                scheduledTime = scheduledTime.AddDays(1);
            }

            _schedulerService.ScheduleShutdown(scheduledTime);
            System.Windows.MessageBox.Show($"已设置定时关机：{scheduledTime:yyyy-MM-dd HH:mm}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnCancelSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (_schedulerService.IsScheduled)
            {
                _schedulerService.CancelSchedule();
                System.Windows.MessageBox.Show("已取消定时关机", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show("当前没有定时关机任务", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnBrowseProgram_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                Title = "选择要运行的程序"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                TxtProgramPath.Text = openFileDialog.FileName;
            }
        }

        private void BtnAddTask_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseTaskTime(out int hour, out int minute))
                return;
            string timeText = $"{hour:D2}:{minute:D2}";

            // 根据选中的索引确定任务类型
            var taskType = CmbTaskType.SelectedIndex switch
            {
                1 => Services.TaskType.Restart,
                2 => Services.TaskType.RunProgram,
                3 => Services.TaskType.RunCommand,
                _ => Services.TaskType.Shutdown
            };

            // 读取倒计时时长
            int reminderMinutes = 5;
            if (!int.TryParse(TxtReminderMinutes.Text.Trim(), out reminderMinutes) || reminderMinutes < 1 || reminderMinutes > 60)
            {
                System.Windows.MessageBox.Show("倒计时时长必须是1-60之间的整数分钟", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 检查时间是否已存在（编辑模式下，如果时间和类型都没变则允许保存）
            bool isSameTimeAndType = _editingTask != null && 
                                     _editingTask.Time == timeText && 
                                     _editingTask.Type == taskType;
            
            if (!isSameTimeAndType)
            {
                var existingTask = _dailyTaskService.Tasks.FirstOrDefault(t => 
                    t.Time == timeText && 
                    t.Type == taskType && 
                    t != _editingTask);
                if (existingTask != null)
                {
                    System.Windows.MessageBox.Show("该时间已存在相同类型的任务", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (_editingTask != null)
            {
                // 编辑模式：更新现有任务
                _editingTask.Time = timeText;
                _editingTask.Type = taskType;
                _editingTask.ReminderMinutes = reminderMinutes;

                if (taskType == Services.TaskType.RunProgram)
                {
                    var programPath = TxtProgramPath.Text.Trim();
                    if (string.IsNullOrEmpty(programPath) || !File.Exists(programPath))
                    {
                        System.Windows.MessageBox.Show("请填写有效的程序路径，或点击\"浏览\"按钮选择程序。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    _editingTask.ProgramPath = programPath;
                }
                else if (taskType == Services.TaskType.RunCommand)
                {
                    var command = TxtCommand.Text.Trim();
                    if (string.IsNullOrEmpty(command))
                    {
                        System.Windows.MessageBox.Show("请输入要执行的命令。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    _editingTask.ProgramPath = command;
                }

                _dailyTaskService.SaveTasks();
                _logService.Log("编辑任务", $"修改了任务：{_editingTask.Description}");

                // 刷新任务列表显示
                System.Windows.Data.CollectionViewSource.GetDefaultView(LstScheduledTasks.ItemsSource)?.Refresh();

                // 重置编辑状态
                _editingTask = null;
                BtnAddTask.Content = "添加任务";
            }
            else
            {
                // 添加模式：先检查是否重复
                var task = new Services.ScheduledTask
                {
                    Time = timeText,
                    Type = taskType,
                    ReminderMinutes = reminderMinutes
                };

                if (taskType == Services.TaskType.RunProgram)
                {
                    var programPath = TxtProgramPath.Text.Trim();
                    if (string.IsNullOrEmpty(programPath) || !File.Exists(programPath))
                    {
                        System.Windows.MessageBox.Show("请填写有效的程序路径，或点击\"浏览\"按钮选择程序。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    task.ProgramPath = programPath;
                }

                if (taskType == Services.TaskType.RunCommand)
                {
                    var command = TxtCommand.Text.Trim();
                    if (string.IsNullOrEmpty(command))
                    {
                        System.Windows.MessageBox.Show("请输入要执行的命令。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    task.ProgramPath = command;
                }

                _dailyTaskService.AddTask(task);
                _logService.Log("添加任务", $"添加了每日定时任务：{task.Description}");
            }

            TxtCommand.Text = "";
        }

        // 当前正在编辑的任务
        private Services.ScheduledTask? _editingTask = null;

        // 右键点击任务列表
        private void LstScheduledTasks_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var listBox = sender as System.Windows.Controls.ListBox;
            if (listBox == null) return;

            var hitTest = VisualTreeHelper.HitTest(listBox, e.GetPosition(listBox));
            if (hitTest != null)
            {
                var item = FindParent<System.Windows.Controls.ListBoxItem>(hitTest.VisualHit);
                if (item != null)
                {
                    listBox.SelectedItem = item.DataContext;
                }
            }
        }

        // 编辑任务菜单
        private void EditTaskMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var task = LstScheduledTasks.SelectedItem as Services.ScheduledTask;
            if (task == null)
            {
                System.Windows.MessageBox.Show("请先选择要编辑的任务", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 保存当前任务引用
            _editingTask = task;

            // 填充表单
            var timeParts = task.Time.Split(':');
            if (timeParts.Length == 2)
            {
                TxtTaskHour.Text = timeParts[0];
                TxtTaskMinute.Text = timeParts[1];
            }

            CmbTaskType.SelectedIndex = task.Type switch
            {
                Services.TaskType.Restart => 1,
                Services.TaskType.RunProgram => 2,
                Services.TaskType.RunCommand => 3,
                _ => 0
            };

            TxtReminderMinutes.Text = task.ReminderMinutes.ToString();

            if (task.Type == Services.TaskType.RunProgram)
            {
                TxtProgramPath.Text = task.ProgramPath ?? "";
            }
            else if (task.Type == Services.TaskType.RunCommand)
            {
                TxtCommand.Text = task.ProgramPath ?? "";
            }

            // 更改按钮文本
            BtnAddTask.Content = "保存修改";
        }

        // 删除任务菜单
        private void DeleteTaskMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var task = LstScheduledTasks.SelectedItem as Services.ScheduledTask;
            if (task == null)
            {
                System.Windows.MessageBox.Show("请先选择要删除的任务", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = System.Windows.MessageBox.Show($"确定要删除任务 '{task.Description}' 吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _logService.Log("删除任务", $"删除了任务：{task.Description}");
                _dailyTaskService.RemoveTask(task);
            }
        }

        // 查找父控件
        private T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            if (parent == null) return null;
            return parent is T ? (T)parent : FindParent<T>(parent);
        }

        private void BtnRemoveTask_Click(object sender, RoutedEventArgs e)
        {
            var tasksToDelete = new List<Services.ScheduledTask>();
            
            for (int i = 0; i < LstScheduledTasks.Items.Count; i++)
            {
                var item = LstScheduledTasks.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                if (item != null)
                {
                    var checkBox = FindVisualChild<System.Windows.Controls.CheckBox>(item);
                    if (checkBox != null && checkBox.IsChecked == true)
                    {
                        var task = LstScheduledTasks.Items[i] as Services.ScheduledTask;
                        if (task != null)
                        {
                            tasksToDelete.Add(task);
                        }
                    }
                }
            }
            
            if (tasksToDelete.Count > 0)
            {
                int count = tasksToDelete.Count;
                string message = count == 1 
                    ? $"确定要删除选中的任务吗？" 
                    : $"确定要删除选中的 {count} 个任务吗？";
                
                var result = System.Windows.MessageBox.Show(message, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    foreach (var task in tasksToDelete)
                    {
                        _logService.Log("删除任务", $"删除了任务：{task.Description}");
                        _dailyTaskService.RemoveTask(task);
                    }
                }
            }
            else
            {
                System.Windows.MessageBox.Show("请先选择要删除的任务", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        
        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                {
                    return result;
                }
                var childResult = FindVisualChild<T>(child);
                if (childResult != null)
                {
                    return childResult;
                }
            }
            return null;
        }

        private void BtnClearAllTasks_Click(object sender, RoutedEventArgs e)
        {
            if (_dailyTaskService.Tasks.Count == 0)
            {
                System.Windows.MessageBox.Show("当前没有定时任务", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show("确定要清空所有定时任务吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                foreach (var task in _dailyTaskService.Tasks.ToList())
                {
                    _logService.Log("删除任务", $"删除了任务：{task.Description}");
                    _dailyTaskService.RemoveTask(task);
                }
            }
        }

        // 跳转到整理桌面页面
        private void BtnOpenOrganizer_Click(object sender, RoutedEventArgs e)
        {
            LoadOrganizerSettings();
            HomePanel.Visibility = Visibility.Collapsed;
            DesktopOrganizerPanel.Visibility = Visibility.Visible;
            GlobalBackButton.Visibility = Visibility.Collapsed;
        }

        private void InitializeOrganizer()
        {
            _desktopOrganizerService.StatusChanged += (s, msg) =>
            {
                Dispatcher.Invoke(() =>
                {
                    TxtOrganizeStatus.Text = msg;
                });
            };
        }

        private void LoadOrganizerSettings()
        {
            var settings = _desktopOrganizerService.Settings;
            TxtOrganizeSourceFolder.Text = settings.SourceFolderPath ?? "";
            TxtOrganizeTargetFolder.Text = settings.TargetFolderPath ?? "";

            ChkAutoOrganizeEnabled.IsChecked = settings.AutoExecuteEnabled;

            LstExcludedFolders.ItemsSource = null;
            LstExcludedFolders.ItemsSource = settings.ExcludedFolders;

            LstExcludedExtensions.ItemsSource = null;
            LstExcludedExtensions.ItemsSource = settings.ExcludedExtensions;

            LstAutoOrganizeTimes.ItemsSource = null;
            LstAutoOrganizeTimes.ItemsSource = settings.AutoExecuteTimes;
        }

        private void ChkAutoOrganizeEnabled_Click(object sender, RoutedEventArgs e)
        {
            _desktopOrganizerService.Settings.AutoExecuteEnabled = ChkAutoOrganizeEnabled.IsChecked == true;
            _desktopOrganizerService.SaveSettings();
        }

        private void BtnBrowseSourceFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择要整理的文件来源文件夹",
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TxtOrganizeSourceFolder.Text = dialog.SelectedPath;
            }
        }

        private void BtnBrowseTargetFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择桌面文件整理的目标文件夹",
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TxtOrganizeTargetFolder.Text = dialog.SelectedPath;
            }
        }

        private void BtnAddExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtNewExcludedFolder.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                System.Windows.MessageBox.Show("请输入要排除的文件夹名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var excluded = _desktopOrganizerService.Settings.ExcludedFolders;
            if (excluded.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                System.Windows.MessageBox.Show("该文件夹名称已在排除列表中", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            excluded.Add(name);
            TxtNewExcludedFolder.Text = "";
            _desktopOrganizerService.SaveSettings();
        }

        private void BtnRemoveExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (LstExcludedFolders.SelectedItems.Count > 0)
            {
                var selectedFolders = LstExcludedFolders.SelectedItems.Cast<string>().ToList();
                foreach (var folder in selectedFolders)
                {
                    _desktopOrganizerService.Settings.ExcludedFolders.Remove(folder);
                }
                _desktopOrganizerService.SaveSettings();
            }
            else
            {
                System.Windows.MessageBox.Show("请先选择要移除的文件夹", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClearAllExcludedFolders_Click(object sender, RoutedEventArgs e)
        {
            if (_desktopOrganizerService.Settings.ExcludedFolders.Count == 0)
            {
                System.Windows.MessageBox.Show("当前没有排除的文件夹", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show("确定要清空所有排除的文件夹吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _desktopOrganizerService.Settings.ExcludedFolders.Clear();
                _desktopOrganizerService.SaveSettings();
            }
        }

        private void BtnAddExcludedExtension_Click(object sender, RoutedEventArgs e)
        {
            string ext = TxtNewExcludedExtension.Text.Trim().TrimStart('.');
            if (string.IsNullOrEmpty(ext))
            {
                System.Windows.MessageBox.Show("请输入要排除的扩展名（如 png、pdf）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var excluded = _desktopOrganizerService.Settings.ExcludedExtensions;
            if (excluded.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            {
                System.Windows.MessageBox.Show("该扩展名已在排除列表中", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            excluded.Add(ext);
            TxtNewExcludedExtension.Text = "";
            _desktopOrganizerService.SaveSettings();
        }

        private void BtnRemoveExcludedExtension_Click(object sender, RoutedEventArgs e)
        {
            if (LstExcludedExtensions.SelectedItems.Count > 0)
            {
                var selectedExtensions = LstExcludedExtensions.SelectedItems.Cast<string>().ToList();
                foreach (var ext in selectedExtensions)
                {
                    _desktopOrganizerService.Settings.ExcludedExtensions.Remove(ext);
                }
                _desktopOrganizerService.SaveSettings();
            }
            else
            {
                System.Windows.MessageBox.Show("请先选择要移除的扩展名", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClearAllExcludedExtensions_Click(object sender, RoutedEventArgs e)
        {
            if (_desktopOrganizerService.Settings.ExcludedExtensions.Count == 0)
            {
                System.Windows.MessageBox.Show("当前没有排除的扩展名", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show("确定要清空所有排除的扩展名吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _desktopOrganizerService.Settings.ExcludedExtensions.Clear();
                _desktopOrganizerService.SaveSettings();
            }
        }

        private void BtnAddAutoTime_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtAutoHour.Text.Trim(), out int h) || h < 0 || h > 23)
            {
                System.Windows.MessageBox.Show("小时请输入 0-23 之间的整数", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtAutoHour.SelectAll();
                TxtAutoHour.Focus();
                return;
            }
            if (!int.TryParse(TxtAutoMinute.Text.Trim(), out int m) || m < 0 || m > 59)
            {
                System.Windows.MessageBox.Show("分钟请输入 0-59 之间的整数", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtAutoMinute.SelectAll();
                TxtAutoMinute.Focus();
                return;
            }

            string normalized = $"{h:D2}:{m:D2}";
            var times = _desktopOrganizerService.Settings.AutoExecuteTimes;

            // 检查时间是否已存在（编辑模式下排除当前时间）
            if (times.Any(t => t.Time == normalized) && normalized != _editingAutoTime)
            {
                System.Windows.MessageBox.Show("该时间已在列表中", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_editingAutoTime != null)
            {
                // 编辑模式：更新现有时间
                var existingItem = times.FirstOrDefault(t => t.Time == _editingAutoTime);
                if (existingItem != null)
                {
                    existingItem.Time = normalized;
                }
                _desktopOrganizerService.SaveSettings();

                // 重置编辑状态
                _editingAutoTime = null;
                BtnAddAutoTime.Content = "添加";
            }
            else
            {
                // 添加模式：添加新时间
                times.Add(new Services.AutoExecuteTimeItem { Time = normalized, IsEnabled = true });
                _desktopOrganizerService.SaveSettings();
            }
        }

        // 当前正在编辑的自动整理时间
        private string? _editingAutoTime = null;

        // 右键点击自动整理时间列表
        private void LstAutoOrganizeTimes_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var listBox = sender as System.Windows.Controls.ListBox;
            if (listBox == null) return;

            var hitTest = VisualTreeHelper.HitTest(listBox, e.GetPosition(listBox));
            if (hitTest != null)
            {
                var item = FindParent<System.Windows.Controls.ListBoxItem>(hitTest.VisualHit);
                if (item != null)
                {
                    listBox.SelectedItem = item.DataContext;
                }
            }
        }

        // 编辑自动整理时间菜单
        private void EditAutoTimeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var timeItem = LstAutoOrganizeTimes.SelectedItem as Services.AutoExecuteTimeItem;
            if (timeItem == null)
            {
                System.Windows.MessageBox.Show("请先选择要编辑的时间", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 保存当前时间引用
            _editingAutoTime = timeItem.Time;

            // 填充表单
            var timeParts = timeItem.Time.Split(':');
            if (timeParts.Length == 2)
            {
                TxtAutoHour.Text = timeParts[0];
                TxtAutoMinute.Text = timeParts[1];
            }

            // 更改按钮文本
            BtnAddAutoTime.Content = "保存修改";
        }

        // 删除自动整理时间菜单
        private void DeleteAutoTimeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var timeItem = LstAutoOrganizeTimes.SelectedItem as Services.AutoExecuteTimeItem;
            if (timeItem == null)
            {
                System.Windows.MessageBox.Show("请先选择要删除的时间", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = System.Windows.MessageBox.Show($"确定要删除时间 '{timeItem.Time}' 吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _desktopOrganizerService.Settings.AutoExecuteTimes.Remove(timeItem);
                _desktopOrganizerService.SaveSettings();
            }
        }

        private void BtnRemoveAutoTime_Click(object sender, RoutedEventArgs e)
        {
            if (LstAutoOrganizeTimes.SelectedItems.Count > 0)
            {
                var selectedItems = LstAutoOrganizeTimes.SelectedItems.Cast<Services.AutoExecuteTimeItem>().ToList();
                foreach (var item in selectedItems)
                {
                    _desktopOrganizerService.Settings.AutoExecuteTimes.Remove(item);
                }
                _desktopOrganizerService.SaveSettings();
            }
            else
            {
                System.Windows.MessageBox.Show("请先选择要移除的时间", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClearAllAutoTimes_Click(object sender, RoutedEventArgs e)
        {
            if (_desktopOrganizerService.Settings.AutoExecuteTimes.Count == 0)
            {
                System.Windows.MessageBox.Show("当前没有定时执行时间", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show("确定要清空所有定时执行时间吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _desktopOrganizerService.Settings.AutoExecuteTimes.Clear();
                _desktopOrganizerService.SaveSettings();
            }
        }

        private bool TryParseTaskTime(out int hour, out int minute)
        {
            hour = 0;
            minute = 0;

            if (!int.TryParse(TxtTaskHour.Text.Trim(), out hour) || hour < 0 || hour > 23)
            {
                System.Windows.MessageBox.Show("小时请输入 0-23 之间的整数", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtTaskHour.SelectAll();
                TxtTaskHour.Focus();
                return false;
            }

            if (!int.TryParse(TxtTaskMinute.Text.Trim(), out minute) || minute < 0 || minute > 59)
            {
                System.Windows.MessageBox.Show("分钟请输入 0-59 之间的整数", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtTaskMinute.SelectAll();
                TxtTaskMinute.Focus();
                return false;
            }

            return true;
        }



        private void BtnExecuteOrganizeNow_Click(object sender, RoutedEventArgs e)
        {
            // 执行前先保存当前设置
            var settings = _desktopOrganizerService.Settings;
            settings.SourceFolderPath = TxtOrganizeSourceFolder.Text.Trim();
            settings.TargetFolderPath = TxtOrganizeTargetFolder.Text.Trim();
            _desktopOrganizerService.SaveSettings();

            var organizeResult = _desktopOrganizerService.OrganizeDesktop();

            if (organizeResult.HasError)
            {
                System.Windows.MessageBox.Show($"整理桌面时出错：\n{organizeResult.ErrorMessage}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string message = $"桌面整理完成！\n\n" +
                             $"已移动文件：{organizeResult.MovedFiles.Count} 个\n" +
                             $"已移动文件夹：{organizeResult.MovedFolders.Count} 个\n" +
                             $"跳过快捷方式：{organizeResult.SkippedShortcuts} 个\n" +
                             $"跳过排除文件夹：{organizeResult.SkippedFolders} 个\n" +
                             $"跳过排除扩展名文件：{organizeResult.SkippedExtensions} 个";

            if (organizeResult.HasFailures)
            {
                message += $"\n\n部分项目移动失败：\n{string.Join("\n", organizeResult.FailedItems)}";
            }

            _logService.Log("桌面整理", $"已移动 {organizeResult.MovedFiles.Count} 个文件、{organizeResult.MovedFolders.Count} 个文件夹");
            
            // 显示整理结果
            TxtOrganizeResult.Text = $"✓ 已移动 {organizeResult.MovedFiles.Count} 个文件、{organizeResult.MovedFolders.Count} 个文件夹";
            TxtOrganizeResult.Visibility = Visibility.Visible;
            
            // 3秒后自动隐藏提示
            var hideTimer = new DispatcherTimer();
            hideTimer.Interval = TimeSpan.FromSeconds(3);
            hideTimer.Tick += (s, e) =>
            {
                TxtOrganizeResult.Visibility = Visibility.Collapsed;
                hideTimer.Stop();
            };
            hideTimer.Start();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 如果不是真正退出，则最小化到托盘
            if (!_isExiting)
            {
                e.Cancel = true;
                _trayIconService.MinimizeToTray();
            }
            // 如果是真正退出，则允许关闭
        }
        
        private bool _isExiting = false;
        
        // 真正退出程序
        public void ExitApplication()
        {
            // 如果设置了密码，需要验证密码才能退出
            if (!string.IsNullOrEmpty(_logClearPassword))
            {
                ShowExitPasswordDialog();
            }
            else
            {
                _isExiting = true;
                _trayIconService.ExitApplication();
            }
        }

        private void ShowExitPasswordDialog()
        {
            var dialog = new System.Windows.Window
            {
                Title = "退出确认",
                Width = 400,
                Height = 280,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F5F5F5"))
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(24) };

            var titleText = new System.Windows.Controls.TextBlock
            {
                Text = "退出程序",
                FontSize = 18,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#37474F")),
                Margin = new System.Windows.Thickness(0, 0, 0, 16)
            };
            panel.Children.Add(titleText);

            var infoText = new System.Windows.Controls.TextBlock
            {
                Text = "请输入密码以退出程序",
                FontSize = 14,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#546E7A")),
                Margin = new System.Windows.Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(infoText);

            var passwordBox = new System.Windows.Controls.PasswordBox
            {
                Height = 36,
                FontSize = 14,
                Padding = new System.Windows.Thickness(12, 0, 0, 0),
                Margin = new System.Windows.Thickness(0, 0, 0, 12),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center
            };
            panel.Children.Add(passwordBox);

            var errorText = new System.Windows.Controls.TextBlock
            {
                Text = "",
                FontSize = 13,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E53935")),
                Margin = new System.Windows.Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(errorText);

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var confirmButton = new System.Windows.Controls.Button
            {
                Content = "确认退出",
                Width = 100,
                Height = 36,
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E88E5")),
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                BorderThickness = new System.Windows.Thickness(0)
            };
            buttonPanel.Children.Add(confirmButton);

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "取消",
                Width = 100,
                Height = 36,
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#90A4AE")),
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                BorderThickness = new System.Windows.Thickness(0)
            };
            buttonPanel.Children.Add(cancelButton);

            panel.Children.Add(buttonPanel);
            dialog.Content = panel;

            confirmButton.Click += (s, args) =>
            {
                if (passwordBox.Password == _logClearPassword)
                {
                    dialog.DialogResult = true;
                    _isExiting = true;
                    _trayIconService.ExitApplication();
                }
                else
                {
                    errorText.Text = "密码错误";
                    passwordBox.SelectAll();
                    passwordBox.Focus();
                }
            };

            cancelButton.Click += (s, args) =>
            {
                dialog.DialogResult = false;
            };

            passwordBox.KeyDown += (s, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter)
                {
                    confirmButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }
            };

            dialog.ShowDialog();
        }

        private string _logClearPassword = "";
        private string GetLogPasswordFilePath()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(appDataPath, AppConstants.AppNameEnglish);
            if (!Directory.Exists(appFolder))
            {
                Directory.CreateDirectory(appFolder);
            }
            return Path.Combine(appFolder, "log_password.json");
        }

        private void LoadLogPassword()
        {
            try
            {
                string passwordFile = GetLogPasswordFilePath();
                if (File.Exists(passwordFile))
                {
                    var json = File.ReadAllText(passwordFile);
                    var encryptedPassword = JsonSerializer.Deserialize<string>(json) ?? "";
                    _logClearPassword = DecryptPassword(encryptedPassword);
                }
                else
                {
                    _logClearPassword = "";
                }
            }
            catch
            {
                _logClearPassword = "";
            }
        }

        private string EncryptPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return "";
            
            try
            {
                byte[] passwordBytes = System.Text.Encoding.UTF8.GetBytes(password);
                string base64Password = Convert.ToBase64String(passwordBytes);
                
                byte[] base64Bytes = System.Text.Encoding.UTF8.GetBytes(base64Password);
                for (int i = 0; i < base64Bytes.Length; i++)
                {
                    base64Bytes[i] = (byte)(base64Bytes[i] ^ 0x5A);
                }
                return Convert.ToBase64String(base64Bytes);
            }
            catch
            {
                return password;
            }
        }

        private string DecryptPassword(string encryptedPassword)
        {
            if (string.IsNullOrEmpty(encryptedPassword)) return "";
            
            try
            {
                byte[] base64Bytes = Convert.FromBase64String(encryptedPassword);
                for (int i = 0; i < base64Bytes.Length; i++)
                {
                    base64Bytes[i] = (byte)(base64Bytes[i] ^ 0x5A);
                }
                string base64Password = System.Text.Encoding.UTF8.GetString(base64Bytes);
                byte[] passwordBytes = Convert.FromBase64String(base64Password);
                return System.Text.Encoding.UTF8.GetString(passwordBytes);
            }
            catch
            {
                return encryptedPassword;
            }
        }

        private void SaveLogPassword()
        {
            try
            {
                string passwordFile = GetLogPasswordFilePath();
                string encryptedPassword = EncryptPassword(_logClearPassword);
                var json = JsonSerializer.Serialize(encryptedPassword);
                File.WriteAllText(passwordFile, json);
            }
            catch { }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            // 无密码时直接确认清空
            if (string.IsNullOrEmpty(_logClearPassword))
            {
                if (System.Windows.MessageBox.Show("确定要清空所有操作日志吗？", "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _logService.Clear();
                }
                return;
            }

            var dialog = new Window
            {
                Title = "清空日志验证",
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(WpfColor.FromArgb(255, 236, 239, 241))
            };

            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "请输入密码以清空日志", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            panel.Children.Add(new TextBlock { Text = "密码", FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });

            var pwdBox = new System.Windows.Controls.PasswordBox { Height = 32, FontSize = 13, Padding = new Thickness(10, 0, 10, 0) };
            var errorText = new TextBlock { Text = "", FontSize = 12, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#E53935")), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            panel.Children.Add(pwdBox);
            panel.Children.Add(errorText);

            var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            var btnOk = CreateDialogButton("确定", (WpfColor)WpfColorConverter.ConvertFromString("#1E88E5"));
            btnOk.Margin = new Thickness(0, 0, 10, 0);
            var btnCancel = CreateDialogButton("取消", (WpfColor)WpfColorConverter.ConvertFromString("#B0BEC5"));
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            panel.Children.Add(btnPanel);
            dialog.Content = panel;

            btnOk.Click += (s, args) =>
            {
                if (pwdBox.Password == _logClearPassword)
                {
                    _logService.Clear();
                    dialog.DialogResult = true;
                }
                else
                {
                    errorText.Text = "密码错误，请重试";
                    errorText.Visibility = Visibility.Visible;
                    pwdBox.SelectAll();
                    pwdBox.Focus();
                }
            };

            btnCancel.Click += (s, args) => { dialog.DialogResult = false; };

            pwdBox.Focus();
            dialog.ShowDialog();
        }

        #region 设置面板

        private void LoadSettingsState()
        {
            LoadAutoStartState();
            UpdatePasswordStatus();
            LoadDevModeState();
        }

        private void LoadAutoStartState()
        {
            ChkAutoStart.IsChecked = IsAutoStartEnabled();
        }

        private static bool IsAutoStartEnabled()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoStartRegKey, false);
            return key?.GetValue(AutoStartRegName) != null;
        }

        private void ChkAutoStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoStartRegKey, true);
                if (ChkAutoStart.IsChecked == true)
                    key!.SetValue(AutoStartRegName, $"\"{Environment.ProcessPath}\" --minimized");
                else
                    key!.DeleteValue(AutoStartRegName, false);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"设置开机自启失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                ChkAutoStart.IsChecked = IsAutoStartEnabled();
            }
        }

        private void UpdatePasswordStatus()
        {
            if (string.IsNullOrEmpty(_logClearPassword))
            {
                TxtPasswordStatus.Text = "当前未设置密码，任何人都可以清空日志。";
                TxtPasswordStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FB8C00"));
                TxtOldPwdLabel.Visibility = Visibility.Collapsed;
                PwdOld.Visibility = Visibility.Collapsed;
            }
            else
            {
                TxtPasswordStatus.Text = "已设置密码保护。";
                TxtPasswordStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#43A047"));
                TxtOldPwdLabel.Visibility = Visibility.Visible;
                PwdOld.Visibility = Visibility.Visible;
            }
        }

        private void BtnSavePassword_Click(object sender, RoutedEventArgs e)
        {
            PwdError.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrEmpty(_logClearPassword))
            {
                if (PwdOld.Password != _logClearPassword)
                {
                    PwdError.Text = "原密码不正确";
                    PwdError.Visibility = Visibility.Visible;
                    PwdOld.SelectAll();
                    PwdOld.Focus();
                    return;
                }
            }

            if (string.IsNullOrEmpty(PwdNew.Password))
            {
                PwdError.Text = "新密码不能为空";
                PwdError.Visibility = Visibility.Visible;
                PwdNew.Focus();
                return;
            }

            if (PwdNew.Password != PwdConfirm.Password)
            {
                PwdError.Text = "两次输入的密码不一致";
                PwdError.Visibility = Visibility.Visible;
                PwdConfirm.SelectAll();
                PwdConfirm.Focus();
                return;
            }

            _logClearPassword = PwdNew.Password;
            SaveLogPassword();
            PwdOld.Password = "";
            PwdNew.Password = "";
            PwdConfirm.Password = "";
            UpdatePasswordStatus();
            System.Windows.MessageBox.Show("密码已保存", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClearPassword_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_logClearPassword))
            {
                System.Windows.MessageBox.Show("当前未设置密码，无需清除。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrEmpty(PwdOld.Password))
            {
                PwdError.Text = "请输入原密码后再清除";
                PwdError.Visibility = Visibility.Visible;
                PwdOld.Focus();
                return;
            }

            if (PwdOld.Password != _logClearPassword)
            {
                PwdError.Text = "原密码不正确";
                PwdError.Visibility = Visibility.Visible;
                PwdOld.SelectAll();
                PwdOld.Focus();
                return;
            }

            if (System.Windows.MessageBox.Show("确定要清除日志密码吗？清除后任何人都可以清空日志。", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _logClearPassword = "";
                SaveLogPassword();
                PwdOld.Password = "";
                PwdNew.Password = "";
                PwdConfirm.Password = "";
                PwdError.Visibility = Visibility.Collapsed;
                UpdatePasswordStatus();
                System.Windows.MessageBox.Show("密码已清除", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnResetSettings_Click(object sender, RoutedEventArgs e)
        {
            // 如果设置了日志密码，需要先验证密码
            if (!string.IsNullOrEmpty(_logClearPassword))
            {
                var passwordBox = new System.Windows.Controls.PasswordBox();
                passwordBox.PasswordChar = '*';
                passwordBox.Width = 200;
                passwordBox.Height = 30;
                passwordBox.FontSize = 14;

                var confirmResult = System.Windows.MessageBox.Show(
                    "请输入密码以确认恢复默认设置", 
                    "需要密码", 
                    MessageBoxButton.OKCancel, 
                    MessageBoxImage.Question);

                if (confirmResult != MessageBoxResult.OK)
                    return;

                // 创建密码输入对话框
                var dialog = new System.Windows.Window
                {
                    Title = "输入密码",
                    Width = 300,
                    Height = 150,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                    ResizeMode = System.Windows.ResizeMode.NoResize,
                    Owner = this
                };

                var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(20) };
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "请输入密码：", Margin = new System.Windows.Thickness(0, 0, 0, 10) });
                panel.Children.Add(passwordBox);

                var btnPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 15, 0, 0) };
                var okBtn = new System.Windows.Controls.Button { Content = "确定", Width = 70, Margin = new System.Windows.Thickness(0, 0, 10, 0) };
                var cancelBtn = new System.Windows.Controls.Button { Content = "取消", Width = 70 };
                btnPanel.Children.Add(okBtn);
                btnPanel.Children.Add(cancelBtn);
                panel.Children.Add(btnPanel);

                bool? dialogResult = false;
                okBtn.Click += (s, args) => { dialogResult = true; dialog.Close(); };
                cancelBtn.Click += (s, args) => { dialogResult = false; dialog.Close(); };

                dialog.Content = panel;
                dialog.ShowDialog();

                if (dialogResult != true || passwordBox.Password != _logClearPassword)
                {
                    System.Windows.MessageBox.Show("密码不正确，操作已取消", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            var result = System.Windows.MessageBox.Show(
                "确定要恢复所有默认设置吗？\n\n此操作将删除以下配置文件：\n- 自定义快速启动应用\n- 定时任务\n- 桌面整理设置\n- 开发者模式设置\n\n注意：日志密码不会被清除。\n\n此操作不可撤销！", 
                "确认恢复默认", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                // 删除配置文件（检查多个可能的位置，不包括日志密码）
                string[] possibleDirs = {
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                    Environment.CurrentDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\" + AppConstants.AppNameEnglish
                };

                string[] configFiles = {
                    "custom_apps.json",
                    "scheduled_tasks.json",
                    "desktop_organizer_settings.json",
                    "dev_mode.json"
                };

                foreach (string dir in possibleDirs)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                        continue;

                    foreach (string file in configFiles)
                    {
                        string filePath = Path.Combine(dir, file);
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                        }
                    }
                }

                // 同时重置内存中的设置（不重置日志密码）
                _customApps.Clear();
                SaveCustomApps();
                RebuildCustomAppButtons();

                _dailyTaskService.Tasks.Clear();
                _dailyTaskService.SaveTasks();

                _desktopOrganizerService.Settings = new Services.DesktopOrganizerSettings();
                _desktopOrganizerService.SaveSettings();

                IsDevModeEnabled = false;
                ChkDevMode.IsChecked = false;
                try
                {
                    var json = JsonSerializer.Serialize(false);
                    File.WriteAllText(DevModeSettingsFile, json);
                }
                catch { }
                UpdateDevPanelVisibility();

                System.Windows.MessageBox.Show("已恢复所有默认设置！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"恢复默认设置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void LoadDevModeStateStatic()
        {
            try
            {
                if (File.Exists(DevModeSettingsFile))
                {
                    var json = File.ReadAllText(DevModeSettingsFile);
                    IsDevModeEnabled = JsonSerializer.Deserialize<bool>(json);
                }
            }
            catch { }
        }

        private void LoadHighPriorityProcesses()
        {
            try
            {
                if (File.Exists(HighPriorityProcessesFile))
                {
                    var json = File.ReadAllText(HighPriorityProcessesFile);
                    _highPriorityProcessIds = JsonSerializer.Deserialize<HashSet<int>>(json) ?? new HashSet<int>();
                }
            }
            catch { _highPriorityProcessIds = new HashSet<int>(); }
        }

        private void SaveHighPriorityProcesses()
        {
            try
            {
                var json = JsonSerializer.Serialize(_highPriorityProcessIds);
                File.WriteAllText(HighPriorityProcessesFile, json);
            }
            catch { }
        }

        private void LoadDevModeState()
        {
            LoadDevModeStateStatic();
            ChkDevMode.IsChecked = IsDevModeEnabled;
        }

        private void ChkDevMode_Click(object sender, RoutedEventArgs e)
        {
            IsDevModeEnabled = ChkDevMode.IsChecked == true;
            try
            {
                var json = JsonSerializer.Serialize(IsDevModeEnabled);
                File.WriteAllText(DevModeSettingsFile, json);
            }
            catch { }

            // 根据开发者模式状态开启/关闭USB检测
            if (IsDevModeEnabled)
            {
                // 开启USB检测
                _usbDetectionService.UsbDevicesChanged += UsbDevicesChanged;
                _usbDetectionService.StartMonitoring();
                UpdateUsbButtons(_usbDetectionService.GetUsbDevices());
            }
            else
            {
                // 关闭USB检测
                _usbDetectionService.StopMonitoring();
                _usbDetectionService.UsbDevicesChanged -= UsbDevicesChanged;
                UsbPanel.Visibility = Visibility.Collapsed;
                UsbDevicesPanel.Children.Clear();
            }
        }

        private void ApplyUIMode()
        {
            if (UseBeautifulUI)
            {
                EnableBeautifulUI();
            }
            else
            {
                DisableBeautifulUI();
            }
        }

        private void EnableBeautifulUI()
        {
            // 启用美观UI效果
            // 这里可以根据需要添加具体的UI效果
        }

        private void DisableBeautifulUI()
        {
            // 禁用美观UI效果
            // 清理图标缓存
            foreach (var icon in _iconCache.Values)
            {
                if (icon is System.Windows.Media.Imaging.BitmapSource bitmap)
                {
                    bitmap.Freeze();
                }
            }
            _iconCache.Clear();
            
            // 强制垃圾回收
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        #endregion

        private void UpdateDevPanelVisibility()
        {
            DevToolsPanel.Visibility = IsDevModeEnabled ? Visibility.Visible : Visibility.Collapsed;
            // 同时控制USB面板的显示
            if (!IsDevModeEnabled)
            {
                UsbPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                // 主页不显示固定返回按钮
                if (scrollViewer.Name == "HomePanel")
                {
                    GlobalBackButton.Visibility = Visibility.Collapsed;
                    return;
                }
                
                if (scrollViewer.VerticalOffset > 60)
                {
                    // 页面滚动超过60像素，显示固定返回按钮
                    GlobalBackButton.Visibility = Visibility.Visible;
                }
                else
                {
                    // 页面在顶部附近，隐藏固定返回按钮
                    GlobalBackButton.Visibility = Visibility.Collapsed;
                }
            }
        }



        private void DevTestShutdownReminder_Click(object sender, RoutedEventArgs e)
        {
            var window = new ShutdownReminderWindow();
            window.CancelRequested += (s, args) =>
            {
                _logService.Log("开发者测试", "关机提醒窗口已关闭（取消关机）");
            };
            window.Show();
        }

        private void DevTestSettings_Click(object sender, RoutedEventArgs e)
        {
            LoadSettingsState();
            HomePanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
        }

        private void DevTestAbout_Click(object sender, RoutedEventArgs e)
        {
            BtnAbout_Click(sender, e);
        }

        private void DevTestAppUsage_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_logClearPassword))
            {
                ShowAppBlockedDialog("测试应用", "C:\\Windows\\System32\\notepad.exe");
            }
            else
            {
                System.Windows.MessageBox.Show("请先在设置页面设置日志密码", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            LoadSettingsState();
            HomePanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
            GlobalBackButton.Visibility = Visibility.Collapsed;
        }

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "关于",
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(WpfColor.FromArgb(255, 236, 239, 241))
            };

            var panel = new StackPanel { Margin = new Thickness(24) };

            var icon = new TextBlock { Text = "💻", FontSize = 48, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 12) };
            panel.Children.Add(icon);

            panel.Children.Add(new TextBlock { Text = "班级电脑控制助手", FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#37474F")) });
            panel.Children.Add(new TextBlock { Text = $"版本 {AppConstants.AppVersion}", FontSize = 13, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#90A4AE")), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 16) });

            var authorLink = new TextBlock { Text = "作者 Bilibili 主页", FontSize = 13, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#00A1D6")), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16), Cursor = System.Windows.Input.Cursors.Hand, TextDecorations = System.Windows.TextDecorations.Underline };
            authorLink.MouseLeftButtonUp += (s, args) =>
            {
                try { Process.Start(new ProcessStartInfo(AppConstants.AppUrl) { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(authorLink);

            panel.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 12) });
            panel.Children.Add(new TextBlock { Text = "功能介绍", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#37474F")), Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(new TextBlock { Text = "• 电源控制：关机、重启、睡眠、休眠\n• 任务计划：设置每日定时任务和提醒\n• 桌面整理：自动整理桌面文件和定时整理\n• 前台优化：进程优先级管理和自动优化\n• 应用管理：限制应用使用时间和密码保护\n• 快速启动：自定义快捷启动应用\n• 操作日志：记录所有操作历史", FontSize = 13, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#546E7A")), LineHeight = 22, Margin = new Thickness(8, 0, 0, 16) });

            var btnCheckUpdate = CreateDialogButton("检查更新", (WpfColor)WpfColorConverter.ConvertFromString("#7E57C2"));
            btnCheckUpdate.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            btnCheckUpdate.Margin = new Thickness(0, 4, 0, 8);
            panel.Children.Add(btnCheckUpdate);

            var btnOk = CreateDialogButton("确定", (WpfColor)WpfColorConverter.ConvertFromString("#1E88E5"));
            btnOk.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            btnOk.Margin = new Thickness(0, 4, 0, 8);
            panel.Children.Add(btnOk);

            dialog.Content = panel;

            btnCheckUpdate.Click += (s, args) => { CheckForUpdate(); };
            btnOk.Click += (s, args) => { dialog.DialogResult = true; };
            dialog.ShowDialog();
        }

        private async void CheckForUpdate()
        {
            if (!CheckNetworkConnection())
            {
                var result = System.Windows.MessageBox.Show(
                    "网络连接不可用，无法检查更新。\n\n请检查网络连接后重试，或点击\"是\"尝试修复网络问题。",
                    "网络连接检查",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    TryFixNetworkConnection();
                }
                return;
            }

            try
            {
                var updateService = new UpdateService();
                var updateInfo = await updateService.CheckForUpdateAsync();

                if (updateInfo.HasUpdate)
                {
                    ShowUpdateAvailableDialog(updateInfo);
                }
                else
                {
                    System.Windows.MessageBox.Show("当前已是最新版本！", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"检查更新失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CheckNetworkConnection()
        {
            try
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = client.GetAsync("https://www.baidu.com").GetAwaiter().GetResult();
                    return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        private void TryFixNetworkConnection()
        {
            try
            {
                var dialog = new Window
                {
                    Title = "网络修复",
                    Width = 400,
                    Height = 300,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.NoResize,
                    Background = new SolidColorBrush(WpfColor.FromArgb(255, 236, 239, 241))
                };

                var panel = new StackPanel { Margin = new Thickness(24) };

                panel.Children.Add(new TextBlock { Text = "网络修复选项", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#37474F")), Margin = new Thickness(0, 0, 0, 16) });
                panel.Children.Add(new TextBlock { Text = "请选择网络修复方式：", FontSize = 13, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#546E7A")), Margin = new Thickness(0, 0, 0, 12) });

                var btnResetDNS = CreateDialogButton("重置DNS", (WpfColor)WpfColorConverter.ConvertFromString("#1E88E5"));
                btnResetDNS.Margin = new Thickness(0, 0, 0, 8);
                panel.Children.Add(btnResetDNS);

                var btnFlushDNS = CreateDialogButton("刷新DNS缓存", (WpfColor)WpfColorConverter.ConvertFromString("#43A047"));
                btnFlushDNS.Margin = new Thickness(0, 0, 0, 8);
                panel.Children.Add(btnFlushDNS);

                var btnResetNetwork = CreateDialogButton("重置网络适配器", (WpfColor)WpfColorConverter.ConvertFromString("#FB8C00"));
                btnResetNetwork.Margin = new Thickness(0, 0, 0, 8);
                panel.Children.Add(btnResetNetwork);

                var btnCancel = CreateDialogButton("取消", (WpfColor)WpfColorConverter.ConvertFromString("#90A4AE"));
                btnCancel.Margin = new Thickness(0, 0, 0, 8);
                panel.Children.Add(btnCancel);

                dialog.Content = panel;

                btnResetDNS.Click += (s, args) =>
                {
                    ExecuteNetworkCommand("ipconfig /flushdns", "DNS缓存已刷新");
                    dialog.DialogResult = true;
                };

                btnFlushDNS.Click += (s, args) =>
                {
                    ExecuteNetworkCommand("ipconfig /registerdns", "DNS已重新注册");
                    dialog.DialogResult = true;
                };

                btnResetNetwork.Click += (s, args) =>
                {
                    ExecuteNetworkCommand("netsh winsock reset", "网络适配器已重置，请重启电脑");
                    dialog.DialogResult = true;
                };

                btnCancel.Click += (s, args) => { dialog.DialogResult = false; };

                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"打开网络修复对话框失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteNetworkCommand(string command, string successMessage)
        {
            try
            {
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };

                System.Diagnostics.Process.Start(processInfo)?.WaitForExit();
                System.Windows.MessageBox.Show(successMessage, "网络修复", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"执行网络修复命令失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowUpdateAvailableDialog(UpdateInfo updateInfo)
        {
            var dialog = new Window
            {
                Title = "发现新版本",
                Width = 420,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(WpfColor.FromArgb(255, 236, 239, 241))
            };

            var panel = new StackPanel { Margin = new Thickness(24) };

            var icon = new TextBlock { Text = "🎉", FontSize = 48, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 12) };
            panel.Children.Add(icon);

            panel.Children.Add(new TextBlock { Text = "发现新版本！", FontSize = 18, FontWeight = FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#37474F")) });
            panel.Children.Add(new TextBlock { Text = $"版本 {updateInfo.LatestVersion}", FontSize = 14, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#7E57C2")), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 16) });

            panel.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 12) });
            panel.Children.Add(new TextBlock { Text = "更新内容：", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#37474F")), Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(new TextBlock { Text = updateInfo.ReleaseNotes, FontSize = 13, Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#546E7A")), LineHeight = 20, Margin = new Thickness(8, 0, 0, 16) });

            var buttonPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            var btnUpdate = CreateDialogButton("立即更新", (WpfColor)WpfColorConverter.ConvertFromString("#43A047"));
            btnUpdate.Width = 120;
            btnUpdate.Margin = new Thickness(0, 0, 8, 0);
            buttonPanel.Children.Add(btnUpdate);

            var btnCancel = CreateDialogButton("稍后", (WpfColor)WpfColorConverter.ConvertFromString("#90A4AE"));
            btnCancel.Width = 120;
            buttonPanel.Children.Add(btnCancel);

            panel.Children.Add(buttonPanel);

            dialog.Content = panel;

            btnUpdate.Click += (s, args) =>
            {
                dialog.DialogResult = true;
                DownloadAndInstallUpdate(updateInfo);
            };
            btnCancel.Click += (s, args) => { dialog.DialogResult = false; };

            dialog.ShowDialog();
        }

        private async void DownloadAndInstallUpdate(UpdateInfo updateInfo)
        {
            try
            {
                var result = System.Windows.MessageBox.Show(
                    "即将下载并安装更新，程序将自动关闭。\n\n是否继续？",
                    "确认更新",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _logService.Log("系统更新", $"开始下载更新版本 {updateInfo.LatestVersion}");
                    
                    await DownloadAndInstallUpdateAsync(updateInfo);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"更新失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                _logService.Log("系统更新", $"更新失败：{ex.Message}");
            }
        }

        private async Task DownloadAndInstallUpdateAsync(UpdateInfo updateInfo)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"ClassRoomControl_Update_{updateInfo.LatestVersion}.exe");
            
            try
            {
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(30);
                    
                    var response = await httpClient.GetAsync(updateInfo.DownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    
                    var totalBytes = response.Content.Headers.ContentLength ?? 0;
                    var progressWindow = CreateDownloadProgressWindow(totalBytes);
                    progressWindow.Show();
                    
                    var downloadedBytes = 0L;
                    var buffer = new byte[8192];
                    
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        int bytesRead;
                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            downloadedBytes += bytesRead;
                            
                            UpdateDownloadProgress(progressWindow, downloadedBytes, totalBytes);
                        }
                    }
                    
                    progressWindow.Close();
                }
                
                _logService.Log("系统更新", $"下载完成，开始安装更新版本 {updateInfo.LatestVersion}");
                
                var processInfo = new ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true,
                    Arguments = $"/SILENT /DIR=\"{Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)}\""
                };
                
                Process.Start(processInfo);
                
                _isExiting = true;
                _trayIconService.ExitApplication();
            }
            catch (Exception ex)
            {
                _logService.Log("系统更新", $"下载更新失败：{ex.Message}");
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                throw;
            }
        }

        private Window CreateDownloadProgressWindow(long totalBytes)
        {
            var window = new Window
            {
                Title = "下载更新",
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(WpfColor.FromArgb(255, 236, 239, 241))
            };
            
            var panel = new StackPanel { Margin = new Thickness(24) };
            
            panel.Children.Add(new TextBlock 
            { 
                Text = "正在下载更新...", 
                FontSize = 16, 
                FontWeight = FontWeights.Bold, 
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#37474F")), 
                Margin = new Thickness(0, 0, 0, 12) 
            });
            
            var progressBar = new System.Windows.Controls.ProgressBar 
            { 
                Height = 24, 
                Minimum = 0, 
                Maximum = totalBytes > 0 ? totalBytes : 100 
            };
            progressBar.Name = "DownloadProgressBar";
            panel.Children.Add(progressBar);
            
            var progressText = new TextBlock 
            { 
                Text = "0%", 
                FontSize = 13, 
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#546E7A")), 
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            progressText.Name = "DownloadProgressText";
            panel.Children.Add(progressText);
            
            window.Content = panel;
            return window;
        }

        private void UpdateDownloadProgress(Window window, long downloadedBytes, long totalBytes)
        {
            if (!window.IsLoaded) return;
            
            window.Dispatcher.Invoke(() =>
            {
                var progressBar = window.FindName("DownloadProgressBar") as System.Windows.Controls.ProgressBar;
                var progressText = window.FindName("DownloadProgressText") as TextBlock;
                
                if (progressBar != null)
                {
                    progressBar.Value = downloadedBytes;
                }
                
                if (progressText != null)
                {
                    var percentage = totalBytes > 0 ? (downloadedBytes * 100.0 / totalBytes) : 0;
                    progressText.Text = $"{percentage:F1}% ({FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes)})";
                }
            });
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void BtnOpenTaskPlan_Click(object sender, RoutedEventArgs e)
        {
            HomePanel.Visibility = Visibility.Collapsed;
            TaskPlanPanel.Visibility = Visibility.Visible;
            GlobalBackButton.Visibility = Visibility.Collapsed;
        }

        // 返回主页
        private void BtnBackToHome_Click(object sender, RoutedEventArgs e)
        {
            GlobalBackButton.Visibility = Visibility.Collapsed;
            HomePanel.Visibility = Visibility.Visible;
            TaskPlanPanel.Visibility = Visibility.Collapsed;
            DesktopOrganizerPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            ForegroundOptimizationPanel.Visibility = Visibility.Collapsed;
            AppUsagePanel.Visibility = Visibility.Collapsed;
            LoadDevModeStateStatic();
            LoadLogPassword();
            UpdateDevPanelVisibility();
        }

        // 供 App 调用，强制释放托盘图标
        public void ForceDisposeTrayIcon()
        {
            _trayIconService?.Dispose();
        }

        // 窗口关闭时释放所有资源
        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _infoUpdateTimer?.Stop();
            _infoUpdateTimer = null;
            
            _schedulerService?.Dispose();
            _dailyTaskService?.Dispose();
            _desktopOrganizerService?.Dispose();
            _appUsageService?.Dispose();
            _usbDetectionService?.Dispose();
            _trayIconService?.Dispose();
            
            _iconCache.Clear();
            _processList.Clear();
            _customApps.Clear();
        }

        // ========== USB检测功能 ==========
        private void InitializeUsbDetection()
        {
            // 只有在开发者模式下才启用USB检测
            if (!IsDevModeEnabled)
            {
                UsbPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // 监听USB设备变化
            _usbDetectionService.UsbDevicesChanged += UsbDevicesChanged;
            _usbDetectionService.StartMonitoring();

            // 初始检测
            UpdateUsbButtons(_usbDetectionService.GetUsbDevices());
        }

        private void UsbDevicesChanged(object? sender, List<Services.UsbDeviceInfo> devices)
        {
            UpdateUsbButtons(devices);
        }

        private void UpdateUsbButtons(List<Services.UsbDeviceInfo> devices)
        {
            if (UsbDevicesPanel == null || UsbPanel == null)
                return;

            // 清空现有按钮
            UsbDevicesPanel.Children.Clear();

            if (devices.Count == 0)
            {
                // 没有U盘，隐藏USB区域
                UsbPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // 显示USB区域
            UsbPanel.Visibility = Visibility.Visible;

            // 按U盘名称分组（同一U盘可能有多个分区）
            var groupedDevices = devices.GroupBy(d => d.FriendlyName).ToList();

            for (int i = 0; i < groupedDevices.Count; i++)
            {
                var group = groupedDevices[i];
                string usbName = group.Key;
                var partitions = group.ToList();

                // 创建U盘条目卡片
                var usbCard = new System.Windows.Controls.Border
                {
                    Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FAFAFA")),
                    BorderBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#E0E0E0")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(12, 10, 12, 10)
                };

                // 创建U盘信息面板
                var usbGroupPanel = new System.Windows.Controls.DockPanel
                {
                    LastChildFill = true
                };

                // U盘名称（显示磁盘信息而非盘符）
                var nameBlock = new TextBlock
                {
                    Text = usbName,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#37474F")),
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Controls.DockPanel.SetDock(nameBlock, System.Windows.Controls.Dock.Left);
                usbGroupPanel.Children.Add(nameBlock);

                // 分区列表（显示在名称右侧）
                var partitionsPanel = new System.Windows.Controls.WrapPanel
                {
                    Margin = new Thickness(12, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                foreach (var partition in partitions)
                {
                    var partitionBlock = new TextBlock
                    {
                        Text = partition.DriveLetter,
                        FontSize = 12,
                        Margin = new Thickness(4, 0, 0, 0),
                        Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#78909C"))
                    };
                    partitionsPanel.Children.Add(partitionBlock);
                }
                System.Windows.Controls.DockPanel.SetDock(partitionsPanel, System.Windows.Controls.Dock.Left);
                usbGroupPanel.Children.Add(partitionsPanel);

                // 弹出按钮（靠右对齐）
                var ejectBtn = new System.Windows.Controls.Button
                {
                    Content = "弹出",
                    Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#E53935")),
                    Foreground = WpfBrushes.White,
                    Padding = new Thickness(8, 3, 8, 3),
                    Width = 50,
                    Height = 28,
                    FontSize = 12,
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right
                };
                ejectBtn.Click += (s, e) => EjectUsbDevices(partitions);
                System.Windows.Controls.DockPanel.SetDock(ejectBtn, System.Windows.Controls.Dock.Right);
                usbGroupPanel.Children.Add(ejectBtn);

                usbCard.Child = usbGroupPanel;
                UsbDevicesPanel.Children.Add(usbCard);
            }
        }

        private void EjectUsbDevices(List<Services.UsbDeviceInfo> devices)
        {
            bool allSuccess = true;
            string usbName = devices[0].FriendlyName;
            int totalClosedProcesses = 0;

            foreach (var device in devices)
            {
                // 首先关闭占用该驱动器的进程
                int closed = _usbDetectionService.CloseProcessesUsingDrive(device.DriveLetter);
                totalClosedProcesses += closed;
                
                // 等待一下让进程完全关闭
                System.Threading.Thread.Sleep(500);
                
                // 然后使用Windows API弹出
                if (!_usbDetectionService.EjectUsbWithExplorerApi(device.DriveLetter))
                {
                    // 如果API方法失败，尝试使用WMI方法
                    if (!_usbDetectionService.EjectUsb(device.DriveLetter))
                    {
                        allSuccess = false;
                        break;
                    }
                }
            }

            if (allSuccess)
            {
                _logService.Log("USB操作", $"已弹出U盘：{usbName}");
                
                string message = $"{usbName} 已成功弹出";
                if (totalClosedProcesses > 0)
                {
                    message += $"\n\n已自动关闭 {totalClosedProcesses} 个占用该设备的程序";
                }
                System.Windows.MessageBox.Show(message, "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                ShowUsbNotification(usbName);
            }
            else
            {
                System.Windows.MessageBox.Show(
                    $"无法弹出 {usbName}，可能有程序正在使用该设备", 
                    "提示", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Warning);
            }
        }

        private void ShowUsbSelectionDialog(object sender, RoutedEventArgs e)
        {
            var devices = _usbDetectionService.GetUsbDevices();
            if (devices.Count == 0)
                return;

            var dialog = new Window
            {
                Title = "选择要弹出的U盘",
                Width = 350,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = "请选择要弹出的U盘：",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#37474F"))
            });

            var listBox = new System.Windows.Controls.ListBox
            {
                Height = 120,
                Margin = new Thickness(0, 0, 0, 12)
            };
            foreach (var device in devices)
            {
                listBox.Items.Add($"{device.VolumeName} ({device.DriveLetter})");
            }
            panel.Children.Add(listBox);

            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };
            var okBtn = new System.Windows.Controls.Button
            {
                Content = "弹出",
                Width = 70,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#1E88E5")),
                Foreground = WpfBrushes.White,
                Style = (Style)FindResource("SmallButton")
            };
            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "取消",
                Width = 70,
                Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#9E9E9E")),
                Foreground = WpfBrushes.White,
                Style = (Style)FindResource("SmallButton")
            };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            okBtn.Click += (s, args) =>
            {
                if (listBox.SelectedIndex >= 0)
                {
                    EjectUsbDevice(devices[listBox.SelectedIndex]);
                }
                dialog.Close();
            };
            cancelBtn.Click += (s, args) => dialog.Close();

            dialog.Content = panel;
            dialog.ShowDialog();
        }

        private void EjectUsbDevice(Services.UsbDeviceInfo device)
        {
            bool success = _usbDetectionService.EjectUsb(device.DriveLetter);

            if (success)
            {
                _logService.Log("USB操作", $"已弹出U盘：{device.VolumeName} ({device.DriveLetter})");
                ShowUsbNotification(device.VolumeName);
            }
            else
            {
                System.Windows.MessageBox.Show(
                    $"无法弹出 {device.VolumeName}，可能有程序正在使用该设备", 
                    "提示", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Warning);
            }
        }

        private void ShowUsbNotification(string volumeName)
        {
            // 创建右下角弹窗提示
            var notification = new Window
            {
                Width = 300,
                Height = 100,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = WpfBrushes.White,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                BorderBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#E0E0E0")),
                BorderThickness = new Thickness(1)
            };

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = $"已弹出 {volumeName}",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#37474F"))
            });
            panel.Children.Add(new TextBlock
            {
                Text = "现在可以安全移除外接设备",
                FontSize = 12,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#78909C")),
                Margin = new Thickness(0, 4, 0, 0)
            });
            notification.Content = panel;

            // 设置位置到右下角
            var screenWidth = SystemParameters.WorkArea.Width;
            var screenHeight = SystemParameters.WorkArea.Height;
            notification.Left = screenWidth - notification.Width - 20;
            notification.Top = screenHeight - notification.Height - 80;

            notification.Show();

            // 3秒后自动关闭
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.Tick += (s, e) =>
            {
                notification.Close();
                timer.Stop();
            };
            timer.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            _infoUpdateTimer?.Stop();
            _infoUpdateTimer = null;
            
            _schedulerService?.Dispose();
            _dailyTaskService?.Dispose();
            _desktopOrganizerService?.Dispose();
            _appUsageService?.Dispose();
            _usbDetectionService?.Dispose();
            _trayIconService?.Dispose();
            
            // 清理图标缓存
            foreach (var icon in _iconCache.Values)
            {
                if (icon is System.Windows.Media.Imaging.BitmapSource bitmap)
                {
                    bitmap.Freeze();
                }
            }
            _iconCache.Clear();
            
            _processList.Clear();
            _customApps.Clear();
            
            // 强制垃圾回收
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            base.OnClosed(e);
        }

        // ========== 前台优化功能 ==========
        private void BtnOpenForegroundOptimization_Click(object sender, RoutedEventArgs e)
        {
            HomePanel.Visibility = Visibility.Collapsed;
            TaskPlanPanel.Visibility = Visibility.Collapsed;
            DesktopOrganizerPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            ForegroundOptimizationPanel.Visibility = Visibility.Visible;
            GlobalBackButton.Visibility = Visibility.Collapsed;
            
            RefreshProcessList();
        }

        private void RefreshProcessList()
        {
            if (_isInBackground) return;
            
            try
            {
                var newProcessList = new List<ProcessInfo>();
                int processCount = 0;
                // 根据UI模式限制进程数量
                int maxProcesses = UseBeautifulUI ? 100 : 50;
                
                foreach (var process in System.Diagnostics.Process.GetProcesses())
                {
                    if (processCount >= maxProcesses) break;
                    
                    using (process)
                    {
                        try
                        {
                            string memoryUsage = $"{(process.WorkingSet64 / 1024 / 1024):N0} MB";
                            string priority = GetPriorityString(process.PriorityClass);
                            string displayName = !string.IsNullOrEmpty(process.MainWindowTitle) 
                                ? process.MainWindowTitle 
                                : process.ProcessName;
                            
                            // 检查是否在高优先级进程ID集合中
                            if (_highPriorityProcessIds.Contains(process.Id))
                            {
                                try
                                {
                                    process.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
                                    priority = "高";
                                }
                                catch { }
                            }
                            
                            newProcessList.Add(new ProcessInfo
                            {
                                Id = process.Id,
                                Name = displayName,
                                ProcessName = process.ProcessName,
                                DisplayName = displayName,
                                Priority = priority,
                                MemoryUsage = memoryUsage,
                                IsSelected = false,
                                Icon = GetProcessIcon(process)
                            });
                            processCount++;
                        }
                        catch { }
                    }
                }
                
                _processList = newProcessList;
                
                // 按进程名称分组
                var groupedProcesses = _processList.GroupBy(p => p.ProcessName)
                    .OrderByDescending(g => g.Sum(p => 
                    {
                        if (long.TryParse(p.MemoryUsage.Replace(" MB", "").Replace(",", ""), out long val))
                            return val;
                        return 0;
                    }))
                    .ToList();
                
                // 构建TreeView
                var treeItems = new List<System.Windows.Controls.TreeViewItem>();
                foreach (var group in groupedProcesses)
                {
                    long totalMem = group.Sum(p => 
                    {
                        if (long.TryParse(p.MemoryUsage.Replace(" MB", "").Replace(",", ""), out long val))
                            return val;
                        return 0;
                    });
                    
                    var groupItem = new System.Windows.Controls.TreeViewItem();
                    groupItem.Header = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(4, 2, 4, 2) };
                    ((System.Windows.Controls.Grid)groupItem.Header).ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                    ((System.Windows.Controls.Grid)groupItem.Header).ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(32) });
                    ((System.Windows.Controls.Grid)groupItem.Header).ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                    ((System.Windows.Controls.Grid)groupItem.Header).ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(100) });
                    ((System.Windows.Controls.Grid)groupItem.Header).ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(100) });
                    
                    // 添加组标题内容
                    var checkBox = new System.Windows.Controls.CheckBox { Margin = new System.Windows.Thickness(0, 0, 8, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    checkBox.Click += (s, e) => 
                    {
                        var cb = s as System.Windows.Controls.CheckBox;
                        foreach (var child in groupItem.Items)
                        {
                            if (child is System.Windows.Controls.TreeViewItem childItem && 
                                childItem.Header is System.Windows.Controls.Grid childGrid &&
                                childGrid.Children.Count > 0 &&
                                childGrid.Children[0] is System.Windows.Controls.CheckBox childCb)
                            {
                                childCb.IsChecked = cb.IsChecked;
                            }
                        }
                    };
                    ((System.Windows.Controls.Grid)groupItem.Header).Children.Add(checkBox);
                    System.Windows.Controls.Grid.SetColumn(checkBox, 0);
                    
                    var icon = new System.Windows.Controls.Image { Width = 24, Height = 24, Margin = new System.Windows.Thickness(0, 0, 8, 0), Stretch = System.Windows.Media.Stretch.UniformToFill };
                    icon.Source = group.FirstOrDefault()?.Icon;
                    ((System.Windows.Controls.Grid)groupItem.Header).Children.Add(icon);
                    System.Windows.Controls.Grid.SetColumn(icon, 1);
                    
                    var nameBlock = new System.Windows.Controls.TextBlock { Text = $"{group.Key} ({group.Count()}个进程)", FontSize = 13, FontWeight = System.Windows.FontWeights.Bold, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    ((System.Windows.Controls.Grid)groupItem.Header).Children.Add(nameBlock);
                    System.Windows.Controls.Grid.SetColumn(nameBlock, 2);
                    
                    var priorityBlock = new System.Windows.Controls.TextBlock { Text = "-", FontSize = 13, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(84, 110, 122)), VerticalAlignment = System.Windows.VerticalAlignment.Center, TextAlignment = System.Windows.TextAlignment.Center };
                    ((System.Windows.Controls.Grid)groupItem.Header).Children.Add(priorityBlock);
                    System.Windows.Controls.Grid.SetColumn(priorityBlock, 3);
                    
                    var memBlock = new System.Windows.Controls.TextBlock { Text = $"{totalMem:N0} MB", FontSize = 13, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 187, 106)), VerticalAlignment = System.Windows.VerticalAlignment.Center, TextAlignment = System.Windows.TextAlignment.Center };
                    ((System.Windows.Controls.Grid)groupItem.Header).Children.Add(memBlock);
                    System.Windows.Controls.Grid.SetColumn(memBlock, 4);
                    
                    // 添加子进程
                    foreach (var processInfo in group)
                    {
                        var childItem = new System.Windows.Controls.TreeViewItem();
                        childItem.Header = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(24, 2, 4, 2), Tag = processInfo };
                        ((System.Windows.Controls.Grid)childItem.Header).ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
                        ((System.Windows.Controls.Grid)childItem.Header).ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(32) });
                        ((System.Windows.Controls.Grid)childItem.Header).ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                        ((System.Windows.Controls.Grid)childItem.Header).ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(100) });
                        ((System.Windows.Controls.Grid)childItem.Header).ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(100) });
                        
                        var childCb = new System.Windows.Controls.CheckBox { IsChecked = processInfo.IsSelected, Margin = new System.Windows.Thickness(0, 0, 8, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center };
                        childCb.Checked += (s, e) => { processInfo.IsSelected = true; };
                        childCb.Unchecked += (s, e) => { processInfo.IsSelected = false; };
                        ((System.Windows.Controls.Grid)childItem.Header).Children.Add(childCb);
                        System.Windows.Controls.Grid.SetColumn(childCb, 0);
                        
                        var childIcon = new System.Windows.Controls.Image { Width = 20, Height = 20, Margin = new System.Windows.Thickness(0, 0, 8, 0), Stretch = System.Windows.Media.Stretch.UniformToFill, Source = processInfo.Icon };
                        ((System.Windows.Controls.Grid)childItem.Header).Children.Add(childIcon);
                        System.Windows.Controls.Grid.SetColumn(childIcon, 1);
                        
                        var childName = new System.Windows.Controls.TextBlock { Text = processInfo.DisplayName, FontSize = 12, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                        ((System.Windows.Controls.Grid)childItem.Header).Children.Add(childName);
                        System.Windows.Controls.Grid.SetColumn(childName, 2);
                        
                        var childPriority = new System.Windows.Controls.TextBlock { Text = processInfo.Priority, FontSize = 12, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(84, 110, 122)), VerticalAlignment = System.Windows.VerticalAlignment.Center, TextAlignment = System.Windows.TextAlignment.Center };
                        ((System.Windows.Controls.Grid)childItem.Header).Children.Add(childPriority);
                        System.Windows.Controls.Grid.SetColumn(childPriority, 3);
                        
                        var childMem = new System.Windows.Controls.TextBlock { Text = processInfo.MemoryUsage, FontSize = 12, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 187, 106)), VerticalAlignment = System.Windows.VerticalAlignment.Center, TextAlignment = System.Windows.TextAlignment.Center };
                        ((System.Windows.Controls.Grid)childItem.Header).Children.Add(childMem);
                        System.Windows.Controls.Grid.SetColumn(childMem, 4);
                        
                        groupItem.Items.Add(childItem);
                    }
                    
                    treeItems.Add(groupItem);
                }
                
                TreeProcesses.ItemsSource = treeItems;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"刷新进程列表失败: {ex.Message}", "错误", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetPriorityString(System.Diagnostics.ProcessPriorityClass priority)
        {
            return priority switch
            {
                System.Diagnostics.ProcessPriorityClass.High => "高",
                System.Diagnostics.ProcessPriorityClass.AboveNormal => "高于正常",
                System.Diagnostics.ProcessPriorityClass.Normal => "正常",
                System.Diagnostics.ProcessPriorityClass.BelowNormal => "低于正常",
                System.Diagnostics.ProcessPriorityClass.Idle => "空闲",
                System.Diagnostics.ProcessPriorityClass.RealTime => "实时",
                _ => "未知"
            };
        }

        private System.Windows.Media.ImageSource? GetProcessIcon(System.Diagnostics.Process process)
        {
            // 如果不使用美观UI，直接返回null以减少内存占用
            if (!UseBeautifulUI)
            {
                return null;
            }
            
            try
            {
                if (!string.IsNullOrEmpty(process.MainModule?.FileName))
                {
                    // 检查缓存
                    if (_iconCache.TryGetValue(process.MainModule.FileName, out var cachedIcon))
                    {
                        return cachedIcon;
                    }
                    
                    // 限制缓存大小，防止内存占用过高
                    if (_iconCache.Count >= 30)
                    {
                        // 清理一半的缓存
                        var keysToRemove = _iconCache.Keys.Take(15).ToList();
                        foreach (var key in keysToRemove)
                        {
                            if (_iconCache[key] is System.Windows.Media.Imaging.BitmapSource bitmap)
                            {
                                bitmap.Freeze();
                            }
                            _iconCache.Remove(key);
                        }
                    }
                    
                    using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(process.MainModule.FileName))
                    {
                        if (icon != null)
                        {
                            var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                icon.Handle,
                                new System.Windows.Int32Rect(0, 0, icon.Width, icon.Height),
                                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                            
                            bitmapSource.Freeze(); // 冻结图像以提高性能
                            
                            // 添加到缓存
                            _iconCache[process.MainModule.FileName] = bitmapSource;
                            return bitmapSource;
                        }
                    }
                }
            }
            catch { }
            
            // 返回默认图标
            return null;
        }

        private void BtnRefreshProcesses_Click(object sender, RoutedEventArgs e)
        {
            RefreshProcessList();
        }

        private void TreeProcesses_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            
        }

        private void SetHighPriorityMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = GetSelectedProcessInfos();
            if (selectedItems.Count == 0)
            {
                System.Windows.MessageBox.Show("请先选择要设置的进程", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int count = 0;
            foreach (var processInfo in selectedItems)
            {
                try
                {
                    using (var process = System.Diagnostics.Process.GetProcessById(processInfo.Id))
                    {
                        process.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
                        processInfo.Priority = "高";
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"设置进程 {processInfo.Name} 优先级失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            
            if (count > 0)
            {
                _logService.Log("前台优化", $"已将 {count} 个进程设为高优先级");
                RefreshProcessList();
                RefreshHighPriorityProcesses();
            }
        }

        private void SetNormalPriorityMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = GetSelectedProcessInfos();
            if (selectedItems.Count == 0)
            {
                System.Windows.MessageBox.Show("请先选择要设置的进程", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int count = 0;
            foreach (var processInfo in selectedItems)
            {
                try
                {
                    using (var process = System.Diagnostics.Process.GetProcessById(processInfo.Id))
                    {
                        process.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
                        processInfo.Priority = "正常";
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"设置进程 {processInfo.Name} 优先级失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            
            if (count > 0)
            {
                _logService.Log("前台优化", $"已将 {count} 个进程恢复为正常优先级");
                RefreshProcessList();
                RefreshHighPriorityProcesses();
            }
        }

        private void EndProcessMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = GetSelectedProcessInfos();
            if (selectedItems.Count == 0)
            {
                System.Windows.MessageBox.Show("请先选择要结束的进程", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = System.Windows.MessageBox.Show($"确定要结束选中的 {selectedItems.Count} 个进程吗？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                int count = 0;
                foreach (var processInfo in selectedItems)
                {
                    try
                    {
                        using (var process = System.Diagnostics.Process.GetProcessById(processInfo.Id))
                        {
                            process.Kill();
                            count++;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show($"结束进程 {processInfo.Name} 失败: {ex.Message}", "错误",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                
                if (count > 0)
                {
                    _logService.Log("前台优化", $"已结束 {count} 个进程");
                    RefreshProcessList();
                    RefreshHighPriorityProcesses();
                }
            }
        }

        private List<ProcessInfo> GetSelectedProcessInfos()
        {
            var selectedItems = new List<ProcessInfo>();
            foreach (var item in TreeProcesses.Items)
            {
                if (item is System.Windows.Controls.TreeViewItem groupItem)
                {
                    foreach (var child in groupItem.Items)
                    {
                        if (child is System.Windows.Controls.TreeViewItem childItem &&
                            childItem.Header is System.Windows.Controls.Grid childGrid &&
                            childGrid.Children.Count > 0 &&
                            childGrid.Children[0] is System.Windows.Controls.CheckBox childCb &&
                            childCb.IsChecked == true &&
                            childGrid.Tag is ProcessInfo processInfo)
                        {
                            selectedItems.Add(processInfo);
                        }
                    }
                }
            }
            return selectedItems;
        }

        private void BtnRefreshHighPriority_Click(object sender, RoutedEventArgs e)
        {
            RefreshHighPriorityProcesses();
        }

        private void RefreshHighPriorityProcesses()
        {
            try
            {
                var highPriorityProcesses = _processList.Where(p => p.Priority == "高").ToList();
                LstHighPriorityProcesses.ItemsSource = highPriorityProcesses;
                TxtNoHighPriority.Visibility = highPriorityProcesses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void BtnRestorePriority_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is ProcessInfo processInfo)
            {
                try
                {
                    // 通过进程ID重新获取进程对象，避免引用已释放的对象
                    using (var process = System.Diagnostics.Process.GetProcessById(processInfo.Id))
                    {
                        process.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
                        processInfo.Priority = "正常";
                        _logService.Log("前台优化", $"已将进程 {processInfo.DisplayName} 恢复为正常优先级");
                        RefreshHighPriorityProcesses();
                        RefreshProcessList();
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"恢复进程 {processInfo.DisplayName} 优先级失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void ProcessItem_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Grid grid && grid.DataContext is ProcessInfo processInfo)
            {
                processInfo.IsSelected = !processInfo.IsSelected;
            }
        }

        private List<ProcessInfo> GetSelectedProcesses()
        {
            var selected = new List<ProcessInfo>();
            foreach (var item in TreeProcesses.Items)
            {
                if (item is System.Windows.Controls.TreeViewItem groupItem)
                {
                    foreach (var child in groupItem.Items)
                    {
                        if (child is System.Windows.Controls.TreeViewItem childItem &&
                            childItem.Header is System.Windows.Controls.Grid grid &&
                            grid.Children[0] is System.Windows.Controls.CheckBox cb &&
                            cb.IsChecked == true &&
                            grid.Tag is ProcessInfo processInfo)
                        {
                            selected.Add(processInfo);
                        }
                    }
                }
            }
            return selected;
        }

        private void BtnSetHighPriority_Click(object sender, RoutedEventArgs e)
        {
            var selectedProcesses = GetSelectedProcesses();
            int count = 0;
            
            foreach (var item in selectedProcesses)
            {
                try
                {
                    // 通过进程ID重新获取进程对象
                    using (var process = System.Diagnostics.Process.GetProcessById(item.Id))
                    {
                        process.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
                        item.Priority = "高";
                        _highPriorityProcessIds.Add(item.Id);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"设置进程 {item.Name} 优先级失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            
            if (count > 0)
            {
                SaveHighPriorityProcesses();
                _logService.Log("前台优化", $"已将 {count} 个进程设为高优先级");
                System.Windows.MessageBox.Show($"成功将 {count} 个进程设为高优先级", "操作完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshProcessList();
                RefreshHighPriorityProcesses();
            }
            else
            {
                System.Windows.MessageBox.Show("请先选择要设置优先级的进程", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnSetNormalPriority_Click(object sender, RoutedEventArgs e)
        {
            var selectedProcesses = GetSelectedProcesses();
            int count = 0;
            
            foreach (var item in selectedProcesses)
            {
                try
                {
                    // 通过进程ID重新获取进程对象
                    using (var process = System.Diagnostics.Process.GetProcessById(item.Id))
                    {
                        process.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
                        item.Priority = "正常";
                        _highPriorityProcessIds.Remove(item.Id);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"恢复进程 {item.Name} 优先级失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            
            if (count > 0)
            {
                SaveHighPriorityProcesses();
                _logService.Log("前台优化", $"已将 {count} 个进程恢复为正常优先级");
                System.Windows.MessageBox.Show($"成功将 {count} 个进程恢复为正常优先级", "操作完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshProcessList();
                RefreshHighPriorityProcesses();
            }
            else
            {
                System.Windows.MessageBox.Show("请先选择要恢复优先级的进程", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnEndProcess_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = GetSelectedProcesses();
            
            if (selectedItems.Count == 0)
            {
                System.Windows.MessageBox.Show("请先选择要结束的进程", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            string message = $"确定要结束 {selectedItems.Count} 个进程吗？\n\n";
            foreach (var item in selectedItems.Take(5))
            {
                message += $"- {item.Name}\n";
            }
            if (selectedItems.Count > 5)
            {
                message += $"以及其他 {selectedItems.Count - 5} 个进程";
            }
            
            var result = System.Windows.MessageBox.Show(message, "确认结束进程",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                int successCount = 0;
                int failCount = 0;
                
                foreach (var item in selectedItems)
                {
                    try
                    {
                        using (var process = System.Diagnostics.Process.GetProcessById(item.Id))
                        {
                            process.Kill();
                            _highPriorityProcessIds.Remove(item.Id);
                            successCount++;
                        }
                    }
                    catch
                    {
                        failCount++;
                    }
                }
                
                if (successCount > 0)
                {
                    SaveHighPriorityProcesses();
                }
                
                _logService.Log("前台优化", $"结束进程: 成功 {successCount} 个, 失败 {failCount} 个");
                
                if (failCount > 0)
                {
                    System.Windows.MessageBox.Show($"成功结束 {successCount} 个进程，{failCount} 个进程无法结束", "操作完成",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    System.Windows.MessageBox.Show($"成功结束 {successCount} 个进程", "操作完成",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                
                RefreshProcessList();
            }
        }

        private void BtnAutoOptimizeNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                IntPtr foregroundHandle = GetForegroundWindow();
                uint processId = 0;
                GetWindowThreadProcessId(foregroundHandle, ref processId);
                
                var foregroundProcess = System.Diagnostics.Process.GetProcessById((int)processId);
                
                foregroundProcess.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
                
                _logService.Log("前台优化", $"自动优化: 将 {foregroundProcess.ProcessName} 设为高优先级");
                System.Windows.MessageBox.Show($"已将前台应用 \"{foregroundProcess.MainWindowTitle ?? foregroundProcess.ProcessName}\" 设为高优先级", 
                    "自动优化完成", MessageBoxButton.OK, MessageBoxImage.Information);
                
                RefreshProcessList();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"自动优化失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Windows API声明
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, ref uint lpdwProcessId);

        private void BtnAppUsage_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_logClearPassword))
            {
                var result = System.Windows.MessageBox.Show(
                    "进入应用使用管理需要设置密码，是否前往设置页面添加密码？",
                    "需要密码", MessageBoxButton.YesNo, MessageBoxImage.Information);
                
                if (result == MessageBoxResult.Yes)
                {
                    BtnSettings_Click(sender, e);
                }
                return;
            }
            
            var passwordDialog = new Window
            {
                Title = "输入密码",
                Width = 400,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 250, 250))
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var messageText = new TextBlock
            {
                Text = "请输入密码以进入应用使用管理",
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                FontSize = 14,
                Margin = new Thickness(20)
            };
            Grid.SetRow(messageText, 0);

            var buttonPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            Grid.SetRow(buttonPanel, 2);

            var okButton = new System.Windows.Controls.Button
            {
                Content = "确定",
                Width = 100,
                Height = 35,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(123, 31, 162)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14
            };
            
            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "取消",
                Width = 100,
                Height = 35,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(189, 189, 189)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            var passwordBox = new PasswordBox
            {
                Width = 250,
                Height = 35,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                FontSize = 14
            };
            Grid.SetRow(passwordBox, 1);
            
            passwordBox.KeyDown += (s, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter)
                {
                    okButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }
            };

            grid.Children.Add(messageText);
            grid.Children.Add(passwordBox);
            grid.Children.Add(buttonPanel);

            passwordDialog.Content = grid;

            okButton.Click += (s, args) =>
            {
                if (passwordBox?.Password == _logClearPassword)
                {
                    passwordDialog.Close();
                    HomePanel.Visibility = Visibility.Collapsed;
                    AppUsagePanel.Visibility = Visibility.Visible;
                    GlobalBackButton.Visibility = Visibility.Collapsed;
                    LstAppRules.ItemsSource = _appUsageService?.GetRules();
                }
                else
                {
                    System.Windows.MessageBox.Show("密码错误", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    passwordBox.Password = "";
                    passwordBox.Focus();
                }
            };

            cancelButton.Click += (s, args) =>
            {
                passwordDialog.Close();
            };

            passwordDialog.ShowDialog();
        }

        private void BtnBrowseApp_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                TxtAppName.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }

        private void BtnAddAppRule_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtAppName.Text)) return;
            if (!int.TryParse(TxtStartHour.Text, out int sh)) sh = 8;
            if (!int.TryParse(TxtStartMinute.Text, out int sm)) sm = 0;
            if (!int.TryParse(TxtEndHour.Text, out int eh)) eh = 18;
            if (!int.TryParse(TxtEndMinute.Text, out int em)) em = 0;

            var newTimeRange = new Services.TimeRange { StartHour = sh, StartMinute = sm, EndHour = eh, EndMinute = em };

            if (_editingRule != null)
            {
                _editingRule.TimeRanges.Add(newTimeRange);
                _appUsageService?.UpdateRule(_editingRule);
                _logService.Log("编辑应用规则", $"为应用 {_editingRule.AppName} 添加了时间段: {newTimeRange}");
                _editingRule = null;
            }
            else
            {
                var existingRule = _appUsageService?.GetRules().FirstOrDefault(r => r.AppName.Equals(TxtAppName.Text, StringComparison.OrdinalIgnoreCase));
                if (existingRule != null)
                {
                    existingRule.TimeRanges.Add(newTimeRange);
                    _appUsageService?.UpdateRule(existingRule);
                    _logService.Log("添加应用规则", $"为应用 {existingRule.AppName} 添加了时间段: {newTimeRange}");
                }
                else
                {
                    var rule = new Services.AppUsageRule
                    {
                        AppName = TxtAppName.Text,
                        TimeRanges = new List<Services.TimeRange>
                        {
                            newTimeRange
                        }
                    };
                    _appUsageService?.AddRule(rule);
                    _logService.Log("添加应用规则", $"添加了新应用规则: {rule.AppName} - {newTimeRange}");
                }
            }
            
            TxtAppName.Text = "";
            TxtStartHour.Text = "08";
            TxtStartMinute.Text = "00";
            TxtEndHour.Text = "18";
            TxtEndMinute.Text = "00";
        }

        private void BtnDeleteAppRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is Services.AppUsageRule rule)
            {
                _appUsageService?.RemoveRule(rule.Id);
            }
        }

        private void AppRuleCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox checkBox && checkBox.Tag is Services.AppUsageRule rule)
            {
                rule.IsEnabled = true;
                _appUsageService?.UpdateRule(rule);
            }
        }

        private void AppRuleCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox checkBox && checkBox.Tag is Services.AppUsageRule rule)
            {
                rule.IsEnabled = false;
                _appUsageService?.UpdateRule(rule);
            }
        }

        private void LstAppRules_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBox listBox && e.OriginalSource is FrameworkElement element)
            {
                var item = ItemsControl.ContainerFromElement(listBox, element) as ListBoxItem;
                if (item != null && item.DataContext is Services.AppUsageRule rule)
                {
                    listBox.SelectedItem = rule;
                }
            }
        }

        private void EditAppRuleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var rule = LstAppRules.SelectedItem as Services.AppUsageRule;
            if (rule == null)
            {
                System.Windows.MessageBox.Show("请先选择要编辑的规则", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _editingRule = rule;
            TxtAppName.Text = rule.AppName;
            if (rule.TimeRanges.Count > 0)
            {
                TxtStartHour.Text = rule.TimeRanges[0].StartHour.ToString("D2");
                TxtStartMinute.Text = rule.TimeRanges[0].StartMinute.ToString("D2");
                TxtEndHour.Text = rule.TimeRanges[0].EndHour.ToString("D2");
                TxtEndMinute.Text = rule.TimeRanges[0].EndMinute.ToString("D2");
            }
            
            var addButton = (System.Windows.Controls.Button)FindName("BtnAddAppRule");
            if (addButton != null)
            {
                addButton.Content = "保存修改";
            }
        }

        private void DeleteAppRuleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = LstAppRules.SelectedItems.Cast<Services.AppUsageRule>().ToList();
            if (selectedItems.Count == 0)
            {
                System.Windows.MessageBox.Show("请先选择要删除的规则", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (var rule in selectedItems)
            {
                _appUsageService?.RemoveRule(rule.Id);
            }
        }

        private void EditTimeRangeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var rule = LstAppRules.SelectedItem as Services.AppUsageRule;
            if (rule == null)
            {
                System.Windows.MessageBox.Show("请先选择要编辑的规则", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (rule.TimeRanges.Count == 0)
            {
                System.Windows.MessageBox.Show("该规则没有设置时间段，请先添加时间段", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var timeRangeList = string.Join("\n", rule.TimeRanges.Select((tr, index) => $"{index + 1}. {tr}"));
            
            var dialog = new Window
            {
                Title = "选择要删除的时间段",
                Width = 400,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 250, 250))
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var messageText = new TextBlock
            {
                Text = $"当前时间段：\n{timeRangeList}\n\n请输入要删除的时间段编号：",
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                FontSize = 14,
                Margin = new Thickness(20)
            };
            Grid.SetRow(messageText, 0);

            var inputTextBox = new System.Windows.Controls.TextBox
            {
                Width = 100,
                Height = 35,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                FontSize = 14,
                TextAlignment = TextAlignment.Center
            };
            Grid.SetRow(inputTextBox, 1);

            var buttonPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            Grid.SetRow(buttonPanel, 2);

            var okButton = new System.Windows.Controls.Button
            {
                Content = "删除",
                Width = 100,
                Height = 35,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 57, 53)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14
            };
            
            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "取消",
                Width = 100,
                Height = 35,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(189, 189, 189)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            grid.Children.Add(messageText);
            grid.Children.Add(inputTextBox);
            grid.Children.Add(buttonPanel);

            dialog.Content = grid;

            okButton.Click += (s, args) =>
            {
                if (int.TryParse(inputTextBox.Text, out int index) && index >= 1 && index <= rule.TimeRanges.Count)
                {
                    var removedRange = rule.TimeRanges[index - 1];
                    rule.TimeRanges.RemoveAt(index - 1);
                    _appUsageService?.UpdateRule(rule);
                    _logService.Log("删除时间段", $"从应用 {rule.AppName} 删除了时间段: {removedRange}");
                    dialog.Close();
                }
                else
                {
                    System.Windows.MessageBox.Show("请输入有效的时间段编号", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            cancelButton.Click += (s, args) =>
            {
                dialog.Close();
            };

            dialog.ShowDialog();
        }

        private void AddTimeRangeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var rule = LstAppRules.SelectedItem as Services.AppUsageRule;
            if (rule == null)
            {
                System.Windows.MessageBox.Show("请先选择要添加时间段的规则", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _editingRule = rule;
            TxtAppName.Text = rule.AppName;
            TxtStartHour.Text = "08";
            TxtStartMinute.Text = "00";
            TxtEndHour.Text = "18";
            TxtEndMinute.Text = "00";
            
            var addButton = (System.Windows.Controls.Button)FindName("BtnAddAppRule");
            if (addButton != null)
            {
                addButton.Content = "添加时间段";
            }
        }

        private void ShowAppBlockedDialog(string appName, string appPath)
        {
            var window = new Window
            {
                Title = "应用使用限制",
                Width = 450,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 250, 250))
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var messageText = new TextBlock
            {
                Text = $"当前时间段禁止使用 {appName}",
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(20)
            };
            Grid.SetRow(messageText, 0);

            var buttonPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            Grid.SetRow(buttonPanel, 1);

            var confirmButton = new System.Windows.Controls.Button
            {
                Content = "确认",
                Width = 120,
                Height = 40,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(189, 189, 189)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14
            };
            
            var passwordButton = new System.Windows.Controls.Button
            {
                Content = "输入密码",
                Width = 120,
                Height = 40,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(123, 31, 162)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14
            };

            buttonPanel.Children.Add(confirmButton);
            buttonPanel.Children.Add(passwordButton);

            grid.Children.Add(messageText);
            grid.Children.Add(buttonPanel);

            window.Content = grid;

            confirmButton.Click += (s, args) =>
            {
                window.Close();
            };

            passwordButton.Click += (s, args) =>
            {
                window.Close();
                ShowPasswordDialog(appName, appPath);
            };

            window.ShowDialog();
        }

        private void ShowPasswordDialog(string appName, string appPath)
        {
            var window = new Window
            {
                Title = "输入密码",
                Width = 400,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 250, 250))
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var messageText = new TextBlock
            {
                Text = $"请输入密码以临时允许使用 {appName}（15分钟）",
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                FontSize = 14,
                Margin = new Thickness(20)
            };
            Grid.SetRow(messageText, 0);

            var passwordBox = new PasswordBox
            {
                Width = 250,
                Height = 35,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                FontSize = 14
            };
            Grid.SetRow(passwordBox, 1);

            var buttonPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            Grid.SetRow(buttonPanel, 2);

            var okButton = new System.Windows.Controls.Button
            {
                Content = "确定",
                Width = 100,
                Height = 35,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(123, 31, 162)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14
            };
            
            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "取消",
                Width = 100,
                Height = 35,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(189, 189, 189)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            grid.Children.Add(messageText);
            grid.Children.Add(passwordBox);
            grid.Children.Add(buttonPanel);

            passwordBox.KeyDown += (s, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter)
                {
                    okButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }
            };

            window.Content = grid;

            okButton.Click += (s, args) =>
            {
                if (passwordBox != null && passwordBox.Password == _logClearPassword)
                {
                    _appUsageService.AllowAppTemporarily(appName, 15);
                    _logService.Log("临时启用应用", $"通过密码验证临时启用了应用: {appName} (15分钟)");
                    window.Close();
                    
                    if (!string.IsNullOrEmpty(appPath) && System.IO.File.Exists(appPath))
                    {
                        try
                        {
                            System.Threading.Thread.Sleep(500);
                            Process.Start(appPath);
                        }
                        catch { }
                    }
                }
                else
                {
                    System.Windows.MessageBox.Show("密码错误", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    passwordBox.Password = "";
                    passwordBox.Focus();
                }
            };

            cancelButton.Click += (s, args) =>
            {
                window.Close();
            };

            window.ShowDialog();
        }

        private void BtnDeleteSelectedAppRules_Click(object sender, RoutedEventArgs e)
        {
            var rulesToDelete = new List<Services.AppUsageRule>();
            
            for (int i = 0; i < LstAppRules.Items.Count; i++)
            {
                var item = LstAppRules.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                if (item != null)
                {
                    var checkBox = FindVisualChild<System.Windows.Controls.CheckBox>(item);
                    if (checkBox != null && checkBox.IsChecked == true)
                    {
                        var rule = LstAppRules.Items[i] as Services.AppUsageRule;
                        if (rule != null)
                        {
                            rulesToDelete.Add(rule);
                        }
                    }
                }
            }
            
            if (rulesToDelete.Count > 0)
            {
                int count = rulesToDelete.Count;
                string message = count == 1 
                    ? $"确定要删除选中的规则吗？" 
                    : $"确定要删除选中的 {count} 个规则吗？";
                
                var result = System.Windows.MessageBox.Show(message, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    foreach (var rule in rulesToDelete)
                    {
                        _appUsageService?.RemoveRule(rule.Id);
                    }
                    _logService.Log("删除应用规则", $"删除了 {count} 个应用规则");
                }
            }
            else
            {
                System.Windows.MessageBox.Show("请先选择要删除的规则", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnClearAllAppRules_Click(object sender, RoutedEventArgs e)
        {
            var rules = _appUsageService?.GetRules();
            if (rules == null || rules.Count == 0)
            {
                System.Windows.MessageBox.Show("当前没有应用规则", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show("确定要清空所有应用规则吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                foreach (var rule in rules.ToList())
                {
                    _appUsageService?.RemoveRule(rule.Id);
                }
                _logService.Log("清空应用规则", "清空了所有应用规则");
            }
        }
    }

    // 转换器类
    public class TimeRangesConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is List<Services.TimeRange> timeRanges)
            {
                if (timeRanges.Count == 0)
                    return "未设置";
                return string.Join(", ", timeRanges.Select(tr => tr.ToString()));
            }
            return "未设置";
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 数据类
    public class ProcessInfo : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;
        
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string MemoryUsage { get; set; } = string.Empty;
        public System.Diagnostics.Process? Process { get; set; }
        public System.Windows.Media.ImageSource? Icon { get; set; }
        
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
        
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }
}

public class CustomAppEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int ColorIndex { get; set; }
    public string IconType { get; set; } = "default"; // "default" | "exe" | "custom"
    public string IconData { get; set; } = ""; // Base64 encoded PNG data for custom/exe icons
}