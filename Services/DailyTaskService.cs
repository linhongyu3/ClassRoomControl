using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace ClassroomControl.Services
{
    public class ScheduledTask
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public bool IsEnabled { get; set; } = true;
        public string Time { get; set; } = "22:00";
        public TaskType Type { get; set; } = TaskType.Shutdown;
        public string? ProgramPath { get; set; }
        public int ReminderMinutes { get; set; } = 5; // 倒计时时长（分钟），默认为5分钟
        [JsonIgnore]
        public DateTime? LastExecutedDate { get; set; } // 记录上次执行日期，用于避免重复执行
        public string Description => Type switch
        {
            TaskType.Shutdown => $"关机 (倒计时 {ReminderMinutes} 分钟)",
            TaskType.Restart => $"重启 (倒计时 {ReminderMinutes} 分钟)",
            TaskType.RunProgram => $"运行程序: {Path.GetFileName(ProgramPath ?? "")}",
            TaskType.RunCommand => $"执行命令: {ProgramPath}",
            _ => "未知"
        };
    }

    public enum TaskType
    {
        Shutdown,
        Restart,
        RunProgram,
        RunCommand
    }

    public class DailyTaskService
    {
        private DispatcherTimer _timer = null!;
        private ObservableCollection<ScheduledTask> _tasks = new();
        private string _configFile;

        public DailyTaskService()
        {
            // 使用用户数据文件夹保存配置
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appDataPath, "ClassroomControl");
            Directory.CreateDirectory(appFolder);
            _configFile = Path.Combine(appFolder, "scheduled_tasks.json");
            
            LoadTasks();
            StartTimer();
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<ScheduledTask>? TaskExecuting;

        public ObservableCollection<ScheduledTask> Tasks => _tasks;

        public DailyTaskService(bool init = true)
        {
            // 使用用户数据文件夹保存配置
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appDataPath, "ClassroomControl");
            Directory.CreateDirectory(appFolder);
            _configFile = Path.Combine(appFolder, "scheduled_tasks.json");

            if (init)
            {
                LoadTasks();
                StartTimer();
            }
        }

        private void StartTimer()
        {
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(30);
            _timer.Tick += CheckTasks;
            _timer.Start();
        }

        // 记录正在等待执行的任务（用于提醒后等待执行）
        private Dictionary<string, DateTime> _pendingTasks = new();

        private void CheckTasks(object? sender, EventArgs e)
        {
            var now = DateTime.Now;
            var today = DateTime.Today;

            foreach (var task in _tasks.Where(t => t.IsEnabled))
            {
                if (TryParseTime(task.Time, out var taskTime))
                {
                    var targetTime = today.Add(taskTime);
                    
                    // 计算提醒时间
                    var reminderTime = targetTime.AddMinutes(-task.ReminderMinutes);
                    
                    // 检查是否到达提醒时间
                    var reminderDiff = now - reminderTime;
                    if (reminderDiff.TotalSeconds >= 0 && reminderDiff.TotalSeconds < 60)
                    {
                        // 检查今天是否已经提醒过
                        if (task.LastExecutedDate != today)
                        {
                            // 标记为已提醒
                            task.LastExecutedDate = today;
                            // 触发提醒事件
                            TaskExecuting?.Invoke(this, task);
                            // 添加到等待执行列表
                            _pendingTasks[task.Id] = targetTime;
                            StatusChanged?.Invoke(this, $"任务即将执行，已提醒用户: {task.Description} ({task.Time})");
                        }
                    }
                    
                    // 检查是否到达执行时间（针对已提醒的任务）
                    if (_pendingTasks.ContainsKey(task.Id))
                    {
                        var executeDiff = now - targetTime;
                        if (executeDiff.TotalSeconds >= 0)
                        {
                            // 移除等待执行列表
                            _pendingTasks.Remove(task.Id);
                            // 执行任务
                            ExecuteTask(task);
                        }
                    }
                }
            }
        }

        private bool TryParseTime(string timeStr, out TimeSpan time)
        {
            return TimeSpan.TryParse(timeStr, out time);
        }

        private static string GetSystem32Path()
        {
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return windir;
        }

        private void ExecuteTask(ScheduledTask task)
        {
            try
            {
                TaskExecuting?.Invoke(this, task);

                switch (task.Type)
                {
                    case TaskType.Shutdown:
                        try
                        {
                            var powerService = new SystemPowerService();
                            powerService.Shutdown();
                            StatusChanged?.Invoke(this, $"已执行定时关机任务 ({task.Time})");
                        }
                        catch (Exception ex)
                        {
                            StatusChanged?.Invoke(this, $"执行关机任务失败: {ex.Message}");
                            Debug.WriteLine($"执行关机任务失败: {ex.Message}");
                        }
                        break;

                    case TaskType.Restart:
                        try
                        {
                            var powerService = new SystemPowerService();
                            powerService.Restart();
                            StatusChanged?.Invoke(this, $"已执行定时重启任务 ({task.Time})");
                        }
                        catch (Exception ex)
                        {
                            StatusChanged?.Invoke(this, $"执行重启任务失败: {ex.Message}");
                            Debug.WriteLine($"执行重启任务失败: {ex.Message}");
                        }
                        break;

                    case TaskType.RunProgram:
                        if (!string.IsNullOrEmpty(task.ProgramPath) && File.Exists(task.ProgramPath))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = task.ProgramPath,
                                UseShellExecute = true
                            });
                            StatusChanged?.Invoke(this, $"已运行程序: {task.ProgramPath}");
                        }
                        else
                        {
                            StatusChanged?.Invoke(this, $"运行程序失败: 文件不存在 ({task.ProgramPath})");
                        }
                        break;

                    case TaskType.RunCommand:
                        if (!string.IsNullOrEmpty(task.ProgramPath))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = $"/c {task.ProgramPath}",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            });
                            StatusChanged?.Invoke(this, $"已执行命令: {task.ProgramPath}");
                        }
                        else
                        {
                            StatusChanged?.Invoke(this, "执行命令失败: 命令为空");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"执行任务失败: {ex.Message}");
                Debug.WriteLine($"执行任务失败: {ex.Message}");
            }
        }

        public bool AddTask(ScheduledTask task)
        {
            try
            {
                _tasks.Add(task);
                var saved = SaveTasks();
                StatusChanged?.Invoke(this, saved ? $"已添加任务: {task.Description} ({task.Time})" : "任务已添加但保存失败");
                return saved;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"添加任务失败: {ex.Message}");
                Debug.WriteLine($"添加任务失败: {ex.Message}");
                return false;
            }
        }

        public void RemoveTask(ScheduledTask task)
        {
            _tasks.Remove(task);
            SaveTasks();
            StatusChanged?.Invoke(this, "已删除任务");
        }

        public void ToggleTask(ScheduledTask task)
        {
            task.IsEnabled = !task.IsEnabled;
            SaveTasks();
            StatusChanged?.Invoke(this, task.IsEnabled ? "已启用任务" : "已禁用任务");
        }

        /// <summary>
        /// 取消待执行的任务（用户在提醒弹窗中点击取消时调用）
        /// </summary>
        public void CancelPendingTask(string taskId)
        {
            if (_pendingTasks.ContainsKey(taskId))
            {
                _pendingTasks.Remove(taskId);
                StatusChanged?.Invoke(this, "已取消待执行的任务");
            }
        }

        public bool SaveTasks()
        {
            try
            {
                var json = JsonSerializer.Serialize(_tasks, _jsonOptions);
                File.WriteAllText(_configFile, json);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存任务失败: {ex.Message}");
                return false;
            }
        }

        private void LoadTasks()
        {
            try
            {
                if (File.Exists(_configFile))
                {
                    var json = File.ReadAllText(_configFile);
                    var tasks = JsonSerializer.Deserialize<ObservableCollection<ScheduledTask>>(json, _jsonOptions);
                    if (tasks != null)
                    {
                        _tasks = tasks;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载任务失败: {ex.Message}");
                // 加载失败时尝试备份并重新创建
                try
                {
                    if (File.Exists(_configFile))
                    {
                        File.Copy(_configFile, _configFile + ".bak", overwrite: true);
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _timer?.Stop();
        }
    }
}