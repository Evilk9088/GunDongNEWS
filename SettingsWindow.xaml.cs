using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

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
// 在 SettingsWindow.xaml.cs 中

        // 替换现有的 LoadConfigData 方法
        private void LoadConfigData()
        {
            _config = ConfigService.LoadConfig();

            // 基础设置
            ChkIsVisible.IsChecked = _config.IsVisible;
            ChkStartMinimized.IsChecked = _config.StartMinimized;
            CmbMode.SelectedIndex = _config.Mode == "News" ? 0 : 1;
            TxtNovelPath.Text = _config.NovelFilePath;
            TxtInterval.Text = _config.RefreshIntervalMinutes.ToString();

            // 新增：外观与位置绑定
            SliderSpeed.Value = _config.ScrollSpeed;
            SliderFontSize.Value = _config.FontSize;
            TxtTop.Text = _config.Top.ToString("F0");
            TxtLeft.Text = _config.Left.ToString("F0");
            ChkPositionLocked.IsChecked = _config.IsPositionLocked;

            // 数据源和黑名单保持不变
            ApiDataGrid.ItemsSource = _config.ApiEndpoints;
            TxtBlacklist.Text = string.Join(Environment.NewLine, _config.KeywordBlacklist);
        }

        // 替换现有的 BtnSave_Click 方法
        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 提取基础设置
                _config.IsVisible = ChkIsVisible.IsChecked == true;
                _config.StartMinimized = ChkStartMinimized.IsChecked == true;
                _config.Mode = (CmbMode.SelectedItem as ComboBoxItem)?.Tag.ToString();
                _config.NovelFilePath = TxtNovelPath.Text;

                if (!int.TryParse(TxtInterval.Text, out int interval)) { /*...*/ }
                _config.RefreshIntervalMinutes = interval;

                // 2. 新增：提取外观与位置设置
                _config.ScrollSpeed = SliderSpeed.Value;
                _config.FontSize = (int)SliderFontSize.Value;
                _config.IsPositionLocked = ChkPositionLocked.IsChecked == true;

                if (!double.TryParse(TxtTop.Text, out double top) || !double.TryParse(TxtLeft.Text, out double left))
                {
                    System.Windows.MessageBox.Show("位置坐标必须是有效的数字！", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _config.Top = top;
                _config.Left = left;

                // 3. 提取黑名单 (使用最稳健的方式)
                _config.KeywordBlacklist = TxtBlacklist.Text
                    // 先按换行符劈开成数组，允许产生空条目
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.None)
                    // 接着，使用 .Where 这一终极过滤器，它会：
                    // a) 过滤掉 null
                    // b) 过滤掉 "" (空字符串)
                    // c) 过滤掉 "   " (只包含空格、Tab等的字符串)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    // 最后，为确保万无一失，去掉每个有效词条前后的多余空格
                    .Select(line => line.Trim())
                    .ToList();

                // 4. 保存到文件
                ConfigService.SaveConfig(_config);

                // 5. 实时应用
                if (_config.IsVisible)
                {
                    await _mainWindow.ApplyNewConfigAsync();
                }
                else
                {
                    _mainWindow.Visibility = Visibility.Collapsed;
                }

                //this.Hide();
                System.Windows.MessageBox.Show("配置已保存，滚动条已实时应用新设置！", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"保存配置时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "请选择一个文本文件作为小说源",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*" // 筛选文件类型
            };

            // 如果用户选择了文件并点击了“打开”
            if (openFileDialog.ShowDialog() == true)
            {
                // 将选择的文件完整路径更新到文本框中
                TxtNovelPath.Text = openFileDialog.FileName;
            }
        }
    }
}