using System;
using System.Drawing; // 如果报错，在头部 using System.Drawing;
using System.Windows;
using System.Windows.Forms; // 引入 WinForms 托盘支持
using Application = System.Windows.Application;

namespace 桌面新闻
{
    public partial class App : Application
    {
        private NotifyIcon _notifyIcon;
        private MainWindow _mainWindow;
        private SettingsWindow _settingsWindow;
        private AppConfig _config;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            _config = ConfigService.LoadConfig();

            // 1. 初始化系统托盘图标
            InitNotifyIcon();

            // 2. 初始化滚动条 (仅作纯显示用)
            _mainWindow = new MainWindow();
            if (_config.IsVisible)
            {
                _mainWindow.Show();
            }

            // 3. 初始化配置窗口
            _settingsWindow = new SettingsWindow(_mainWindow);
            if (!_config.StartMinimized)
            {
                _settingsWindow.Show();
            }
        }

        private void InitNotifyIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Information, // 使用系统自带的默认图标(你以后可以换成自己的 .ico)
                Visible = true,
                Text = "桌面新闻控件"
            };

            // 双击托盘图标，唤醒配置窗口
            _notifyIcon.DoubleClick += (s, args) => ShowSettingsWindow();

            // 右键菜单
            var contextMenu = new ContextMenuStrip();

            var settingsItem = new ToolStripMenuItem("⚙️ 配置中心");
            settingsItem.Click += (s, args) => ShowSettingsWindow();
            contextMenu.Items.Add(settingsItem);

            var toggleItem = new ToolStripMenuItem("👁️ 显示/隐藏 新闻条");
            toggleItem.Click += (s, args) =>
            {
                _config = ConfigService.LoadConfig();
                _config.IsVisible = !_config.IsVisible;
                ConfigService.SaveConfig(_config);

                _mainWindow.Visibility = _config.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            };
            contextMenu.Items.Add(toggleItem);

            contextMenu.Items.Add(new ToolStripSeparator()); // 分割线

            var exitItem = new ToolStripMenuItem("❌ 完全退出");
            exitItem.Click += (s, args) => ExitApplication();
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        public void ShowSettingsWindow()
        {
            _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }

        private void ExitApplication()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose(); // 销毁托盘图标，防止出现残影
            }
            Current.Shutdown(); // 真正结束程序
        }
    }
}