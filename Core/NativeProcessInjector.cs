using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StreamCapture.Core
{
    /// <summary>
    /// 基于 Windows API 的进程注入器（支持 .NET 8.0）
    /// </summary>
    public class NativeProcessInjector : IDisposable
    {
        private readonly Action<string> _logger;
        private readonly Action<byte[], bool> _onDataCaptured;
        private Process? _targetProcess;
        private NamedPipeIpcServer? _ipcServer;
        private string? _tempDllPath; // 临时DLL文件路径

        #region Windows API 声明

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            ProcessAccessFlags processAccess,
            bool bInheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(
            IntPtr hProcess,
            IntPtr lpAddress,
            uint dwSize,
            AllocationType flAllocationType,
            MemoryProtection flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            byte[] lpBuffer,
            uint nSize,
            out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(
            IntPtr hProcess,
            IntPtr lpThreadAttributes,
            uint dwStackSize,
            IntPtr lpStartAddress,
            IntPtr lpParameter,
            uint dwCreationFlags,
            out IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [Flags]
        private enum ProcessAccessFlags : uint
        {
            All = 0x001F0FFF,
            Terminate = 0x00000001,
            CreateThread = 0x00000002,
            VirtualMemoryOperation = 0x00000008,
            VirtualMemoryRead = 0x00000010,
            VirtualMemoryWrite = 0x00000020,
            DuplicateHandle = 0x00000040,
            CreateProcess = 0x000000080,
            SetQuota = 0x00000100,
            SetInformation = 0x00000200,
            QueryInformation = 0x00000400,
            QueryLimitedInformation = 0x00001000,
            Synchronize = 0x00100000
        }

        [Flags]
        private enum AllocationType : uint
        {
            Commit = 0x1000,
            Reserve = 0x2000,
            Decommit = 0x4000,
            Release = 0x8000,
            Reset = 0x80000,
            Physical = 0x400000,
            TopDown = 0x100000,
            WriteWatch = 0x200000,
            LargePages = 0x20000000
        }

        [Flags]
        private enum MemoryProtection : uint
        {
            Execute = 0x10,
            ExecuteRead = 0x20,
            ExecuteReadWrite = 0x40,
            ExecuteWriteCopy = 0x80,
            NoAccess = 0x01,
            ReadOnly = 0x02,
            ReadWrite = 0x04,
            WriteCopy = 0x08,
            GuardModifierflag = 0x100,
            NoCacheModifierflag = 0x200,
            WriteCombineModifierflag = 0x400
        }

        #endregion

        public NativeProcessInjector(Action<string> logger, Action<byte[], bool> onDataCaptured)
        {
            _logger = logger;
            _onDataCaptured = onDataCaptured;
        }

        /// <summary>
        /// 从嵌入资源或本地文件提取 Hook DLL
        /// </summary>
        private string? ExtractHookDllFromResource()
        {
            try
            {
                // 方案1：尝试从嵌入资源提取（避免文件占用问题）
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = "StreamCapture.KuaishouHook.dll";

                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        // 提取到临时目录，使用进程ID确保唯一性
                        _tempDllPath = Path.Combine(Path.GetTempPath(), $"KuaishouHook_{Process.GetCurrentProcess().Id}.dll");

                        using (var fileStream = new FileStream(_tempDllPath, FileMode.Create, FileAccess.Write))
                        {
                            stream.CopyTo(fileStream);
                        }

                        _logger($"✅ 从嵌入资源提取 Hook DLL 到临时目录");
                        _logger($"   临时路径: {_tempDllPath}");
                        return _tempDllPath;
                    }
                }

                // 方案2：使用本地文件（开发环境）
                var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KuaishouHook.dll");
                if (File.Exists(localPath))
                {
                    _logger($"✅ 使用本地 Hook DLL 文件");
                    return localPath;
                }

                _logger("❌ 未找到 Hook DLL（嵌入资源或本地文件）");
                return null;
            }
            catch (Exception ex)
            {
                _logger($"❌ 提取 Hook DLL 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 查找快手进程并注入
        /// </summary>
        public bool InjectToKuaishou()
        {
            try
            {
                _logger("========================================");
                _logger("🎯 查找快手直播伴侣进程...");
                _logger("========================================");

                // 查找快手进程（可能的进程名）
                var possibleNames = new[]
                {
                    "kwai",
                    "kuaishou",
                    "快手",
                    "KwaiLive",
                    "KuaishouLive"
                };

                _targetProcess = null;
                foreach (var name in possibleNames)
                {
                    var processes = Process.GetProcesses()
                        .Where(p => p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (processes.Any())
                    {
                        // 选择内存占用最大的进程（通常是主进程）
                        _targetProcess = processes.OrderByDescending(p => p.WorkingSet64).First();

                        _logger($"✅ 找到 {processes.Count} 个快手进程，选择主进程:");
                        _logger($"   进程名: {_targetProcess.ProcessName}");
                        _logger($"   PID: {_targetProcess.Id}");
                        _logger($"   内存: {_targetProcess.WorkingSet64 / 1024 / 1024} MB");

                        // 显示所有进程供参考
                        if (processes.Count > 1)
                        {
                            _logger($"");
                            _logger($"   其他进程:");
                            foreach (var p in processes.Where(p => p.Id != _targetProcess.Id))
                            {
                                _logger($"   - PID {p.Id}: {p.WorkingSet64 / 1024 / 1024} MB");
                            }
                        }

                        break;
                    }
                }

                if (_targetProcess == null)
                {
                    _logger("⚠️  未找到快手直播伴侣进程");
                    _logger("💡 请先启动快手直播伴侣，然后重新点击【开始捕获】");
                    _logger("");
                    _logger("支持的进程名：");
                    foreach (var name in possibleNames)
                    {
                        _logger($"   - {name}*.exe");
                    }
                    return false;
                }

                // 启动 IPC 服务器
                _ipcServer = new NamedPipeIpcServer(_logger, _onDataCaptured);
                _ipcServer.Start();

                _logger("✅ IPC 服务器已启动");

                // 获取注入 DLL 路径（优先从嵌入资源提取，避免文件占用）
                var dllPath = ExtractHookDllFromResource();

                if (string.IsNullOrEmpty(dllPath))
                {
                    _logger("⚠️  未找到 Hook DLL");
                    _logger("💡 请先编译 Native Hook DLL");
                    _logger("   运行: cd NativeHook && compile.bat");
                    _logger("");
                    _logger("⚠️  将只使用 HTTPS 代理和数据包捕获方式");
                    return false;
                }

                _logger($"📦 Hook DLL: {dllPath}");

                // 注入 DLL 到目标进程
                _logger($"🚀 正在注入 DLL 到进程 {_targetProcess.ProcessName} (PID: {_targetProcess.Id})...");

                if (!InjectDll(_targetProcess.Id, dllPath))
                {
                    _logger("❌ DLL 注入失败");
                    return false;
                }

                // 等待 DLL 初始化
                Thread.Sleep(1000);

                _logger("========================================");
                _logger("✅ DLL 注入成功！");
                _logger("💡 Hook 已激活，正在监控 WinSock 调用...");
                _logger("========================================");
                _logger("");

                return true;
            }
            catch (Exception ex)
            {
                _logger($"❌ 进程注入失败: {ex.Message}");
                _logger($"   详情: {ex.GetType().Name}");

                if (ex.InnerException != null)
                {
                    _logger($"   内部错误: {ex.InnerException.Message}");
                }

                _logger("");
                _logger("💡 可能的原因：");
                _logger("   1. 权限不足（需要管理员权限）");
                _logger("   2. 杀毒软件拦截");
                _logger("   3. 快手进程架构不匹配（32/64位）");
                _logger("   4. 快手进程有反注入保护");
                _logger("");
                _logger("💡 建议：使用 HTTPS 代理或数据包捕获方式");

                return false;
            }
        }

        /// <summary>
        /// 注入 DLL 到目标进程（原生方法）
        /// </summary>
        private bool InjectDll(int processId, string dllPath)
        {
            IntPtr hProcess = IntPtr.Zero;
            IntPtr allocMemAddress = IntPtr.Zero;

            try
            {
                // 1. 打开目标进程
                hProcess = OpenProcess(
                    ProcessAccessFlags.CreateThread |
                    ProcessAccessFlags.QueryInformation |
                    ProcessAccessFlags.VirtualMemoryOperation |
                    ProcessAccessFlags.VirtualMemoryWrite |
                    ProcessAccessFlags.VirtualMemoryRead,
                    false,
                    processId);

                if (hProcess == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger($"❌ 打开进程失败，错误码: {error}");
                    return false;
                }

                // 2. 在目标进程中分配内存
                var dllPathBytes = Encoding.Unicode.GetBytes(dllPath);
                allocMemAddress = VirtualAllocEx(
                    hProcess,
                    IntPtr.Zero,
                    (uint)((dllPathBytes.Length + 1) * Marshal.SizeOf(typeof(char))),
                    AllocationType.Commit | AllocationType.Reserve,
                    MemoryProtection.ReadWrite);

                if (allocMemAddress == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger($"❌ 分配内存失败，错误码: {error}");
                    return false;
                }

                // 3. 写入 DLL 路径到目标进程
                if (!WriteProcessMemory(hProcess, allocMemAddress, dllPathBytes, (uint)dllPathBytes.Length, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger($"❌ 写入内存失败，错误码: {error}");
                    return false;
                }

                // 4. 获取 LoadLibraryW 地址
                var kernel32Handle = GetModuleHandle("kernel32.dll");
                var loadLibraryAddr = GetProcAddress(kernel32Handle, "LoadLibraryW");

                if (loadLibraryAddr == IntPtr.Zero)
                {
                    _logger("❌ 获取 LoadLibraryW 地址失败");
                    return false;
                }

                // 5. 创建远程线程加载 DLL
                var hThread = CreateRemoteThread(
                    hProcess,
                    IntPtr.Zero,
                    0,
                    loadLibraryAddr,
                    allocMemAddress,
                    0,
                    out _);

                if (hThread == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger($"❌ 创建远程线程失败，错误码: {error}");
                    return false;
                }

                // 6. 等待线程完成
                WaitForSingleObject(hThread, 5000);
                CloseHandle(hThread);

                _logger("✅ DLL 注入成功！");
                return true;
            }
            catch (Exception ex)
            {
                _logger($"❌ DLL 注入异常: {ex.Message}");
                return false;
            }
            finally
            {
                if (hProcess != IntPtr.Zero)
                {
                    CloseHandle(hProcess);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _logger("🛑 停止进程注入器...");
                _ipcServer?.Stop();
                _targetProcess = null;

                // 清理临时 DLL 文件
                if (!string.IsNullOrEmpty(_tempDllPath) && File.Exists(_tempDllPath))
                {
                    try
                    {
                        File.Delete(_tempDllPath);
                        _logger($"🗑️  已清理临时 DLL 文件");
                    }
                    catch (Exception ex)
                    {
                        // 临时文件被占用是正常的，进程退出后会自动清理
                        _logger($"ℹ️  临时 DLL 文件将在系统重启后清理: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"⚠️  清理异常: {ex.Message}");
            }
        }
    }
}
