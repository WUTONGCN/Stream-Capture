using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace StreamCapture.Core
{
    /// <summary>
    /// HTTPS代理服务，用于解密快手等平台的加密API流量
    /// </summary>
    public class HttpsProxyService : IDisposable
    {
        private ProxyServer? _proxyServer;
        private ExplicitProxyEndPoint? _explicitEndPoint;
        private bool _isRunning;
        private readonly Action<string> _logger;
        private readonly Action<string> _onPushUrlFound;

        public HttpsProxyService(Action<string> logger, Action<string> onPushUrlFound)
        {
            _logger = logger;
            _onPushUrlFound = onPushUrlFound;
        }

        /// <summary>
        /// 启动代理服务
        /// </summary>
        public bool Start()
        {
            try
            {
                if (_isRunning) return true;

                _logger("========== 启动HTTPS代理服务 ==========");

                _proxyServer = new ProxyServer();

                // 启用HTTPS解密 - 使用BouncyCastle引擎（更稳定）
                _proxyServer.CertificateManager.CertificateEngine = Titanium.Web.Proxy.Network.CertificateEngine.BouncyCastle;

                // 生成并信任根证书
                try
                {
                    _proxyServer.CertificateManager.EnsureRootCertificate();
                    _proxyServer.CertificateManager.TrustRootCertificate(true);
                    Log("✓ 证书生成并安装成功");
                }
                catch (Exception certEx)
                {
                    Log($"⚠ 证书安装警告: {certEx.Message}");
                    Log("  继续尝试启动代理...");
                }

                // 创建代理端点
                _explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 8888, true);

                // 注册请求处理
                _proxyServer.BeforeRequest += OnRequest;
                _proxyServer.BeforeResponse += OnResponse;
                _proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;

                // 启动代理服务器
                _proxyServer.AddEndPoint(_explicitEndPoint);
                _proxyServer.Start();

                // 设置系统代理
                _proxyServer.SetAsSystemHttpProxy(_explicitEndPoint);
                _proxyServer.SetAsSystemHttpsProxy(_explicitEndPoint);

                _isRunning = true;
                Log($"✅ HTTPS代理已启动: http://127.0.0.1:8888");
                Log($"✅ 系统代理已配置");
                Log($"💡 现在可以打开快手直播伴侣进行推流");
                Log("=========================================");

                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ 代理启动失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 停止代理服务
        /// </summary>
        public void Stop()
        {
            if (!_isRunning || _proxyServer == null) return;

            try
            {
                Log("正在停止HTTPS代理服务...");

                // 移除系统代理
                _proxyServer.DisableSystemHttpProxy();
                _proxyServer.DisableSystemHttpsProxy();

                // 停止代理服务器
                _proxyServer.Stop();
                _isRunning = false;

                Log("✅ HTTPS代理已停止");
                Log("✅ 系统代理已恢复");
            }
            catch (Exception ex)
            {
                Log($"⚠ 停止代理时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理请求
        /// </summary>
        // 需要放行的URL列表（不拦截，直接转发）
        private static readonly string[] _bypassUrls = new[]
        {
            "drlives.jd.com/live-image/uploadImage",
            "drlives.jd.com/live/tab-list",
        };

        /// <summary>
        /// 检查是否需要放行（不拦截）
        /// </summary>
        private bool ShouldBypass(string host, string path)
        {
            string fullUrl = host + path;
            foreach (var bypass in _bypassUrls)
            {
                if (fullUrl.Contains(bypass, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private async Task OnRequest(object sender, SessionEventArgs e)
        {
            try
            {
                var requestUrl = e.HttpClient.Request.RequestUri.ToString();
                var host = e.HttpClient.Request.RequestUri.Host;
                var path = e.HttpClient.Request.RequestUri.PathAndQuery;

                // 放行名单：跳过日志记录和响应分析
                if (ShouldBypass(host, path)) return;

                // 🔥 全量记录所有请求（不限制域名）
                Log($"[代理请求] {e.HttpClient.Request.Method} {host}{path}");
            }
            catch (Exception ex)
            {
                Log($"[代理] OnRequest错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理响应
        /// </summary>
        private async Task OnResponse(object sender, SessionEventArgs e)
        {
            try
            {
                var requestUrl = e.HttpClient.Request.RequestUri.ToString();
                var host = e.HttpClient.Request.RequestUri.Host;
                var path = e.HttpClient.Request.RequestUri.PathAndQuery;

                // 放行名单：不读取响应体，直接跳过
                if (ShouldBypass(host, path)) return;

                // 检查是否是京东相关域名（放宽匹配：所有 jd 相关域名）
                bool isJDHost = host.Contains("jd.com", StringComparison.OrdinalIgnoreCase) ||
                               host.Contains("jdcloud.com", StringComparison.OrdinalIgnoreCase) ||
                               host.Contains("360buyimg.com", StringComparison.OrdinalIgnoreCase) ||
                               host.Contains("jd.co", StringComparison.OrdinalIgnoreCase) ||
                               host.Contains("jdlive", StringComparison.OrdinalIgnoreCase) ||
                               host.Contains("jcloud", StringComparison.OrdinalIgnoreCase);

                // 检查是否是京东直播相关 API 路径（关键路径重点监控）
                bool isJDLiveApi = isJDHost && (
                    path.Contains("live", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("push", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("rtmp", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("broadcast", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("publish", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("anchor", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("studio", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("obs", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("config", StringComparison.OrdinalIgnoreCase));

                // 记录请求（京东域名全部记录，其他只记录关键的）
                if (isJDHost)
                {
                    Log($"[代理响应] {e.HttpClient.Request.Method} {host}{path} → {e.HttpClient.Response.StatusCode}");
                }

                // 读取响应体（如果有）
                if (e.HttpClient.Response.HasBody)
                {
                    try
                    {
                        string responseBody = await e.GetResponseBodyAsString();
                        int bodyLen = responseBody.Length;

                        // 只记录有意义的响应（大于10字节）
                        if (bodyLen > 10)
                        {
                            // ===== 京东域名：全量记录所有 JSON 响应 =====
                            if (isJDHost)
                            {
                                // 过滤掉纯静态资源（图片、JS、CSS等）
                                bool isStaticResource = path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                                                        path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
                                                        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                                        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                                        path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                                                        path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
                                                        path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) ||
                                                        path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                                                        host.StartsWith("img", StringComparison.OrdinalIgnoreCase) ||
                                                        host.Contains("storage.360buyimg", StringComparison.OrdinalIgnoreCase);

                                if (!isStaticResource)
                                {
                                    // 京东 API 响应 - 全量记录（最多 5000 字符）
                                    int logLen = Math.Min(5000, bodyLen);
                                    Log($"[京东API] 🔴 {e.HttpClient.Request.Method} {host}{path}");
                                    Log($"[京东API] 响应体({bodyLen}字节): {responseBody.Substring(0, logLen)}");
                                    if (bodyLen > logLen) Log($"[京东API]   ...(已截断，共 {bodyLen} 字节)");

                                    // 尝试提取推流地址
                                    ExtractPushUrl(responseBody);
                                }
                            }

                            // ===== 京东直播 API：最高优先级 =====
                            if (isJDLiveApi)
                            {
                                Log($"[京东直播API] 🔥🔥🔥 关键API: {host}{path}");
                                Log($"[京东直播API] 完整响应({bodyLen}字节): {responseBody}");
                                ExtractPushUrl(responseBody);
                            }

                            // ===== 所有域名：检查推流关键字 =====
                            bool hasRtmpKeywords = responseBody.Contains("rtmp://", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("rtmps://", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("auth_key", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("jdcloud", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("mpush", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("pushRtmpUrl", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("push_url", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("publishUrl", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("pushUrl", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("streamPushUrl", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("liveUrl", StringComparison.OrdinalIgnoreCase) ||
                                                   responseBody.Contains("zt-push", StringComparison.OrdinalIgnoreCase);

                            if (hasRtmpKeywords)
                            {
                                if (!isJDHost) // 京东域名已记录过，避免重复
                                {
                                    Log($"[代理响应] 🔥 非京东域名包含推流关键字！{host}{path}");
                                    Log($"[代理响应] 完整内容: {responseBody.Substring(0, Math.Min(3000, bodyLen))}");
                                }
                                ExtractPushUrl(responseBody);
                            }
                        }
                    }
                    catch (Exception bodyEx)
                    {
                        Log($"[代理响应] ⚠ 读取响应体失败: {bodyEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[代理] 处理响应时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 从API响应中提取推流地址
        /// </summary>
        private void ExtractPushUrl(string jsonResponse)
        {
            try
            {
                // 方法0: 京东专用 - 优先匹配 mpush 字段（推流地址）
                var jdPushPattern = new Regex(@"""mpush""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                var jdPushMatch = jdPushPattern.Match(jsonResponse);
                if (jdPushMatch.Success)
                {
                    string fullRtmpUrl = jdPushMatch.Groups[1].Value;
                    Log($"[代理] ✅ 京东推流地址(mpush): {fullRtmpUrl}");
                    _onPushUrlFound(fullRtmpUrl);
                    return;
                }

                // 方法1: 正则提取所有可能的推流字段名（大幅扩展）
                var pushFieldPatterns = new[]
                {
                    // 标准推流字段
                    @"""pushRtmpUrl""\s*:\s*""([^""]+)""",
                    @"""rtmpUrl""\s*:\s*""([^""]+)""",
                    @"""pushUrl""\s*:\s*""([^""]+)""",
                    @"""streamUrl""\s*:\s*""([^""]+)""",
                    @"""rtmp_url""\s*:\s*""([^""]+)""",
                    @"""push_url""\s*:\s*""([^""]+)""",
                    @"""publishUrl""\s*:\s*""([^""]+)""",
                    // 京东可能的新字段名
                    @"""streamPushUrl""\s*:\s*""([^""]+)""",
                    @"""liveUrl""\s*:\s*""([^""]+)""",
                    @"""livePushUrl""\s*:\s*""([^""]+)""",
                    @"""pushAddress""\s*:\s*""([^""]+)""",
                    @"""push_address""\s*:\s*""([^""]+)""",
                    @"""pushAddr""\s*:\s*""([^""]+)""",
                    @"""rtmpPushUrl""\s*:\s*""([^""]+)""",
                    @"""pushStreamUrl""\s*:\s*""([^""]+)""",
                    @"""push_stream_url""\s*:\s*""([^""]+)""",
                    @"""streamAddress""\s*:\s*""([^""]+)""",
                    @"""stream_address""\s*:\s*""([^""]+)""",
                    @"""serverUrl""\s*:\s*""([^""]+)""",
                    @"""server_url""\s*:\s*""([^""]+)""",
                    @"""ingestUrl""\s*:\s*""([^""]+)""",
                    @"""ingest_url""\s*:\s*""([^""]+)""",
                    @"""uploadUrl""\s*:\s*""([^""]+)""",
                    @"""flvUrl""\s*:\s*""([^""]+)""",
                    @"""hlsUrl""\s*:\s*""([^""]+)""",
                    @"""webrtcUrl""\s*:\s*""([^""]+)""",
                    @"""srtUrl""\s*:\s*""([^""]+)""",
                    // 宽松匹配任何包含 push/stream 且值像 URL 的字段
                    @"""[^""]*[Pp]ush[^""]*""\s*:\s*""(rtmps?://[^""]+)""",
                    @"""[^""]*[Ss]tream[^""]*""\s*:\s*""(rtmps?://[^""]+)""",
                    @"""[^""]*[Ll]ive[^""]*""\s*:\s*""(rtmps?://[^""]+)""",
                };

                foreach (var pattern in pushFieldPatterns)
                {
                    var match = Regex.Match(jsonResponse, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string fullRtmpUrl = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                        // 放宽条件：不只是 rtmp 开头，任何 URL 都记录
                        Log($"[代理] ✅ 找到推流地址(字段匹配): {fullRtmpUrl}");
                        if (fullRtmpUrl.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase) ||
                            fullRtmpUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                            fullRtmpUrl.StartsWith("srt", StringComparison.OrdinalIgnoreCase) ||
                            fullRtmpUrl.StartsWith("webrtc", StringComparison.OrdinalIgnoreCase))
                        {
                            _onPushUrlFound(fullRtmpUrl);
                            return;
                        }
                    }
                }

                // 方法2: 直接搜索所有 rtmp:// / rtmps:// URL
                var rtmpMatches = Regex.Matches(jsonResponse, @"(rtmps?://[^\s""'<>\x00-\x1F\\]+)", RegexOptions.IgnoreCase);
                if (rtmpMatches.Count > 0)
                {
                    foreach (Match m in rtmpMatches)
                    {
                        string fullRtmpUrl = m.Groups[1].Value;
                        Log($"[代理] ✅ 直接匹配到RTMP地址: {fullRtmpUrl}");
                        _onPushUrlFound(fullRtmpUrl);
                    }
                    return;
                }

                // 方法3: 搜索 srt:// / webrtc:// 等新协议
                var altProtocolMatch = Regex.Match(jsonResponse, @"((?:srt|webrtc|whip|whep)://[^\s""'<>\x00-\x1F\\]+)", RegexOptions.IgnoreCase);
                if (altProtocolMatch.Success)
                {
                    string fullUrl = altProtocolMatch.Groups[1].Value;
                    Log($"[代理] ✅ 匹配到新协议推流地址: {fullUrl}");
                    _onPushUrlFound(fullUrl);
                    return;
                }

                // 不再打印"未找到"日志（太多噪音），只在京东域名时打印
            }
            catch (Exception ex)
            {
                Log($"[代理] 提取推流地址失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 证书验证回调
        /// </summary>
        private Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            // Reject invalid upstream certificates.
            if (e.SslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                e.IsValid = true;
            else
                e.IsValid = false; // Preserve upstream TLS certificate validation

            return Task.CompletedTask;
        }

        private void Log(string message)
        {
            _logger($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        public void Dispose()
        {
            Stop();
            _proxyServer?.Dispose();
        }
    }
}
