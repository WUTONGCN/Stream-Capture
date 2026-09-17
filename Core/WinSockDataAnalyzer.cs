using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamCapture.Core
{
    /// <summary>
    /// WinSock数据分析器
    /// 分析Hook捕获的数据，提取HTTP/RTMP推流地址
    /// </summary>
    public class WinSockDataAnalyzer
    {
        private readonly Action<string> _logger;
        private readonly Action<string, string, string> _onStreamFound; // platform, url, key
        private long _httpPacketCount = 0;
        private long _rtmpPacketCount = 0;
        private long _kuaishouHttpCount = 0;

        public WinSockDataAnalyzer(Action<string> logger, Action<string, string, string> onStreamFound)
        {
            _logger = logger;
            _onStreamFound = onStreamFound;
        }

        /// <summary>
        /// 分析捕获的数据
        /// </summary>
        public void AnalyzeData(byte[] data, bool isSend)
        {
            try
            {
                // 尝试解析为文本
                var text = Encoding.UTF8.GetString(data);

                // [调试] 记录所有TLS解密数据
                if (data.Length > 0)
                {
                    try
                    {
                        _logger($"[Native Hook数据] {(isSend ? "发送" : "接收")} {data.Length} 字节");

                        // 安全地提取文本预览
                        if (text != null && text.Length > 0)
                        {
                            var preview = text.Length > 100 ? text.Substring(0, 100) : text;
                            _logger($"   内容: {preview}");
                        }
                    }
                    catch
                    {
                        _logger($"[Native Hook数据] 接收 {data.Length} 字节（解析异常）");
                    }
                }

                // 检查是否是HTTP请求/响应
                if (text.Contains("HTTP/") || text.Contains("GET ") || text.Contains("POST "))
                {
                    AnalyzeHttpData(text, isSend);
                }

                // 检查是否是RTMP数据
                if (data.Length > 12 && (data[0] == 0x03 || data[0] == 0x06))
                {
                    AnalyzeRtmpData(data, text, isSend);
                }
            }
            catch (Exception ex)
            {
                // 忽略解析错误
                // _logger($"数据分析异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 智能检测并格式化JSON数据
        /// </summary>
        private bool TryFormatJson(string text, out string formatted)
        {
            formatted = text;
            try
            {
                // 查找JSON起始位置
                int jsonStart = text.IndexOf('{');
                int jsonArrayStart = text.IndexOf('[');

                if (jsonStart == -1 && jsonArrayStart == -1)
                    return false;

                // 使用最早出现的JSON标记
                int start = jsonStart != -1 && jsonArrayStart != -1
                    ? Math.Min(jsonStart, jsonArrayStart)
                    : (jsonStart != -1 ? jsonStart : jsonArrayStart);

                // 提取JSON部分
                string jsonPart = text.Substring(start);

                // 简单验证是否包含JSON关键字
                if (jsonPart.Contains("\"") && (jsonPart.Contains(":") || jsonPart.Contains(",")))
                {
                    // 限制长度避免日志过长
                    if (jsonPart.Length > 2000)
                        jsonPart = jsonPart.Substring(0, 2000) + "...(truncated)";

                    formatted = jsonPart;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 分析HTTP数据
        /// </summary>
        private void AnalyzeHttpData(string text, bool isSend)
        {
            try
            {
                System.Threading.Interlocked.Increment(ref _httpPacketCount);

                // 提取HTTP头信息
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    var firstLine = lines[0];

                    // 提取Host
                    var hostMatch = Regex.Match(text, @"Host:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
                    var host = hostMatch.Success ? hostMatch.Groups[1].Value.Trim() : "unknown";

                    // 快手API特征检测
                    if (text.Contains("kuaishou") || text.Contains("yximgs") || text.Contains("gifshow") || text.Contains("ksapisrv"))
                    {
                        System.Threading.Interlocked.Increment(ref _kuaishouHttpCount);

                        // 检测是否包含JSON
                        bool hasJson = TryFormatJson(text, out string jsonData);

                        // 只记录包含JSON或关键信息的请求
                        bool isImportant = hasJson ||
                                          text.Contains("rtmp") ||
                                          text.Contains("push") ||
                                          text.Contains("stream") ||
                                          text.Contains("live") ||
                                          text.Contains("/rest/");

                        if (isImportant)
                        {
                            _logger("");
                            _logger($"🎯 [Hook] 快手HTTP流量 (#{_kuaishouHttpCount})");
                            _logger($"   方向: {(isSend ? "发送 ↑" : "接收 ↓")}");
                            _logger($"   Host: {host}");
                            _logger($"   请求: {firstLine}");

                            // 如果是响应，提取状态码
                            if (text.StartsWith("HTTP/"))
                            {
                                var statusMatch = Regex.Match(firstLine, @"HTTP/[\d.]+ (\d+)");
                                if (statusMatch.Success)
                                {
                                    _logger($"   状态: {statusMatch.Groups[1].Value}");
                                }
                            }

                            // 优先显示JSON数据
                            if (hasJson)
                            {
                                _logger($"   JSON数据:");
                                _logger($"   {jsonData}");
                            }
                            else
                            {
                                _logger($"   数据: {GetPreview(text, 300)}");
                            }

                            // 查找推流URL
                            ExtractKuaishouPushUrl(text);
                        }
                    }

                    // 通用推流URL检测
                    if (text.Contains("rtmp://") || text.Contains("rtmps://"))
                    {
                        _logger($"🔍 [Hook] 发现RTMP URL (方向: {(isSend ? "发送" : "接收")}, Host: {host})");
                        ExtractRtmpUrl(text);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 分析RTMP数据
        /// </summary>
        private void AnalyzeRtmpData(byte[] data, string text, bool isSend)
        {
            try
            {
                System.Threading.Interlocked.Increment(ref _rtmpPacketCount);

                // RTMP connect/publish命令
                if (text.Contains("connect") || text.Contains("publish"))
                {
                    _logger("");
                    _logger($"📡 [Hook] RTMP命令 (第{_rtmpPacketCount}个)");
                    _logger($"   方向: {(isSend ? "发送 ↑" : "接收 ↓")}");
                    _logger($"   命令: {(text.Contains("connect") ? "connect" : "publish")}");
                    _logger($"   数据长度: {data.Length} 字节");
                    _logger($"   数据片段: {GetPreview(text, 200)}");

                    // 提取RTMP URL
                    ExtractRtmpUrl(text);
                }
            }
            catch { }
        }

        /// <summary>
        /// 提取快手推流URL
        /// </summary>
        private void ExtractKuaishouPushUrl(string text)
        {
            try
            {
                // 匹配 pushRtmpUrl 或 rtmpPushUrl
                var patterns = new[]
                {
                    @"""pushRtmpUrl""\s*:\s*""([^""]+)""",
                    @"""rtmpPushUrl""\s*:\s*""([^""]+)""",
                    @"""push_url""\s*:\s*""([^""]+)""",
                    @"rtmp://[^""'\s]+"
                };

                _logger($"   🔍 尝试提取推流URL (共{patterns.Length}个正则模式)...");

                foreach (var pattern in patterns)
                {
                    var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        var url = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;

                        _logger($"   ✓ 正则匹配成功: {pattern}");
                        _logger($"   原始URL: {url}");

                        // 解码URL
                        url = System.Net.WebUtility.HtmlDecode(url);
                        url = url.Replace("\\/", "/");

                        _logger($"   解码后URL: {url}");

                        if (url.StartsWith("rtmp://") || url.StartsWith("rtmps://"))
                        {
                            _logger("");
                            _logger("🎉🎉🎉 ==========================================");
                            _logger("🎉🎉🎉 [Hook] 成功发现快手推流地址！");
                            _logger("🎉🎉🎉 ==========================================");
                            _logger($"   完整URL: {url}");
                            _logger($"   URL长度: {url.Length} 字符");
                            _logger("");

                            // 解析URL和StreamKey
                            ParseRtmpUrl(url, "快手");
                            return;
                        }
                        else
                        {
                            _logger($"   ⚠️ URL格式不符合RTMP协议，忽略");
                        }
                    }
                }

                _logger($"   ✗ 未找到匹配的推流URL");
            }
            catch (Exception ex)
            {
                _logger($"   ❌ 提取快手URL异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 提取通用RTMP URL
        /// </summary>
        private void ExtractRtmpUrl(string text)
        {
            try
            {
                var match = Regex.Match(text, @"rtmps?://[^\s""'<>]+", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var url = match.Value;
                    _logger($"[Hook] 发现RTMP地址: {url}");

                    // 判断平台
                    string platform = "未知平台";
                    if (url.Contains("douyin") || url.Contains("douyincdn"))
                        platform = "抖音";
                    else if (url.Contains("kuaishou") || url.Contains("yximgs"))
                        platform = "快手";
                    else if (url.Contains("bilivideo") || url.Contains("bili"))
                        platform = "B站";
                    else if (url.Contains("xiaohongshu") || url.Contains("xhscdn"))
                        platform = "小红书";

                    ParseRtmpUrl(url, platform);
                }
            }
            catch { }
        }

        /// <summary>
        /// 解析RTMP URL，分离服务器地址和StreamKey
        /// </summary>
        private void ParseRtmpUrl(string fullUrl, string platform)
        {
            try
            {
                // RTMP URL格式: rtmp://server:port/app/streamkey?params
                var uri = new Uri(fullUrl);
                var pathParts = uri.AbsolutePath.TrimStart('/').Split('/');

                string serverUrl = $"{uri.Scheme}://{uri.Host}";
                if (uri.Port > 0 && uri.Port != 1935)
                    serverUrl += $":{uri.Port}";

                // 应用名称（通常是第一段路径）
                if (pathParts.Length > 0)
                    serverUrl += $"/{pathParts[0]}";

                // StreamKey（剩余路径 + 查询参数）
                string streamKey = "";
                if (pathParts.Length > 1)
                {
                    streamKey = string.Join("/", pathParts, 1, pathParts.Length - 1);
                }
                if (!string.IsNullOrEmpty(uri.Query))
                {
                    streamKey += uri.Query;
                }

                _onStreamFound?.Invoke(platform, serverUrl, streamKey);
            }
            catch (Exception ex)
            {
                _logger($"[Hook] 解析RTMP URL异常: {ex.Message}");
                // 如果解析失败，直接使用完整URL
                _onStreamFound?.Invoke(platform, fullUrl, "");
            }
        }

        /// <summary>
        /// 获取文本预览
        /// </summary>
        private string GetPreview(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            // 替换不可见字符
            text = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "");

            if (text.Length > maxLength)
                return text.Substring(0, maxLength) + "...";

            return text;
        }
    }
}
