using System;
using System.Windows;

namespace StreamCapture
{
    /// <summary>
    /// OBS推流提示窗口
    /// </summary>
    public partial class ObsStreamPromptWindow : Window
    {
        public ObsStreamPromptWindow(string platform, string targetName)
        {
            InitializeComponent();

            // 设置目标名称
            TargetNameRun.Text = targetName;

            // 设置窗口标题
            Title = $"{platform} - OBS推流提示";
        }

        private void OnOKClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
