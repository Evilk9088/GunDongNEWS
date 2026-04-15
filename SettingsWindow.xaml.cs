using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace 桌面新闻
{
    public partial class SettingsWindow : Window
    {
        private MainWindow _mainWindow;
        private AppConfig _config;

        public SettingsWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;

            // 拦截关闭事件，点 X 只是隐藏到托盘
            this.Closing += (s, e) =>
            {
                e.Cancel = true;
                this.Hide();
            };

            // 每次窗口被重新激活（比如从托盘双击打开），都重新加载一次配置，确保数据是最新的
            this.Activated += (s, e) => LoadConfigData();

            // 首次加载数据
            LoadConfigData();
        }

        /// <summary>
        /// 从 config.json 读取数据并填充到UI界面
        /// </summary>
        private void LoadConfigData()
        {
            _config = ConfigService.LoadConfig();

            // 1. 基础设置绑定
            ChkIsVisible.IsChecked = _config.IsVisible;
            ChkStartMinimized.IsChecked = _config.StartMinimized;
            CmbMode.SelectedIndex = _config.Mode == "News" ? 0 : 1;
            TxtInterval.Text = _config.RefreshIntervalMinutes.ToString();

            // 2. 数据源管理绑定 (WPF 魔法：直接把 List 塞给 ItemSource，它会自动生成表格内容！)
            ApiDataGrid.ItemsSource = _config.ApiEndpoints;

            // 3. 黑名单绑定 (把 List 转换成以回车分隔的文本)
            TxtBlacklist.Text = string.Join(Environment.NewLine, _config.KeywordBlacklist);
        }

        /// <summary>
        /// 用户点击“保存并应用”按钮
        /// </summary>
        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 提取基础设置
                _config.IsVisible = ChkIsVisible.IsChecked == true;
                _config.StartMinimized = ChkStartMinimized.IsChecked == true;
                _config.Mode = (CmbMode.SelectedItem as ComboBoxItem)?.Tag.ToString();

                if (int.TryParse(TxtInterval.Text, out int interval))
                {
                    _config.RefreshIntervalMinutes = interval;
                }
                else
                {
                    System.Windows.MessageBox.Show("刷新间隔必须是有效的数字！", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 2. 数据源 (ApiEndpoints) 不需要手动提取！
                // 因为 DataGrid 是双向绑定的，你在表格里打勾、改数字，它已经自动实时修改了 _config.ApiEndpoints 里的对象！

                // 3. 提取黑名单 (把文本框内容按回车劈开，过滤掉空行，重新变成 List)
                _config.KeywordBlacklist = TxtBlacklist.Text
                    .Split(new[] { Environment.NewLine, "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim()) // 去掉每行前后的多余空格
                    .Where(line => !string.IsNullOrWhiteSpace(line)) // 再次确保是有效行
                    .ToList();

                // 4. 将所有修改保存到 config.json 文件
                ConfigService.SaveConfig(_config);

                // 5. 实时应用到滚动条主程序
                _mainWindow.Visibility = _config.IsVisible ? Visibility.Visible : Visibility.Collapsed;
                if (_config.IsVisible)
                {
                    // 通知主窗口热重载
                    await _mainWindow.ApplyNewConfigAsync();
                }

                // 6. 保存完顺手把配置窗口隐藏进托盘，干净利落
                this.Hide();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"保存配置时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}