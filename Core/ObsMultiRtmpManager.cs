using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Collections.Generic;

namespace StreamCapture.Core
{
    /// <summary>
    /// OBS多路推流插件管理器
    /// 自动配置obs-multi-rtmp插件并启动推流
    /// </summary>
    public class ObsMultiRtmpManager
    {
        private readonly Action<string> _logger;
        private string _obsProfilePath;
        private string _obsConfigPath;

        public ObsMultiRtmpManager(Action<string> logger)
        {
            _logger = logger;
            FindObsConfigPath();
        }

        /// <summary>
        /// 查找OBS配置文件路径
        /// </summary>
        private void FindObsConfigPath()
        {
            try
            {
                // OBS配置文件通常在 %APPDATA%\obs-studio
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var obsBasePath = Path.Combine(appData, "obs-studio");

                if (!Directory.Exists(obsBasePath))
                {
                    _logger($"❌ 未找到OBS配置目录: {obsBasePath}");
                    return;
                }

                // 查找当前使用的profile
                var globalConfigPath = Path.Combine(obsBasePath, "global.ini");
                if (File.Exists(globalConfigPath))
                {
                    var globalConfig = File.ReadAllLines(globalConfigPath);
                    var profileLine = globalConfig.FirstOrDefault(l => l.StartsWith("Profile="));
                    if (profileLine != null)
                    {
                        var profileName = profileLine.Split('=')[1].Trim();
                        _obsProfilePath = Path.Combine(obsBasePath, "basic", "profiles", profileName);
                        _obsConfigPath = Path.Combine(_obsProfilePath, "obs-multi-rtmp.json");

                        _logger($"✅ 找到OBS配置目录");
                        _logger($"   Profile: {profileName}");
                        _logger($"   路径: {_obsProfilePath}");
                        return;
                    }
                }

                // 如果找不到profile，使用第一个找到的
                var profilesPath = Path.Combine(obsBasePath, "basic", "profiles");
                if (Directory.Exists(profilesPath))
                {
                    var profiles = Directory.GetDirectories(profilesPath);
                    if (profiles.Length > 0)
                    {
                        _obsProfilePath = profiles[0];
                        _obsConfigPath = Path.Combine(_obsProfilePath, "obs-multi-rtmp.json");
                        _logger($"✅ 使用默认Profile: {Path.GetFileName(_obsProfilePath)}");
                        return;
                    }
                }

                _logger($"❌ 未找到OBS Profile目录");
            }
            catch (Exception ex)
            {
                _logger($"❌ 查找OBS配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 添加推流目标到OBS多路推流配置（使用命令文件方式）
        /// </summary>
        /// <param name="platform">平台名称</param>
        /// <param name="serverUrl">推流服务器地址</param>
        /// <param name="streamKey">流密钥</param>
        /// <param name="autoStart">是否自动开始推流，默认为false</param>
        /// <returns>是否成功</returns>
        public bool AddStreamTarget(string platform, string serverUrl, string streamKey, bool autoStart = false)
        {
            try
            {
                if (string.IsNullOrEmpty(_obsProfilePath))
                {
                    _logger("❌ OBS配置路径未找到，无法自动配置");
                    return false;
                }

                _logger("");
                _logger("🔧 ==========================================");
                _logger("🔧 开始配置OBS多路推流插件...");
                _logger("🔧 ==========================================");

                // 创建命令JSON对象
                var command = new JsonObject
                {
                    ["action"] = "add_target",
                    ["platform"] = platform,
                    ["name"] = platform,  // 直接使用平台名称作为命名
                    ["server"] = serverUrl,
                    ["key"] = streamKey,
                    ["auto_start"] = autoStart,
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                // 命令文件路径（放在OBS Profile目录）
                var cmdFilePath = Path.Combine(_obsProfilePath, "obs-multi-rtmp-streamcapture-cmd.json");

                // 写入命令文件（使用UTF-8 without BOM）
                var utf8NoBom = new UTF8Encoding(false);
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                var jsonString = command.ToJsonString(options);
                File.WriteAllText(cmdFilePath, jsonString, utf8NoBom);

                _logger($"   ✅ 命令文件已创建: {Path.GetFileName(cmdFilePath)}");
                _logger($"   📝 平台: {platform}");
                _logger($"   📝 服务器: {serverUrl}");
                _logger($"   📝 流密钥: {streamKey.Substring(0, Math.Min(20, streamKey.Length))}...");
                _logger($"   🚀 自动启动: {(autoStart ? "是" : "否")}");
                _logger("");
                _logger("🔧 ==========================================");
                _logger("🔧 OBS多路推流配置完成！");
                _logger($"🔧 插件将在500ms内自动检测并添加{(autoStart ? "并开始推流" : "")}");
                _logger("🔧 ==========================================");
                _logger("");

                return true;
            }
            catch (Exception ex)
            {
                _logger($"❌ 配置OBS失败: {ex.Message}");
                _logger($"   堆栈: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 发送开始推流命令到OBS
        /// </summary>
        /// <param name="targetName">推流目标名称（平台名称），空字符串或"all"表示全部</param>
        /// <returns>是否成功</returns>
        public bool StartStream(string targetName = "")
        {
            try
            {
                if (string.IsNullOrEmpty(_obsProfilePath))
                {
                    _logger("❌ OBS配置路径未找到，无法发送命令");
                    return false;
                }

                _logger("");
                _logger("🚀 ==========================================");
                _logger($"🚀 发送开始推流命令: {(string.IsNullOrEmpty(targetName) ? "全部" : targetName)}");
                _logger("🚀 ==========================================");

                // 创建命令JSON对象
                var command = new JsonObject
                {
                    ["action"] = "start_stream",
                    ["target_name"] = targetName,
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                // 命令文件路径
                var cmdFilePath = Path.Combine(_obsProfilePath, "obs-multi-rtmp-streamcapture-cmd.json");

                // 写入命令文件
                var utf8NoBom = new UTF8Encoding(false);
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                var jsonString = command.ToJsonString(options);
                File.WriteAllText(cmdFilePath, jsonString, utf8NoBom);

                _logger($"   ✅ 开始推流命令已发送");
                _logger($"   📺 OBS插件将在500ms内响应");
                _logger("");

                return true;
            }
            catch (Exception ex)
            {
                _logger($"❌ 发送开始推流命令失败: {ex.Message}");
                return false;
            }
        }


        /// <summary>
        /// 启动OBS（如果未运行）并提示用户开始推流
        /// </summary>
        public void StartObs()
        {
            try
            {
                // 检查OBS是否已运行
                var obsProcesses = Process.GetProcessesByName("obs64");
                if (obsProcesses.Length == 0)
                {
                    obsProcesses = Process.GetProcessesByName("obs32");
                }

                if (obsProcesses.Length > 0)
                {
                    _logger("📺 OBS已在运行");
                    _logger("");
                    _logger("⚠️  重要提示:");
                    _logger("   1. OBS需要重新加载配置才能看到新的推流目标");
                    _logger("   2. 请在OBS中: 工具 -> 多路推流设置 -> 刷新");
                    _logger("   3. 或者重启OBS以加载新配置");
                    _logger("");
                    return;
                }

                // 尝试启动OBS
                _logger("🚀 尝试启动OBS...");

                var possiblePaths = new[]
                {
                    @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
                    @"C:\Program Files (x86)\obs-studio\bin\64bit\obs64.exe",
                    @"C:\Program Files\obs-studio\bin\32bit\obs32.exe"
                };

                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        Process.Start(path);
                        _logger($"   ✅ OBS已启动: {path}");
                        _logger("");
                        _logger("📝 下一步:");
                        _logger("   1. 等待OBS完全启动");
                        _logger("   2. 工具 -> 多路推流设置");
                        _logger("   3. 勾选新添加的推流目标");
                        _logger("   4. 点击【开始推流】");
                        _logger("");
                        return;
                    }
                }

                _logger("⚠️  未找到OBS安装路径，请手动启动OBS");
                _logger("   配置已保存，OBS启动后会自动加载");
                _logger("");
            }
            catch (Exception ex)
            {
                _logger($"❌ 启动OBS失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理过期的推流目标（可选）
        /// </summary>
        public void CleanupOldTargets(int keepRecentCount = 5)
        {
            var utf8NoBom = new UTF8Encoding(false);

            try
            {
                if (!File.Exists(_obsConfigPath))
                    return;

                var content = File.ReadAllText(_obsConfigPath, utf8NoBom);
                var config = JsonNode.Parse(content);
                var targets = config["targets"] as JsonArray;

                if (targets == null || targets.Count <= keepRecentCount)
                    return;

                _logger($"🧹 清理旧的推流目标 (保留最近 {keepRecentCount} 个)...");

                // 保留最后N个
                while (targets.Count > keepRecentCount)
                {
                    targets.RemoveAt(0);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                File.WriteAllText(_obsConfigPath, config.ToJsonString(options), utf8NoBom);
                _logger($"   ✅ 已清理，剩余 {targets.Count} 个推流目标");
            }
            catch (Exception ex)
            {
                _logger($"⚠️  清理失败: {ex.Message}");
            }
        }
    }
}
