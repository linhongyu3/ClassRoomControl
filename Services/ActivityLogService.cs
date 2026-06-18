using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassroomControl.Services
{
    public class ActivityLogService
    {
        private const string LogFile = "activity_log.json";
        private const int MaxLogs = 200;

        public ObservableCollection<LogEntry> Logs { get; } = new();

        public void Load()
        {
            try
            {
                if (File.Exists(LogFile))
                {
                    var json = File.ReadAllText(LogFile);
                    var entries = JsonSerializer.Deserialize<List<LogEntry>>(json);
                    if (entries != null)
                    {
                        foreach (var entry in entries)
                            Logs.Add(entry);
                    }
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                var entries = Logs.ToList();
                if (entries.Count > MaxLogs)
                    entries = entries.Take(MaxLogs).ToList();
                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(LogFile, json);
            }
            catch { }
        }

        public void Log(string action, string detail = "")
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Action = action,
                Detail = detail
            };
            Logs.Insert(0, entry);

            // 限制数量
            while (Logs.Count > MaxLogs)
                Logs.RemoveAt(Logs.Count - 1);

            Save();
        }

        public void Clear()
        {
            Logs.Clear();
            Save();
        }
    }

    public class LogEntry
    {
        [JsonPropertyName("time")]
        public DateTime Time { get; set; } = DateTime.Now;

        [JsonPropertyName("action")]
        public string Action { get; set; } = "";

        [JsonPropertyName("detail")]
        public string Detail { get; set; } = "";
    }
}