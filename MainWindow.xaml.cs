using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using StreamCapture.Core;
using StreamCapture.Models;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace StreamCapture
{
    public partial class MainWindow : Window
    {
        private PacketCapture? _packetCapture;
        private HttpsProxyService? _httpsProxy;
        private NativeProcessInjector? _processInjector; // Windows API 进程注入器（.NET 8.0 兼容）
        private WinSockDataAnalyzer? _dataAnalyzer;
        private ProcessMonitor? _processMonitor; // 进程监控器
        private ObsMultiRtmpManager? _obsManager; // OBS多路推流管理器
        private AppConfig _config = null!;
        private ObservableCollection<CaptureResult> _results;
        private List<CheckBox> _platformCheckBoxes;
        private string _logFilePath;
        private bool _isCapturing = false;
        private bool _kuaishouProcessDetected = false; // 快手进程检测标记

        public MainWindow()
        {
            InitializeComponent();

            // 最大化时调整边距防止遮挡任务栏
            StateChanged += (s, e) =>
            {
                if (WindowState == WindowState.Maximized)
                {
                    RootBorder.BorderThickness = new Thickness(0);
                    RootBorder.Padding = new Thickness(7);
                    MaximizeBtn.Content = "❐";
                }
                else
                {
                    RootBorder.BorderThickness = new Thickness(1);
                    RootBorder.Padding = new Thickness(0);
                    MaximizeBtn.Content = "□";
                }
            };
            _results = new ObservableCollection<CaptureResult>();
            _platformCheckBoxes = new List<CheckBox>();

            _logFilePath = "disabled (privacy)";

            InitializeApp();
            VersionText.Text = "Community";
        }

        private void InitializeApp()
        {
            try
            {
                // 加载配置
                _config = ConfigManager.LoadConfig();

                // 初始化平台选择框
                InitializePlatformCheckBoxes();

                // 初始化网卡列表
                InitializeDeviceList();

                // 绑定结果列表
                ResultListBox.ItemsSource = _results;

                // 加载二维码图片（从嵌入资源）


                AddLog("========================================");
                AddLog("🎉 应用初始化成功");
                AddLog("========================================");
                AddLog($"📝 日志文件: {_logFilePath}");
                AddLog("💡 使用说明：");
                AddLog("   1. 默认已选择【所有网卡】模式（推荐）");
                AddLog("   2. 选择要监听的平台（默认全选）");
                AddLog("   3. 点击【开始捕获】按钮");
                AddLog("   4. 打开直播软件开始推流");
                AddLog("   5. 捕获到推流地址后自动配置OBS");
                AddLog("========================================");

                // 初始化OBS管理器
                InitializeObsManager();

                // 启动进程监控
                InitializeProcessMonitor();

                // 更新右上角授权状态显示

            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InitializeObsManager()
        {
            try
            {
                AddLog("");
                _obsManager = new ObsMultiRtmpManager(AddLog);
            }
            catch (Exception ex)
            {
                AddLog($"⚠️  初始化OBS管理器失败: {ex.Message}");
                AddLog("   OBS自动配置功能将不可用");
            }
        }

        /// <summary>
        /// 显示OBS推流提示窗口
        /// </summary>
        private void ShowObsStreamPrompt(string platform)
        {
            try
            {
                // 生成目标名称
                string targetName = $"{platform} - {DateTime.Now:HH:mm:ss}";

                // 创建并显示提示窗口
                var promptWindow = new ObsStreamPromptWindow(platform, targetName);
                promptWindow.Owner = this; // 设置父窗口
                promptWindow.ShowDialog();

                AddLog("💬 已显示OBS推流提示窗口");
            }
            catch (Exception ex)
            {
                AddLog($"⚠️  显示提示窗口异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 自动配置OBS多路推流
        /// </summary>
        private void AutoConfigureObs(string platform, string serverUrl, string streamKey)
        {
            try
            {
                if (_obsManager == null)
                {
                    AddLog("⚠️  OBS管理器未初始化，跳过自动配置");
                    return;
                }

                AddLog("");
                AddLog("🚀 ==========================================");
                AddLog("🚀 开始自动配置OBS...");
                AddLog("🚀 ==========================================");
                AddLog($"   平台: {platform}");
                AddLog($"   服务器: {serverUrl}");
                AddLog($"   流密钥: {streamKey.Substring(0, Math.Min(50, streamKey.Length))}...");

                // 添加到OBS配置
                bool success = _obsManager.AddStreamTarget(platform, serverUrl, streamKey);

                if (success)
                {
                    AddLog("");
                    AddLog("✅ OBS配置完成！");
                    AddLog("");
                    AddLog("⚡ 重要提示：推流地址有效期很短（约30秒-2分钟）");
                    AddLog("");
                    AddLog("📝 下一步操作：");
                    AddLog("   1. 如果OBS未运行，将自动启动");
                    AddLog("   2. 在OBS中：工具 -> 多路推流设置");
                    AddLog("   3. 勾选刚添加的推流目标");
                    AddLog("   4. ⚡ 立即点击【开始所有推流】");
                    AddLog("");

                    // 尝试启动OBS
                    _obsManager.StartObs();

                    // 显示显眼的提示窗口
                    ShowObsStreamPrompt(platform);
                }
                else
                {
                    AddLog("❌ OBS配置失败，请检查日志");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ 自动配置OBS异常: {ex.Message}");
            }
        }

        private void InitializeProcessMonitor()
        {
            try
            {
                AddLog("");
                AddLog("========================================");
                AddLog("🔍 启动快手进程自动检测...");
                AddLog("========================================");

                // 创建进程监控器
                _processMonitor = new ProcessMonitor(
                    AddLog,
                    OnKuaishouProcessFound,
                    OnKuaishouProcessExited
                );

                // 立即进行一次检测
                var process = _processMonitor.FindKuaishouProcess();
                if (process != null)
                {
                    _kuaishouProcessDetected = true;
                    AddLog("");
                    AddLog("💡 提示：快手进程已在运行");
                    AddLog("   点击【开始捕获】即可开始监控");
                    AddLog("");
                }
                else
                {
                    AddLog("ℹ️  未检测到快手进程");
                    AddLog($"💡 支持的进程名: {string.Join(", ", ProcessMonitor.GetPossibleProcessNames())}");
                    AddLog("");
                }

                // 启动后台监控（每3秒检查一次）
                _processMonitor.StartMonitoring(intervalSeconds: 3);

            }
            catch (Exception ex)
            {
                AddLog($"⚠️  进程监控初始化失败: {ex.Message}");
            }
        }

        private void OnKuaishouProcessFound(System.Diagnostics.Process process)
        {
            _kuaishouProcessDetected = true;

            // 在UI线程上更新
            Dispatcher.Invoke(() =>
            {
                // 如果已经在捕获中，且还没有注入器（或注入失败），自动执行注入
                if (_isCapturing && _processInjector == null && _config.Platforms.Any(p => p.Id == "kuaishou" && p.Enabled))
                {
                    AddLog("");
                    AddLog("========================================");
                    AddLog("🎯 检测到快手进程启动，自动执行注入...");
                    AddLog("========================================");

                    try
                    {
                        // 创建数据分析器
                        _dataAnalyzer = new WinSockDataAnalyzer(
                            AddLog,
                            (platform, url, key) =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    AddResult(new CaptureResult
                                    {
                                        Platform = platform,
                                        Protocol = "RTMP",
                                        Url = url,
                                        StreamKey = key
                                    });

                                    // 自动配置OBS多路推流
                                    AutoConfigureObs(platform, url, key);
                                });
                            });

                        // 创建进程注入器
                        _processInjector = new NativeProcessInjector(
                            AddLog,
                            (data, isSend) =>
                            {
                                // 分析捕获的数据
                                _dataAnalyzer?.AnalyzeData(data, isSend);
                            });

                        if (_processInjector.InjectToKuaishou())
                        {
                            AddLog("✅ 自动注入成功！开始监控快手数据...");
                            AddLog("");
                        }
                        else
                        {
                            AddLog("⚠️  自动注入失败，将只使用数据包捕获");
                            _processInjector?.Dispose();
                            _processInjector = null;
                            _dataAnalyzer = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"❌ 自动注入异常: {ex.Message}");
                        _processInjector?.Dispose();
                        _processInjector = null;
                        _dataAnalyzer = null;
                    }
                }
            });
        }

        private void OnKuaishouProcessExited(int pid)
        {
            _kuaishouProcessDetected = false;
            AddLog("");
            AddLog("⚠️  快手进程已关闭");
            AddLog("💡 如需继续监控，请重启快手直播伴侣");
            AddLog("");
        }


        private Image? FindImageByTag(string tag)
        {
            return FindVisualChildByTag<Image>(this, tag);
        }

        private T? FindVisualChildByTag<T>(DependencyObject parent, string tag) where T : FrameworkElement
        {
            if (parent == null) return null;

            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

                if (child is T element && element.Tag?.ToString() == tag)
                {
                    return element;
                }

                var result = FindVisualChildByTag<T>(child, tag);
                if (result != null)
                    return result;
            }

            return null;
        }

        private void InitializePlatformCheckBoxes()
        {
            PlatformCheckBoxes.Children.Clear();
            _platformCheckBoxes.Clear();

            foreach (var platform in _config.Platforms)
            {
                // 跳过快手平台（隐藏图标）
                if (platform.Id?.Equals("kuaishou", StringComparison.OrdinalIgnoreCase) == true)
                {
                    platform.Enabled = false;
                    continue;
                }

                // 使用配置文件中的 enabled 设置（不再强制覆盖）
                // platform.Enabled 保持配置文件中的值

                // Build content with icon + name
                var contentPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var iconPath = GetPlatformIconPath(platform);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    try
                    {
                        // 优先从嵌入资源加载图片
                        var iconImage = Core.EmbeddedResourceHelper.LoadImageFromResource(iconPath);
                        if (iconImage != null)
                        {
                            var img = new Image
                            {
                                Width = 16,
                                Height = 16,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                Margin = new Thickness(0, 0, 6, 0),
                                Source = iconImage
                            };
                            contentPanel.Children.Add(img);
                        }
                        else
                        {
                            // 回退到文件系统（兼容模式）
                            var img = new Image
                            {
                                Width = 16,
                                Height = 16,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                Margin = new Thickness(0, 0, 6, 0),
                                Source = new BitmapImage(new Uri(iconPath, UriKind.Relative))
                            };
                            contentPanel.Children.Add(img);
                        }
                    }
                    catch { /* ignore resource load errors */ }
                }
                contentPanel.Children.Add(new TextBlock { Text = platform.Name, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#c9d1d9")) });

                var checkbox = new CheckBox
                {
                    Content = contentPanel,
                    IsChecked = platform.Enabled,  // 使用配置文件中的 enabled 设置
                    Margin = new Thickness(0, 0, 15, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = platform
                };

                // 添加平台切换事件，实现快手与其他平台互斥
                checkbox.Checked += PlatformCheckBox_Changed;
                checkbox.Unchecked += PlatformCheckBox_Changed;

                _platformCheckBoxes.Add(checkbox);
                PlatformCheckBoxes.Children.Add(checkbox);
            }
        }

        /// <summary>
        /// 京东平台勾选事件 - 检查是否已授权
        /// </summary>

        /// <summary>
        /// 移除付费标识
        /// </summary>

        /// <summary>
        /// 平台选择变更事件 - 快手和其他平台互斥选择
        /// </summary>
        private void PlatformCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox changedCheckBox || changedCheckBox.Tag is not PlatformConfig platform)
                return;

            // 只在选中时检查冲突
            if (changedCheckBox.IsChecked != true)
                return;

            bool isKuaishou = platform.Id?.Equals("kuaishou", StringComparison.OrdinalIgnoreCase) == true;

            // 暂时移除事件处理，避免递归触发
            foreach (var cb in _platformCheckBoxes)
            {
                cb.Checked -= PlatformCheckBox_Changed;
                cb.Unchecked -= PlatformCheckBox_Changed;
            }

            // 如果选中快手，取消其他平台
            if (isKuaishou)
            {
                foreach (var cb in _platformCheckBoxes)
                {
                    if (cb != changedCheckBox && cb.Tag is PlatformConfig p)
                    {
                        if (p.Id?.Equals("kuaishou", StringComparison.OrdinalIgnoreCase) != true)
                        {
                            cb.IsChecked = false;
                        }
                    }
                }
            }
            // 如果选中其他平台，取消快手
            else
            {
                foreach (var cb in _platformCheckBoxes)
                {
                    if (cb.Tag is PlatformConfig p &&
                        p.Id?.Equals("kuaishou", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        cb.IsChecked = false;
                    }
                }
            }

            // 恢复事件处理
            foreach (var cb in _platformCheckBoxes)
            {
                cb.Checked += PlatformCheckBox_Changed;
                cb.Unchecked += PlatformCheckBox_Changed;
            }
        }

        private string? GetPlatformIconPath(PlatformConfig platform)
        {
            // Map by id first, then by name
            var id = platform.Id?.ToLowerInvariant() ?? string.Empty;
            switch (id)
            {
                case "douyin": return "image/douyin.png";
                case "xiaohongshu": return "image/xiaohongshu.png";
                case "bilibili": return "image/bilibili.png";
                case "kuaishou": return "image/kuaishou.png";
                case "jd": return "image/jd.png";
            }
            var name = platform.Name;
            return name switch
            {
                "抖音" => "image/douyin.png",
                "小红书" => "image/xiaohongshu.png",
                "哔哩哔哩" => "image/bilibili.png",
                "快手" => "image/kuaishou.png",
                "京东" => "image/jd.png",
                _ => null
            };
        }

        private void InitializeDeviceList()
        {
            try
            {
                var tempCapture = new PacketCapture(_config.Platforms, debugFullTraffic: false);
                var devices = tempCapture.GetDevices();
                tempCapture.Dispose();

                // 添加"所有网卡"选项到列表顶部
                var deviceList = new List<string> { "🌐 所有网卡（推荐，自动监听）" };
                deviceList.AddRange(devices);

                DeviceComboBox.ItemsSource = deviceList;

                // 默认选择"所有网卡"
                DeviceComboBox.SelectedIndex = 0;

                AddLog($"💡 已自动选择监听模式：所有网卡");
                AddLog($"   共检测到 {devices.Count} 个网卡，可手动切换");
            }
            catch (Exception ex)
            {
                AddLog($"获取网卡列表失败: {ex.Message}");
                MessageBox.Show($"无法获取网卡列表，请确保已安装Npcap/WinPcap\n\n错误: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 切换捕获状态（开始/停止）
        /// </summary>
        private void ToggleCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCapturing)
            {
                StopCapture();
            }
            else
            {
                StartCapture();
            }
        }

        /// <summary>
        /// 开始捕获
        /// </summary>
        private void StartCapture()
        {
            try
            {
                if (DeviceComboBox.SelectedIndex < 0)
                {
                    MessageBox.Show("请选择网卡", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 更新平台启用状态
                var selectedPlatforms = _platformCheckBoxes
                    .Where(cb => cb.IsChecked == true)
                    .Select(cb => cb.Tag as PlatformConfig)
                    .ToList();

                if (selectedPlatforms.Count == 0)
                {
                    MessageBox.Show("请至少选择一个平台", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                foreach (var platform in _config.Platforms)
                {
                    platform.Enabled = selectedPlatforms.Any(p => p?.Id == platform.Id);
                }

                // 显示选中的平台
                AddLog("========================================");
                AddLog($"📋 已选择 {selectedPlatforms.Count} 个平台:");
                foreach (var p in selectedPlatforms.Where(x => x != null))
                {
                    AddLog($"   - [{p!.Id}] {p.Name}");
                }
                AddLog("========================================");

                // 创建捕获实例
                _packetCapture = new PacketCapture(_config.Platforms, debugFullTraffic: false);
                _packetCapture.OnLog += (s, log) => Dispatcher.Invoke(() => AddLog(log));
                _packetCapture.OnResultFound += (s, result) => Dispatcher.Invoke(() => AddResult(result));

                // 启动HTTPS代理（快手/京东平台）
                bool hasKuaishou = selectedPlatforms.Any(p =>
                    p?.Id?.Equals("kuaishou", StringComparison.OrdinalIgnoreCase) == true);
                bool hasJD = selectedPlatforms.Any(p =>
                    p?.Id?.Equals("jd", StringComparison.OrdinalIgnoreCase) == true);

                if ((hasKuaishou || hasJD) && !_config.General.EnableSSLDecrypt)
                {
                    MessageBox.Show("此平台的 HTTPS 捕获需手动启用。请在程序旁的 platforms.json 中设置 general.enableSSLDecrypt 为 true 后重启。普通抓包不安装证书、不修改系统代理。", "HTTPS 捕获未启用");
                }
                if ((hasKuaishou || hasJD) && _config.General.EnableSSLDecrypt &&
                    MessageBox.Show("启用后将在此电脑信任本地代理证书，并修改系统 HTTP/HTTPS 代理。仅在你有权管理的设备和直播账号上使用。停止捕获后关闭代理；证书需在 Windows 证书管理器中手动移除。是否启用？", "启用本地 HTTPS 捕获", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    // 快手/京东需要HTTPS代理拦截API
                    AddLog("========================================");
                    AddLog($"🔐 启动HTTPS代理（拦截{(hasJD ? "京东" : "快手")}加密流量）");
                    AddLog("========================================");
                    _httpsProxy = new HttpsProxyService(
                        AddLog,
                        (url) =>
                        {
                            // 从HTTP API响应中提取推流地址
                            CaptureResult? result = null;

                            // 检测是京东还是快手（放宽京东匹配条件）
                            if (url.Contains("jdcloud", StringComparison.OrdinalIgnoreCase) ||
                                url.Contains("jd.com", StringComparison.OrdinalIgnoreCase) ||
                                url.Contains("jd.co", StringComparison.OrdinalIgnoreCase) ||
                                url.Contains("jdlive", StringComparison.OrdinalIgnoreCase) ||
                                url.Contains("jcloud", StringComparison.OrdinalIgnoreCase) ||
                                url.Contains("zt-push", StringComparison.OrdinalIgnoreCase))
                            {
                                // 京东推流地址
                                result = ExtractJDFromAPI(url);
                            }
                            else if (url.Contains("kuaishou", StringComparison.OrdinalIgnoreCase) ||
                                     url.Contains("kwai", StringComparison.OrdinalIgnoreCase) ||
                                     url.Contains("gifshow", StringComparison.OrdinalIgnoreCase))
                            {
                                // 快手推流地址
                                result = _packetCapture?.ExtractKuaishouFromAPI(url);
                            }
                            else
                            {
                                // 未知来源但也是推流地址，尝试通用解析
                                AddLog($"[通用] 🔥 捕获到未知来源推流地址: {url}");
                                result = ExtractJDFromAPI(url); // 用京东解析器通用解析
                            }

                            if (result != null)
                            {
                                Dispatcher.Invoke(() => AddResult(result));
                            }
                        });

                    if (_httpsProxy.Start())
                    {
                        AddLog("✅ HTTPS代理已启动");
                        AddLog("");
                    }
                    else
                    {
                        AddLog("⚠️  HTTPS代理启动失败，快手可能无法捕获");
                    }

                    // 启动 Native Hook
                    AddLog("========================================");
                    AddLog("🔧 启动 Native Hook");
                    AddLog("========================================");

                    // 始终启动 Native Hook（双重保障）
                    if (hasKuaishou)
                    {
                        AddLog("========================================");
                        AddLog("🎯 启动原生 Hook（后备方案）");
                        AddLog("========================================");

                        // 检查快手进程是否已运行
                        var kuaishouProcess = _processMonitor?.FindKuaishouProcess();

                        if (kuaishouProcess != null)
                        {
                            // 快手已运行，立即注入
                            AddLog($"✅ 检测到快手进程运行中，准备注入...");

                            // 创建数据分析器
                            _dataAnalyzer = new WinSockDataAnalyzer(
                                AddLog,
                                (platform, url, key) =>
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        AddResult(new CaptureResult
                                        {
                                            Platform = platform,
                                            Protocol = "RTMP",
                                            Url = url,
                                            StreamKey = key
                                        });

                                        // 自动配置OBS多路推流
                                        AutoConfigureObs(platform, url, key);
                                    });
                                });

                            // 使用 Windows API 进程注入器（.NET 8.0 兼容）
                            _processInjector = new NativeProcessInjector(
                                AddLog,
                                (data, isSend) =>
                                {
                                    // 分析捕获的数据
                                    _dataAnalyzer?.AnalyzeData(data, isSend);
                                });

                            if (_processInjector.InjectToKuaishou())
                            {
                                AddLog("✅ 原生 Hook 已启动，等待捕获数据...");
                                AddLog("");
                            }
                            else
                            {
                                AddLog("⚠️  原生 Hook 注入失败，将只使用 HTTPS 代理和数据包捕获");
                                _processInjector?.Dispose();
                                _processInjector = null;
                                _dataAnalyzer = null;
                            }
                        }
                        else
                        {
                            // 快手未运行，等待自动检测
                            AddLog("💡 当前未检测到快手进程");
                            AddLog("📌 提示：启动快手直播伴侣后，将自动执行注入");
                            AddLog("");
                            // 不创建注入器，等待ProcessMonitor的OnKuaishouProcessFound触发
                        }
                    }
                }

                // 判断是监听所有网卡还是单个网卡
                if (DeviceComboBox.SelectedIndex == 0)
                {
                    // 选择了"所有网卡"
                    AddLog("========================================");
                    AddLog("🚀 启动模式：监听所有网卡");
                    AddLog("========================================");
                    _packetCapture.StartCaptureOnAllDevices();
                }
                else
                {
                    // 选择了特定网卡（索引需要减1，因为第一项是"所有网卡"）
                    var actualDeviceIndex = DeviceComboBox.SelectedIndex - 1;
                    AddLog($"🎯 启动模式：监听指定网卡（索引 {actualDeviceIndex}）");
                    _packetCapture.StartCapture(actualDeviceIndex);
                }

                // 更新UI状态
                _isCapturing = true;
                UpdateCaptureButtonState();
                DeviceComboBox.IsEnabled = false;
                foreach (var cb in _platformCheckBoxes)
                {
                    cb.IsEnabled = false;
                }

                StatusText.Text = DeviceComboBox.SelectedIndex == 0 ? "🌐 监听所有网卡中..." : "捕获中...";
                AddLog("✅ 捕获已启动，等待推流数据...");
            }
            catch (Exception ex)
            {
                StopCapture();
                MessageBox.Show($"启动捕获失败: {ex.Message}\n\n提示：\n1. 请以管理员身份运行\n2. 确保已安装Npcap\n3. 检查网络连接",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AddLog($"❌ 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止捕获
        /// </summary>
        private void StopCapture()
        {
            try
            {

                // 停止进程注入器
                if (_processInjector != null)
                {
                    _processInjector.Dispose();
                    _processInjector = null;
                }

                _dataAnalyzer = null;

                // 停止HTTPS代理
                if (_httpsProxy != null)
                {
                    _httpsProxy.Stop();
                    _httpsProxy.Dispose();
                    _httpsProxy = null;
                }

                // 停止网卡捕获
                _packetCapture?.StopCapture();
                _packetCapture?.Dispose();
                _packetCapture = null;

                // 更新UI状态
                _isCapturing = false;
                UpdateCaptureButtonState();
                DeviceComboBox.IsEnabled = true;
                foreach (var cb in _platformCheckBoxes)
                {
                    cb.IsEnabled = true;
                }

                StatusText.Text = "就绪";
                AddLog("停止捕获");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止捕获失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 解析从HTTPS代理中获取的完整RTMP URL
        /// </summary>
        private void ParseProxyRtmpUrl(string fullRtmpUrl)
        {
            try
            {
                // 解析 RTMP URL: rtmp://server/app/streamKey?params
                var rtmpUrlPattern = new System.Text.RegularExpressions.Regex(
                    @"^(rtmps?://[^/]+/[^/\?]+)/([^\?]+)(\?.*)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                var rtmpMatch = rtmpUrlPattern.Match(fullRtmpUrl);

                if (rtmpMatch.Success)
                {
                    string baseUrl = rtmpMatch.Groups[1].Value;
                    string streamKey = rtmpMatch.Groups[2].Value;
                    string queryParams = rtmpMatch.Groups.Count > 3 ? rtmpMatch.Groups[3].Value : "";

                    var result = new CaptureResult
                    {
                        Platform = "快手",
                        Protocol = "RTMP (HTTPS API)",
                        Url = baseUrl,
                        StreamKey = streamKey + queryParams,
                        Parameters = new Dictionary<string, string>(),
                        RawData = fullRtmpUrl,
                        Timestamp = DateTime.Now
                    };

                    AddResult(result);
                }
                else
                {
                    AddLog($"⚠ 无法解析RTMP URL格式: {fullRtmpUrl}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ 解析代理URL失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新捕获按钮的状态（开始/停止）
        /// </summary>
        private void UpdateCaptureButtonState()
        {
            if (_isCapturing)
            {
                ToggleCaptureButton.Content = "停止捕获";
                ToggleCaptureButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f85149"));
            }
            else
            {
                ToggleCaptureButton.Content = "开始捕获";
                ToggleCaptureButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#58a6ff"));
            }
        }

        private void AddResult(CaptureResult result)
        {
            // 严格去重检查：同一平台 + 相同URL + 相同StreamKey 才认为是重复
            var isDuplicate = _results.Any(r =>
                r.Platform == result.Platform &&
                r.Url == result.Url &&
                r.StreamKey == result.StreamKey);

            if (isDuplicate)
            {
                AddLog($"🔄 去重：已存在相同的推流配置 [{result.Platform}]");
                return;
            }

            // 插入到列表顶部
            _results.Insert(0, result);
            ResultCountText.Text = $"({_results.Count})";

            AddLog($"✨ 新增推流配置 [{result.Platform}]: {result.Url}/{result.StreamKey}");

            // 保持列表在合理大小（最多保留100条）
            while (_results.Count > 100)
            {
                _results.RemoveAt(_results.Count - 1);
            }
        }

        private void AddLog(string message)
        {
            // Diagnostics are intentionally not persisted: payloads can contain
            // stream keys, cookies and personally identifying information.
        }

        // 复制推流地址
        private void CopyUrlButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string url)
            {
                CopyToClipboard(url, "推流地址");
            }
        }

        // 复制流密钥
        private void CopyKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string key)
            {
                CopyToClipboard(key, "流密钥");
            }
        }

        // 复制完整地址
        private void CopyFullButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is CaptureResult result)
            {
                var fullUrl = $"{result.Url}{result.StreamKey}";
                CopyToClipboard(fullUrl, "完整推流地址");
            }
        }

        // 填写到OBS按钮点击事件（不自动开始推流）
        private void FillToObsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is CaptureResult result)
            {
                AddLog($"");
                AddLog($"========================================");
                AddLog($"📝 填写到OBS（以平台名称命名：{result.Platform}）");
                AddLog($"========================================");
                AddLog($"平台: {result.Platform}");
                AddLog($"推流地址: {result.Url}");
                AddLog($"流密钥: {result.StreamKey}");
                AddLog($"");

                try
                {
                    if (_obsManager != null)
                    {
                        // autoStart = false，只填写不自动开始
                        bool success = _obsManager.AddStreamTarget(result.Platform, result.Url, result.StreamKey, false);
                        if (success)
                        {
                            AddLog($"✅ 成功配置OBS多路推流");
                            AddLog($"📺 OBS插件将在500ms内自动检测并添加推流目标");
                            AddLog($"💡 点击【开始直播】按钮开始推流");

                            MessageBox.Show(
                                $"✅ 推流配置已填写到OBS！\n\n" +
                                $"平台: {result.Platform}\n" +
                                $"服务器: {result.Url}\n\n" +
                                $"OBS多路推流插件将在500ms内添加推流目标。\n" +
                                $"准备好后，点击【开始直播】按钮开始推流。",
                                "填写成功",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                        else
                        {
                            AddLog($"❌ OBS配置失败");
                            MessageBox.Show("OBS配置失败，请检查日志获取详细信息", "填写失败",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    else
                    {
                        AddLog($"❌ OBS管理器未初始化");
                        MessageBox.Show("OBS管理器未初始化，请重启应用", "填写失败",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"❌ 填写到OBS时出错: {ex.Message}");
                    MessageBox.Show($"填写到OBS时出错: {ex.Message}", "填写失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // 开始直播按钮点击事件
        private void StartStreamButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is CaptureResult result)
            {
                AddLog($"");
                AddLog($"========================================");
                AddLog($"🚀 开始直播: {result.Platform}");
                AddLog($"========================================");

                try
                {
                    if (_obsManager != null)
                    {
                        // 使用平台名称作为目标名称
                        bool success = _obsManager.StartStream(result.Platform);
                        if (success)
                        {
                            AddLog($"✅ 开始直播命令已发送");
                            AddLog($"📺 OBS插件将在500ms内开始推流");

                            MessageBox.Show(
                                $"✅ 开始直播命令已发送！\n\n" +
                                $"平台: {result.Platform}\n\n" +
                                $"OBS多路推流插件将立即开始推流。\n" +
                                $"请查看OBS确认推流状态。",
                                "开始直播",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                        else
                        {
                            AddLog($"❌ 发送开始直播命令失败");
                            MessageBox.Show("发送命令失败，请检查日志获取详细信息", "开始直播失败",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    else
                    {
                        AddLog($"❌ OBS管理器未初始化");
                        MessageBox.Show("OBS管理器未初始化，请重启应用", "开始直播失败",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"❌ 开始直播时出错: {ex.Message}");
                    MessageBox.Show($"开始直播时出错: {ex.Message}", "开始直播失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // 推送到OBS按钮点击事件（保留兼容，自动填写并开始推流）
        private void PushToObsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is CaptureResult result)
            {
                AddLog($"");
                AddLog($"========================================");
                AddLog($"🚀 手动推送到OBS");
                AddLog($"========================================");
                AddLog($"平台: {result.Platform}");
                AddLog($"推流地址: {result.Url}");
                AddLog($"流密钥: {result.StreamKey}");
                AddLog($"");

                try
                {
                    // 调用OBS配置方法，autoStart = true 自动开始推流
                    if (_obsManager != null)
                    {
                        bool success = _obsManager.AddStreamTarget(result.Platform, result.Url, result.StreamKey, true);
                        if (success)
                        {
                            AddLog($"✅ 成功配置OBS多路推流");
                            AddLog($"📺 OBS插件将自动检测并添加推流目标（约500ms）");
                            AddLog($"🚀 推流将自动开始，无需手动操作！");

                            MessageBox.Show(
                                $"✅ 推流配置已发送到OBS！\n\n" +
                                $"平台: {result.Platform}\n" +
                                $"服务器: {result.Url}\n\n" +
                                $"OBS多路推流插件将在500ms内自动检测并开始推流。\n" +
                                $"请查看OBS多路推流窗口确认推流状态。",
                                "推送成功",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                        else
                        {
                            AddLog($"❌ OBS配置失败");
                            MessageBox.Show("OBS配置失败，请检查日志获取详细信息", "推送失败",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    else
                    {
                        AddLog($"❌ OBS管理器未初始化");
                        MessageBox.Show("OBS管理器未初始化，请重启应用", "推送失败",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"❌ 推送到OBS时出错: {ex.Message}");
                    MessageBox.Show($"推送到OBS时出错: {ex.Message}", "推送失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // 复制到剪贴板
        private void CopyToClipboard(string text, string label)
        {
            try
            {
                string? failReason;
                if (!TrySetClipboardText(text, out failReason))
                {
                    throw new InvalidOperationException(failReason ?? "无法打开剪贴板");
                }
                StatusText.Text = $"✅ 已复制{label}";
                AddLog($"复制{label}: {text}");

                // 3秒后恢复状态
                var timer = new System.Windows.Threading.DispatcherTimer();
                timer.Interval = TimeSpan.FromSeconds(3);
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    StatusText.Text = _isCapturing ? "捕获中..." : "就绪";
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"复制失败: {ex.Message}\n\n可能原因: 剪贴板被其他程序占用(如输入法/剪贴板管理器/远程桌面)。请关闭占用程序后重试。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 在独立STA线程上执行Clipboard操作，并返回明确失败原因
        private bool TrySetClipboardText(string text, out string? failReason)
        {
            failReason = null;
            bool ok = false;
            Exception? captured = null;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    // SetDataObject(true) 确保应用退出后内容仍在剪贴板
                    Clipboard.SetDataObject(text, true);
                    ok = true;
                }
                catch (COMException ex)
                {
                    captured = ex;
                }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(1500);

            if (!ok)
            {
                if (captured is COMException comEx && (uint)comEx.HResult == 0x800401D0)
                {
                    // Fallback: Win32原生API强制设置
                    if (NativeSetClipboardText(text))
                    {
                        return true;
                    }
                    var owner = GetClipboardOwnerProcess();
                    failReason = owner != null
                        ? $"剪贴板被其它进程长时间占用: {owner} (CLIPBRD_E_CANT_OPEN)"
                        : "剪贴板被其它进程长时间占用 (CLIPBRD_E_CANT_OPEN)";
                }
                else if (captured != null)
                {
                    failReason = captured.Message;
                }
                else
                {
                    failReason = "未知原因导致写入超时";
                }
            }
            return ok;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetOpenClipboardWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private string? GetClipboardOwnerProcess()
        {
            try
            {
                var hWnd = GetOpenClipboardWindow();
                if (hWnd == IntPtr.Zero) return null;
                GetWindowThreadProcessId(hWnd, out uint pid);
                var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                return $"{proc.ProcessName} (PID={pid})";
            }
            catch
            {
                return null;
            }
        }

        // ===== 原生Win32剪贴板写入（避免COM层失败） =====
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        private bool NativeSetClipboardText(string text)
        {
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = new WindowInteropHelper(this).Handle; } catch { }

            for (int i = 0; i < 5; i++)
            {
                if (OpenClipboard(hwnd))
                {
                    try
                    {
                        if (!EmptyClipboard()) return false;
                        // 以双零结尾的UTF-16
                        var bytes = (text.Length + 1) * 2; // 含末尾\0
                        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                        if (hGlobal == IntPtr.Zero) return false;
                        IntPtr pGlobal = GlobalLock(hGlobal);
                        if (pGlobal == IntPtr.Zero) return false;
                        try
                        {
                            Marshal.Copy(text.ToCharArray(), 0, pGlobal, text.Length);
                            Marshal.WriteInt16(pGlobal, text.Length * 2, 0);
                        }
                        finally
                        {
                            GlobalUnlock(hGlobal);
                        }
                        if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                        {
                            return false;
                        }
                        return true;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                System.Threading.Thread.Sleep(80);
            }
            return false;
        }

        private void ClearResultsButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确定要清空所有结果吗?", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _results.Clear();
                ResultCountText.Text = "(0)";
                StatusText.Text = "已清空结果";
                AddLog("清空所有结果");
            }
        }

        private void ConfigButton_Click(object sender, RoutedEventArgs e)
        {
            var configWindow = new ConfigWindow(_config);
            if (configWindow.ShowDialog() == true)
            {
                _config = ConfigManager.LoadConfig();
                InitializePlatformCheckBoxes();
                AddLog("配置已更新");
            }
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "感谢使用",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // 点击二维码弹出原图查看

        #region WebRTC监控已移除
        // WebRTC/UDP监控功能已移除，专注于RTMP URL捕获
        #endregion

        /// <summary>
        /// 从京东API响应中提取推流地址（放宽匹配）
        /// </summary>
        private CaptureResult? ExtractJDFromAPI(string rtmpUrl)
        {
            try
            {
                AddLog($"[京东] 🔥 从代理捕获到推流地址: {rtmpUrl}");

                // 格式1: rtmp://zt-push.jdcloud.com/live/example-stream?auth_key=REDACTED
                var match = System.Text.RegularExpressions.Regex.Match(rtmpUrl,
                    @"(rtmps?://[^/]+/[^/]+)/([^\s""'<>]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    string baseUrl = match.Groups[1].Value + "/";
                    string streamKey = match.Groups[2].Value;

                    AddLog($"[京东] ✅ 解析完成:");
                    AddLog($"[京东]   服务器: {baseUrl}");
                    AddLog($"[京东]   密钥: {streamKey}");

                    return new CaptureResult
                    {
                        Platform = "京东",
                        Protocol = "RTMP",
                        Url = baseUrl,
                        StreamKey = streamKey
                    };
                }

                // 后备：尝试 Uri 解析（支持任意路径格式）
                try
                {
                    var uri = new Uri(rtmpUrl);
                    string pathSegments = uri.AbsolutePath.TrimStart('/');
                    int firstSlash = pathSegments.IndexOf('/');

                    string fallbackBaseUrl;
                    string fallbackStreamKey;

                    if (firstSlash > 0)
                    {
                        // 有路径层级：scheme://host/app/ + stream_key?query
                        string app = pathSegments.Substring(0, firstSlash);
                        fallbackBaseUrl = $"{uri.Scheme}://{uri.Host}/{app}/";
                        fallbackStreamKey = pathSegments.Substring(firstSlash + 1) + uri.Query;
                    }
                    else
                    {
                        // 没有路径层级
                        fallbackBaseUrl = $"{uri.Scheme}://{uri.Host}/";
                        fallbackStreamKey = pathSegments + uri.Query;
                    }
                    fallbackStreamKey = fallbackStreamKey.TrimStart('/');

                    AddLog($"[京东] ✅ 后备解析完成:");
                    AddLog($"[京东]   服务器: {fallbackBaseUrl}");
                    AddLog($"[京东]   密钥: {fallbackStreamKey}");

                    return new CaptureResult
                    {
                        Platform = "京东",
                        Protocol = "RTMP",
                        Url = fallbackBaseUrl,
                        StreamKey = fallbackStreamKey
                    };
                }
                catch
                {
                    // Uri 解析也失败，直接返回原始 URL
                    AddLog($"[京东] ⚠ Uri 解析失败，直接使用原始地址");
                    return new CaptureResult
                    {
                        Platform = "京东",
                        Protocol = "RTMP",
                        Url = rtmpUrl,
                        StreamKey = ""
                    };
                }
            }
            catch (Exception ex)
            {
                AddLog($"[京东] ❌ 解析推流地址失败: {ex.Message}");
                return null;
            }
        }

        #region 窗口控制按钮
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
        #endregion

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _httpsProxy?.Stop();
            _processInjector?.Dispose();
            _packetCapture?.StopCapture();
            _packetCapture?.Dispose();

            // 停止进程监控
            _processMonitor?.Dispose();

            AddLog("========================================");
            AddLog($"程序退出，完整日志已保存到:");
            AddLog(_logFilePath);
            AddLog("========================================");

            base.OnClosing(e);
        }
    }
}
