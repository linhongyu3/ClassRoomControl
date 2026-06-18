using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Timers;

namespace ClassroomControl.Services
{
    public class TimeRange
    {
        [JsonPropertyName("startHour")]
        public int StartHour { get; set; } = 0;
        
        [JsonPropertyName("startMinute")]
        public int StartMinute { get; set; } = 0;
        
        [JsonPropertyName("endHour")]
        public int EndHour { get; set; } = 23;
        
        [JsonPropertyName("endMinute")]
        public int EndMinute { get; set; } = 59;
        
        public TimeSpan StartTime => new TimeSpan(StartHour, StartMinute, 0);
        public TimeSpan EndTime => new TimeSpan(EndHour, EndMinute, 0);
        
        public bool IsWithinRestrictedTime(TimeSpan currentTime)
        {
            if (StartTime <= EndTime)
            {
                return currentTime >= StartTime && currentTime <= EndTime;
            }
            else
            {
                return currentTime >= StartTime || currentTime <= EndTime;
            }
        }
        
        public override string ToString()
        {
            return $"{StartHour:D2}:{StartMinute:D2} - {EndHour:D2}:{EndMinute:D2}";
        }
    }

    public class AppUsageRule : INotifyPropertyChanged
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        
        [JsonPropertyName("appName")]
        public string AppName { get; set; } = "";
        
        [JsonPropertyName("appPath")]
        public string AppPath { get; set; } = "";
        
        private bool _isEnabled = true;
        [JsonPropertyName("isEnabled")]
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged(nameof(IsEnabled));
                }
            }
        }
        
        [JsonPropertyName("timeRanges")]
        public List<TimeRange> TimeRanges { get; set; } = new List<TimeRange>();
        
        public event PropertyChangedEventHandler? PropertyChanged;
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
        public bool IsWithinRestrictedTime()
        {
            var now = DateTime.Now.TimeOfDay;
            return TimeRanges.Any(tr => tr.IsWithinRestrictedTime(now));
        }
        
        public string GetTimeRangesString()
        {
            if (TimeRanges.Count == 0)
                return "未设置";
            return string.Join(", ", TimeRanges.Select(tr => tr.ToString()));
        }
    }

    public class AppUsageService : IDisposable
    {
        private List<AppUsageRule> _rules = new();
        private string _settingsFile;
        private System.Timers.Timer _monitorTimer;
        private HashSet<int> _blockedProcesses = new();
        private HashSet<string> _notifiedRules = new();
        private HashSet<string> _temporaryAllowedApps = new();
        private Dictionary<string, DateTime> _temporaryAllowedExpiry = new();
        private bool _disposed;
        
        public event EventHandler<AppUsageRule>? AppBlocked;
        public event EventHandler<List<AppUsageRule>>? RulesChanged;
        public event EventHandler<(string appName, string appPath)>? AppBlockedRequest;

        public AppUsageService(string settingsFolder)
        {
            _settingsFile = System.IO.Path.Combine(settingsFolder, "app_usage_rules.json");
            LoadRules();
            
            _monitorTimer = new System.Timers.Timer(2000);
            _monitorTimer.Elapsed += OnMonitorTick;
            _monitorTimer.AutoReset = true;
        }

        public void StartMonitoring()
        {
            if (!_disposed)
            {
                _monitorTimer.Start();
            }
        }

        public void StopMonitoring()
        {
            if (!_disposed)
            {
                _monitorTimer.Stop();
            }
        }

        public List<AppUsageRule> GetRules() => _rules.ToList();

        public void AllowAppTemporarily(string appName, int minutes = 15)
        {
            var key = appName.ToLower().Replace(".exe", "");
            if (_temporaryAllowedApps.Contains(key))
            {
                _temporaryAllowedExpiry[key] = DateTime.Now.AddMinutes(minutes);
            }
            else
            {
                _temporaryAllowedApps.Add(key);
                _temporaryAllowedExpiry[key] = DateTime.Now.AddMinutes(minutes);
            }
        }

        public void AddRule(AppUsageRule rule)
        {
            _rules.Add(rule);
            SaveRules();
            RulesChanged?.Invoke(this, _rules);
        }

        public void UpdateRule(AppUsageRule rule)
        {
            var index = _rules.FindIndex(r => r.Id == rule.Id);
            if (index >= 0)
            {
                _rules[index] = rule;
                _notifiedRules.Remove(rule.Id);
                SaveRules();
                RulesChanged?.Invoke(this, _rules);
            }
        }

        public void RemoveRule(string ruleId)
        {
            _rules.RemoveAll(r => r.Id == ruleId);
            _notifiedRules.Remove(ruleId);
            SaveRules();
            RulesChanged?.Invoke(this, _rules);
        }

        public void LoadRules()
        {
            try
            {
                if (System.IO.File.Exists(_settingsFile))
                {
                    var json = System.IO.File.ReadAllText(_settingsFile);
                    _rules = JsonSerializer.Deserialize<List<AppUsageRule>>(json) ?? new();
                }
            }
            catch { _rules = new(); }
        }

        public void SaveRules()
        {
            try
            {
                var json = JsonSerializer.Serialize(_rules, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(_settingsFile, json);
            }
            catch { }
        }

        private void OnMonitorTick(object? sender, ElapsedEventArgs e)
        {
            var enabledRules = _rules.Where(r => r.IsEnabled && r.IsWithinRestrictedTime()).ToList();
            if (enabledRules.Count == 0) return;

            try
            {
                var processesToKill = new List<int>();
                
                var processes = Process.GetProcesses();
                foreach (var process in processes)
                {
                    try
                    {
                        var processName = process.ProcessName.ToLower();
                        var mainModule = process.MainModule;
                        var exePath = mainModule?.FileName?.ToLower() ?? "";

                        foreach (var rule in enabledRules)
                        {
                            var ruleAppName = rule.AppName.ToLower().Replace(".exe", "");
                            var ruleAppPath = rule.AppPath.ToLower();
                            
                            var nameMatch = processName == ruleAppName || 
                                            processName.EndsWith("." + ruleAppName) ||
                                            processName.StartsWith(ruleAppName + ".");
                            
                            var pathMatch = !string.IsNullOrEmpty(ruleAppPath) && 
                                            exePath.Contains(ruleAppPath);
                            
                            if (nameMatch || pathMatch)
                            {
                                if (_temporaryAllowedApps.Contains(ruleAppName))
                                {
                                    if (_temporaryAllowedExpiry.TryGetValue(ruleAppName, out var expiry) && DateTime.Now > expiry)
                                    {
                                        _temporaryAllowedApps.Remove(ruleAppName);
                                        _temporaryAllowedExpiry.Remove(ruleAppName);
                                    }
                                    else
                                    {
                                        continue;
                                    }
                                }
                                
                                processesToKill.Add(process.Id);
                                
                                if (!_notifiedRules.Contains(rule.Id))
                                {
                                    _notifiedRules.Add(rule.Id);
                                    AppBlockedRequest?.Invoke(this, (rule.AppName, mainModule?.FileName ?? ""));
                                }
                                break;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        process.Dispose();
                    }
                }
                
                foreach (var processId in processesToKill)
                {
                    try
                    {
                        var process = Process.GetProcessById(processId);
                        
                        var processName = process.ProcessName.ToLower();
                        var mainModule = process.MainModule;
                        var exePath = mainModule?.FileName?.ToLower() ?? "";
                        
                        bool shouldKill = true;
                        foreach (var rule in enabledRules)
                        {
                            var ruleAppName = rule.AppName.ToLower().Replace(".exe", "");
                            var ruleAppPath = rule.AppPath.ToLower();
                            
                            var nameMatch = processName == ruleAppName || 
                                            processName.EndsWith("." + ruleAppName) ||
                                            processName.StartsWith(ruleAppName + ".");
                            
                            var pathMatch = !string.IsNullOrEmpty(ruleAppPath) && 
                                            exePath.Contains(ruleAppPath);
                            
                            if (nameMatch || pathMatch)
                            {
                                if (_temporaryAllowedApps.Contains(ruleAppName))
                                {
                                    if (_temporaryAllowedExpiry.TryGetValue(ruleAppName, out var expiry) && DateTime.Now > expiry)
                                    {
                                        _temporaryAllowedApps.Remove(ruleAppName);
                                        _temporaryAllowedExpiry.Remove(ruleAppName);
                                    }
                                    else
                                    {
                                        shouldKill = false;
                                    }
                                }
                                break;
                            }
                        }
                        
                        if (shouldKill)
                        {
                            process.Kill();
                        }
                        process.Dispose();
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _monitorTimer.Stop();
            _monitorTimer.Dispose();
            _blockedProcesses.Clear();
            _notifiedRules.Clear();
        }
    }
}