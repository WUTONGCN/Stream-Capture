using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using SharpPcap;
using PacketDotNet;
using StreamCapture.Models;

namespace StreamCapture.Core
{
    /// <summary>
    /// 数据包捕获核心类
    /// </summary>
    public class PacketCapture : IDisposable
    {
        private ILiveDevice? _device;
        private readonly List<ILiveDevice> _devices = new List<ILiveDevice>(); // 多网卡支持
        private readonly List<PlatformConfig> _platforms;
        private readonly HashSet<int> _targetPorts;
        private bool _isCapturing = false;
        private readonly bool _captureAllTraffic = false; // 环境变量 SC_CAPTURE_ALL=1 时启用
        private bool _debugFullTraffic = false; // 调试模式：捕获所有流量

        // TCP stream reassembly buffers (per 5-tuple flow key)
        private readonly Dictionary<string, ReassemblyState> _flowBuffers = new();
        private int _packetCounter = 0;
        private const int MaxBufferBytes = 512 * 1024;     // 512KB cap per flow
        private const int TrimToBytes   = 128 * 1024;      // trim to last 128KB when exceeding cap
        private const int ReasmWindow   = 64 * 1024;       // analyze last 64KB window
        private static readonly TimeSpan FlowIdleTimeout = TimeSpan.FromSeconds(45);

        public event EventHandler<string>? OnLog;
        public event EventHandler<CaptureResult>? OnResultFound;
        private readonly Dictionary<string, string> _lastKeyByPlatform = new();
        private readonly Dictionary<string, string> _tcUrlByFlow = new(); // 存储每个TCP流的tcUrl (来自connect命令)

        public PacketCapture(List<PlatformConfig> platforms, bool debugFullTraffic = false)
        {
            _platforms = platforms.Where(p => p.Enabled).ToList();
            _targetPorts = new HashSet<int>();
            _captureAllTraffic = string.Equals(Environment.GetEnvironmentVariable("SC_CAPTURE_ALL"), "1", StringComparison.OrdinalIgnoreCase);
            _debugFullTraffic = debugFullTraffic;

            // 调试模式下强制全量捕获
            if (_debugFullTraffic)
            {
                _captureAllTraffic = true;
                Log("========== 🔥 全流量调试模式已启用 🔥 ==========");
                Log("将捕获所有TCP/UDP流量，不限制端口和平台");
                Log("=================================================");
            }

            // 收集所有平台的端口
            foreach (var platform in _platforms)
            {
                foreach (var port in platform.Ports)
                {
                    _targetPorts.Add(port);
                }
            }

            // 初始化日志
            Log($"========== PacketCapture 初始化 ==========");
            Log($"已加载 {_platforms.Count} 个启用的平台配置:");
            foreach (var p in _platforms)
            {
                Log($"  - [{p.Id}] {p.Name}");
                Log($"    端口: {string.Join(", ", p.Ports)}");
                Log($"    域名模式数: {p.DomainPatterns.Count}");
                Log($"    关键字数: {p.KeywordPatterns.Count}");
            }
            Log($"监听端口列表: {string.Join(", ", _targetPorts.OrderBy(p => p))}");
            Log($"全量捕获模式: {(_captureAllTraffic ? "启用" : "禁用")}");
            Log($"=========================================");
        }

        /// <summary>
        /// 获取所有网络设备
        /// </summary>
        public List<string> GetDevices()
        {
            var devices = CaptureDeviceList.Instance;
            return devices.Select((d, i) => $"{i}: {d.Description ?? d.Name}").ToList();
        }

        /// <summary>
        /// 开始捕获（单个网卡）
        /// </summary>
        public void StartCapture(int deviceIndex)
        {
            if (_isCapturing)
            {
                Log("已经在捕获中...");
                return;
            }

            var devices = CaptureDeviceList.Instance;
            if (deviceIndex < 0 || deviceIndex >= devices.Count)
            {
                Log($"无效的设备索引: {deviceIndex}");
                return;
            }

            _device = devices[deviceIndex];
            _device.OnPacketArrival += OnPacketArrival;

            // 设置BPF过滤器
            _device.Open(DeviceModes.Promiscuous, 1000);
            if (_captureAllTraffic)
            {
                // 全量捕获：不设置过滤器（抓取全部数据包）
                Log($"开始捕获设备: {_device.Description ?? _device.Name}");
                Log($"过滤器: <无>（全量捕获，SC_CAPTURE_ALL=1）");
            }
            else
            {
                // TCP限制端口，UDP不限制（B站RTMPSRT使用动态UDP端口）
                var portFilter = string.Join(" or ", _targetPorts.Select(p => $"port {p}"));
                var filter = $"(tcp and ({portFilter})) or udp";
                _device.Filter = filter;
                Log($"开始捕获设备: {_device.Description ?? _device.Name}");
                Log($"过滤器: {filter}");
            }

            _device.StartCapture();
            _isCapturing = true;
        }

        /// <summary>
        /// 开始捕获（所有网卡）
        /// </summary>
        public void StartCaptureOnAllDevices()
        {
            if (_isCapturing)
            {
                Log("已经在捕获中...");
                return;
            }

            var devices = CaptureDeviceList.Instance;
            int successCount = 0;
            int skippedCount = 0;

            Log("========================================");
            Log("🌐 开始监听所有网卡...");
            Log("========================================");

            foreach (var device in devices)
            {
                try
                {
                    // 检查是否为环回接口
                    var description = device.Description ?? device.Name;
                    bool isLoopback = description.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                                      description.Contains("回环", StringComparison.OrdinalIgnoreCase);

                    // 检查是否只监听快手平台（快手可能使用本地通信）
                    bool isKuaishouOnly = _platforms.Count == 1 &&
                                          _platforms.Any(p => p.Id?.Equals("kuaishou", StringComparison.OrdinalIgnoreCase) == true);

                    // 如果只监听快手，保留Loopback设备；否则跳过
                    if (isLoopback && !isKuaishouOnly)
                    {
                        Log($"⊗ 跳过环回设备: {description}");
                        skippedCount++;
                        continue;
                    }

                    if (isLoopback && isKuaishouOnly)
                    {
                        Log($"🔄 启用环回设备（捕获本地通信）: {description}");
                    }

                    device.OnPacketArrival += OnPacketArrival;
                    device.Open(DeviceModes.Promiscuous, 1000);

                    // 设置BPF过滤器
                    if (_captureAllTraffic)
                    {
                        Log($"✓ 已启动: {description}");
                        Log($"  过滤器: <无>（全量捕获）");
                    }
                    else
                    {
                        var portFilter = string.Join(" or ", _targetPorts.Select(p => $"port {p}"));
                        var filter = $"(tcp and ({portFilter})) or udp";
                        device.Filter = filter;
                        Log($"✓ 已启动: {description}");
                        Log($"  过滤器: {filter}");
                    }

                    device.StartCapture();
                    _devices.Add(device);
                    successCount++;
                }
                catch (Exception ex)
                {
                    var description = device.Description ?? device.Name;
                    Log($"⚠ 无法启动 {description}: {ex.Message}");
                    skippedCount++;
                }
            }

            if (successCount == 0)
            {
                Log("❌ 错误: 没有可用的网卡！");
                throw new InvalidOperationException("没有可用的网卡，请检查网络连接或以管理员身份运行");
            }

            _isCapturing = true;
            Log("========================================");
            Log($"🎯 监听状态: 成功 {successCount} 个，跳过 {skippedCount} 个");
            Log("========================================");
        }

        /// <summary>
        /// 停止捕获
        /// </summary>
        public void StopCapture()
        {
            if (!_isCapturing)
                return;

            // 停止单个设备
            if (_device != null)
            {
                try
                {
                    _device.StopCapture();
                    _device.Close();
                }
                catch (Exception ex)
                {
                    Log($"停止设备时出错: {ex.Message}");
                }
            }

            // 停止所有设备
            foreach (var device in _devices)
            {
                try
                {
                    device.StopCapture();
                    device.Close();
                }
                catch (Exception ex)
                {
                    Log($"停止设备时出错: {ex.Message}");
                }
            }

            _devices.Clear();
            _isCapturing = false;
            Log("已停止捕获");
        }

        /// <summary>
        /// 数据包到达事件处理
        /// </summary>
        private void OnPacketArrival(object sender, SharpPcap.PacketCapture e)
        {
            try
            {
                var rawPacket = e.GetPacket();
                var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);

                var tcpPacket = packet.Extract<TcpPacket>();
                var udpPacket = packet.Extract<PacketDotNet.UdpPacket>();
                if (tcpPacket == null && udpPacket == null)
                    return;

                // UDP 处理（B站RTMPSRT可能使用UDP）
                if (udpPacket != null)
                {
                    var ipPacket = packet.Extract<IPPacket>();
                    var udpPayload = udpPacket.PayloadData ?? Array.Empty<byte>();
                    if (udpPayload.Length > 0)
                    {
                        var udpAscii = Encoding.Latin1.GetString(udpPayload);

                        // 全量模式下记录所有UDP包
                        if (_captureAllTraffic)
                        {
                            Log("");
                            Log($"==================== [ALL-UDP] 数据包 ====================");
                            Log($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                            Log($"端口: {udpPacket.SourcePort} -> {udpPacket.DestinationPort}");
                            Log($"长度: {udpAscii.Length} 字节");
                            var clean = System.Text.RegularExpressions.Regex.Replace(udpAscii, @"[\x00-\x08\x0B-\x0C\x0E-\x1F]", ".");
                            Log($"--- ASCII内容 (前800字符) ---");
                            Log(clean.Substring(0, Math.Min(800, clean.Length)));
                            Log($"================================================================");
                            Log("");
                        }

                        // UDP 平台匹配与 B 站专用 publish 解析
                        foreach (var platform in _platforms)
                        {
                            bool hasKeyword = platform.KeywordPatterns.Any(keyword =>
                                udpAscii.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                            bool hasDomain = platform.DomainPatterns.Any(domainPattern =>
                            {
                                var regex = new System.Text.RegularExpressions.Regex(domainPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                return regex.IsMatch(udpAscii);
                            });

                            // B站兜底：IP + rtmpsrt + live-bvc 也视作命中
                            if (!hasDomain && platform.Id.Equals("bilibili", StringComparison.OrdinalIgnoreCase))
                            {
                                if (udpAscii.Contains("live-bvc", StringComparison.OrdinalIgnoreCase)
                                    || udpAscii.Contains("rtmpsrt://", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasDomain = true;
                                }
                            }

                            // 🔥 快手UDP全量捕获：不过滤任何快手相关UDP包
                            bool isKuaishouUdp = platform.Id.Equals("kuaishou", StringComparison.OrdinalIgnoreCase);

                            // 快手平台：记录所有UDP包（即使不匹配关键字）
                            if (isKuaishouUdp)
                            {
                                Log("");
                                Log($"==================== [快手 UDP] 数据包 ====================");
                                Log($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                                Log($"端口: {udpPacket.SourcePort} -> {udpPacket.DestinationPort}");
                                Log($"长度: {udpPayload.Length} 字节");
                                Log($"关键字匹配={hasKeyword}, 域名匹配={hasDomain}");

                                // 显示清理后的ASCII内容
                                var cleanUdp = System.Text.RegularExpressions.Regex.Replace(udpAscii, @"[\x00-\x08\x0B-\x0C\x0E-\x1F]", ".");
                                Log($"--- ASCII内容 (前500字符) ---");
                                Log(cleanUdp.Substring(0, Math.Min(500, cleanUdp.Length)));

                                // 显示十六进制（前200字节）
                                if (udpPayload.Length > 0)
                                {
                                    Log($"--- HEX内容 (前200字节) ---");
                                    int hexLen = Math.Min(200, udpPayload.Length);
                                    for (int i = 0; i < hexLen; i += 32)
                                    {
                                        int lineLen = Math.Min(32, hexLen - i);
                                        var hexLine = string.Join(" ", udpPayload.Skip(i).Take(lineLen).Select(b => b.ToString("X2")));
                                        var asciiLine = new string(udpPayload.Skip(i).Take(lineLen).Select(b => (b >= 32 && b <= 126) ? (char)b : '.').ToArray());
                                        Log($"{i:X4}: {hexLine,-95} | {asciiLine}");
                                    }
                                }
                                Log($"================================================================");
                                Log("");
                            }

                            if (!isKuaishouUdp && !hasKeyword && !hasDomain)
                                continue;

                            // 在 UDP 负载包含 publish/FCPublish/streamname/releaseStream 时尝试专用解析（不区分大小写）
                            if (platform.Id.Equals("bilibili", StringComparison.OrdinalIgnoreCase)
                                && (
                                    udpAscii.IndexOf("publish", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    udpAscii.IndexOf("FCPublish", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    udpAscii.IndexOf("streamname=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    udpAscii.IndexOf("releaseStream", StringComparison.OrdinalIgnoreCase) >= 0
                                ))
                            {
                                // 获取UDP目标IP地址
                                string destIp = ipPacket?.DestinationAddress?.ToString() ?? "";
                                int destPort = udpPacket.DestinationPort;

                                var result = ExtractRtmpPublish_Bilibili(udpAscii, udpPayload, platform, destIp, destPort);
                                if (result != null)
                                {
                                    OnResultFound?.Invoke(this, result);
                                    Log($"✅ [{platform.Name}] 推流地址: {result.Url}");
                                    Log($"✅ [{platform.Name}] 流密钥: {result.StreamKey}");
                                }
                            }
                        }
                    }
                }

                if (tcpPacket == null || tcpPacket.PayloadData == null || tcpPacket.PayloadData.Length == 0)
                    return;

                // 端口限制（全量捕获时不限制）
                if (!_captureAllTraffic)
                {
                    if (!_targetPorts.Contains(tcpPacket.SourcePort) && !_targetPorts.Contains(tcpPacket.DestinationPort))
                        return;
                }

                // 1) Get flow key first for both per-packet and reassembly analysis
                var key = GetFlowKey(packet, tcpPacket);

                // 2) Per-packet analysis/log for visibility (binary-safe via Latin1)
                var payload = Encoding.Latin1.GetString(tcpPacket.PayloadData);

                // 全流量调试模式：输出所有HTTP/HTTPS流量
                if (_debugFullTraffic)
                {
                    var ipPacket = packet.Extract<PacketDotNet.IPPacket>();
                    if (ipPacket != null && tcpPacket.PayloadData.Length > 10)
                    {
                        int srcPort = tcpPacket.SourcePort;
                        int dstPort = tcpPacket.DestinationPort;

                        // 记录HTTP/HTTPS或包含推流关键字的流量
                        if (srcPort == 80 || dstPort == 80 || srcPort == 443 || dstPort == 443 ||
                            srcPort == 8080 || dstPort == 8080 || srcPort == 1935 || dstPort == 1935)
                        {
                            bool hasInterest = payload.Contains("HTTP/") || payload.Contains("GET ") || payload.Contains("POST ") ||
                                               payload.Contains("rtmp", StringComparison.OrdinalIgnoreCase) ||
                                               payload.Contains("push", StringComparison.OrdinalIgnoreCase) ||
                                               payload.Contains("live", StringComparison.OrdinalIgnoreCase) ||
                                               payload.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
                                               payload.Contains("kuaishou", StringComparison.OrdinalIgnoreCase) ||
                                               payload.Contains("gifshow", StringComparison.OrdinalIgnoreCase) ||
                                               payload.Contains("yximgs", StringComparison.OrdinalIgnoreCase);

                            if (hasInterest)
                            {
                                Log("");
                                Log($"==================== [🔥全流量] ====================");
                                Log($"时间: {DateTime.Now:HH:mm:ss.fff}");
                                Log($"流向: {ipPacket.SourceAddress}:{srcPort} → {ipPacket.DestinationAddress}:{dstPort}");
                                Log($"长度: {tcpPacket.PayloadData.Length} 字节");

                                // 显示内容摘要
                                var cleanPayload = System.Text.RegularExpressions.Regex.Replace(payload, @"[\x00-\x08\x0B-\x0C\x0E-\x1F]", ".");
                                int displayLen = Math.Min(500, cleanPayload.Length);
                                Log($"内容摘要: {cleanPayload.Substring(0, displayLen)}");
                                if (cleanPayload.Length > 500) Log("  ...(已截断)");
                                Log($"====================================================");
                                Log("");
                            }
                        }
                    }
                }

                AnalyzePayload(payload, tcpPacket, tcpPacket.PayloadData, key);

                // 3) TCP stream reassembly to catch cross-packet RTMP publish
                if (!string.IsNullOrEmpty(key))
                {
                    var state = AppendToFlow(key, tcpPacket.PayloadData);
                    // Periodic cleanup
                    _packetCounter++;
                    if ((_packetCounter % 200) == 0)
                    {
                        CleanupOldFlows();
                    }

                    // Analyze last window of the reassembled stream
                    AnalyzeReassembledWindow(key, state, tcpPacket);
                }
            }
            catch (Exception ex)
            {
                // 静默处理，避免日志过多
            }
        }

        // ----- TCP Reassembly helpers -----
        private sealed class ReassemblyState
        {
            public List<byte> Buffer = new List<byte>(65536);
            public DateTime LastSeen = DateTime.Now;
        }

        private string GetFlowKey(Packet rootPacket, TcpPacket tcp)
        {
            var ip4 = rootPacket.Extract<PacketDotNet.IPv4Packet>();
            if (ip4 != null)
            {
                return $"{ip4.SourceAddress}:{tcp.SourcePort}->{ip4.DestinationAddress}:{tcp.DestinationPort}";
            }
            var ip6 = rootPacket.Extract<PacketDotNet.IPv6Packet>();
            if (ip6 != null)
            {
                return $"{ip6.SourceAddress}:{tcp.SourcePort}->{ip6.DestinationAddress}:{tcp.DestinationPort}";
            }
            return string.Empty;
        }

        private ReassemblyState AppendToFlow(string key, byte[] data)
        {
            if (!_flowBuffers.TryGetValue(key, out var state))
            {
                state = new ReassemblyState();
                _flowBuffers[key] = state;
            }
            // append
            state.Buffer.AddRange(data);
            state.LastSeen = DateTime.Now;
            // cap and trim
            if (state.Buffer.Count > MaxBufferBytes)
            {
                int remove = state.Buffer.Count - TrimToBytes;
                state.Buffer.RemoveRange(0, remove);
            }
            return state;
        }

        private void CleanupOldFlows()
        {
            var now = DateTime.Now;
            var keys = _flowBuffers.Keys.ToList();
            foreach (var k in keys)
            {
                if ((now - _flowBuffers[k].LastSeen) > FlowIdleTimeout)
                {
                    _flowBuffers.Remove(k);
                }
            }
        }

        private void AnalyzeReassembledWindow(string key, ReassemblyState state, TcpPacket tcpPacket)
        {
            if (state.Buffer.Count == 0)
                return;

            int window = Math.Min(ReasmWindow, state.Buffer.Count);
            var span = state.Buffer.Count == window ? state.Buffer : state.Buffer.GetRange(state.Buffer.Count - window, window);
            var windowBytes = span.ToArray();
            var windowPayload = Encoding.Latin1.GetString(windowBytes);

            // Minimal guard: only continue if publish/connect hints or platform keywords exist in window
            if (!(windowPayload.Contains("publish") || windowPayload.Contains("connect") || windowPayload.Contains("tcUrl") || windowPayload.Contains("rtmp://")))
                return;

            Log($"[Reassembly] Flow={key} bytes={state.Buffer.Count}, window={window}B");
            AnalyzePayload(windowPayload, tcpPacket, windowBytes, key);
        }

        /// <summary>
        /// 分析payload数据
        /// </summary>
        private void AnalyzePayload(string payload, TcpPacket tcpPacket, byte[] rawBytes, string flowKey = "")
        {
            // 全量模式下，先记录所有TCP数据包的通用日志
            if (_captureAllTraffic)
            {
                Log("");
                Log($"==================== [ALL] 数据包 ====================");
                Log($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                Log($"端口: {tcpPacket.SourcePort} -> {tcpPacket.DestinationPort}");
                Log($"长度: {payload.Length} 字节");
                var cleanAll = System.Text.RegularExpressions.Regex.Replace(payload, @"[\x00-\x08\x0B-\x0C\x0E-\x1F]", ".");
                Log($"--- ASCII内容 (前800字符) ---");
                Log(cleanAll.Substring(0, Math.Min(800, cleanAll.Length)));
                Log($"================================================================");
                Log("");
            }

            foreach (var platform in _platforms)
            {
                // 检查关键字或域名
                bool hasKeyword = platform.KeywordPatterns.Any(keyword =>
                    payload.Contains(keyword, StringComparison.OrdinalIgnoreCase));

                bool hasDomain = platform.DomainPatterns.Any(domainPattern =>
                {
                    var regex = new System.Text.RegularExpressions.Regex(domainPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    return regex.IsMatch(payload);
                });

                // B站兜底：直播姬常见形态为 IP + rtmpsrt + live-bvc，即使无域名也应触发
                if (!hasDomain && platform.Id.Equals("bilibili", StringComparison.OrdinalIgnoreCase))
                {
                    if (payload.Contains("live-bvc", StringComparison.OrdinalIgnoreCase)
                        || payload.Contains("rtmpsrt://", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDomain = true; // 放宽为"视作命中平台"，以便触发专用解析
                    }
                }

                // 若检测到 RTMP 命令语义，也作为候选（避免因域名/关键字缺失而漏判）
                bool hasRtmpVerb = payload.Contains("publish", StringComparison.OrdinalIgnoreCase)
                                   || payload.Contains("FCPublish", StringComparison.OrdinalIgnoreCase)
                                   || payload.Contains("connect", StringComparison.OrdinalIgnoreCase);

                // 快手调试：特别记录快手平台的匹配尝试
                bool isKuaishou = platform.Id.Equals("kuaishou", StringComparison.OrdinalIgnoreCase);
                if (isKuaishou)
                {
                    Log($"[快手检测] 关键字匹配={hasKeyword}, 域名匹配={hasDomain}, RTMP命令={hasRtmpVerb}");
                    // 🔥 方案A：快手平台不进行过滤，记录所有流量！
                    if (!hasKeyword && !hasDomain && !hasRtmpVerb)
                    {
                        Log($"[快手检测] ⚠️  虽然未匹配关键字，但仍然记录此数据包（全量捕获模式）");
                    }
                }

                // 其他平台继续使用过滤逻辑，快手平台跳过过滤
                if (!isKuaishou && !hasKeyword && !hasDomain && !hasRtmpVerb)
                    continue;

                // ===== 简化输出（TLS加密数据不需要详细HEX dump）=====
                // 快手使用TLS加密，Hook会解密，所以这里的数据包主要是加密数据
                // 只输出摘要信息，减少日志冗余
                if (isKuaishou && tcpPacket.DestinationPort == 443)
                {
                    // HTTPS流量：简化输出
                    Log($"");
                    Log($"[{platform.Name} HTTPS] {tcpPacket.SourcePort} -> {tcpPacket.DestinationPort}, {payload.Length}字节 (TLS加密，由Hook解密)");
                }
                else
                {
                    // 非HTTPS流量或其他平台：保留详细输出
                    Log($"");
                    Log($"==================== [{platform.Name}] 数据包 ====================");
                    Log($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                    Log($"端口: {tcpPacket.SourcePort} -> {tcpPacket.DestinationPort}");
                    Log($"长度: {payload.Length} 字节");
                    Log($"匹配: 关键字={hasKeyword}, 域名={hasDomain}");

                    // 输出ASCII内容预览（清理不可见字符）
                    var cleanPayload = System.Text.RegularExpressions.Regex.Replace(payload, @"[\x00-\x08\x0B-\x0C\x0E-\x1F]", ".");
                    Log($"--- ASCII内容 (前500字符) ---");
                    Log(cleanPayload.Substring(0, Math.Min(500, cleanPayload.Length)));

                    // 只在明文或RTMP数据时输出HEX
                    if (hasKeyword || hasRtmpVerb || !isKuaishou)
                    {
                        Log($"--- HEX内容 (前200字节) ---");
                        int hexLen = Math.Min(200, payload.Length);
                        for (int i = 0; i < hexLen; i += 32)
                        {
                            int lineLen = Math.Min(32, hexLen - i);
                            var hexLine = string.Join(" ", payload.Substring(i, lineLen).Select(c => ((byte)c).ToString("X2")));
                            var asciiLine = new string(payload.Substring(i, lineLen).Select(c => (c >= 32 && c <= 126) ? c : '.').ToArray());
                            Log($"{i:X4}: {hexLine,-95} | {asciiLine}");
                        }
                    }
                }
                Log($"================================================================");
                Log($"");

                // 触发专用解析：出现 RTMP 命令语义时尝试解析，由具体平台解析器自行校验域名/特征
                if (hasRtmpVerb)
                {
                    var rtmpResult = ExtractRtmpPublish(payload, rawBytes, platform, flowKey);
                    if (rtmpResult != null)
                    {
                        OnResultFound?.Invoke(this, rtmpResult);
                        Log($"✅ [{platform.Name}] 推流地址: {rtmpResult.Url}");
                        Log($"✅ [{platform.Name}] 流密钥: {rtmpResult.StreamKey}");
                        continue;
                    }
                }

                // 禁止通用URL提取（仅保留专用解析）
                continue;
            }
        }

        /// <summary>
        /// 提取RTMP publish命令中的推流地址 - 根据平台选择不同的解析方法
        /// </summary>
        private CaptureResult? ExtractRtmpPublish(string payload, byte[] rawBytes, PlatformConfig platform, string flowKey)
        {
            try
            {
                // 检测命令提示（放宽，允许 FCPublish/streamname 等触发）
                bool isConnect = payload.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0
                                  && payload.IndexOf("tcUrl", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isPublish = payload.IndexOf("publish", StringComparison.OrdinalIgnoreCase) >= 0
                                  || payload.IndexOf("FCPublish", StringComparison.OrdinalIgnoreCase) >= 0
                                  || payload.IndexOf("releaseStream", StringComparison.OrdinalIgnoreCase) >= 0
                                  || payload.IndexOf("streamname", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isConnect && !isPublish)
                    return null;

                // 如果包含connect命令，提取并存储tcUrl（但不立即返回，可能同一包还有publish命令）
                if (isConnect && !string.IsNullOrEmpty(flowKey))
                {
                    // 精确匹配 RTMP URL，避免捕获垃圾字符
                    var tcUrlMatch = System.Text.RegularExpressions.Regex.Match(payload, @"(rtmps?://[a-z0-9\.\-]+/[a-z0-9_\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (tcUrlMatch.Success)
                    {
                        _tcUrlByFlow[flowKey] = tcUrlMatch.Groups[1].Value;
                        Log($"[Flow {flowKey}] 存储tcUrl: {tcUrlMatch.Groups[1].Value}");
                    }
                }

                // 如果只有connect没有publish，直接返回
                if (isConnect && !isPublish)
                    return null;

                Log($"========== [{platform.Name}] RTMP Publish 数据包分析 ==========");
                Log($"数据包总长度: {payload.Length} 字节");

                // 输出完整的十六进制dump（前200字节）
                int dumpLength = Math.Min(200, payload.Length);
                var hexDump = string.Join(" ", payload.Substring(0, dumpLength).Select(c => ((byte)c).ToString("X2")));
                Log($"HEX Dump (前{dumpLength}字节):");
                for (int i = 0; i < hexDump.Length; i += 96) // 每行48字节
                {
                    Log($"  {hexDump.Substring(i, Math.Min(96, hexDump.Length - i))}");
                }

                // 根据平台ID选择不同的解析方法
                CaptureResult? result = null;
                switch (platform.Id.ToLower())
                {
                    case "xiaohongshu":
                        result = ExtractRtmpPublish_XiaoHongShu(payload, rawBytes, platform, flowKey);
                        break;
                    case "douyin":
                        result = ExtractRtmpPublish_Douyin(payload, rawBytes, platform);
                        break;
                    case "bilibili":
                        result = ExtractRtmpPublish_Bilibili(payload, rawBytes, platform);
                        break;
                    case "kuaishou":
                        result = ExtractRtmpPublish_Kuaishou(payload, rawBytes, platform, flowKey);
                        break;
                    case "jd":
                        result = ExtractRtmpPublish_JD(payload, rawBytes, platform, flowKey);
                        break;
                    default:
                        result = ExtractRtmpPublish_Generic(payload, platform);
                        break;
                }

                if (result != null)
                {
                    Log($"========== ✅ 解析成功 ==========");
                    Log($"推流地址: {result.Url}");
                    Log($"流密钥: {result.StreamKey}");
                    Log($"完整地址: {result.Url}/{result.StreamKey}");
                    Log($"=====================================");
                }
                else
                {
                    Log($"========== ❌ 解析失败 ==========");
                }

                return result;
            }
            catch (Exception ex)
            {
                Log($"❌ RTMP解析异常: {ex.Message}");
                Log($"堆栈: {ex.StackTrace}");
            }

            return null;
        }


        /// <summary>
        /// 小红书专用RTMP解析（字节级严格切片）
        /// </summary>
        private CaptureResult? ExtractRtmpPublish_XiaoHongShu(string payload, byte[] rawBytes, PlatformConfig platform, string flowKey)
        {
            string baseUrl = "";
            var m = System.Text.RegularExpressions.Regex.Match(payload, @"(rtmp://[^\x00]*?xhscdn\.com/[^\x00]*?)[\x00]");
            if (m.Success)
                baseUrl = System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"[\x00-\x1F]", "");

            // 如果当前包中没有找到baseUrl，尝试从之前的connect命令中获取
            if (string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(flowKey) && _tcUrlByFlow.ContainsKey(flowKey))
            {
                baseUrl = _tcUrlByFlow[flowKey];
                Log($"[小红书] 使用之前存储的tcUrl: {baseUrl}");
            }

            string streamKey = "";
            int publishIdx = payload.IndexOf("publish", StringComparison.Ordinal);
            if (publishIdx >= 0)
            {
                int searchStart = publishIdx + 7; // "publish"
                for (int i = searchStart; i < rawBytes.Length - 3; i++)
                {
                    if (rawBytes[i] == 0x05 && rawBytes[i + 1] == 0x02)
                    {
                        int len = (rawBytes[i + 2] << 8) | rawBytes[i + 3];
                        int keyStart = i + 4;
                        if (keyStart + len <= rawBytes.Length)
                        {
                            streamKey = Encoding.UTF8.GetString(rawBytes, keyStart, len);
                        }
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(streamKey))
            {
                // 可信度校验：必须含 txSecret 参数，避免把 connect 阶段的短字段误判为密钥
                if (!streamKey.Contains("txSecret=", StringComparison.OrdinalIgnoreCase))
                {
                    Log("[小红书] 丢弃不含 txSecret 的疑似密钥");
                    return null;
                }
                streamKey = System.Text.RegularExpressions.Regex.Replace(streamKey, @"[\x00-\x1F\x7F-\x9F]", "");
                var result = new CaptureResult
                {
                    Platform = platform.Name,
                    Protocol = "RTMP",
                    Url = baseUrl,
                    StreamKey = streamKey,
                    Parameters = ExtractParameters(streamKey),
                    RawData = payload.Length > 300 ? payload.Substring(0, 300) + "..." : payload,
                    Timestamp = DateTime.Now
                };
                if (IsDuplicateAndRemember(platform.Id ?? platform.Name, result.Url, result.StreamKey))
                {
                    Log("[小红书] 去重：忽略连续重复的结果");
                    return null;
                }
                return result;
            }
            return null;
        }

        /// <summary>
        /// 抖音专用RTMP解析（AMF0）：
        /// publish 命令 AMF 序列通常为：
        ///   String("publish"), Number(txId), Null, String(streamName), String(type)
        /// 我们在 payload 中定位到 "publish" 后，向后扫描 Null(0x05) + String(0x02) 序列，
        /// 读取后续 length(2 bytes) 得到 streamKey；
        /// baseUrl 优先从同一窗口内的 tcUrl 或 rtmp(s)://...douyin... 提取。
        /// </summary>
        private CaptureResult? ExtractRtmpPublish_Douyin(string payload, byte[] rawBytes, PlatformConfig platform)
        {
            Log($"[抖音] 使用专用解析器");

            // 1) 提取 Base URL（来自 tcUrl 或直接匹配 RTMP/RTMPS 地址）
            string baseUrl = "";
            var douyinUrlRegex = new System.Text.RegularExpressions.Regex(
                @"(rtmps?://[^\x00]*?(douyin|douyincdn|douyinpic)\.com/[^\x00]*?)[\x00]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            var urlMatch = douyinUrlRegex.Match(payload);
            if (urlMatch.Success)
            {
                baseUrl = System.Text.RegularExpressions.Regex.Replace(urlMatch.Groups[1].Value, @"[\x00-\x1F]", "");
                // 清理非ASCII字符（修复Latin1解码乱码如 Â）
                baseUrl = System.Text.RegularExpressions.Regex.Replace(baseUrl, @"[^\x20-\x7E]", "");
            }
            else
            {
                // 备用：从 tcUrl 提取
                var tcMatch = System.Text.RegularExpressions.Regex.Match(
                    payload,
                    @"tcUrl[\x00-\x20]+([^\x00]+?)[\x00]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                if (tcMatch.Success)
                {
                    baseUrl = System.Text.RegularExpressions.Regex.Replace(tcMatch.Groups[1].Value, @"[\x00-\x1F]", "");
                    // 清理非ASCII字符
                    baseUrl = System.Text.RegularExpressions.Regex.Replace(baseUrl, @"[^\x20-\x7E]", "");
                }
            }

            // 校验 Base URL 域名是否确为抖音系
            bool baseUrlMatchesDouyin = false;
            if (!string.IsNullOrEmpty(baseUrl))
            {
                foreach (var pattern in platform.DomainPatterns)
                {
                    var re = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (re.IsMatch(baseUrl))
                    {
                        baseUrlMatchesDouyin = true;
                        break;
                    }
                }
            }

            // 2) AMF0 扫描以提取 streamKey（publish 之后的第一个 String 字段）
            string streamKey = string.Empty;
            int publishIdx = payload.IndexOf("publish", StringComparison.Ordinal);
            if (publishIdx >= 0)
            {
                // 在 publish 之后，使用原始字节扫描更稳健（参考小红书做法）
                // 搜索模式： 0x05 0x02 [lenHi] [lenLo] [streamKey bytes]
                int scanStart = Math.Min(publishIdx + 7, rawBytes.Length - 4);
                int scanEnd = Math.Min(rawBytes.Length - 4, scanStart + 4096);
                for (int i = scanStart; i < scanEnd; i++)
                {
                    if (rawBytes[i] == 0x05 && rawBytes[i + 1] == 0x02)
                    {
                        int len = (rawBytes[i + 2] << 8) | rawBytes[i + 3];
                        int keyStart = i + 4;
                        if (len > 0 && keyStart + len <= rawBytes.Length)
                        {
                            var candidate = Encoding.UTF8.GetString(rawBytes, keyStart, len);
                            candidate = System.Text.RegularExpressions.Regex.Replace(candidate, @"[\x00-\x1F\x7F-\x9F]", "");
                            if (candidate.Length >= 4)
                            {
                                // 清理非ASCII字符
                                streamKey = System.Text.RegularExpressions.Regex.Replace(candidate, @"[^\x20-\x7E]", "");
                                break;
                            }
                        }
                    }
                }
            }

            // 3) 正则后备：适配部分变体（publish后若存在 padding 或额外字段）
            if (string.IsNullOrEmpty(streamKey))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    payload,
                    @"publish.{6,64}\x05\x02..([\x20-\x7E]{4,200})",
                    System.Text.RegularExpressions.RegexOptions.Singleline
                );
                if (m.Success)
                {
                    // 清理非ASCII字符
                    streamKey = System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"[^\x20-\x7E]", "");
                }
            }

            // 4) 结果校验与输出
            if (string.IsNullOrEmpty(streamKey) || string.IsNullOrEmpty(baseUrl) || !baseUrlMatchesDouyin)
            {
                return null;
            }

            // 去重检查
            if (IsDuplicateAndRemember(platform.Id ?? platform.Name, baseUrl, streamKey))
            {
                Log("[抖音] 去重：忽略连续重复的结果");
                return null;
            }

            return new CaptureResult
            {
                Platform = platform.Name,
                Protocol = "RTMP",
                Url = baseUrl,
                StreamKey = streamKey,
                Parameters = ExtractParameters(streamKey),
                RawData = payload.Length > 300 ? payload.Substring(0, 300) + "..." : payload,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// 从快手API响应中提取推流地址（/rest/n/live/mate/pc/startPushMobileOrigin）
        /// </summary>
        public CaptureResult? ExtractKuaishouFromAPI(string payload)
        {
            try
            {
                // 检查是否包含快手API特征
                if (!payload.Contains("startPushMobileOrigin", StringComparison.OrdinalIgnoreCase) &&
                    !payload.Contains("pushRtmpUrl", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                Log($"[快手API] 检测到API响应特征");

                // 查找 JSON 响应体的开始位置（查找 HTTP body）
                int jsonStart = payload.IndexOf("{", StringComparison.Ordinal);
                if (jsonStart < 0)
                {
                    Log($"[快手API] ✗ 未找到JSON起始标记");
                    return null;
                }

                string jsonContent = payload.Substring(jsonStart);

                // 尝试清理到合法JSON结尾
                int lastBrace = jsonContent.LastIndexOf('}');
                if (lastBrace > 0)
                {
                    jsonContent = jsonContent.Substring(0, lastBrace + 1);
                }

                Log($"[快手API] JSON内容长度: {jsonContent.Length}");
                Log($"[快手API] JSON预览: {jsonContent.Substring(0, Math.Min(200, jsonContent.Length))}...");

                // 方法1: 正则提取 pushRtmpUrl
                var urlPattern = new System.Text.RegularExpressions.Regex(
                    @"""pushRtmpUrl""\s*:\s*""([^""]+)""",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                var urlMatch = urlPattern.Match(jsonContent);

                if (!urlMatch.Success)
                {
                    Log($"[快手API] ✗ 未找到 pushRtmpUrl 字段");
                    return null;
                }

                string fullRtmpUrl = urlMatch.Groups[1].Value;
                Log($"[快手API] ✓ 找到完整RTMP地址: {fullRtmpUrl}");

                // 解析 RTMP URL: rtmp://server/app/streamKey?params
                var rtmpUrlPattern = new System.Text.RegularExpressions.Regex(
                    @"^(rtmps?://[^/]+/[^/\?]+)/([^\?]+)(\?.*)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                var rtmpMatch = rtmpUrlPattern.Match(fullRtmpUrl);

                if (!rtmpMatch.Success)
                {
                    Log($"[快手API] ✗ RTMP URL格式解析失败");
                    return null;
                }

                string baseUrl = rtmpMatch.Groups[1].Value;
                string streamKey = rtmpMatch.Groups[2].Value;
                string queryParams = rtmpMatch.Groups.Count > 3 ? rtmpMatch.Groups[3].Value : "";

                Log($"[快手API] ✓ 服务器地址: {baseUrl}");
                Log($"[快手API] ✓ 流密钥: {streamKey}");
                Log($"[快手API] ✓ 参数: {queryParams}");

                // 去重检查
                if (IsDuplicateAndRemember("kuaishou", baseUrl, streamKey))
                {
                    Log("[快手API] 🔄 去重：忽略重复结果");
                    return null;
                }

                return new CaptureResult
                {
                    Platform = "快手",
                    Protocol = "RTMP (from API)",
                    Url = baseUrl,
                    StreamKey = streamKey + queryParams,
                    Parameters = ExtractParameters(streamKey + queryParams),
                    RawData = jsonContent.Length > 300 ? jsonContent.Substring(0, 300) + "..." : jsonContent,
                    Timestamp = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                Log($"[快手API] ❌ 解析异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 快手专用解析（优先HTTP API，后备RTMP）
        /// </summary>
        private CaptureResult? ExtractRtmpPublish_Kuaishou(string payload, byte[] rawBytes, PlatformConfig platform, string flowKey)
        {
            Log($"========== [快手] 数据包分析 ==========");
            Log($"数据包总长度: {payload.Length} 字节");
            Log($"TCP流标识: {flowKey}");

            // 【优先】尝试从HTTP API响应提取
            var apiResult = ExtractKuaishouFromAPI(payload);
            if (apiResult != null)
            {
                Log($"[快手] ✓ 从API响应成功提取");
                return apiResult;
            }

            // 【后备】尝试从RTMP协议提取
            Log($"[快手] 尝试RTMP协议解析...");

            // 1) 提取 Base URL（来自 tcUrl 或直接匹配 RTMP/RTMPS 地址）
            string baseUrl = "";
            var kuaishouUrlRegex = new System.Text.RegularExpressions.Regex(
                @"(rtmps?://[^\x00]*?(kuaishou|ksyun|yximgs|gifshow)\.com/[^\x00]*?)[\x00]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            var urlMatch = kuaishouUrlRegex.Match(payload);
            if (urlMatch.Success)
            {
                baseUrl = System.Text.RegularExpressions.Regex.Replace(urlMatch.Groups[1].Value, @"[\x00-\x1F]", "");
                Log($"[快手] ✓ 从正则提取到 baseUrl: {baseUrl}");
            }
            else
            {
                Log($"[快手] ✗ 正则未匹配到快手域名 URL");

                // 备用：从 tcUrl 提取
                var tcMatch = System.Text.RegularExpressions.Regex.Match(
                    payload,
                    @"tcUrl[\x00-\x20]+([^\x00]+?)[\x00]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                if (tcMatch.Success)
                {
                    baseUrl = System.Text.RegularExpressions.Regex.Replace(tcMatch.Groups[1].Value, @"[\x00-\x1F]", "");
                    Log($"[快手] ✓ 从 tcUrl 字段提取: {baseUrl}");
                }
                else
                {
                    Log($"[快手] ✗ tcUrl 字段也未找到");
                }
            }

            // 如果当前包中没有找到baseUrl，尝试从之前的connect命令中获取
            if (string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(flowKey) && _tcUrlByFlow.ContainsKey(flowKey))
            {
                baseUrl = _tcUrlByFlow[flowKey];
                Log($"[快手] ✓ 使用缓存的tcUrl: {baseUrl}");
            }

            // 校验 Base URL 域名是否确为快手系
            bool baseUrlMatchesKuaishou = false;
            if (!string.IsNullOrEmpty(baseUrl))
            {
                Log($"[快手] 开始域名校验...");
                foreach (var pattern in platform.DomainPatterns)
                {
                    var re = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (re.IsMatch(baseUrl))
                    {
                        baseUrlMatchesKuaishou = true;
                        Log($"[快手] ✓ 域名匹配成功: {pattern}");
                        break;
                    }
                }

                if (!baseUrlMatchesKuaishou)
                {
                    Log($"[快手] ✗ 域名校验失败，URL: {baseUrl}");
                }
            }
            else
            {
                Log($"[快手] ✗ baseUrl 为空，无法进行域名校验");
            }

            // 2) AMF0 扫描以提取 streamKey（publish 之后的第一个 String 字段）
            string streamKey = string.Empty;
            int publishIdx = payload.IndexOf("publish", StringComparison.Ordinal);
            if (publishIdx >= 0)
            {
                Log($"[快手] ✓ 找到 publish 命令，位置: {publishIdx}");

                // 在 publish 之后，使用原始字节扫描
                // 搜索模式： 0x05 0x02 [lenHi] [lenLo] [streamKey bytes]
                int scanStart = Math.Min(publishIdx + 7, rawBytes.Length - 4);
                int scanEnd = Math.Min(rawBytes.Length - 4, scanStart + 4096);
                Log($"[快手] AMF0 扫描范围: {scanStart} -> {scanEnd}");

                for (int i = scanStart; i < scanEnd; i++)
                {
                    if (rawBytes[i] == 0x05 && rawBytes[i + 1] == 0x02)
                    {
                        int len = (rawBytes[i + 2] << 8) | rawBytes[i + 3];
                        int keyStart = i + 4;
                        if (len > 0 && keyStart + len <= rawBytes.Length)
                        {
                            var candidate = Encoding.UTF8.GetString(rawBytes, keyStart, len);
                            candidate = System.Text.RegularExpressions.Regex.Replace(candidate, @"[\x00-\x1F\x7F-\x9F]", "");
                            Log($"[快手] AMF0候选streamKey (长度{len}): {candidate.Substring(0, Math.Min(50, candidate.Length))}...");

                            if (candidate.Length >= 4)
                            {
                                streamKey = candidate;
                                Log($"[快手] ✓ 提取streamKey成功 (完整长度: {streamKey.Length})");
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(streamKey))
                {
                    Log($"[快手] ✗ AMF0 扫描未找到有效 streamKey");
                }
            }
            else
            {
                Log($"[快手] ✗ 未找到 publish 命令");
            }

            // 3) 正则后备：适配部分变体
            if (string.IsNullOrEmpty(streamKey))
            {
                Log($"[快手] 尝试正则后备提取...");
                var m = System.Text.RegularExpressions.Regex.Match(
                    payload,
                    @"publish.{6,64}\x05\x02..([\x20-\x7E]{4,200})",
                    System.Text.RegularExpressions.RegexOptions.Singleline
                );
                if (m.Success)
                {
                    streamKey = System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"[\x00-\x1F\x7F-\x9F]", "");
                    Log($"[快手] ✓ 正则后备提取成功: {streamKey.Substring(0, Math.Min(50, streamKey.Length))}...");
                }
                else
                {
                    Log($"[快手] ✗ 正则后备也未匹配");
                }
            }

            // 4) 结果校验与输出
            Log($"[快手] 最终校验结果:");
            Log($"  - baseUrl: {(string.IsNullOrEmpty(baseUrl) ? "✗ 空" : "✓ " + baseUrl)}");
            Log($"  - streamKey: {(string.IsNullOrEmpty(streamKey) ? "✗ 空" : "✓ " + streamKey.Substring(0, Math.Min(50, streamKey.Length)) + "...")}");
            Log($"  - 域名匹配: {(baseUrlMatchesKuaishou ? "✓ 通过" : "✗ 失败")}");

            if (string.IsNullOrEmpty(streamKey) || string.IsNullOrEmpty(baseUrl) || !baseUrlMatchesKuaishou)
            {
                Log($"========== [快手] ❌ 解析失败 ==========");
                return null;
            }

            // 去重检查
            if (IsDuplicateAndRemember(platform.Id ?? platform.Name, baseUrl, streamKey))
            {
                Log("[快手] 🔄 去重：忽略连续重复的结果");
                Log($"========== [快手] ⏭ 已跳过（重复） ==========");
                return null;
            }

            var result = new CaptureResult
            {
                Platform = platform.Name,
                Protocol = "RTMP",
                Url = baseUrl,
                StreamKey = streamKey,
                Parameters = ExtractParameters(streamKey),
                RawData = payload.Length > 300 ? payload.Substring(0, 300) + "..." : payload,
                Timestamp = DateTime.Now
            };

            Log($"========== [快手] ✅ 解析成功 ==========");
            Log($"推流地址: {result.Url}");
            Log($"流密钥: {result.StreamKey}");
            Log($"完整地址: {result.Url}/{result.StreamKey}");
            Log($"参数数量: {result.Parameters.Count}");
            Log($"=========================================");

            return result;
        }

        /// <summary>
        /// 京东专用RTMP解析
        /// 推流地址格式: rtmp://zt-push-ai.jdcloud.com/live/
        /// 流密钥格式: {stream_id}?auth_key={timestamp}-0-0-{hash}
        /// </summary>
        private CaptureResult? ExtractRtmpPublish_JD(string payload, byte[] rawBytes, PlatformConfig platform, string flowKey)
        {
            Log($"========== [京东] 数据包分析 ==========");
            Log($"数据包总长度: {payload.Length} 字节");
            Log($"TCP流标识: {flowKey}");

            // 1) 提取 Base URL - 京东格式: rtmp://zt-push.jdcloud.com/live
            string baseUrl = "";

            // 精确匹配京东推流地址: rtmp://xxx.jdcloud.com/live
            var jdUrlRegex = new System.Text.RegularExpressions.Regex(
                @"(rtmps?://[a-z0-9\-]+\.jdcloud\.com/live)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            var urlMatch = jdUrlRegex.Match(payload);
            if (urlMatch.Success)
            {
                baseUrl = urlMatch.Groups[1].Value;
                Log($"[京东] ✓ 从正则提取到 baseUrl: {baseUrl}");
            }
            else
            {
                // 备用：从 tcUrl 提取，只取 rtmp:// 开头的部分
                var tcMatch = System.Text.RegularExpressions.Regex.Match(
                    payload,
                    @"tcUrl[^\x20-\x7E]*(rtmps?://[a-z0-9\-]+\.jdcloud\.com/live)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                if (tcMatch.Success)
                {
                    baseUrl = tcMatch.Groups[1].Value;
                    Log($"[京东] ✓ 从 tcUrl 字段提取: {baseUrl}");
                }
            }

            // 如果当前包中没有找到baseUrl，尝试从之前的connect命令中获取
            if (string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(flowKey) && _tcUrlByFlow.ContainsKey(flowKey))
            {
                baseUrl = _tcUrlByFlow[flowKey];
                Log($"[京东] ✓ 使用缓存的tcUrl: {baseUrl}");
            }

            // 校验 Base URL 域名是否确为京东系
            bool baseUrlMatchesJD = false;
            if (!string.IsNullOrEmpty(baseUrl))
            {
                foreach (var pattern in platform.DomainPatterns)
                {
                    var re = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (re.IsMatch(baseUrl))
                    {
                        baseUrlMatchesJD = true;
                        Log($"[京东] ✓ 域名匹配成功: {pattern}");
                        break;
                    }
                }

                if (!baseUrlMatchesJD)
                {
                    Log($"[京东] ✗ 域名校验失败，URL: {baseUrl}");
                }
            }

            // 2) AMF0 扫描以提取 streamKey（publish 之后的第一个 String 字段）
            string streamKey = string.Empty;
            int publishIdx = payload.IndexOf("publish", StringComparison.Ordinal);
            if (publishIdx < 0)
            {
                publishIdx = payload.IndexOf("FCPublish", StringComparison.Ordinal);
            }
            if (publishIdx >= 0)
            {
                Log($"[京东] ✓ 找到 publish 命令，位置: {publishIdx}");

                // 在 publish 之后，使用原始字节扫描
                // 搜索模式： 0x05 0x02 [lenHi] [lenLo] [streamKey bytes]
                int scanStart = Math.Min(publishIdx + 7, rawBytes.Length - 4);
                int scanEnd = Math.Min(rawBytes.Length - 4, scanStart + 4096);

                for (int i = scanStart; i < scanEnd; i++)
                {
                    if (rawBytes[i] == 0x05 && rawBytes[i + 1] == 0x02)
                    {
                        int len = (rawBytes[i + 2] << 8) | rawBytes[i + 3];
                        int keyStart = i + 4;
                        if (len > 0 && keyStart + len <= rawBytes.Length)
                        {
                            var candidate = Encoding.UTF8.GetString(rawBytes, keyStart, len);
                            candidate = System.Text.RegularExpressions.Regex.Replace(candidate, @"[\x00-\x1F\x7F-\x9F]", "");

                            // 京东流密钥格式: {stream_id}?auth_key=...
                            if (candidate.Length >= 4)
                            {
                                streamKey = System.Text.RegularExpressions.Regex.Replace(candidate, @"[^\x20-\x7E]", "");
                                Log($"[京东] ✓ AMF0提取streamKey: {streamKey.Substring(0, Math.Min(50, streamKey.Length))}...");
                                break;
                            }
                        }
                    }
                }

                // 后备：扫描 0x02 [lenHi] [lenLo] 格式
                if (string.IsNullOrEmpty(streamKey))
                {
                    for (int i = scanStart; i < scanEnd; i++)
                    {
                        if (rawBytes[i] == 0x02 && i + 3 < rawBytes.Length)
                        {
                            int len = (rawBytes[i + 1] << 8) | rawBytes[i + 2];
                            int keyStart = i + 3;
                            if (len > 4 && len <= 256 && keyStart + len <= rawBytes.Length)
                            {
                                var candidate = Encoding.UTF8.GetString(rawBytes, keyStart, len);
                                candidate = System.Text.RegularExpressions.Regex.Replace(candidate, @"[\x00-\x1F\x7F-\x9F]", "");
                                // 必须看起来像流密钥（数字开头或包含auth_key）
                                if (candidate.Length >= 4 && (char.IsDigit(candidate[0]) || candidate.Contains("auth_key")))
                                {
                                    streamKey = System.Text.RegularExpressions.Regex.Replace(candidate, @"[^\x20-\x7E]", "");
                                    Log($"[京东] ✓ AMF0后备提取streamKey: {streamKey.Substring(0, Math.Min(50, streamKey.Length))}...");
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // 3) 正则后备：尝试直接匹配包含 auth_key 的流密钥
            if (string.IsNullOrEmpty(streamKey))
            {
                var authKeyMatch = System.Text.RegularExpressions.Regex.Match(
                    payload,
                    @"(\d+\?auth_key=[^\x00\s""'<>]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                if (authKeyMatch.Success)
                {
                    streamKey = authKeyMatch.Groups[1].Value;
                    streamKey = System.Text.RegularExpressions.Regex.Replace(streamKey, @"[^\x20-\x7E]", "");
                    Log($"[京东] ✓ 正则提取streamKey: {streamKey}");
                }
            }

            // 4) 结果校验与输出
            if (string.IsNullOrEmpty(streamKey) || string.IsNullOrEmpty(baseUrl) || !baseUrlMatchesJD)
            {
                Log($"[京东] ✗ 解析失败 - baseUrl: {!string.IsNullOrEmpty(baseUrl)}, streamKey: {!string.IsNullOrEmpty(streamKey)}, 域名匹配: {baseUrlMatchesJD}");
                return null;
            }

            // 标准化 baseUrl：确保末尾有 /
            if (!baseUrl.EndsWith("/"))
            {
                baseUrl += "/";
            }

            // 去重检查
            if (IsDuplicateAndRemember(platform.Id ?? platform.Name, baseUrl, streamKey))
            {
                Log($"========== [京东] ⏭ 已跳过（重复） ==========");
                return null;
            }

            var result = new CaptureResult
            {
                Platform = platform.Name,
                Protocol = "RTMP",
                Url = baseUrl,
                StreamKey = streamKey,
                Parameters = ExtractParameters(streamKey),
                RawData = payload.Length > 300 ? payload.Substring(0, 300) + "..." : payload,
                Timestamp = DateTime.Now
            };

            Log($"========== [京东] ✅ 解析成功 ==========");
            Log($"推流地址: {result.Url}");
            Log($"流密钥: {result.StreamKey}");
            Log($"完整地址: {result.Url}/{result.StreamKey}");
            Log($"参数数量: {result.Parameters.Count}");
            Log($"=========================================");

            return result;
        }

        /// <summary>
        /// 通用RTMP解析（作为后备方案）
        /// </summary>
        private CaptureResult? ExtractRtmpPublish_Generic(string payload, PlatformConfig platform)
        {
            Log($"[{platform.Name}] 使用通用解析器");

            string baseUrl = "";
            string streamKey = "";

            // 查找tcUrl
            var tcUrlMatch = System.Text.RegularExpressions.Regex.Match(payload, @"tcUrl[\x00-\x20]+([^\x00]+?)[\x00]");
            if (!tcUrlMatch.Success)
                tcUrlMatch = System.Text.RegularExpressions.Regex.Match(payload, @"(rtmp://[^\x00]+?)[\x00]");

            if (tcUrlMatch.Success)
            {
                baseUrl = tcUrlMatch.Groups[1].Value.Trim();
                baseUrl = System.Text.RegularExpressions.Regex.Replace(baseUrl, @"[\x00-\x1F]", "");
            }

            // 查找流密钥
            var streamKeyMatch = System.Text.RegularExpressions.Regex.Match(
                payload,
                @"publish.{8,15}\x05\x02..([\x20-\xFF]{4,150}?)(?:\x02|\x00)",
                System.Text.RegularExpressions.RegexOptions.Singleline
            );

            if (streamKeyMatch.Success)
            {
                var rawStr = streamKeyMatch.Groups[1].Value.Trim();
                // 尝试修复编码：Latin1 -> UTF-8
                try {
                   var bytes = Encoding.Latin1.GetBytes(rawStr);
                   streamKey = Encoding.UTF8.GetString(bytes);
                } catch {
                   streamKey = rawStr;
                }
                streamKey = System.Text.RegularExpressions.Regex.Replace(streamKey, @"[\x00-\x1F\x7F-\x9F]", "");

                return new CaptureResult
                {
                    Platform = platform.Name,
                    Protocol = "RTMP",
                    Url = baseUrl,
                    StreamKey = streamKey,
                    Parameters = ExtractParameters(streamKey),
                    RawData = payload.Length > 300 ? payload.Substring(0, 300) + "..." : payload,
                    Timestamp = DateTime.Now
                };
            }

            return null;
        }

        /// <summary>
        /// 哔哩哔哩专用RTMP解析（AMF0）
        /// - baseUrl: 从窗口内的 rtmp://live-push.bilivideo.com/... 或 tcUrl 提取
        /// - streamKey: publish 后的第一个 String 字段
        /// 仅当 baseUrl 命中 B站域名时返回，避免误报
        /// </summary>
        private CaptureResult? ExtractRtmpPublish_Bilibili(string payload, byte[] rawBytes, PlatformConfig platform, string udpDestIp = "", int udpDestPort = 0)
        {
            // 1) 提取 baseUrl（优先 rtmp://...bilivideo.com，其次 tcUrl，再次 rtmpsrt://IP/live-bvc，最后UDP目标IP）
            string baseUrl = "";

            // 如果是UDP包且有目标IP，优先使用UDP目标IP构造地址
            if (!string.IsNullOrEmpty(udpDestIp) && udpDestPort > 0)
            {
                baseUrl = $"rtmp://{udpDestIp}/live-bvc";
            }
            // 如果baseUrl还是空（UDP包没有提供IP），尝试从payload中提取
            if (string.IsNullOrEmpty(baseUrl))
            {
                var biliUrlRegex = new System.Text.RegularExpressions.Regex(
                    @"(rtmps?://[^\x00]*?bilivideo\.com/[^\x00]*?)[\x00]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                var um = biliUrlRegex.Match(payload);
                if (um.Success)
                {
                    baseUrl = System.Text.RegularExpressions.Regex.Replace(um.Groups[1].Value, @"[\x00-\x1F]", "");
                }
                else
                {
                    var tc = System.Text.RegularExpressions.Regex.Match(payload, @"tcUrl[\x00-\x20]+([^\x00]+?)[\x00]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (tc.Success)
                    {
                        baseUrl = System.Text.RegularExpressions.Regex.Replace(tc.Groups[1].Value, @"[\x00-\x1F]", "");
                    }
                    else
                    {
                        // 兼容 IP + rtmpsrt + live-bvc 形态
                        var ipRtmpsrt = System.Text.RegularExpressions.Regex.Match(
                            payload,
                            @"(rtmps?rt://[0-9\.]+/(?:live-?bvc))",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        );
                        if (ipRtmpsrt.Success)
                        {
                            baseUrl = ipRtmpsrt.Groups[1].Value;
                        }
                    }
                }
            }

            // 1.1 归一化 baseUrl：去掉可能的前导符(@,*,>,空白)，并从中抽取真正的 rtmp(s)/rtmpsrt URL
            if (!string.IsNullOrEmpty(baseUrl))
            {
                baseUrl = baseUrl.Trim();
                // 去掉日志中的前缀符号
                baseUrl = baseUrl.TrimStart(' ', '\t', '\r', '\n', '*', '@', '>');
                // 从中提取规范的 URL 片段
                var realUrl = System.Text.RegularExpressions.Regex.Match(
                    baseUrl,
                    @"(rtmps?rt://[^\s\x00]+|rtmps?://[^\s\x00]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                if (realUrl.Success)
                {
                    baseUrl = realUrl.Groups[1].Value;
                }
                // 将 rtmp://IP/live-bvc/live_xxx 截断为 rtmp://IP/live-bvc
                var rtmpLive = System.Text.RegularExpressions.Regex.Match(baseUrl, @"^(rtmps?rt?://[^/]+/live-?bvc)(?:/.*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (rtmpLive.Success)
                {
                    baseUrl = rtmpLive.Groups[1].Value;
                }
                // 将 /live-bvc 后面多余的路径截断为基址，避免 Url+StreamKey 产生重复
                var lb = baseUrl.IndexOf("/live-bvc", StringComparison.OrdinalIgnoreCase);
                if (lb >= 0)
                {
                    int end = lb + "/live-bvc".Length;
                    baseUrl = baseUrl.Substring(0, end);
                }
            }

            // 确认为 B站域名
            bool baseUrlMatchesBili = false;
            if (!string.IsNullOrEmpty(baseUrl))
            {
                foreach (var pattern in platform.DomainPatterns)
                {
                    var re = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (re.IsMatch(baseUrl))
                    {
                        baseUrlMatchesBili = true;
                        break;
                    }
                }
                // 兜底：IP + rtmpsrt/rtmp + live-bvc 形态（包括UDP构造的地址）
                if (!baseUrlMatchesBili && baseUrl.IndexOf("live-bvc", StringComparison.OrdinalIgnoreCase) >= 0
                    && (baseUrl.StartsWith("rtmpsrt://", StringComparison.OrdinalIgnoreCase)
                        || baseUrl.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)))
                {
                    baseUrlMatchesBili = true;
                }
            }

            // 2) AMF0 扫描 publish 后的 streamKey
            string streamKey = string.Empty;
            int publishIdx = payload.IndexOf("publish", StringComparison.Ordinal);
            if (publishIdx < 0)
            {
                // 兼容 FCPublish 变体
                publishIdx = payload.IndexOf("FCPublish", StringComparison.Ordinal);
            }
            if (publishIdx >= 0)
            {
                int scanStart = Math.Min(publishIdx + 7, rawBytes.Length - 4);
                int scanEnd = Math.Min(rawBytes.Length - 4, scanStart + 4096);
                for (int i = scanStart; i < scanEnd; i++)
                {
                    if (rawBytes[i] == 0x05 && rawBytes[i + 1] == 0x02)
                    {
                        int len = (rawBytes[i + 2] << 8) | rawBytes[i + 3];
                        int keyStart = i + 4;
                        if (len > 0 && keyStart + len <= rawBytes.Length)
                        {
                            var candidate = Encoding.UTF8.GetString(rawBytes, keyStart, len);
                            candidate = System.Text.RegularExpressions.Regex.Replace(candidate, @"[\x00-\x1F\x7F-\x9F]", "");
                            if (candidate.Length >= 2)
                            {
                                streamKey = candidate;
                                break;
                            }
                        }
                    }
                }

                // 后备：若未按 0x05 0x02 命中，则向后寻找第一个 AMF0 String 作为发布名（带轻度校验）
                if (string.IsNullOrEmpty(streamKey))
                {
                    for (int i = scanStart; i < scanEnd; i++)
                    {
                        if (rawBytes[i] == 0x02 && i + 3 < rawBytes.Length)
                        {
                            int len = (rawBytes[i + 1] << 8) | rawBytes[i + 2];
                            int keyStart = i + 3;
                            if (len > 0 && len <= 256 && keyStart + len <= rawBytes.Length)
                            {
                                var candidate = Encoding.UTF8.GetString(rawBytes, keyStart, len);
                                candidate = System.Text.RegularExpressions.Regex.Replace(candidate, @"[\x00-\x1F\x7F-\x9F]", "");
                                if (candidate.Length >= 4)
                                {
                                    // 轻度规则：不是域名/协议字样，且看起来像路径或ID
                                    bool looksOk = !candidate.Contains("rtmp", StringComparison.OrdinalIgnoreCase)
                                        && (candidate.Contains('/') || candidate.Contains('?') || candidate.Contains('=')
                                            || System.Text.RegularExpressions.Regex.IsMatch(candidate, @"^[A-Za-z0-9_-]{6,}$"));
                                    if (looksOk)
                                    {
                                        streamKey = candidate;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 3) 后备正则（应对 padding / 变体）
            if (string.IsNullOrEmpty(streamKey))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    payload,
                    @"publish.{6,64}\x05\x02..([\x20-\x7E]{2,128})",
                    System.Text.RegularExpressions.RegexOptions.Singleline
                );
                if (m.Success)
                {
                    streamKey = System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"[\x00-\x1F\x7F-\x9F]", "");
                }
            }

            // 4) 进一步后备：直接解析 streamname=...（直播姬常见形态）
            // 如果streamKey已经存在但不包含key参数，尝试从streamname=中提取完整参数
            if (string.IsNullOrEmpty(streamKey) || (!streamKey.Contains("key=", StringComparison.OrdinalIgnoreCase) && payload.Contains("streamname=", StringComparison.OrdinalIgnoreCase)))
            {
                var sn = System.Text.RegularExpressions.Regex.Match(
                    payload,
                    @"streamname=([A-Za-z0-9_\-]+(?:[?&][^\x00\s""'&]+)*)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                if (sn.Success)
                {
                    var fullStreamName = sn.Groups[1].Value;
                    // 如果新提取的包含key参数，或者原streamKey为空，使用新的
                    if (string.IsNullOrEmpty(streamKey) || fullStreamName.Contains("key=", StringComparison.OrdinalIgnoreCase))
                    {
                        streamKey = fullStreamName;
                    }
                }
            }

            // 4.1 规范化 streamKey（清理 streamname= 前缀、剔除 tcUrl/swfUrl、去重 live-bvc 前缀）
            if (!string.IsNullOrEmpty(streamKey))
            {
                // 清理控制字符与尾随的 %00 等
                streamKey = System.Text.RegularExpressions.Regex.Replace(streamKey, @"[\x00-\x1F\x7F-\x9F]", "").Trim();
                streamKey = System.Text.RegularExpressions.Regex.Replace(streamKey, @"(%00)+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // 如果包含 "streamname=...&key=..." 形式，重组为 live_xxx?key=...
                if (streamKey.IndexOf("streamname=", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var nameM = System.Text.RegularExpressions.Regex.Match(streamKey, @"streamname=([^&\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (nameM.Success)
                    {
                        var name = nameM.Groups[1].Value;
                        // 收集所有形如 k=v 的对
                        var kvMatches = System.Text.RegularExpressions.Regex.Matches(streamKey, @"([A-Za-z0-9_]+)=([^&\s]+)");
                        var parts = new List<string>();
                        foreach (System.Text.RegularExpressions.Match kv in kvMatches)
                        {
                            var k = kv.Groups[1].Value;
                            var v = kv.Groups[2].Value;
                            if (k.Equals("streamname", StringComparison.OrdinalIgnoreCase)) continue;
                            if (k.Equals("tcUrl", StringComparison.OrdinalIgnoreCase)) continue;
                            if (k.Equals("swfUrl", StringComparison.OrdinalIgnoreCase)) continue;
                            parts.Add($"{k}={v}");
                        }
                        streamKey = parts.Count > 0 ? $"{name}?{string.Join("&", parts)}" : name;
                    }
                    else
                    {
                        // 若未匹配到 name，截断到 tcUrl 之前
                        var tcIdx = streamKey.IndexOf("tcUrl=", StringComparison.OrdinalIgnoreCase);
                        if (tcIdx > 0) streamKey = streamKey.Substring(0, tcIdx);
                    }
                }

                // 去掉前置的 "/live-bvc/" 或 "live-bvc/"，避免和 baseUrl 重复
                streamKey = System.Text.RegularExpressions.Regex.Replace(streamKey, @"^/?live-?bvc/", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // 若携带 tcUrl= 残段，截断
                var tcPos = streamKey.IndexOf("tcUrl=", StringComparison.OrdinalIgnoreCase);
                if (tcPos > 0)
                {
                    streamKey = streamKey.Substring(0, tcPos).TrimEnd('&');
                }
                var swfPos = streamKey.IndexOf("swfUrl=", StringComparison.OrdinalIgnoreCase);
                if (swfPos > 0)
                {
                    streamKey = streamKey.Substring(0, swfPos).TrimEnd('&');
                }
            }

            // 对于UDP的情况，如果streamKey存在但baseUrl为空，返回null（不应该发生，因为UDP调用时已传入IP）
            if (string.IsNullOrEmpty(streamKey))
                return null;

            if (string.IsNullOrEmpty(baseUrl))
            {
                // 如果没有UDP IP也没有从payload提取到URL，返回null
                return null;
            }

            if (!baseUrlMatchesBili)
                return null;

            // 合并参数：来自 streamKey 及 baseUrl 的查询串
            var mergedParams = ExtractParameters(streamKey);
            var baseParams = ExtractParameters(baseUrl);
            foreach (var kv in baseParams)
            {
                if (!mergedParams.ContainsKey(kv.Key))
                {
                    mergedParams[kv.Key] = kv.Value;
                }
            }

            // 判断协议类型
            string protocol = "RTMP";
            if (baseUrl.StartsWith("rtmpsrt://", StringComparison.OrdinalIgnoreCase))
                protocol = "RTMPSRT";
            else if (baseUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
                protocol = "SRT";
            else if (baseUrl.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
                protocol = "RTMPS";

            // 去重检查
            if (IsDuplicateAndRemember(platform.Id ?? platform.Name, baseUrl, streamKey))
            {
                Log("[哔哩哔哩] 去重：忽略连续重复的结果");
                return null;
            }

            return new CaptureResult
            {
                Platform = platform.Name,
                Protocol = protocol,
                Url = baseUrl,
                StreamKey = streamKey,
                Parameters = mergedParams,
                RawData = payload.Length > 300 ? payload.Substring(0, 300) + "..." : payload,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// 确定协议类型
        /// </summary>
        private string DetermineProtocol(string url)
        {
            if (url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
                return "RTMPS";
            if (url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
                return "RTMP";
            if (url.Contains("webrtc", StringComparison.OrdinalIgnoreCase))
                return "WebRTC";
            return "Unknown";
        }

        /// <summary>
        /// 提取流密钥
        /// </summary>
        private string ExtractStreamKey(string url)
        {
            try
            {
                var uri = new Uri(url);
                var segments = uri.AbsolutePath.Split('/');
                return segments.Length > 0 ? segments[^1] : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 提取URL参数
        /// </summary>
        private Dictionary<string, string> ExtractParameters(string url)
        {
            var parameters = new Dictionary<string, string>();
            try
            {
                var uri = new Uri(url);
                var query = uri.Query.TrimStart('?');
                var pairs = query.Split('&');

                foreach (var pair in pairs)
                {
                    var kv = pair.Split('=');
                    if (kv.Length == 2)
                    {
                        parameters[kv[0]] = kv[1];
                    }
                }
            }
            catch { }

            return parameters;
        }

        private void Log(string message)
        {
            OnLog?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        /// <summary>
        /// 去重检查：同一平台的相同 URL + StreamKey 组合在短时间内只触发一次
        /// </summary>
        private bool IsDuplicateAndRemember(string platformId, string url, string streamKey)
        {
            try
            {
                // 使用 URL + StreamKey 作为唯一标识
                var uniqueKey = $"{url}|{streamKey}";
                var pid = platformId.ToLowerInvariant();

                // 检查是否与上次捕获的相同
                if (_lastKeyByPlatform.TryGetValue(pid, out var lastKey))
                {
                    if (string.Equals(lastKey, uniqueKey, StringComparison.Ordinal))
                    {
                        Log($"[去重] {pid} 重复推流配置被忽略");
                        return true; // 重复
                    }
                }

                // 记录本次捕获
                _lastKeyByPlatform[pid] = uniqueKey;
                Log($"[去重] {pid} 新推流配置已记录");
                return false; // 不重复
            }
            catch (Exception ex)
            {
                Log($"[去重] 检查异常: {ex.Message}");
                return false;
            }
        }

        #region WebRTC/UDP 流量统计

        #endregion

        public void Dispose()
        {
            StopCapture();

            _device?.Dispose();

            foreach (var device in _devices)
            {
                try
                {
                    device?.Dispose();
                }
                catch { }
            }
            _devices.Clear();
        }
    }
}
