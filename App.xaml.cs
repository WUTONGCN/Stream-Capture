using System;
using System.IO;
using System.Windows;
using NLog;
using NLog.Config;
using NLog.Targets;
using StreamCapture.Core;

namespace StreamCapture
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            CheckAndInstallNpcap();

            // 恢复为主窗口关闭时退出
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }

        /// <summary>
        /// 检测并引导安装 Npcap
        /// </summary>
        private void CheckAndInstallNpcap()
        {
            try
            {
                // 检查是否已安装
                if (NpcapInstaller.IsInstalled())
                {
                    var version = NpcapInstaller.GetInstalledVersion();
                    LogManager.GetCurrentClassLogger().Info($"Npcap 已安装，版本: {version ?? "未知"}");
                    return;
                }

                LogManager.GetCurrentClassLogger().Warn("未检测到 Npcap");

                // 友好提示并引导用户下载
                var result = MessageBox.Show(
                    "检测到未安装 Npcap 驱动程序\n\n" +
                    "Npcap 是网络抓包必需的驱动程序，用于捕获网络数据包。\n\n" +
                    "📥 安装步骤：\n" +
                    "  1. 点击【是】打开 Npcap 官方下载页面\n" +
                    "  2. 下载最新版 Npcap 安装包\n" +
                    "  3. 运行安装包完成安装\n" +
                    "  4. 重新启动本程序\n\n" +
                    "⚠ 注意：安装 Npcap 需要管理员权限\n\n" +
                    "点击【是】打开官网下载，点击【否】退出程序",
                    "需要安装 Npcap",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    NpcapInstaller.OpenDownloadPage();
                }

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error(ex, "Npcap 检测失败");
            }
        }

    }
}
