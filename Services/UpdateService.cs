using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;

namespace ClassroomControl
{
    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string LatestVersion { get; set; } = "";
        public string CurrentVersion { get; set; } = AppConstants.AppVersion;
        public string ReleaseNotes { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public DateTime ReleaseDate { get; set; }
        public long FileSize { get; set; }
    }

    public class UpdateService
    {
        private readonly HttpClient _httpClient;

        public UpdateService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", $"{AppConstants.AppNameEnglish}/{AppConstants.AppVersion}");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        }

        public async Task<UpdateInfo> CheckForUpdateAsync()
        {
            var updateInfo = new UpdateInfo
            {
                CurrentVersion = AppConstants.AppVersion,
                HasUpdate = false
            };

            try
            {
                var response = await _httpClient.GetAsync(AppConstants.GitHubApiUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        throw new Exception("GitHub仓库中未找到发布版本，请先在GitHub上创建Release");
                    }
                    else
                    {
                        throw new Exception($"GitHub API返回错误：{response.StatusCode} - {response.ReasonPhrase}");
                    }
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var releaseData = JsonSerializer.Deserialize<GitHubRelease>(responseContent);

                if (releaseData != null && !string.IsNullOrEmpty(releaseData.tag_name))
                {
                    var latestVersion = ExtractVersionFromTag(releaseData.tag_name);
                    
                    if (!IsValidVersion(latestVersion))
                    {
                        throw new Exception($"GitHub Release的tag名称\"{releaseData.tag_name}\"不是有效的版本号格式，请使用v1.0.1或1.0.1等格式");
                    }
                    
                    if (!string.IsNullOrEmpty(latestVersion) && CompareVersions(latestVersion, AppConstants.AppVersion) > 0)
                    {
                        updateInfo.HasUpdate = true;
                        updateInfo.LatestVersion = latestVersion;
                        updateInfo.ReleaseNotes = releaseData.body ?? "更新内容请查看发布说明";
                        
                        var asset = releaseData.assets?.FirstOrDefault(a => a.name.EndsWith(".exe") || a.name.EndsWith(".zip"));
                        if (asset != null)
                        {
                            var selectedMirror = AppConstants.ProxyMirrors[new Random().Next(AppConstants.ProxyMirrors.Length)];
                            updateInfo.DownloadUrl = $"{selectedMirror}{asset.browser_download_url}";
                            updateInfo.FileSize = asset.size;
                        }
                        else
                        {
                            updateInfo.DownloadUrl = releaseData.html_url;
                        }
                        
                        if (DateTime.TryParse(releaseData.published_at, out var publishDate))
                        {
                            updateInfo.ReleaseDate = publishDate;
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"网络请求失败：{ex.Message}");
            }
            catch (TaskCanceledException)
            {
                throw new Exception("请求超时，请检查网络连接");
            }
            catch (Exception ex)
            {
                throw new Exception($"检查更新失败：{ex.Message}");
            }

            return updateInfo;
        }

        private string ExtractVersionFromTag(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return "";
            
            var version = tagName.TrimStart('v', 'V');
            return version;
        }

        private bool IsValidVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            
            var parts = version.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;
            
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out _)) return false;
            }
            
            return true;
        }

        private int CompareVersions(string version1, string version2)
        {
            var v1Parts = version1.Split('.').Select(int.Parse).ToArray();
            var v2Parts = version2.Split('.').Select(int.Parse).ToArray();
            
            for (int i = 0; i < Math.Max(v1Parts.Length, v2Parts.Length); i++)
            {
                int v1 = i < v1Parts.Length ? v1Parts[i] : 0;
                int v2 = i < v2Parts.Length ? v2Parts[i] : 0;
                
                if (v1 > v2) return 1;
                if (v1 < v2) return -1;
            }
            
            return 0;
        }

        private class GitHubRelease
        {
            public string tag_name { get; set; } = "";
            public string name { get; set; } = "";
            public string body { get; set; } = "";
            public string html_url { get; set; } = "";
            public string published_at { get; set; } = "";
            public GitHubAsset[]? assets { get; set; }
        }

        private class GitHubAsset
        {
            public string name { get; set; } = "";
            public string browser_download_url { get; set; } = "";
            public long size { get; set; }
        }
    }
}