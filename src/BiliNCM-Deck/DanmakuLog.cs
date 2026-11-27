using System;
using System.IO;
using System.Text;

namespace BiliNetEaseIntegratedApp
{
    /// <summary>
    /// Persistent log of every DANMU_MSG received from Bilibili. This is
    /// intentionally separate from deck.log so a busy room does not bury
    /// command and queue diagnostics.
    /// </summary>
    internal static class DanmakuLog
    {
        private const long MaxBytes = 10 * 1024 * 1024;
        private const int MaxArchives = 3;
        private static readonly object Sync = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "danmaku.log");

        public static void Append(string message)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message?.Replace("\r", " ").Replace("\n", " ")}{Environment.NewLine}";
                lock (Sync)
                {
                    RotateIfNeeded();
                    File.AppendAllText(FilePath, line, Utf8NoBom);
                }
            }
            catch
            {
                // Danmaku logging must never stop the bot.
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists || info.Length < MaxBytes) return;

                var oldest = FilePath + "." + MaxArchives;
                if (File.Exists(oldest)) File.Delete(oldest);

                for (int i = MaxArchives - 1; i >= 1; i--)
                {
                    var from = FilePath + "." + i;
                    var to = FilePath + "." + (i + 1);
                    if (!File.Exists(from)) continue;
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }

                File.Move(FilePath, FilePath + ".1");
            }
            catch
            {
                // Keep appending to the current file if rotation fails.
            }
        }
    }
}
