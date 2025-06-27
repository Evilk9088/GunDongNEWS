using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace 桌面新闻
{
    public partial class MainWindow : Window
    {
        private const string ApiUrl = "https://weibo.com/ajax/side/hotSearch";
        private DispatcherTimer _refreshTimer;
        private string _continuousText = "";
        private const double ScrollSpeed = 100; // 像素/秒
        private double _textWidth = 0;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) =>
            {
                await LoadHotSearchData();
                SetupTimers();
                StartMarquee();
            };
        }

        private void SetupTimers()
        {
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromMinutes(10);
            _refreshTimer.Tick += async (s, e) => await LoadHotSearchData();
            _refreshTimer.Start();
        }

        private async Task LoadHotSearchData()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

                    var response = await client.GetStringAsync(ApiUrl);
                    var result = JsonConvert.DeserializeObject<WeiboApiResponse>(response);

                    if (result?.Data?.Realtime != null)
                    {
                        var hotSearchItems = new List<string>();
                        int rank = 1;

                        foreach (var item in result.Data.Realtime)
                        {
                            if (item.Word == null) continue;

                            string hotValue = item.Num > 0 ? $"{item.Num / 10000}万" : "热";
                            hotSearchItems.Add($"{rank++}. {item.Word} ({hotValue})");

                            if (hotSearchItems.Count >= 45) break;
                        }

                        _continuousText = string.Join("    ", hotSearchItems) + "    ";

                        Dispatcher.Invoke(() =>
                        {
                            MarqueeText1.Text = _continuousText;
                            MarqueeText2.Text = _continuousText;

                            // 测量文本宽度
                            var formattedText = new FormattedText(
                                _continuousText,
                                System.Globalization.CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight,
                                new Typeface(MarqueeText1.FontFamily, MarqueeText1.FontStyle, MarqueeText1.FontWeight, MarqueeText1.FontStretch),
                                MarqueeText1.FontSize,
                                Brushes.White,
                                VisualTreeHelper.GetDpi(this).PixelsPerDip);

                            _textWidth = formattedText.Width;
                            ResetMarqueePosition();
                        });
                    }
                }
            }
            catch
            {
                _continuousText = "正在加载微博热搜...";
                Dispatcher.Invoke(() =>
                {
                    MarqueeText1.Text = _continuousText;
                    MarqueeText2.Text = _continuousText;
                });
            }
        }

        private void StartMarquee()
        {
            // 将文本复制一份（实际内容重复两次）
            string doubleText = _continuousText + _continuousText;
            MarqueeText1.Text = doubleText;

            // 动画距离设为单个文本宽度
            var animation = new DoubleAnimation
            {
                From = 0,
                To = -_textWidth - MarqueeText1.FontSize - 2,
                Duration = TimeSpan.FromSeconds((_textWidth + MarqueeText1.FontSize) / ScrollSpeed),
                RepeatBehavior = RepeatBehavior.Forever
            };

            MarqueeText1.BeginAnimation(Canvas.LeftProperty, animation);
        }

        private void ResetMarqueePosition()
        {
            // 设置两个TextBlock的初始位置
            Canvas.SetLeft(MarqueeText1, 100);
            Canvas.SetLeft(MarqueeText2, _textWidth);

            // 重新开始动画
            StartMarquee();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        public List<string> FilterHotItems(List<string> items)
        {
            var config = ConfigService.LoadConfig();
            return items.Where(item =>
                !config.KeywordBlacklist.Any(blackWord =>
                    item.Contains(blackWord, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private string GenerateMarqueeText(List<ApiEndpoint> endpoints)
        {
            var sb = new StringBuilder();
            foreach (var endpoint in endpoints)
            {
                sb.Append($"[{endpoint.Name}] ");
                sb.Append(string.Join("    ", GetFilteredHotItems(endpoint)));
                sb.Append("    ");
            }
            return sb.ToString();
        }

    }

    // API响应数据结构
    public class WeiboApiResponse
    {
        [JsonProperty("data")]
        public HotSearchData? Data { get; set; }
    }

    public class HotSearchData
    {
        [JsonProperty("realtime")]
        public List<RealtimeItem>? Realtime { get; set; }
    }

    public class RealtimeItem
    {
        [JsonProperty("word")]
        public string? Word { get; set; }

        [JsonProperty("word_scheme")]
        public string? Word_Scheme { get; set; }

        [JsonProperty("num")]
        public long Num { get; set; }

        [JsonProperty("flag_desc")]
        public string? Flag_Desc { get; set; }

        [JsonProperty("note")]
        public string? Note { get; set; }
    }

    public class HotSearchItem
    {
        public int Rank { get; set; }
        public string? Title { get; set; }
        public string? HotValue { get; set; }
        public string? Url { get; set; }
        public string? Flag { get; set; }
    }

    public static class ConfigService
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeiboHotSearch",
            "config.json");

        public static AppConfig LoadConfig()
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();

            return JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(ConfigPath));
        }

        public static void SaveConfig(AppConfig config)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
        }
    }

    public class AppConfig
    {
        public List<ApiEndpoint> ApiEndpoints { get; set; } = new();
        public List<string> KeywordBlacklist { get; set; } = new();
    }

    public class ApiEndpoint
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Color { get; set; }
    }

}