using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace ClassroomControl
{
    public partial class App : System.Windows.Application
    {
        // 最低支持的 Windows 版本：Windows 7 SP1 (6.1.7601)
        private const int MinMajorVersion = 6;
        private const int MinMinorVersion = 1;
        private const int MinBuildNumber = 7601;

        // .NET 10 Desktop Runtime 下载地址
        private const string DotNetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";

        // 单实例互斥体
        private static Mutex? _instanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                // 检查单实例
                if (!CheckSingleInstance())
                {
                    System.Windows.MessageBox.Show("程序已在运行中！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }

                if (!CheckEnvironment())
                {
                    Shutdown();
                    return;
                }
                
                // 检查是否需要最小化启动（开机自启时）
                bool startMinimized = e.Args.Contains("--minimized");
                
                base.OnStartup(e);
                
                // 如果是开机自启，让窗口最小化到托盘
                if (startMinimized && MainWindow is MainWindow mw)
                {
                    mw.WindowState = WindowState.Minimized;
                    mw.Hide();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"程序启动失败：{ex.Message}\n\n{ex.StackTrace}", "启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private bool CheckSingleInstance()
        {
            bool createdNew;
            _instanceMutex = new Mutex(true, "ClassroomControl_SingleInstance_Mutex", out createdNew);
            return createdNew;
        }

        private bool CheckEnvironment()
        {
            var issues = new System.Collections.Generic.List<string>();

            // 1. 检查 Windows 版本
            var osVer = Environment.OSVersion.Version;
            if (osVer.Major < MinMajorVersion ||
                (osVer.Major == MinMajorVersion && osVer.Minor < MinMinorVersion) ||
                (osVer.Major == MinMajorVersion && osVer.Minor == MinMinorVersion && osVer.Build < MinBuildNumber))
            {
                issues.Add($"当前操作系统版本过低（{GetOSVersionString()}），本程序最低要求 Windows 7 SP1。");
            }

            // 2. 检查 .NET Runtime 版本
            var runtimeInfo = GetDotNetRuntimeInfo();
            if (runtimeInfo == null)
            {
                issues.Add("未检测到 .NET 运行时环境。");
            }
            else if (runtimeInfo.StartsWith("10."))
            {
                // .NET 10 已安装，满足要求
            }
            else
            {
                issues.Add($"检测到 .NET {runtimeInfo}，但本程序需要 .NET 10.0 Desktop Runtime。");
            }

            if (issues.Count == 0)
                return true;

            // 弹出提示窗口
            ShowEnvironmentError(issues);
            return false;
        }

        private string GetOSVersionString()
        {
            var ver = Environment.OSVersion.Version;
            return ver.Major switch
            {
                10 => "Windows 10/11",
                6 => ver.Minor switch
                {
                    3 => "Windows 8.1",
                    2 => "Windows 8",
                    1 => "Windows 7",
                    0 => "Windows Vista",
                    _ => $"Windows NT {ver.Major}.{ver.Minor}"
                },
                5 => ver.Minor switch
                {
                    1 => "Windows XP",
                    2 => "Windows XP/Server 2003",
                    _ => $"Windows NT {ver.Major}.{ver.Minor}"
                },
                _ => $"Windows NT {ver.Major}.{ver.Minor}"
            };
        }

        private string? GetDotNetRuntimeInfo()
        {
            try
            {
                var assembly = typeof(object).Assembly;
                var infoAttr = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>();
                if (infoAttr != null && infoAttr.InformationalVersion != null)
                {
                    // 格式类似 "8.0.1234" 或 "8.0.1234+abcdef"
                    var versionPart = infoAttr.InformationalVersion.Split('+')[0];
                    var parts = versionPart.Split('.');
                    if (parts.Length >= 1)
                    {
                        return parts[0] + "." + parts[1];
                    }
                }
            }
            catch { }

            // 回退：通过 Environment.Version 获取
            var envVer = Environment.Version;
            return $"{envVer.Major}.{envVer.Minor}";
        }

        private void ShowEnvironmentError(System.Collections.Generic.List<string> issues)
        {
            var window = new Window
            {
                Title = "环境检查",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 236, 239, 241))
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };

            // 警告图标
            var icon = new System.Windows.Controls.TextBlock
            {
                Text = "\u26A0",  // ⚠
                FontSize = 48,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 12),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFB, 0x8C, 0x00))
            };
            panel.Children.Add(icon);

            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "程序无法启动",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35))
            });

            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "", Margin = new Thickness(0, 8, 0, 0) });

            // 列出所有问题
            foreach (var issue in issues)
            {
                panel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "\u2022 " + issue,
                    FontSize = 13,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x54, 0x6E, 0x7A)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 4, 0, 4)
                });
            }

            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "", Margin = new Thickness(0, 8, 0, 0) });

            // 下载链接
            var downloadLink = new System.Windows.Controls.TextBlock
            {
                Text = "\uD83D\uDD17 点击此处前往 .NET 10.0 下载页面",
                FontSize = 13,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x88, 0xE5)),
                Cursor = System.Windows.Input.Cursors.Hand,
                TextDecorations = System.Windows.TextDecorations.Underline,
                Margin = new Thickness(8, 4, 0, 8)
            };
            downloadLink.MouseLeftButtonUp += (s, args) =>
            {
                try { Process.Start(new ProcessStartInfo(DotNetDownloadUrl) { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(downloadLink);

            // 提示
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "请安装 .NET 10.0 Desktop Runtime 后重新启动程序。",
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0xA4, 0xAE)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 0, 0, 12)
            });

            // 确定按钮
            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 8)
            };

            var btnOk = new System.Windows.Controls.Button
            {
                Content = "确定",
                Height = 32,
                Padding = new Thickness(40, 0, 40, 0),
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0)
            };

            var btnTemplate = (System.Windows.Controls.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
                "<Border x:Name='bd' Background='{TemplateBinding Background}' CornerRadius='6' Padding='{TemplateBinding Padding}'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Opacity' Value='0.85'/></Trigger></ControlTemplate.Triggers>" +
                "</ControlTemplate>");
            btnOk.Template = btnTemplate;
            btnOk.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x88, 0xE5));

            btnOk.Click += (s, args) => { window.DialogResult = true; };
            btnPanel.Children.Add(btnOk);
            panel.Children.Add(btnPanel);

            window.Content = panel;
            window.ShowDialog();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (MainWindow is MainWindow mw)
            {
                mw.ForceDisposeTrayIcon();
            }
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            if (MainWindow is MainWindow mw)
            {
                mw.ForceDisposeTrayIcon();
            }
            base.OnSessionEnding(e);
        }
    }
}