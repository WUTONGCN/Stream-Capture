using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StreamCapture.Core
{
    /// <summary>
    /// 进程监控服务 - 自动检测快手直播伴侣进程
    /// </summary>
    public class ProcessMonitor : IDisposable
    {
        private readonly Action<string> _logger;
        private readonly Action<Process> _onProcessFound;
        private readonly Action<int> _onProcessExited;
        private CancellationTokenSource? _cts;
        private Task? _monitorTask;
        private Process? _currentMonitoredProcess;

        private static readonly string[] PossibleProcessNames = new[]
        {
            "kwai",
            "kuaishou",
            "快手",
            "KwaiLive",
            "KuaishouLive",
            "KSLive",
            "ks_live"
        };

        public bool IsMonitoring => _cts != null && !_cts.IsCancellationRequested;
        public Process? CurrentProcess => _currentMonitoredProcess;

        public ProcessMonitor(Action<string> logger, Action<Process> onProcessFound, Action<int>? onProcessExited = null)
        {
            _logger = logger;
            _onProcessFound = onProcessFound;
            _onProcessExited = onProcessExited ?? (_ => { });
        }

        /// <summary>
        /// 立即查找快手进程（单次检测）
        /// </summary>
        public Process? FindKuaishouProcess()
        {
            try
            {
                foreach (var name in PossibleProcessNames)
                {
                    var processes = Process.GetProcesses()
                        .Where(p => !string.IsNullOrEmpty(p.ProcessName) &&
                                   p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (processes.Any())
                    {
                        var process = processes.First();
                        _logger($"✅ 检测到快手进程: {process.ProcessName} (PID: {process.Id})");
                        return process;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger($"⚠️  进程查找异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 启动进程监控（持续监控）
        /// </summary>
        public void StartMonitoring(int intervalSeconds = 3)
        {
            if (IsMonitoring)
            {
                _logger("⚠️  进程监控已在运行");
                return;
            }

            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoop(intervalSeconds, _cts.Token), _cts.Token);
            _logger($"🔍 已启动进程监控 (间隔: {intervalSeconds}秒)");
        }

        /// <summary>
        /// 停止进程监控
        /// </summary>
        public void StopMonitoring()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                try
                {
                    _monitorTask?.Wait(2000);
                }
                catch (OperationCanceledException) { /* Expected */ }
                catch (Exception ex)
                {
                    _logger($"⚠️  停止监控异常: {ex.Message}");
                }
                _logger("🛑 已停止进程监控");
            }
        }

        private async Task MonitorLoop(int intervalSeconds, CancellationToken cancellationToken)
        {
            bool wasFound = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 检查当前监控的进程是否还存在
                    if (_currentMonitoredProcess != null)
                    {
                        try
                        {
                            if (_currentMonitoredProcess.HasExited)
                            {
                                var pid = _currentMonitoredProcess.Id;
                                _logger($"⚠️  快手进程已退出 (PID: {pid})");
                                _onProcessExited?.Invoke(pid);
                                _currentMonitoredProcess = null;
                                wasFound = false;
                            }
                        }
                        catch
                        {
                            // 进程可能已经不存在
                            _currentMonitoredProcess = null;
                            wasFound = false;
                        }
                    }

                    // 查找新进程
                    if (_currentMonitoredProcess == null)
                    {
                        var process = FindKuaishouProcess();

                        if (process != null && !wasFound)
                        {
                            _currentMonitoredProcess = process;
                            wasFound = true;

                            _logger("");
                            _logger("========================================");
                            _logger("🎯 检测到快手直播伴侣启动！");
                            _logger($"📌 进程名: {process.ProcessName}");
                            _logger($"📌 进程ID: {process.Id}");
                            _logger("💡 建议：点击【开始捕获】按钮进行监控");
                            _logger("========================================");
                            _logger("");

                            _onProcessFound?.Invoke(process);
                        }
                        else if (process == null && wasFound)
                        {
                            wasFound = false;
                        }
                    }

                    await Task.Delay(intervalSeconds * 1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger($"⚠️  监控循环异常: {ex.Message}");
                    await Task.Delay(intervalSeconds * 1000, cancellationToken);
                }
            }
        }

        /// <summary>
        /// 获取所有可能的快手进程列表
        /// </summary>
        public static string[] GetPossibleProcessNames() => PossibleProcessNames;

        public void Dispose()
        {
            StopMonitoring();
            _currentMonitoredProcess = null;
        }
    }
}
