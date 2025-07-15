
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
        // 使用一个静态、共享的HttpClient实例，避免套接字耗尽并提高性能。
        private static readonly HttpClient _httpClient;

        private DispatcherTimer _refreshTimer;
        private string _continuousText = "";
        private const double ScrollSpeed = 80; // 每秒滚动的像素值
        private double _textWidth = 0;
        private AppConfig _config;
        private Dictionary<string, Func<ApiEndpoint, Task<List<HotItem>>>> _apiHandlers;

        // 用于动画的变换对象
        private readonly TranslateTransform _marqueeTransform;

        // 静态构造函数：用于初始化静态成员，例如HttpClient。它只会在程序生命周期中执行一次。
        static MainWindow()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        public MainWindow()
        {
            InitializeComponent();
            InitializeApiHandlers();
            _config = ConfigService.LoadConfig();

            // 创建并应用变换，这是实现高性能动画的关键
            _marqueeTransform = new TranslateTransform();
            MarqueeText1.RenderTransform = _marqueeTransform;

            // 第二个TextBlock在新的动画机制下不再需要
            MarqueeText2.Visibility = Visibility.Collapsed;

            Loaded += MainWindow_Loaded;
        }

        private void InitializeApiHandlers()
        {
            _apiHandlers = new Dictionary<string, Func<ApiEndpoint, Task<List<HotItem>>>>
            {
                ["微博热搜"] = FetchWeiboHotSearch,
                ["贴吧热议"] = FetchTiebaHotTopics,
                ["腾讯新闻"] = FetchQQNews,
                ["新浪国内"] = FetchSinaNews,
                ["新浪国际"] = FetchSinaNews, // 同一个处理器可以处理不同的新浪分类
                ["今日头条"] = FetchToutiaoHot
            };
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadAndDisplayData();
            SetupTimers();
        }

        private void SetupTimers()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(_config.RefreshIntervalMinutes),
                IsEnabled = true
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
        }

        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            // 使用 catch(Exception) 而不是 catch(Exception ex) 来避免“ex未使用”的警告
            try
            {
                await LoadAndDisplayData();
            }
            catch (Exception)
            {
                _continuousText = "数据刷新失败，请检查网络或配置。";
                UpdateMarqueeText();
            }
        }

        private async Task LoadAndDisplayData()
        {
            try
            {
                var allItems = new List<string>();
                var tasks = _config.ApiEndpoints
                    .Where(e => e.IsEnabled)
                    .Select(ProcessEndpoint);

                var results = await Task.WhenAll(tasks);
                allItems.AddRange(results.SelectMany(r => r));

                // 修正：使用 .Count > 0 代替 .Any()，更清晰且对List等集合性能更好
                if (allItems.Count > 0)
                {
                    _continuousText = string.Join("    ", allItems) + "    ";
                }
                else
                {
                    _continuousText = "没有启用的数据源或所有数据源加载失败。请检查配置。";
                }
            }
            catch (Exception)
            {
                _continuousText = "数据加载失败，请检查网络连接。";
            }
            finally
            {
                // 确保UI更新在UI线程上执行
                Dispatcher.Invoke(UpdateMarqueeText);
            }
        }

        private async Task<List<string>> ProcessEndpoint(ApiEndpoint endpoint)
        {
            try
            {
                if (!_apiHandlers.TryGetValue(endpoint.Name, out var handler))
                    return new List<string>();

                var items = await handler(endpoint);
                return FilterAndFormatHotItems(items, endpoint.Name);
            }
            catch
            {
                return new List<string> { $"[{endpoint.Name}数据加载失败]" };
            }
        }

        private List<string> FilterAndFormatHotItems(List<HotItem> items, string sourceName)
        {
            return items
                .Where(item => !_config.KeywordBlacklist.Any(blackWord =>
                    item.Title.Contains(blackWord, StringComparison.OrdinalIgnoreCase)))
                .Select(item => $"[{sourceName}] {item.Title} ({item.FormattedHot})")
                .ToList();
        }

        #region API 数据获取方法 (已重构)
        private async Task<List<HotItem>> FetchWeiboHotSearch(ApiEndpoint endpoint)
        {
            var response = await _httpClient.GetStringAsync(endpoint.Url);
            var result = JsonConvert.DeserializeObject<WeiboApiResponse>(response);

            return result?.Data?.Realtime?
                .Where(x => !string.IsNullOrEmpty(x.Word))
                .Select((x, i) => new HotItem { Rank = i + 1, Title = x.Word, Hot = x.Num, Source = "微博" })
                .Take(endpoint.ShowCount)
                .ToList() ?? new List<HotItem>();
        }

        private async Task<List<HotItem>> FetchTiebaHotTopics(ApiEndpoint endpoint)
        {
            var response = await _httpClient.GetStringAsync(endpoint.Url);
            var result = JsonConvert.DeserializeObject<TiebaApiResponse>(response);

            return result?.Data?.BangTopic?.TopicList?
                .Where(x => !string.IsNullOrEmpty(x.TopicName))
                .Select((x, i) => new HotItem { Rank = i + 1, Title = x.TopicName, Hot = x.DiscussNum, Source = "贴吧" })
                .Take(endpoint.ShowCount)
                .ToList() ?? new List<HotItem>();
        }

        private async Task<List<HotItem>> FetchQQNews(ApiEndpoint endpoint)
        {
            var response = await _httpClient.GetStringAsync(endpoint.Url);
            var result = JsonConvert.DeserializeObject<QQNewsApiResponse>(response);

            return result?.IdList?.FirstOrDefault()?.NewsList?
                .Skip(1)
                .Where(x => !string.IsNullOrEmpty(x.Title))
                .Select((x, i) => new HotItem { Rank = i + 1, Title = x.Title, Hot = x.HotEvent?.HotScore ?? 0, Source = "腾讯新闻" })
                .Take(endpoint.ShowCount)
                .ToList() ?? new List<HotItem>();
        }

        private async Task<List<HotItem>> FetchSinaNews(ApiEndpoint endpoint)
        {
            var uri = new Uri(endpoint.Url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var dateStr = DateTime.Now.ToString("yyyyMMdd");
            var newUrl = $"https://top.sina.com.cn/ws/GetTopDataList.php?top_type=day&top_cat={query["top_cat"]}&top_time={dateStr}&top_show_num={endpoint.ShowCount + 5}";

            var response = await _httpClient.GetStringAsync(newUrl);
            var jsonString = response.Replace("var data = ", "").TrimEnd(';');
            var result = JsonConvert.DeserializeObject<SinaApiResponse>(jsonString);

            return result?.Data?
                .Where(x => !string.IsNullOrEmpty(x.Title))
                .Select((x, i) => new HotItem { Rank = i + 1, Title = x.Title, Hot = long.TryParse(x.TopNum.Replace(",", ""), out var num) ? num : 0, Source = "新浪新闻" })
                .Take(endpoint.ShowCount)
                .ToList() ?? new List<HotItem>();
        }

        private async Task<List<HotItem>> FetchToutiaoHot(ApiEndpoint endpoint)
        {
            var response = await _httpClient.GetStringAsync(endpoint.Url);
            var result = JsonConvert.DeserializeObject<ToutiaoApiResponse>(response);

            return result?.Data?
                .Where(x => !string.IsNullOrEmpty(x.Title))
                .Select((x, i) => new HotItem { Rank = i + 1, Title = x.Title, Hot = long.TryParse(x.HotValue, out var num) ? num : 0, Source = "今日头条" })
                .Take(endpoint.ShowCount)
                .ToList() ?? new List<HotItem>();
        }
        #endregion

        private void UpdateMarqueeText()
        {
            // 为了实现无缝滚动，将文本内容复制一份
            MarqueeText1.Text = _continuousText + _continuousText;

            // 测量单份文本的宽度
            var formattedText = new FormattedText(
                _continuousText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(MarqueeText1.FontFamily, MarqueeText1.FontStyle, MarqueeText1.FontWeight, MarqueeText1.FontStretch),
                MarqueeText1.FontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            _textWidth = formattedText.Width;

            if (_textWidth <= 0)
            {
                _marqueeTransform.BeginAnimation(TranslateTransform.XProperty, null); // 停止动画
                return;
            }

            StartMarqueeAnimation();
        }

        // 核心优化：使用RenderTransform进行动画
        private void StartMarqueeAnimation()
        {
            var animation = new DoubleAnimation
            {
                //                To = -_textWidth - MarqueeText1.FontSize - 2,
                //                Duration = TimeSpan.FromSeconds((_textWidth + MarqueeText1.FontSize) / ScrollSpeed),
                From = 0,
                To = -_textWidth, // 动画终点为单份文本的负宽度
                Duration = TimeSpan.FromSeconds(_textWidth / ScrollSpeed),
                //To = -_textWidth - MarqueeText1.FontSize - 2,
                //Duration = TimeSpan.FromSeconds((_textWidth + MarqueeText1.FontSize) / ScrollSpeed),
                RepeatBehavior = RepeatBehavior.Forever // 无限循环
            };

            // 将动画应用于TranslateTransform的X属性。此操作由渲染线程处理，可利用GPU加速。
            _marqueeTransform.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }

    #region 数据模型 (修正：已包含所有必要的类)
    public class HotItem
    {
        public int Rank { get; set; }
        public string Title { get; set; }
        public long Hot { get; set; }
        public string Source { get; set; }

        public string FormattedHot => Hot switch
        {
            > 1000000 => $"{Hot / 10000.0:F1}万",
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

    #region 配置系统 (修正：已包含所有必要的类和方法)
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
                {
                    var defaultConfig = CreateDefaultConfig();
                    SaveConfig(defaultConfig);
                    return defaultConfig;
                }

                var configJson = File.ReadAllText(ConfigPath);
                var config = JsonConvert.DeserializeObject<AppConfig>(configJson);
                return config ?? CreateDefaultConfig();
            }
            catch
            {
                return CreateDefaultConfig();
            }
        }

        public static void SaveConfig(AppConfig config)
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
        }

        private static AppConfig CreateDefaultConfig()
        {
            return new AppConfig
            {
                RefreshIntervalMinutes = 10, // 新增全局刷新间隔
                ApiEndpoints = new List<ApiEndpoint> {
                    new ApiEndpoint {
                        Name = "微博热搜",
                        Url = "https://weibo.com/ajax/side/hotSearch",
                        Color = "#FF0000",
                        Category = "社交",
                        IsEnabled = true,
                        ShowCount = 1 // 新增显示数量配置
                    },
                    new ApiEndpoint {
                        Name = "贴吧热议",
                        Url = "https://tieba.baidu.com/hottopic/browse/topicList",
                        Color = "#1E90FF",
                        Category = "社区",
                        IsEnabled = true,
                        ShowCount = 1
                    },
                    new ApiEndpoint {
                        Name = "腾讯新闻",
                        Url = "https://r.inews.qq.com/gw/event/hot_ranking_list?page_size=50",
                        Color = "#32CD32",
                        Category = "新闻",
                        IsEnabled = true,
                        ShowCount = 1
                    },
                    //new ApiEndpoint {
                    //    Name = "新浪国内",
                    //    Url = "https://top.news.sina.com.cn/ws/GetTopDataList.php?top_cat=news_china_suda",
                    //    Color = "#FF8C00",
                    //    Category = "新闻",
                    //    IsEnabled = true,
                    //    ShowCount = 0
                    //},
                    //new ApiEndpoint {
                    //    Name = "新浪国际",
                    //    Url = "https://top.news.sina.com.cn/ws/GetTopDataList.php?top_cat=news_world_suda",
                    //    Color = "#FF6347",
                    //    Category = "新闻",
                    //    IsEnabled = false,
                    //    ShowCount = 0
                    //},
                    new ApiEndpoint {
                        Name = "今日头条",
                        Url = "https://www.toutiao.com/hot-event/hot-board/?origin=toutiao_pc",
                        Color = "#FF4500",
                        Category = "资讯",
                        IsEnabled = true,
                        ShowCount = 1
                    }
                },
                KeywordBlacklist = new List<string> {
                    "明星", "广告", "推广","杨紫","王一博","肖战","赵丽颖","迪丽热巴","杨幂","虞书欣","赵露思","白鹿","檀健次","成毅","邓为",
                    "张颂文","王鹤棣","魏大勋","刘亦菲","刘诗诗","唐嫣","张若昀","周深","贾玲","沈腾","马丽","黄晓明","胡歌","雷佳音","刘宇宁",
                    "白敬亭","吴磊","张晚意","曾舜晞","田曦薇","张婧仪","周也","王星越","陈哲远","张凌赫","于适","丞磊","卢昱晓","林一",
                    "李一桐","金晨","秦岚","辛芷蕾","景甜","高叶","宋轶","古力娜扎","刘涛","童瑶","王传君","井柏然","黄景瑜","彭昱畅",
                    "李现","朱一龙","易烊千玺","王俊凯","王源","鹿晗","华晨宇","毛不易","汪苏泷","张杰","薛之谦","李荣浩","许嵩","蔡徐坤",
                    "范丞丞","黄子韬","张艺兴","陈伟霆","李易峰","任嘉伦","罗云熙","宋茜","江疏影","关晓彤","谭松韵","杨超越","鞠婧祎","李沁",
                    "张予曦","陈都灵","李兰迪","周依然","张宥浩","王阳","万茜","黄轩","欧豪","窦骁","韩东君","魏晨","陈楚生","苏醒","王铮亮",
                    "张远","陆虎","王栎鑫","郭麒麟","大张伟","papi酱","傅首尔","池子","李诞","蔡康永","杨笠","庞博","呼兰","王建国","李雪琴",
                    "周奇墨","张绍刚","王自健","李宇春","韩红","毛阿敏","那英","张靓颖","邓紫棋","王菲","张惠妹","林忆莲","范玮琪",
                    "Angelababy", "杨颖","张天爱","蔡依林","周杰伦","林俊杰","五月天","陈奕迅","王力宏","李克勤","张学友", "刘德华",
                    "孙颖莎", "丁宁", "马龙", "樊振东", "许昕", "刘诗雯", "朱雨玲", "陈梦", "王曼昱", "张本智和", "伊藤美诚", "石川佳纯",
                    "哪吒", "大圣归来", "白蛇：缘起", "姜子牙", "熊出没", "喜羊羊与灰太狼", "小猪佩奇", "哆啦A梦", "海贼王", "火影忍者", 
                    "名侦探柯南","雷军","小米"
                }
            };
        }
    }

    public class AppConfig
    {
        public int RefreshIntervalMinutes { get; set; } = 10;
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
        public int ShowCount { get; set; } = 20; // 用于替换硬编码的显示数量
    }
    #endregion
}