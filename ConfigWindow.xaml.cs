using System.Windows;
using StreamCapture.Core;
using StreamCapture.Models;

namespace StreamCapture
{
    public partial class ConfigWindow : Window
    {
        private readonly AppConfig _config;

        public ConfigWindow(AppConfig config)
        {
            InitializeComponent();
            _config = config;
            PlatformItemsControl.ItemsSource = _config.Platforms;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConfigManager.SaveConfig(_config);
                MessageBox.Show("配置保存成功!", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"保存配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
