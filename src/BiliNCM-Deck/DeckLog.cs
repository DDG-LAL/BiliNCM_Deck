using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BiliNetEaseIntegratedApp
{
    /// <summary>
    /// Persistent deck.log writer. All runtime and queue-engine logs go
    /// through here so a live session can be reconstructed after the fact.
    /// </summary>
    internal static class DeckLog
    {
        private const long MaxBytes = 10 * 1024 * 1024;
        private const int MaxArchives = 3;

        private static readonly object Sync = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // Redact common credential values before they reach the console,
        // the in-memory /api/logs ring or deck.log.
        private static readonly Regex SecretPattern = new Regex(
            @"(?i)\b(SESSDATA|bili_jct|DedeUserID__ckMd5|qrcode_key|Authorization|Cookie|BiliCookie|AuthJson|access_key|refresh_token)\b[""']?\s*[:=]\s*[^\r\n]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "deck.log");

        public static string Redact(string? message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            return SecretPattern.Replace(message, match => match.Groups[1].Value + "=***");
        }

        public static void Append(string level, string message)
        {
            try
            {
                var safe = Redact(message);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {safe}{Environment.NewLine}";
                lock (Sync)
                {
                    RotateIfNeeded();
                    File.AppendAllText(FilePath, line, Utf8NoBom);
                }
            }
            catch
            {
                // Logging must never take the application down.
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
                // If rotation fails, keep appending to the current file.
            }
        }
    }
}
