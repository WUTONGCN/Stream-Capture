using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace StreamCapture.Models
{
    /// <summary>
    /// 平台配置模型
    /// </summary>
    public class PlatformConfig
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public List<string> Protocols { get; set; } = new();
        public List<int> Ports { get; set; } = new();
        public List<string> DomainPatterns { get; set; } = new();
        public List<string> UrlPatterns { get; set; } = new();
        public List<string> KeywordPatterns { get; set; } = new();
    }

    /// <summary>
    /// 通用配置
    /// </summary>
    public class GeneralConfig
    {
        public int CaptureTimeout { get; set; } = 300;
        public int MaxPacketsBuffer { get; set; } = 10000;
        public bool EnableSSLDecrypt { get; set; } = false;
        public bool AutoSave { get; set; } = false;
        public string SaveDirectory { get; set; } = "./captures";
    }

    /// <summary>
    /// 配置根对象
    /// </summary>
    public class AppConfig
    {
        public List<PlatformConfig> Platforms { get; set; } = new();
        public GeneralConfig General { get; set; } = new();
    }

    /// <summary>
    /// 捕获结果
    /// </summary>
    public class CaptureResult
    {
        public string Platform { get; set; } = string.Empty;
        public string Protocol { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string StreamKey { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new();
        [Newtonsoft.Json.JsonIgnore]
        public string RawData { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;

        // UI icon path for platform (from embedded resources)
        private BitmapImage? _platformIconCache;
        [Newtonsoft.Json.JsonIgnore]
        public BitmapImage? PlatformIcon
        {
            get
            {
                if (_platformIconCache != null)
                    return _platformIconCache;

                var iconPath = Platform switch
                {
                    "抖音" => "image/douyin.png",
                    "小红书" => "image/xiaohongshu.png",
                    "哔哩哔哩" => "image/bilibili.png",
                    "快手" => "image/kuaishou.png",
                    "京东" => "image/jd.png",
                    _ => null
                };

                if (iconPath != null)
                {
                    _platformIconCache = Core.EmbeddedResourceHelper.LoadImageFromResource(iconPath);
                }

                return _platformIconCache;
            }
        }
    }
}
