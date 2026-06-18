using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace ClassroomControl.Services
{
    public class SchedulerService
    {
        private DispatcherTimer? _timer = null;
        private DateTime _targetTime;
        private bool _isScheduled;
        private int _reminderMinutes = 5; // 提前5分钟提醒

        public event EventHandler<string>? StatusChanged;
        public event EventHandler? ShutdownImminent;

        public bool IsScheduled => _isScheduled;
        public DateTime? ScheduledTime => _isScheduled ? _targetTime : null;

        public void ScheduleShutdown(DateTime targetTime)
        {
            _targetTime = targetTime;
            _isScheduled = true;
            
            StartTimer();
            StatusChanged?.Invoke(this, $"已设置定时关机：{targetTime:yyyy-MM-dd HH:mm}");
        }

        public void ScheduleShutdown(int delayMinutes)
        {
            ScheduleShutdown(DateTime.Now.AddMinutes(delayMinutes));
        }

        public void CancelSchedule()
        {
            _isScheduled = false;
            _timer?.Stop();
            _timer = null;
            StatusChanged?.Invoke(this, "已取消定时关机");
        }

        private void StartTimer()
        {
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (!_isScheduled) return;

            var now = DateTime.Now;
            var timeLeft = _targetTime - now;

            // 提前提醒
            if (timeLeft.TotalMinutes <= _reminderMinutes && timeLeft.TotalMinutes > _reminderMinutes - 1)
            {
                ShutdownImminent?.Invoke(this, EventArgs.Empty);
            }

            // 执行关机
            if (timeLeft.TotalSeconds <= 0)
            {
                CancelSchedule();
                ExecuteShutdown();
            }
            else
            {
                // 更新状态
                string status = $"定时关机已设置：{_targetTime:HH:mm}，还剩 {timeLeft.Hours}小时{timeLeft.Minutes}分钟";
                StatusChanged?.Invoke(this, status);
            }
        }

        private void ExecuteShutdown()
        {
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

        public TimeSpan? GetRemainingTime()
        {
            if (!_isScheduled) return null;
            
            var now = DateTime.Now;
            if (_targetTime > now)
            {
                return _targetTime - now;
            }
            return null;
        }

        public void Dispose()
        {
            CancelSchedule();
        }
    }
}