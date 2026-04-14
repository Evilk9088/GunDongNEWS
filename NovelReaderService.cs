using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace 桌面新闻
{
    public static class NovelReaderService
    {
        /// <summary>
        /// 流式读取小说的下一截内容
        /// </summary>
        /// <param name="config">配置对象（用于读取和更新书签）</param>
        /// <param name="linesToRead">每次滚动的行数，默认10行</param>
        public static async Task<string> GetNextChunkAsync(AppConfig config, int linesToRead = 10)
        {
            string filePath = Path.Combine(AppContext.BaseDirectory, config.NovelFilePath);

            // 1. 如果没找到文件，自动创建一个示例文件
            if (!File.Exists(filePath))
            {
                string defaultText = "欢迎使用摸鱼小说模式！\n请将你的小说重命名为 novel.txt 并放在程序根目录下。\n此模式支持鼠标悬停暂停，并且会自动记忆你的阅读进度，哪怕关机重启也能无缝接上。";
                await Task.Run(() => File.WriteAllText(filePath, defaultText));
            }

            return await Task.Run(() =>
            {
                // 2. 流式读取：跳过前面已经读过的行，只取我们需要的行数
                var lines = File.ReadLines(filePath)
                                .Skip(config.NovelCurrentLine)
                                .Take(linesToRead)
                                .Where(l => !string.IsNullOrWhiteSpace(l)) // 过滤掉小说中的纯空行
                                .ToList();

                // 3. 判断是否读到了大结局
                if (lines.Count == 0)
                {
                    config.NovelCurrentLine = 0; // 重置进度到第一行
                    lines = File.ReadLines(filePath).Take(linesToRead).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

                    if (lines.Count == 0) return "小说文件内没有找到文字内容。";
                }

                // 4. 更新书签，并保存到本地 json 中
                config.NovelCurrentLine += linesToRead;
                ConfigService.SaveConfig(config);

                // 5. 将这几行文字拼接成一段长文本返回 (使用魔法空格作为段落间隙)
                string separator = "    \u00A0\u00A0\u00A0\u00A0    ";
                return string.Join(separator, lines);
            });
        }
    }
}