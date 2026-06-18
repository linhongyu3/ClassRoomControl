using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace ClassroomControl.Services
{
    public class DesktopOrganizerService : IDisposable
    {
        private DispatcherTimer? _autoTimer;
        private string _configFile;

        public event EventHandler<string>? StatusChanged;

        public DesktopOrganizerSettings Settings { get; set; } = new();

        public DesktopOrganizerService()
        {
            // 使用用户数据文件夹保存配置
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appDataPath, "ClassroomControl");
            Directory.CreateDirectory(appFolder);
            _configFile = Path.Combine(appFolder, "desktop_organizer_settings.json");

            LoadSettings();
            StartAutoTimer();
        }

        public string GetDesktopPath()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        public string GetSourceFolderPath()
        {
            string source = Settings.SourceFolderPath;
            if (string.IsNullOrWhiteSpace(source))
            {
                return GetDesktopPath();
            }
            return source;
        }

        public string GetTargetFolderPath()
        {
            string target = Settings.TargetFolderPath;
            if (string.IsNullOrWhiteSpace(target))
            {
                target = Path.Combine(GetSourceFolderPath(), "整理后的文件");
            }
            return target;
        }

        public void UpdateSettings(DesktopOrganizerSettings settings)
        {
            Settings = settings;
            SaveSettings();
            RestartAutoTimer();
            StatusChanged?.Invoke(this, "设置已保存");
        }

        public void SaveSettings()
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFile, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存设置失败: {ex.Message}");
            }
        }

        public void LoadSettings()
        {
            try
            {
                if (File.Exists(_configFile))
                {
                    var json = File.ReadAllText(_configFile);
                    var settings = JsonSerializer.Deserialize<DesktopOrganizerSettings>(json);
                    if (settings != null)
                    {
                        Settings = settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载设置失败: {ex.Message}");
            }
        }

        private void StartAutoTimer()
        {
            _autoTimer = new DispatcherTimer();
            _autoTimer.Interval = TimeSpan.FromSeconds(30);
            _autoTimer.Tick += CheckAutoExecute;
            _autoTimer.Start();
        }

        private void RestartAutoTimer()
        {
            _autoTimer?.Stop();
            StartAutoTimer();
        }

        private void CheckAutoExecute(object? sender, EventArgs e)
        {
            if (!Settings.AutoExecuteEnabled) return;

            var currentTime = DateTime.Now.ToString("HH:mm");

            var enabledTime = Settings.AutoExecuteTimes.FirstOrDefault(t => t.Time == currentTime && t.IsEnabled);
            if (enabledTime != null)
            {
                var result = OrganizeDesktop();
                if (result.HasError)
                {
                    StatusChanged?.Invoke(this, $"自动整理失败: {result.ErrorMessage}");
                }
                else
                {
                    StatusChanged?.Invoke(this, $"[{currentTime}] 自动整理完成，共移动 {result.TotalMoved} 个项目");
                }
            }
        }

        /// <summary>
        /// 获取要移动的文件和文件夹列表（预览）
        /// </summary>
        public OrganizePreview GetItemsToOrganize()
        {
            var result = new OrganizePreview();
            string sourcePath = GetSourceFolderPath();
            string targetFolder = GetTargetFolderPath();

            // 构建排除集合：目标文件夹 + 用户配置的排除列表
            var excludedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            excludedFolders.Add(Path.GetFileName(targetFolder.TrimEnd(Path.DirectorySeparatorChar)));
            foreach (var folder in Settings.ExcludedFolders)
            {
                if (!string.IsNullOrWhiteSpace(folder))
                    excludedFolders.Add(folder);
            }

            // 构建排除扩展名集合（统一小写，含点号）
            var excludedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in Settings.ExcludedExtensions)
            {
                if (!string.IsNullOrWhiteSpace(ext))
                {
                    string normalized = ext.Trim().StartsWith('.') ? ext.Trim() : $".{ext.Trim()}";
                    excludedExts.Add(normalized);
                }
            }

            try
            {
                var allItems = Directory.GetFileSystemEntries(sourcePath);

                foreach (var itemPath in allItems)
                {
                    string itemName = Path.GetFileName(itemPath);

                    // 跳过目标文件夹本身
                    if (string.Equals(itemName, Path.GetFileName(targetFolder.TrimEnd(Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 跳过快捷方式
                    if (itemName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Shortcuts.Add(itemPath);
                        continue;
                    }

                    // 跳过排除列表中的文件夹
                    if (excludedFolders.Contains(itemName))
                    {
                        result.ExcludedFolders.Add(itemPath);
                        continue;
                    }

                    if (File.Exists(itemPath))
                    {
                        // 跳过排除扩展名的文件
                        var ext = Path.GetExtension(itemPath);
                        if (!string.IsNullOrEmpty(ext) && excludedExts.Contains(ext))
                        {
                            result.ExcludedExtensions.Add(itemPath);
                            continue;
                        }
                        result.FilesToMove.Add(itemPath);
                    }
                    else if (Directory.Exists(itemPath))
                    {
                        result.FoldersToMove.Add(itemPath);
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// 执行桌面整理
        /// </summary>
        public OrganizeResult OrganizeDesktop()
        {
            var result = new OrganizeResult();
            string targetFolderPath = GetTargetFolderPath();

            try
            {
                if (!Directory.Exists(targetFolderPath))
                {
                    Directory.CreateDirectory(targetFolderPath);
                    result.CreatedFolders.Add(targetFolderPath);
                }

                var preview = GetItemsToOrganize();

                foreach (var filePath in preview.FilesToMove)
                {
                    try
                    {
                        string fileName = Path.GetFileName(filePath);
                        string targetPath = Path.Combine(targetFolderPath, fileName);

                        if (File.Exists(targetPath))
                        {
                            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                            string ext = Path.GetExtension(fileName);
                            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                            targetPath = Path.Combine(targetFolderPath, $"{nameWithoutExt}_{timestamp}{ext}");
                        }

                        File.Move(filePath, targetPath);
                        result.MovedFiles.Add(filePath);
                    }
                    catch (Exception ex)
                    {
                        result.FailedItems.Add($"{Path.GetFileName(filePath)}: {ex.Message}");
                    }
                }

                foreach (var folderPath in preview.FoldersToMove)
                {
                    try
                    {
                        string folderName = Path.GetFileName(folderPath);
                        string targetPath = Path.Combine(targetFolderPath, folderName);

                        if (Directory.Exists(targetPath))
                        {
                            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                            targetPath = Path.Combine(targetFolderPath, $"{folderName}_{timestamp}");
                        }

                        Directory.Move(folderPath, targetPath);
                        result.MovedFolders.Add(folderPath);
                    }
                    catch (Exception ex)
                    {
                        result.FailedItems.Add($"{Path.GetFileName(folderPath)}: {ex.Message}");
                    }
                }

                result.SkippedShortcuts = preview.Shortcuts.Count;
                result.SkippedFolders = preview.ExcludedFolders.Count;
                result.SkippedExtensions = preview.ExcludedExtensions.Count;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public void Dispose()
        {
            _autoTimer?.Stop();
        }
    }

    /// <summary>
    /// 桌面整理设置
    /// </summary>
    public class AutoExecuteTimeItem
    {
        [JsonPropertyName("time")]
        public string Time { get; set; } = "";
        
        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; } = true;
    }

    public class DesktopOrganizerSettings
    {
        [JsonPropertyName("source_folder_path")]
        public string SourceFolderPath { get; set; } = "";

        [JsonPropertyName("target_folder_path")]
        public string TargetFolderPath { get; set; } = "";

        [JsonPropertyName("excluded_folders")]
        public ObservableCollection<string> ExcludedFolders { get; set; } = new ObservableCollection<string>
        {
            "Desktop",
            "Documents",
            "Downloads"
        };

        [JsonPropertyName("excluded_extensions")]
        public ObservableCollection<string> ExcludedExtensions { get; set; } = new ObservableCollection<string>();

        [JsonPropertyName("auto_execute_enabled")]
        public bool AutoExecuteEnabled { get; set; } = false;

        [JsonPropertyName("auto_execute_times")]
        public ObservableCollection<AutoExecuteTimeItem> AutoExecuteTimes { get; set; } = new ObservableCollection<AutoExecuteTimeItem>
        {
            new AutoExecuteTimeItem { Time = "17:00", IsEnabled = true }
        };
    }

    /// <summary>
    /// 整理预览信息
    /// </summary>
    public class OrganizePreview
    {
        public List<string> FilesToMove { get; set; } = new();
        public List<string> FoldersToMove { get; set; } = new();
        public List<string> Shortcuts { get; set; } = new();
        public List<string> ExcludedFolders { get; set; } = new();
        public List<string> ExcludedExtensions { get; set; } = new();
        public string ErrorMessage { get; set; } = "";
    }

    /// <summary>
    /// 整理结果
    /// </summary>
    public class OrganizeResult
    {
        public List<string> MovedFiles { get; set; } = new();
        public List<string> MovedFolders { get; set; } = new();
        public List<string> CreatedFolders { get; set; } = new();
        public List<string> FailedItems { get; set; } = new();
        public int SkippedShortcuts { get; set; }
        public int SkippedFolders { get; set; }
        public int SkippedExtensions { get; set; }
        public string ErrorMessage { get; set; } = "";

        public int TotalMoved => MovedFiles.Count + MovedFolders.Count;
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public bool HasFailures => FailedItems.Count > 0;
    }
}