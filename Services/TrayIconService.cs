using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Forms;

namespace ClassroomControl
{
    public class TrayIconService : IDisposable
    {
        private NotifyIcon? _notifyIcon;
        private readonly MainWindow _mainWindow;
        private bool _disposed;
        private Icon? _appIcon;

        public TrayIconService(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            InitializeTrayIcon();
            
            // 确保窗口在初始化后保持可见
            mainWindow.Show();
            mainWindow.WindowState = WindowState.Normal;
        }

        private void InitializeTrayIcon()
        {
            // 优先使用logo.ico作为托盘图标
            try
            {
                // 尝试从exe所在目录加载
                var exeDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()?.Location);
                var iconPath = System.IO.Path.Combine(exeDir ?? ".", "logo.ico");
                
                if (System.IO.File.Exists(iconPath))
                {
                    try
                    {
                        _appIcon = new System.Drawing.Icon(iconPath);
                    }
                    catch
                    {
                        // logo.ico格式无效
                    }
                }
                
                // 如果exe目录没有，尝试从程序集目录加载
                if (_appIcon == null)
                {
                    var asmDir = System.IO.Path.GetDirectoryName(typeof(TrayIconService).Assembly.Location);
                    iconPath = System.IO.Path.Combine(asmDir ?? ".", "logo.ico");
                    if (System.IO.File.Exists(iconPath))
                    {
                        try
                        {
                            _appIcon = new System.Drawing.Icon(iconPath);
                        }
                        catch { }
                    }
                }
                
                // 如果还是没有，尝试从程序本身提取图标
                if (_appIcon == null)
                {
                    var exePath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                    if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                    {
                        try
                        {
                            _appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            _notifyIcon = new NotifyIcon
            {
                Icon = _appIcon ?? SystemIcons.Application,
                Text = "班级电脑控制助手",
                Visible = true
            };

            _notifyIcon.DoubleClick += OnShowWindow;
            _notifyIcon.ContextMenuStrip = CreateContextMenu();

            // 注册窗口状态变化事件，确保托盘图标始终可见
            _mainWindow.StateChanged += MainWindow_StateChanged;
            _mainWindow.IsVisibleChanged += MainWindow_IsVisibleChanged;
        }

        private ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new CustomMenuRenderer();
            menu.Font = new Font("微软雅黑", 12F, System.Drawing.FontStyle.Regular);
            menu.Padding = new Padding(8, 6, 8, 6);
            
            var showItem = new ToolStripMenuItem("打开主界面", null, (s, e) => OnShowWindow(s, e));
            showItem.Font = new Font("微软雅黑", 13F, System.Drawing.FontStyle.Bold);
            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());
            
            var shutdownItem = new ToolStripMenuItem("关机", null, (s, e) => ExecutePowerCommand("shutdown", "-s -t 0"));
            shutdownItem.Font = new Font("微软雅黑", 12F);
            menu.Items.Add(shutdownItem);
            
            var restartItem = new ToolStripMenuItem("重启", null, (s, e) => ExecutePowerCommand("shutdown", "-r -t 0"));
            restartItem.Font = new Font("微软雅黑", 12F);
            menu.Items.Add(restartItem);
            
            menu.Items.Add(new ToolStripSeparator());
            
            var exitItem = new ToolStripMenuItem("退出", null, (s, e) => OnExit(s, e));
            exitItem.Font = new Font("微软雅黑", 12F);
            exitItem.ForeColor = Color.FromArgb(255, 80, 80, 80);
            menu.Items.Add(exitItem);
            
            return menu;
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            EnsureTrayVisible();
        }

        private void MainWindow_IsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            EnsureTrayVisible();
        }

        private void EnsureTrayVisible()
        {
            if (_disposed || _notifyIcon == null) return;

            if (!_notifyIcon.Visible)
            {
                _notifyIcon.Visible = true;
            }

            // 强制刷新托盘图标，解决 Windows 有时不显示的问题
            _notifyIcon.Visible = false;
            _notifyIcon.Visible = true;
        }

        private void ExecutePowerCommand(string command, string arguments)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"执行命令失败: {ex.Message}");
            }
        }

        private void OnShowWindow(object? sender, EventArgs e)
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        private void OnExit(object? sender, EventArgs e)
        {
            _mainWindow.ExitApplication();
        }

        public void MinimizeToTray()
        {
            _mainWindow.Hide();
            EnsureTrayVisible();

            try
            {
                _notifyIcon?.ShowBalloonTip(
                    3000,
                    "班级电脑控制助手",
                    "程序已最小化到系统托盘，双击图标可恢复窗口。",
                    ToolTipIcon.Info);
            }
            catch { }
        }

        public void ExitApplication()
        {
            // 先取消窗口关闭拦截，确保能正常退出
            _mainWindow.StateChanged -= MainWindow_StateChanged;
            _mainWindow.IsVisibleChanged -= MainWindow_IsVisibleChanged;

            Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _mainWindow.StateChanged -= MainWindow_StateChanged;
            _mainWindow.IsVisibleChanged -= MainWindow_IsVisibleChanged;

            if (_notifyIcon != null)
            {
                _notifyIcon.DoubleClick -= OnShowWindow;
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null!;
            }
            if (_appIcon != null)
            {
                _appIcon.Dispose();
                _appIcon = null!;
            }
        }
    }

    // 自定义菜单渲染器 - 现代化风格
    public class CustomMenuRenderer : ToolStripProfessionalRenderer
    {
        public CustomMenuRenderer() { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            var item = e.Item;
            var rect = new Rectangle(2, 1, item.Bounds.Width - 4, item.Bounds.Height - 2);

            if (item.Selected || item.Pressed)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = CreateRoundedRect(rect, 4))
                using (var brush = new SolidBrush(Color.FromArgb(227, 242, 253))) // 浅蓝色背景
                {
                    g.FillPath(brush, path);
                }
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            var y = e.Item.Height / 2;
            using (var pen = new Pen(Color.FromArgb(224, 224, 224), 1)) // 浅灰色分隔线
            {
                g.DrawLine(pen, 8, y, e.Item.Width - 8, y);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Selected || e.Item.Pressed)
            {
                e.TextColor = Color.FromArgb(30, 136, 229); // 蓝色文字
            }
            else
            {
                e.TextColor = Color.FromArgb(55, 71, 79); // 深灰色文字
            }
            e.TextFont = new Font("微软雅黑", 11F, System.Drawing.FontStyle.Regular);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            var rect = e.AffectedBounds;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = CreateRoundedRect(rect, 6))
            using (var brush = new SolidBrush(Color.White)) // 白色背景
            {
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = CreateRoundedRect(rect, 6))
            using (var pen = new Pen(Color.FromArgb(224, 224, 224), 1)) // 浅灰色边框
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 不绘制图像边距
        }

        private GraphicsPath CreateRoundedRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
            path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
            path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}