using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private DispatcherTimer _refreshTimer;
        private string _continuousText = "";
        private const double ScrollSpeed = 80;
        private double _textWidth = 0;
        private int WB_show_num = 40;
        private int TB_show_num = 10;
        private int QQ_show_num = 45;
        private int TT_show_num = 45;
        private AppConfig _config;
        private Dictionary<string, Func<string, Task<List<HotItem>>>> _apiHandlers;

        public MainWindow()
        {
            InitializeComponent();
            InitializeApiHandlers();
            _config = ConfigService.LoadConfig();

            Loaded += async (s, e) => {
                await LoadHotSearchData();
                SetupTimers();
                StartMarquee();
            };
        }

        private void InitializeApiHandlers()
        {
            _apiHandlers = new Dictionary<string, Func<string, Task<List<HotItem>>>>
            {
                ["微博热搜"] = FetchWeiboHotSearch,
                ["贴吧热议"] = FetchTiebaHotTopics,
                ["腾讯新闻"] = FetchQQNews,
                ["新浪国内"] = FetchSinaNews,
                ["今日头条"] = FetchToutiaoHot
            };
        }

        private void SetupTimers()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(10),
                IsEnabled = true
            };
            _refreshTimer.Tick += async (s, e) => await LoadHotSearchData();
        }

        private async Task LoadHotSearchData()
        {
            try
            {
                var allItems = new List<string>();
                var tasks = _config.ApiEndpoints
                    .Where(e => e.IsEnabled)
                    .Select(endpoint => ProcessEndpoint(endpoint));

                var results = await Task.WhenAll(tasks);
                allItems.AddRange(results.SelectMany(r => r));

                _continuousText = string.Join("    ", allItems) + "    ";

                Dispatcher.Invoke(() => {
                    MarqueeText1.Text = _continuousText;
                    MarqueeText2.Text = _continuousText;
                    UpdateTextWidth();
                    ResetMarqueePosition();
                });
            }
            catch (Exception ex)
            {
                _continuousText = "数据加载失败，请检查网络连接";
                Dispatcher.Invoke(() => {
                    MarqueeText1.Text = _continuousText;
                    MarqueeText2.Text = _continuousText;
                });
            }
        }

        private async Task<List<string>> ProcessEndpoint(ApiEndpoint endpoint)
        {
            try
            {
                if (!_apiHandlers.TryGetValue(endpoint.Name, out var handler))
                    return new List<string>();

                var items = await handler(endpoint.Url);
                return FilterHotItems(items, endpoint.Name, endpoint.Color);
            }
            catch
            {
                return new List<string> { $"[{endpoint.Name}数据加载失败]" };
            }
        }

        private List<string> FilterHotItems(List<HotItem> items, string sourceName, string color)
        {
            return items
                .Where(item => !_config.KeywordBlacklist.Any(blackWord =>
                    item.Title.Contains(blackWord, StringComparison.OrdinalIgnoreCase)))
                .Select(item => $"[{sourceName}] {item.Title} ({item.FormattedHot})")
                .ToList();
        }

        #region API 数据获取方法
        private async Task<List<HotItem>> FetchWeiboHotSearch(string url)
        {
            using var client = CreateHttpClient();
            var response = await client.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<WeiboApiResponse>(response);

            return result?.Data?.Realtime?
                .Where(x => !string.IsNullOrEmpty(x.Word))
                .Select((x, i) => new HotItem
                {
                    Rank = i + 1,
                    Title = x.Word,
                    Hot = x.Num,
                    Source = "微博"
                })
                .Take(WB_show_num)
                .ToList() ?? new List<HotItem>();
        }

        private async Task<List<HotItem>> FetchTiebaHotTopics(string url)
        {
            using var client = CreateHttpClient();
            var response = await client.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<TiebaApiResponse>(response);

            return result?.Data?.BangTopic?.TopicList?
                .Where(x => !string.IsNullOrEmpty(x.TopicName))
                .Select((x, i) => new HotItem
                {
                    Rank = i + 1,
                    Title = x.TopicName,
                    Hot = x.DiscussNum,
                    Source = "贴吧"
                })
                .Take(TB_show_num)
                .ToList() ?? new List<HotItem>();
        }

        private async Task<List<HotItem>> FetchQQNews(string url)
        {
            using var client = CreateHttpClient();
            var response = await client.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<QQNewsApiResponse>(response);

            return result?.IdList?.FirstOrDefault()?.NewsList?
                .Skip(1) // 跳过第一条置顶新闻
                .Where(x => !string.IsNullOrEmpty(x.Title))
                .Select((x, i) => new HotItem
                {
                    Rank = i + 1,
                    Title = x.Title,
                    Hot = x.HotEvent?.HotScore ?? 0,
                    Source = "腾讯新闻"
                })
                .Take(QQ_show_num)
                .ToList() ?? new List<HotItem>();
        }

        private async Task<List<HotItem>> FetchSinaNews(string url)
        {
            // 从URL解析参数
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var www = uri.Host.Split('.')[1]; // 从top.news.sina.com.cn提取news

            // 生成当前日期
            var now = DateTime.Now;
            var dateStr = $"{now:yyyy}{now:MM}{now:dd}";

            // 重建URL
            var newUrl = $"https://top.sina.com.cn/ws/GetTopDataList.php?" +
                         $"top_type=day&" +
                         $"top_cat={query["top_cat"]}&" +
                         $"top_time={dateStr}&" +
                         $"top_show_num=50";

            using var client = CreateHttpClient();
            var response = await client.GetStringAsync(newUrl);
            var jsonString = response.Replace("var data = ", "").TrimEnd(';');
            var result = JsonConvert.DeserializeObject<SinaApiResponse>(jsonString);

            return result?.Data?
                .Where(x => !string.IsNullOrEmpty(x.Title))
                .Select((x, i) => new HotItem
                {
                    Rank = i + 1,
                    Title = x.Title,
                    Hot = int.Parse(x.TopNum.Replace(",", "")),
                    Source = "新浪新闻"
                })
                .Take(10)
                .ToList() ?? new List<HotItem>();
        }


        private async Task<List<HotItem>> FetchToutiaoHot(string url)
        {
            using var client = CreateHttpClient();
            var response = await client.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<ToutiaoApiResponse>(response);

            return result?.Data?
                .Where(x => !string.IsNullOrEmpty(x.Title))
                .Select((x, i) => new HotItem
                {
                    Rank = i + 1,
                    Title = x.Title,
                    Hot = int.Parse(x.HotValue),
                    Source = "今日头条"
                })
                .Take(TT_show_num)
                .ToList() ?? new List<HotItem>();
        }

        private HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            client.Timeout = TimeSpan.FromSeconds(10);
            return client;
        }
        #endregion

        private void UpdateTextWidth()
        {
            var formattedText = new FormattedText(
                _continuousText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(MarqueeText1.FontFamily, MarqueeText1.FontStyle, MarqueeText1.FontWeight, MarqueeText1.FontStretch),
                MarqueeText1.FontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            _textWidth = formattedText.Width;
        }

        private void StartMarquee()
        {
            string doubleText = _continuousText + _continuousText;
            MarqueeText1.Text = doubleText;

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
            Canvas.SetLeft(MarqueeText1, 100);
            Canvas.SetLeft(MarqueeText2, _textWidth);
            StartMarquee();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }

    #region 数据模型
    public class HotItem
    {
        public int Rank { get; set; }
        public string Title { get; set; }
        public long Hot { get; set; }
        public string Source { get; set; }

        public string FormattedHot => Hot switch
        {
            > 1000000 => $"{Hot / 10000}万",
            > 10000 => $"{Hot / 1000.0:F1}千",
            _ => Hot.ToString()
        };
    }

    // 微博API响应模型
    public class WeiboApiResponse
    {
        [JsonProperty("data")] public HotSearchData Data { get; set; }
    }
    public class HotSearchData
    {
        [JsonProperty("realtime")] public List<RealtimeItem> Realtime { get; set; }
    }
    public class RealtimeItem
    {
        [JsonProperty("word")] public string Word { get; set; }
        [JsonProperty("num")] public long Num { get; set; }
    }

    // 贴吧API响应模型
    public class TiebaApiResponse
    {
        [JsonProperty("data")] public TiebaData Data { get; set; }
    }
    public class TiebaData
    {
        [JsonProperty("bang_topic")] public BangTopic BangTopic { get; set; }
    }
    public class BangTopic
    {
        [JsonProperty("topic_list")] public List<TopicItem> TopicList { get; set; }
    }
    public class TopicItem
    {
        [JsonProperty("topic_name")] public string TopicName { get; set; }
        [JsonProperty("discuss_num")] public long DiscussNum { get; set; }
    }

    // 腾讯新闻API响应模型
    public class QQNewsApiResponse
    {
        [JsonProperty("idlist")] public List<IdListItem> IdList { get; set; }
    }
    public class IdListItem
    {
        [JsonProperty("newslist")] public List<NewsItem> NewsList { get; set; }
    }
    public class NewsItem
    {
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("hotEvent")] public HotEvent HotEvent { get; set; }
    }
    public class HotEvent
    {
        [JsonProperty("hotScore")] public long HotScore { get; set; }
    }

    // 新浪新闻API响应模型
    public class SinaApiResponse
    {
        [JsonProperty("data")]
        public List<SinaNewsItem> Data { get; set; }

        [JsonProperty("top_time")]
        public string TopTime { get; set; }
    }

    public class SinaNewsItem
    {
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("top_num")] public string TopNum { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("create_date")] public string CreateDate { get; set; }
    }


    // 今日头条API响应模型
    public class ToutiaoApiResponse
    {
        [JsonProperty("data")] public List<ToutiaoItem> Data { get; set; }
    }
    public class ToutiaoItem
    {
        [JsonProperty("Title")] public string Title { get; set; }
        [JsonProperty("HotValue")] public string HotValue { get; set; }
    }
    #endregion

    #region 配置系统
    public static class ConfigService
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopNews");

        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        public static AppConfig LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return CreateDefaultConfig();

                var config = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(ConfigPath));
                return config ?? CreateDefaultConfig();
            }
            catch
            {
                return CreateDefaultConfig();
            }
        }

        private static AppConfig CreateDefaultConfig()
        {
            return new AppConfig
            {
                ApiEndpoints = new List<ApiEndpoint> {
                    new ApiEndpoint {
                        Name = "微博热搜",
                        Url = "https://weibo.com/ajax/side/hotSearch",
                        Color = "#FF0000",
                        Category = "社交",
                        IsEnabled = true
                    },
                    new ApiEndpoint {
                        Name = "贴吧热议",
                        Url = "https://tieba.baidu.com/hottopic/browse/topicList",
                        Color = "#1E90FF",
                        Category = "社区",
                        IsEnabled = true
                    },
                    new ApiEndpoint {
                        Name = "腾讯新闻",
                        Url = "https://r.inews.qq.com/gw/event/hot_ranking_list?page_size=50",
                        Color = "#32CD32",
                        Category = "新闻",
                        IsEnabled = true
                    },
                    new ApiEndpoint {
                    Name = "新浪国内",
                    Url = "https://top.news.sina.com.cn/ws/GetTopDataList.php?top_cat=news_china_suda",
                    Color = "#FF8C00",
                    Category = "新闻",
                    IsEnabled = true
                    },
                    // 可以添加更多新浪分类...
                    new ApiEndpoint {
                        Name = "新浪国际",
                        Url = "https://top.news.sina.com.cn/ws/GetTopDataList.php?top_cat=news_world_suda",
                        Color = "#FF6347",
                        Category = "新闻",
                        IsEnabled = false
                    },
                    new ApiEndpoint {
                        Name = "今日头条",
                        Url = "https://www.toutiao.com/hot-event/hot-board/?origin=toutiao_pc",
                        Color = "#FF4500",
                        Category = "资讯",
                        IsEnabled = true
                    }
                },
                KeywordBlacklist = new List<string> {
                    "明星绯闻", "广告推广", "赌博", "色情"
                }
            };
        }

        public static void SaveConfig(AppConfig config)
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
        }
    }

    public class AppConfig
    {
        public List<ApiEndpoint> ApiEndpoints { get; set; } = new List<ApiEndpoint>();
        public List<string> KeywordBlacklist { get; set; } = new List<string>();
    }

    public class ApiEndpoint
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Color { get; set; } = "#FFFFFF";
        public string Category { get; set; } = "综合";
        public bool IsEnabled { get; set; } = true;
    }
    #endregion
}
