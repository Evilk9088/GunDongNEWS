
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
        private DispatcherTimer _clockTimer;
        private DispatcherTimer _refreshTimer;
        private string _continuousText = "";
        private const double ScrollSpeed = 80; // 每秒滚动的像素值
        private double _textWidth = 0;
        private AppConfig _config;
        private Dictionary<string, Func<ApiEndpoint, Task<List<HotItem>>>> _apiHandlers;

        // 用于动画的变换对象
        private readonly TranslateTransform _marqueeTransform;
        private Storyboard _marqueeStoryboard;

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
            MarqueeStackPanel.RenderTransform = _marqueeTransform;

            // 第二个TextBlock在新的动画机制下不再需要
            //MarqueeText2.Visibility = Visibility.Collapsed;

            Loaded += MainWindow_Loaded;
        }

        private void InitializeApiHandlers()
        {
            _apiHandlers = new Dictionary<string, Func<ApiEndpoint, Task<List<HotItem>>>>
            {
                ["微博"] = FetchWeiboHotSearch,
                ["贴吧"] = FetchTiebaHotTopics,
                ["腾讯"] = FetchQQNews,
                ["新浪国内"] = FetchSinaNews,
                ["新浪国际"] = FetchSinaNews, // 同一个处理器可以处理不同的新浪分类
                ["头条"] = FetchToutiaoHot
            };
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadAndDisplayData();
            SetupTimers();
            SetupClockTimer();
        }

        private void SetupClockTimer()
        {
            _clockTimer = new DispatcherTimer
            {
                // 每秒触发一次
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();

            // 立即执行一次，避免程序启动时时间显示延迟
            ClockTimer_Tick(null, null);
        }
        private void ClockTimer_Tick(object sender, EventArgs e)
        {
            // 更新UI上的时间显示，格式为 "yyyy-MM-dd HH:mm:ss"
            TimeTextBlock.Text = DateTime.Now.ToString("MM月dd dddd HH:mm:ss");
        }
        private void SetupTimers()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(_config.RefreshIntervalMinutes),
                // 只有新闻模式才需要定时器刷新，小说模式依靠动画接力
                IsEnabled = (_config.Mode == "News")
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
        }

        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            // --- 新增代码：刷新窗口置顶状态 ---
            // 这是一个非常关键的步骤，用于解决窗口在某些情况下（如从全屏应用返回后）“消失”的问题。
            // 通过快速切换Topmost属性，我们强制Windows重新绘制窗口，但不会窃取用户的当前焦点。
            try
            {
                this.Topmost = false;
                this.Topmost = true;
            }
            catch
            {
                // 在极少数情况下（如窗口即将关闭），访问窗口属性可能出错，这里捕获异常以确保程序稳定。
            }

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
                if (_config.Mode == "LocalText")
                {
                    // === 小说模式 ===
                    _continuousText = await NovelReaderService.GetNextChunkAsync(_config, 10);
                }
                else
                {
                    // === 原来的新闻模式 ===
                    var allItems = new List<string>();
                    var tasks = _config.ApiEndpoints.Where(e => e.IsEnabled).Select(ProcessEndpoint);
                    var results = await Task.WhenAll(tasks);
                    allItems.AddRange(results.SelectMany(r => r));

                    if (allItems.Count > 0)
                    {
                        string separator = "\u00A0\u00A0\u00A0\u00A0";
                        _continuousText = string.Join(separator, allItems) + separator;
                    }
                    else
                    {
                        _continuousText = "没有启用的数据源或加载失败。";
                    }
                }
            }
            catch (Exception)
            {
                _continuousText = "数据加载失败。";
            }
            finally
            {
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
                // 确保 Title 不为空，并进行黑名单过滤
                .Where(item => !string.IsNullOrEmpty(item.Title) && !_config.KeywordBlacklist.Any(blackWord =>
                    item.Title.Contains(blackWord, StringComparison.OrdinalIgnoreCase)))
                .Select(item =>
                {
                    // 【核心修复】：过滤掉标题中可能隐藏的换行符(\n)、回车符(\r)和制表符(\t)
                    // 将它们统一替换为空格，避免破坏 WPF TextBlock 的单行显示
                    string cleanTitle = item.Title
                                            .Replace("\r", "")
                                            .Replace("\n", " ")
                                            .Replace("\t", " ")
                                            .Trim();

                    return $"[{sourceName}] {cleanTitle}";
                })
                .ToList();
        }

        #region API 数据获取方法 (已重构)
        private async Task<List<HotItem>> FetchWeiboHotSearch(ApiEndpoint endpoint)
        {
            // 新的微博API需要模拟手机浏览器访问，并携带特定的请求头
            using (var requestMessage = new HttpRequestMessage(HttpMethod.Get, endpoint.Url))
            {
                // 添加必要的请求头，这些是成功获取数据的关键
                requestMessage.Headers.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0.3 Mobile/15E148 Safari/604.1");
                requestMessage.Headers.Add("Referer", "https://s.weibo.com/top/summary?cate=realtimehot");
                requestMessage.Headers.Add("MWeibo-Pwa", "1");
                requestMessage.Headers.Add("X-Requested-With", "XMLHttpRequest");

                var response = await _httpClient.SendAsync(requestMessage);
                response.EnsureSuccessStatusCode(); // 确保请求成功
                var jsonString = await response.Content.ReadAsStringAsync();

                // 使用新的数据模型进行反序列化
                var result = JsonConvert.DeserializeObject<NewWeiboApiResponse>(jsonString);

                // 从新的数据结构中提取热搜列表
                // 路径：result.Data.Cards[0].CardGroup
                return result?.Data?.Cards?.FirstOrDefault()?.CardGroup?
                    .Where(x => !string.IsNullOrEmpty(x.Desc))
                    .Select((x, i) => new HotItem { Rank = i + 1, Title = x.Desc, Hot = x.Num, Source = "微博" })
                    .Take(endpoint.ShowCount)
                    .ToList() ?? new List<HotItem>();
            }
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
        private void UpdateMarqueeText_old()
        {
            if (string.IsNullOrEmpty(_continuousText))
            {
                _marqueeTransform.BeginAnimation(TranslateTransform.XProperty, null);
                return;
            }

            // 清空旧的积木块
            MarqueeStackPanel.Children.Clear();

            // 为了实现无缝滚动，复制一份全文本
            string fullText = _continuousText + _continuousText;

            // 将超长文本切割成多个 TextBlock（每个约 500 字符），避开 WPF 单体 16384 像素的渲染限制
            int chunkSize = 500;
            _textWidth = 0;

            for (int i = 0; i < fullText.Length; i += chunkSize)
            {
                int length = Math.Min(chunkSize, fullText.Length - i);
                string chunk = fullText.Substring(i, length);

                var tb = new TextBlock
                {
                    Text = chunk,
                    Foreground = Brushes.White,
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center
                };

                // 测量这块积木的宽度
                var formattedText = new FormattedText(
                    chunk,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                    tb.FontSize,
                    Brushes.White,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                _textWidth += formattedText.Width;
                MarqueeStackPanel.Children.Add(tb); // 把小块加到横排容器里
            }

            // 因为文本复制了一份，真实的单轮动画滚动宽度是一半
            _textWidth = _textWidth / 2;

            // ===== 新增：像素级微调 =====
            // 如果你发现循环瞬间还有极微弱的抖动（重叠了或者拉开了），
            // 可以在这里直接 + 或者 - 具体的像素值。支持小数！
            // 比如：如果发现跳跃时画面“往左缩了”，说明滚多了，就 -1；如果“往右跳了”，说明滚少了，就 +1。
            double pixelOffset = -1; // 改这里！试试 -1, 1, 0.5, -0.5
            _textWidth += pixelOffset;

            if (_textWidth <= 0)
                return;

            StartMarqueeAnimation();
        }
        private void UpdateMarqueeText()
        {
            if (string.IsNullOrEmpty(_continuousText)) return;

            MarqueeStackPanel.Children.Clear();

            // 新闻模式循环需要复制文本；小说是流水线接力，不需要复制
            string fullText = _config.Mode == "News" ? _continuousText + _continuousText : _continuousText;

            int chunkSize = 500;
            _textWidth = 0;

            for (int i = 0; i < fullText.Length; i += chunkSize)
            {
                int length = Math.Min(chunkSize, fullText.Length - i);
                string chunk = fullText.Substring(i, length);

                var tb = new TextBlock
                {
                    Text = chunk,
                    Foreground = Brushes.White,
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var formattedText = new FormattedText(chunk, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                _textWidth += formattedText.Width;
                MarqueeStackPanel.Children.Add(tb);
            }

            if (_config.Mode == "News")
            {
                _textWidth = _textWidth / 2; // 新闻模式只滚一半的距离
            }

            if (_textWidth <= 0) return;

            StartMarqueeAnimation();
        }
        // 核心优化：使用RenderTransform进行动画
        private void StartMarqueeAnimation_old()
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
        private void StartMarqueeAnimation()
        {
            // 停止并清理之前的动画
            if (_marqueeStoryboard != null)
            {
                _marqueeStoryboard.Stop(this);
                _marqueeStoryboard.Completed -= MarqueeStoryboard_Completed;
            }

            double startX = 0;
            double endX = -_textWidth;
            double distance = _textWidth;

            // 小说模式的特殊处理：新的一截文字从屏幕最右侧平滑滑入
            if (_config.Mode == "LocalText")
            {
                // 如果实际宽度还没算出来，就用屏幕默认宽度
                startX = MarqueeCanvas.ActualWidth > 0 ? MarqueeCanvas.ActualWidth : SystemParameters.PrimaryScreenWidth;
                endX = -_textWidth;
                distance = startX + _textWidth;
            }

            var animation = new DoubleAnimation
            {
                From = startX,
                To = endX,
                Duration = TimeSpan.FromSeconds(distance / ScrollSpeed)
            };

            // 新闻无尽循环，小说跑完单次触发接力
            animation.RepeatBehavior = _config.Mode == "News" ? RepeatBehavior.Forever : new RepeatBehavior(1);

            // ==========================================
            // 【核心修复区域】：完美避开 WPF 的匿名对象动画 Bug
            // 1. 将动画的目标直接锁定为我们在 XAML 中命名的 UI 控件 MarqueeStackPanel
            Storyboard.SetTarget(animation, MarqueeStackPanel);

            // 2. 使用绝对正确的属性路径语法，告诉程序去改变它的 RenderTransform 里面的 TranslateTransform 的 X 值
            Storyboard.SetTargetProperty(animation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
            // ==========================================

            _marqueeStoryboard = new Storyboard();
            _marqueeStoryboard.Children.Add(animation);

            // 如果是小说模式，订阅“跑完了”的事件
            if (_config.Mode == "LocalText")
            {
                _marqueeStoryboard.Completed += MarqueeStoryboard_Completed;
            }

            // true 表示允许在后续代码中控制它（暂停/恢复）
            _marqueeStoryboard.Begin(this, true);
        }

        // --- 以下是新增的三个事件处理方法 ---

        // 小说模式专属：当前这十几行滚完消失后，自动无缝触发加载下一截
        private async void MarqueeStoryboard_Completed(object sender, EventArgs e)
        {
            await LoadAndDisplayData();
        }

        // 鼠标移入：仅在小说模式下生效，暂停滚动
        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_config.Mode == "LocalText" && _marqueeStoryboard != null)
            {
                _marqueeStoryboard.Pause(this);
            }
        }

        // 鼠标移出：仅在小说模式下生效，继续滚动
        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_config.Mode == "LocalText" && _marqueeStoryboard != null)
            {
                _marqueeStoryboard.Resume(this);
            }
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

    #region 数据模型 (修正：已包含所有必要的类)


    // 新的微博API响应模型
    public class NewWeiboApiResponse
    {
        [JsonProperty("data")]
        public NewWeiboData Data { get; set; }
    }

    public class NewWeiboData
    {
        [JsonProperty("cards")]
        public List<NewWeiboCard> Cards { get; set; }
    }

    public class NewWeiboCard
    {
        [JsonProperty("card_group")]
        public List<NewWeiboCardGroupItem> CardGroup { get; set; }
    }

    public class NewWeiboCardGroupItem
    {
        [JsonProperty("desc")]
        public string Desc { get; set; } // 热搜标题在这里

        [JsonProperty("num")]
        public long Num { get; set; } // 热搜数值在这里
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
        //private static readonly string ConfigDir = Path.Combine(
        //    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        //    "DesktopNews");
        // 修改这里：使用 AppContext.BaseDirectory 获取程序根目录
        private static readonly string ConfigDir = AppContext.BaseDirectory;

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
                        Name = "微博",
                        // *** 修改下面这行URL ***
                        Url = "https://m.weibo.cn/api/container/getIndex?containerid=106003type%3D25%26t%3D3%26disable_hot%3D1%26filter_type%3Drealtimehot",
                        Color = "#FF0000",
                        Category = "社交",
                        IsEnabled = true,
                        ShowCount = 40
                    },
                    new ApiEndpoint {
                        Name = "贴吧",
                        Url = "https://tieba.baidu.com/hottopic/browse/topicList",
                        Color = "#1E90FF",
                        Category = "社区",
                        IsEnabled = true,
                        ShowCount = 1
                    },
                    new ApiEndpoint {
                        Name = "腾讯",
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
                        Name = "头条",
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
    #endregion
    public class AppConfig
    {
        public int RefreshIntervalMinutes { get; set; } = 10;

        // ===== 新增小说模式专用的配置 =====
        // 工作模式："News" 为新闻模式，"LocalText" 为小说模式
        public string Mode { get; set; } = "News";

        // 小说文件的名称或路径 (默认与程序同目录)
        public string NovelFilePath { get; set; } = "novel.txt";

        // 自动保存的阅读进度（行数书签）
        public int NovelCurrentLine { get; set; } = 0;
        // ==================================
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