using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BiliNCMDeck.Updater
{
    internal static class Program
    {
        private const string LatestApi = "https://api.github.com/repos/DDG-LAL/BiliNCM_Deck/releases/latest";
        private const string ReleasePage = "https://github.com/DDG-LAL/BiliNCM_Deck/releases/latest";
        private static readonly HttpClient Http = CreateHttpClient();

        private sealed class Options
        {
            public bool Auto;
            public string Target = "";
            public int WaitPid;
        }

        private sealed class ReleaseInfo
        {
            public string Version = "";
            public string ReleaseUrl = ReleasePage;
            public string Notes = "";
            public string ZipUrl = "";
            public string ShaUrl = "";
        }

        private static async Task<int> Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var options = ParseArgs(args);
                return await RunAsync(options);
            }
            catch (Exception ex)
            {
                Log("");
                Log("更新失败：" + ex.Message, ConsoleColor.Red);
                Log("最新版下载页：" + ReleasePage);
                Pause(true);
                return 2;
            }
        }

        private static Options ParseArgs(string[] args)
        {
            var options = new Options
            {
                Target = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ""
            };
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--auto":
                        options.Auto = true;
                        break;
                    case "--target":
                        if (i + 1 < args.Length) options.Target = args[++i];
                        break;
                    case "--wait-pid":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out options.WaitPid);
                        break;
                }
            }
            return options;
        }

        private static async Task<int> RunAsync(Options options)
        {
            string target = Path.GetFullPath(options.Target).TrimEnd(Path.DirectorySeparatorChar);
            string targetExe = Path.Combine(target, "BiliNCM-Deck.exe");
            if (!File.Exists(targetExe))
            {
                Log("找不到 BiliNCM-Deck.exe：" + targetExe, ConsoleColor.Red);
                Log("请把 Updater 放到 Deck 程序目录后再运行。");
                Pause(!options.Auto);
                return 2;
            }

            if (options.WaitPid > 0) WaitForPid(options.WaitPid, 30);
            if (!EnsureDeckClosed(target, targetExe, options.Auto))
            {
                Log("Deck 仍在运行，请退出后重试。", ConsoleColor.Red);
                Pause(!options.Auto);
                return 3;
            }

            string currentVersion = ReadVersion(targetExe);
            Log("当前版本：" + currentVersion);

            ReleaseInfo release;
            try
            {
                release = await GetLatestReleaseAsync();
            }
            catch (Exception ex)
            {
                Log("无法获取 GitHub 最新版本：" + ex.Message, ConsoleColor.Red);
                Log("可以手动下载最新版：" + ReleasePage);
                Pause(true);
                return 4;
            }

            Log("最新版本：" + release.Version);
            if (CompareVersions(release.Version, currentVersion) <= 0)
            {
                Log("当前已经是最新版本，不需要更新。", ConsoleColor.Green);
                Pause(!options.Auto);
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(release.Notes))
            {
                Log("");
                Log("更新说明：");
                Log(release.Notes.Length > 1500 ? release.Notes.Substring(0, 1500) + "..." : release.Notes);
            }

            if (!options.Auto)
            {
                Console.Write("是否更新到 v" + release.Version + "？[Y/n] ");
                string answer = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(answer) &&
                    !answer.Equals("y", StringComparison.OrdinalIgnoreCase) &&
                    !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    Log("已取消。");
                    return 0;
                }
            }

            string work = Path.Combine(Path.GetTempPath(), "BiliNCM-Deck-Update-" + Guid.NewGuid().ToString("N"));
            string staging = Path.Combine(work, "staging");
            string zipPath = Path.Combine(work, "update.zip");
            string shaPath = Path.Combine(work, "update.zip.sha256");
            string backup = Path.Combine(target, "backup-v" + currentVersion + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            bool backupCreated = false;

            try
            {
                Directory.CreateDirectory(staging);
                Log("");
                Log("正在下载更新包...");
                await DownloadAsync(release.ZipUrl, zipPath);
                Log("正在校验 SHA256...");
                await DownloadAsync(release.ShaUrl, shaPath);
                string expected = ReadHash(File.ReadAllText(shaPath));
                string actual = ComputeSha256(zipPath);
                if (string.IsNullOrWhiteSpace(expected) ||
                    !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("SHA256 校验失败");
                }
                Log("校验通过。", ConsoleColor.Green);

                ValidateZip(zipPath);
                ZipFile.ExtractToDirectory(zipPath, staging);
                string sourceRoot = FindAppRoot(staging);
                if (sourceRoot == null)
                    throw new InvalidOperationException("更新包中找不到 BiliNCM-Deck.exe");

                Log("正在备份当前版本到 " + Path.GetFileName(backup) + " ...");
                BackupApp(target, backup);
                backupCreated = true;

                Log("正在替换程序文件...");
                ApplyApp(sourceRoot, target);

                Log("正在启动新版 Deck...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    WorkingDirectory = target,
                    UseShellExecute = true
                });

                Log("");
                Log("更新完成：v" + currentVersion + " -> v" + release.Version, ConsoleColor.Green);
                Log("登录、配置、背景、歌词设置和日志均已保留。");
                Log("旧版本备份：" + backup);
                Pause(!options.Auto);
                return 0;
            }
            catch (Exception ex)
            {
                Log("");
                Log("更新失败：" + ex.Message, ConsoleColor.Red);
                if (backupCreated)
                {
                    try
                    {
                        RestoreApp(target, backup);
                        Log("已自动恢复旧版本。", ConsoleColor.Yellow);
                    }
                    catch (Exception restoreEx)
                    {
                        Log("自动恢复失败：" + restoreEx.Message, ConsoleColor.Red);
                        Log("请手动把备份目录中的程序文件复制回原目录：" + backup);
                    }
                }
                Log("当前版本未被更新。可以稍后重试，或手动下载：" + release.ReleaseUrl);
                Pause(true);
                return 5;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        private static async Task<ReleaseInfo> GetLatestReleaseAsync()
        {
            string json = await Http.GetStringAsync(LatestApi);
            var serializer = new JavaScriptSerializer();
            var root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null) throw new InvalidOperationException("GitHub 返回为空");

            string tag = Convert.ToString(root.ContainsKey("tag_name") ? root["tag_name"] : "", CultureInfo.InvariantCulture);
            var info = new ReleaseInfo
            {
                Version = tag.Trim().TrimStart('v', 'V'),
                ReleaseUrl = Convert.ToString(root.ContainsKey("html_url") ? root["html_url"] : ReleasePage, CultureInfo.InvariantCulture),
                Notes = Convert.ToString(root.ContainsKey("body") ? root["body"] : "", CultureInfo.InvariantCulture)
            };

            var assets = root.ContainsKey("assets") ? root["assets"] as IEnumerable : null;
            if (assets != null)
            {
                foreach (var item in assets)
                {
                    var asset = item as Dictionary<string, object>;
                    if (asset == null) continue;
                    string name = Convert.ToString(asset.ContainsKey("name") ? asset["name"] : "", CultureInfo.InvariantCulture);
                    string url = Convert.ToString(asset.ContainsKey("browser_download_url") ? asset["browser_download_url"] : "", CultureInfo.InvariantCulture);
                    if (name.StartsWith("BiliNCM-Deck-v", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ZipUrl = url;
                    }
                    else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ShaUrl = url;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(info.Version) || string.IsNullOrWhiteSpace(info.ZipUrl) || string.IsNullOrWhiteSpace(info.ShaUrl))
                throw new InvalidOperationException("最新 Release 缺少 zip 或 sha256 附件");
            return info;
        }

        private static async Task DownloadAsync(string url, string path)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            using var input = await response.Content.ReadAsStreamAsync();
            using var output = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            int lastPercent = -1;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await output.WriteAsync(buffer, 0, read);
                done += read;
                if (total > 0)
                {
                    int percent = (int)(done * 100 / total);
                    if (percent != lastPercent && (percent % 5 == 0 || percent == 100))
                    {
                        Console.Write("\r下载中 " + percent + "%");
                        lastPercent = percent;
                    }
                }
            }
            Console.WriteLine();
        }

        private static bool EnsureDeckClosed(string target, string targetExe, bool auto)
        {
            var running = FindDeckProcesses(targetExe);
            if (running.Count == 0) return true;

            Log("检测到 Deck 正在运行。更新必须关闭整个 Deck。", ConsoleColor.Yellow);
            TryPrintPlaybackWarning();
            if (!auto)
            {
                Console.Write("是否让 Updater 请求 Deck 退出？[Y/n] ");
                string answer = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(answer) &&
                    !answer.Equals("y", StringComparison.OrdinalIgnoreCase) &&
                    !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var content = new StringContent("", Encoding.UTF8, "application/json");
                client.PostAsync("http://127.0.0.1:5566/api/exit", content).GetAwaiter().GetResult();
            }
            catch { }

            for (int i = 0; i < 30; i++)
            {
                System.Threading.Thread.Sleep(300);
                if (FindDeckProcesses(targetExe).Count == 0) return true;
            }

            if (!auto)
            {
                Log("请手动右键托盘退出 Deck，然后按回车继续...");
                Console.ReadLine();
                return FindDeckProcesses(targetExe).Count == 0;
            }
            return false;
        }

        private static void TryPrintPlaybackWarning()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                string json = client.GetStringAsync("http://127.0.0.1:5566/state").GetAwaiter().GetResult();
                var serializer = new JavaScriptSerializer();
                var state = serializer.Deserialize<Dictionary<string, object>>(json);
                int pending = 0;
                int live = -1;
                if (state != null && state.ContainsKey("pendingCount"))
                    int.TryParse(Convert.ToString(state["pendingCount"], CultureInfo.InvariantCulture), out pending);
                if (state != null && state.ContainsKey("liveStatus"))
                    int.TryParse(Convert.ToString(state["liveStatus"], CultureInfo.InvariantCulture), out live);
                if (pending > 0 || live == 1)
                {
                    Log("提醒：更新会重启 Deck，当前待播列表会清空" +
                        (pending > 0 ? "（待播 " + pending + " 首）" : "") +
                        (live == 1 ? "；当前正在直播" : "") + "。", ConsoleColor.Yellow);
                }
            }
            catch { }
        }

        private static List<Process> FindDeckProcesses(string targetExe)
        {
            var result = new List<Process>();
            foreach (var process in Process.GetProcessesByName("BiliNCM-Deck"))
            {
                try
                {
                    string path = process.MainModule == null ? "" : process.MainModule.FileName;
                    if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
                        result.Add(process);
                }
                catch { }
            }
            return result;
        }

        private static void WaitForPid(int pid, int seconds)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.WaitForExit(seconds * 1000))
                    throw new InvalidOperationException("等待 Deck 退出超时");
            }
            catch (ArgumentException) { }
        }

        private static string ReadVersion(string exePath)
        {
            try
            {
                string product = FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? "";
                string clean = product.Trim().TrimStart('v', 'V');
                int plus = clean.IndexOf('+');
                if (plus >= 0) clean = clean.Substring(0, plus);
                int dash = clean.IndexOf('-');
                if (dash >= 0) clean = clean.Substring(0, dash);
                return clean;
            }
            catch
            {
                return "0.0.0";
            }
        }

        private static int CompareVersions(string left, string right)
        {
            int[] a = ParseVersion(left);
            int[] b = ParseVersion(right);
            for (int i = 0; i < 3; i++)
            {
                int result = a[i].CompareTo(b[i]);
                if (result != 0) return result;
            }
            return 0;
        }

        private static int[] ParseVersion(string value)
        {
            string clean = (value ?? "").Trim().TrimStart('v', 'V');
            string[] parts = clean.Split('.');
            var result = new int[3];
            for (int i = 0; i < result.Length && i < parts.Length; i++)
                int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]);
            return result;
        }

        private static string ReadHash(string text)
        {
            foreach (string token in (text ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length == 64 && token.All(Uri.IsHexDigit)) return token;
            }
            return "";
        }

        private static string ComputeSha256(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static void ValidateZip(string zipPath)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (Path.IsPathRooted(name) || name.Split('/').Any(part => part == ".."))
                    throw new InvalidOperationException("更新包包含非法路径");
            }
        }

        private static string FindAppRoot(string staging)
        {
            string direct = Path.Combine(staging, "BiliNCM-Deck.exe");
            if (File.Exists(direct)) return staging;
            foreach (string exe in Directory.GetFiles(staging, "BiliNCM-Deck.exe", SearchOption.AllDirectories))
                return Path.GetDirectoryName(exe);
            return null;
        }

        private static void BackupApp(string target, string backup)
        {
            Directory.CreateDirectory(backup);
            foreach (string name in AppFiles())
            {
                string source = Path.Combine(target, name);
                string destination = Path.Combine(backup, name);
                if (File.Exists(source)) CopyFile(source, destination);
                else if (Directory.Exists(source)) CopyDirectory(source, destination);
            }
        }

        private static void ApplyApp(string sourceRoot, string target)
        {
            foreach (string name in AppFiles())
            {
                string source = Path.Combine(sourceRoot, name);
                string destination = Path.Combine(target, name);
                if (File.Exists(source)) CopyFile(source, destination);
                else if (Directory.Exists(source))
                {
                    if (Directory.Exists(destination)) Directory.Delete(destination, true);
                    CopyDirectory(source, destination);
                }
            }
        }

        private static void RestoreApp(string target, string backup)
        {
            foreach (string name in AppFiles())
            {
                string source = Path.Combine(backup, name);
                string destination = Path.Combine(target, name);
                if (File.Exists(source)) CopyFile(source, destination);
                else if (Directory.Exists(source))
                {
                    if (Directory.Exists(destination)) Directory.Delete(destination, true);
                    CopyDirectory(source, destination);
                }
            }
        }

        private static IEnumerable<string> AppFiles()
        {
            return new[]
            {
                "BiliNCM-Deck.exe",
                "BiliNCM-Deck.ico",
                "web",
                "README.md",
                "LICENSE",
                "NOTICE",
                "PORTABLE-README.txt",
                "Start-BiliNCM-Deck.cmd",
                "Stop-BiliNCM-Deck.cmd",
                "ncm-path.txt.example"
            };
        }

        private static void CopyFile(string source, string destination)
        {
            string dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.Copy(source, destination, true);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
                CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (string sub in Directory.GetDirectories(source))
                CopyDirectory(sub, Path.Combine(destination, Path.GetFileName(sub)));
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BiliNCM-Deck-Updater/1.1.1");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            return client;
        }

        private static void Log(string text, ConsoleColor color = ConsoleColor.Gray)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = old;
        }

        private static void Pause(bool always)
        {
            if (!always) return;
            try
            {
                Console.WriteLine();
                Console.Write("按回车退出...");
                Console.ReadLine();
            }
            catch { }
        }
    }
}
