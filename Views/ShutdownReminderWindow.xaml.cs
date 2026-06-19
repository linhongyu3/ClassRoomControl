using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClassroomControl
{
    public partial class ShutdownReminderWindow : Window
    {
        private DispatcherTimer _countdownTimer = null!;
        private int _countdownSeconds = 300; // 默认5分钟倒计时

        public event EventHandler? CancelRequested;
        public event EventHandler? ShutdownNowRequested;

        public ShutdownReminderWindow()
        {
            InitializeComponent();
            InitializeCountdown();
            Loaded += OnLoaded;
        }

        public ShutdownReminderWindow(int reminderMinutes)
        {
            InitializeComponent();
            _countdownSeconds = reminderMinutes * 60; // 使用传入的倒计时分钟数
            InitializeCountdown();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PositionAtBottomRight();
            SlideIn();
        }

        private void PositionAtBottomRight()
        {
            var workArea = SystemParameters.WorkArea;
            // 先测量并排列内容以获取实际高度
            Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Arrange(new System.Windows.Rect(0, 0, DesiredSize.Width, DesiredSize.Height));
            UpdateLayout();

            Left = workArea.Right - ActualWidth - 16;
            Top = workArea.Bottom - ActualHeight - 16;
        }

        private void SlideIn()
        {
            var targetLeft = Left;
            var screenWidth = SystemParameters.WorkArea.Right;
            Left = screenWidth;

            var animation = new DoubleAnimation
            {
                From = screenWidth,
                To = targetLeft,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            BeginAnimation(LeftProperty, animation);
        }

        private void InitializeCountdown()
        {
            UpdateCountdownDisplay();

            _countdownTimer = new DispatcherTimer();
            _countdownTimer.Interval = TimeSpan.FromSeconds(1);
            _countdownTimer.Tick += (s, e) =>
            {
                _countdownSeconds--;
                UpdateCountdownDisplay();

                if (_countdownSeconds <= 0)
                {
                    ExecuteShutdown();
                }
            };
            _countdownTimer.Start();
        }

        private void UpdateCountdownDisplay()
        {
            int minutes = _countdownSeconds / 60;
            int seconds = _countdownSeconds % 60;
            TxtCountdown.Text = $"{minutes:00}:{seconds:00}";
        }

        private void BtnCancelShutdown_Click(object sender, RoutedEventArgs e)
        {
            _countdownTimer?.Stop();
            SlideOut(() =>
            {
                CancelRequested?.Invoke(this, EventArgs.Empty);
                Close();
            });
        }

        private void BtnShutdownNow_Click(object sender, RoutedEventArgs e)
        {
            _countdownTimer?.Stop();
            SlideOut(() =>
            {
                ShutdownNowRequested?.Invoke(this, EventArgs.Empty);
                Close();
            });
        }

        private void SlideOut(Action? callback = null)
        {
            var screenWidth = SystemParameters.WorkArea.Right;
            var animation = new DoubleAnimation
            {
                From = Left,
                To = screenWidth,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            animation.Completed += (s, e) => callback?.Invoke();
            BeginAnimation(LeftProperty, animation);
        }

        private void ExecuteShutdown()
        {
            _countdownTimer?.Stop();
            ShutdownNowRequested?.Invoke(this, EventArgs.Empty);
            Close();

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "-s -t 0",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"执行关机失败: {ex.Message}");
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _countdownTimer?.Stop();
            base.OnClosing(e);
        }
    }
}