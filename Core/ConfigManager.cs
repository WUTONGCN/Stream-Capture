using System;
using System.IO;
using Newtonsoft.Json;
using StreamCapture.Models;

namespace StreamCapture.Core
{
    /// <summary>
    /// 配置管理器
    /// </summary>
    public class ConfigManager
    {
        private const string ConfigFileName = "platforms.json";
        private static AppConfig? _config;

        /// <summary>
        /// 加载配置
        /// </summary>
        public static AppConfig LoadConfig()
        {
            if (_config != null)
                return _config;

            try
            {
                // 优先从外部文件读取（用户自定义配置）
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);

                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    _config = JsonConvert.DeserializeObject<AppConfig>(json) ?? CreateDefaultConfig();
                    return _config;
                }

                // 从嵌入资源读取默认配置
                var embeddedJson = EmbeddedResourceHelper.ReadResourceAsString(ConfigFileName);
                if (!string.IsNullOrEmpty(embeddedJson))
                {
                    _config = JsonConvert.DeserializeObject<AppConfig>(embeddedJson) ?? CreateDefaultConfig();
                    return _config;
                }

                // 都失败则创建默认配置
                _config = CreateDefaultConfig();
                return _config;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载配置失败: {ex.Message}");
                return CreateDefaultConfig();
            }
        }

        /// <summary>
        /// 保存配置
        /// </summary>
        public static void SaveConfig(AppConfig config)
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(configPath, json);
                _config = config;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建默认配置
        /// </summary>
        private static AppConfig CreateDefaultConfig()
        {
            return new AppConfig
            {
                Platforms = new()
                {
                    new PlatformConfig
                    {
                        Id = "douyin",
                        Name = "抖音",
                        Enabled = true,
                        Protocols = new() { "rtmp", "rtmps" },
                        Ports = new() { 1935, 443 },
                        DomainPatterns = new() { "live\\.douyin\\.com", ".*\\.douyincdn\\.com" },
                        UrlPatterns = new() { "rtmps?://[^\\s\"'<>]+" },
                        KeywordPatterns = new() { "rtmp://", "rtmps://", "douyin" }
                    }
                },
                General = new GeneralConfig
                {
                    CaptureTimeout = 300,
                    MaxPacketsBuffer = 10000,
                    EnableSSLDecrypt = false,
                    AutoSave = true,
                    SaveDirectory = "./captures"
                }
            };
        }

        /// <summary>
        /// 重新加载配置
        /// </summary>
        public static void ReloadConfig()
        {
            _config = null;
            LoadConfig();
        }
    }
}
