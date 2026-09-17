using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace StreamCapture.Core
{
    /// <summary>
    /// Npcap 检测与安装助手（需要 OEM 许可）
    /// </summary>
    public static class NpcapInstaller
    {
        private const string NpcapRegistryKey = @"SOFTWARE\Npcap";
        private const string NpcapDriverPath = @"C:\Windows\System32\drivers\npcap.sys";

        /// <summary>
        /// 检测 Npcap 是否已安装
        /// </summary>
        public static bool IsInstalled()
        {
            try
            {
                // 方法1：检查注册表
                using (var key = Registry.LocalMachine.OpenSubKey(NpcapRegistryKey))
                {
                    if (key != null)
                    {
                        var version = key.GetValue("Version");
                        return version != null;
                    }
                }

                // 方法2：检查驱动文件
                if (File.Exists(NpcapDriverPath))
                    return true;

                // 方法3：尝试列举设备（SharpPcap）
                var devices = SharpPcap.CaptureDeviceList.Instance;
                return devices != null && devices.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取已安装的 Npcap 版本
        /// </summary>
        public static string? GetInstalledVersion()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(NpcapRegistryKey))
                {
                    return key?.GetValue("Version")?.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 检查是否以管理员身份运行
        /// </summary>
        public static bool IsRunningAsAdmin()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从嵌入资源提取 Npcap 安装包并静默安装
        /// </summary>
        /// <param name="onProgress">进度回调</param>
        /// <returns>安装是否成功</returns>
        public static bool InstallFromEmbeddedResource(Action<string>? onProgress = null)
        {
            try
            {
                onProgress?.Invoke("正在检查管理员权限...");

                // 必须以管理员身份运行
                if (!IsRunningAsAdmin())
                {
                    onProgress?.Invoke("❌ 错误：需要管理员权限安装 Npcap");
                    return false;
                }

                onProgress?.Invoke("正在提取 Npcap 安装包...");

                // 从嵌入资源中查找 Npcap 安装包
                var assembly = Assembly.GetExecutingAssembly();
                var resourceNames = assembly.GetManifestResourceNames();
                string? npcapResourceName = null;

                foreach (var name in resourceNames)
                {
                    if (name.Contains("npcap") && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        npcapResourceName = name;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(npcapResourceName))
                {
                    onProgress?.Invoke("❌ 错误：未找到嵌入的 Npcap 安装包");
                    return false;
                }

                // 提取到临时文件
                var tempPath = Path.Combine(Path.GetTempPath(), "npcap-installer.exe");

                using (var stream = assembly.GetManifestResourceStream(npcapResourceName))
                {
                    if (stream == null)
                    {
                        onProgress?.Invoke("❌ 错误：无法读取 Npcap 安装包");
                        return false;
                    }

                    using (var fileStream = File.Create(tempPath))
                    {
                        stream.CopyTo(fileStream);
                    }
                }

                onProgress?.Invoke($"✓ 已提取到: {tempPath}");
                onProgress?.Invoke("正在静默安装 Npcap（这可能需要几分钟）...");

                // 静默安装 Npcap
                // 参数说明：
                // /S - 静默安装
                // /winpcap_mode=yes - WinPcap 兼容模式
                // /loopback_support=yes - 启用环回支持
                // /dlt_null=no - 禁用 DLT_NULL
                // /admin_only=no - 允许非管理员用户使用
                // /dot11_support=no - 禁用 802.11 原始帧捕获（可选）
                var startInfo = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/S /winpcap_mode=yes /loopback_support=yes /admin_only=no",
                    UseShellExecute = true,    // ✅ 必须为 true 才能使用 Verb="runas"
                    CreateNoWindow = true,
                    Verb = "runas"             // 以管理员身份运行
                };

                var process = Process.Start(startInfo);
                if (process == null)
                {
                    onProgress?.Invoke("❌ 错误：无法启动安装程序");
                    return false;
                }

                // 等待安装完成（最多5分钟）
                if (!process.WaitForExit(300000)) // 5分钟超时
                {
                    onProgress?.Invoke("⚠ 警告：安装超时");
                    process.Kill();
                    return false;
                }

                var exitCode = process.ExitCode;

                // 清理临时文件
                try
                {
                    File.Delete(tempPath);
                }
                catch { }

                if (exitCode == 0)
                {
                    onProgress?.Invoke("✅ Npcap 安装成功！");
                    return true;
                }
                else
                {
                    onProgress?.Invoke($"❌ 安装失败，退出代码: {exitCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"❌ 安装异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 以管理员身份重启当前程序
        /// </summary>
        public static void RestartAsAdmin()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule?.FileName ?? "",
                    UseShellExecute = true,
                    Verb = "runas" // 以管理员身份运行
                };

                Process.Start(startInfo);
                Environment.Exit(0);
            }
            catch
            {
                // 用户取消了 UAC 提示
            }
        }

        /// <summary>
        /// 打开 Npcap 官方下载页面
        /// </summary>
        public static void OpenDownloadPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://npcap.com/#download",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
