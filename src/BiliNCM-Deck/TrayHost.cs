using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace BiliNetEaseIntegratedApp
{
    /// <summary>
    /// Background tray icon. The deck is a service: closing the overlay or
    /// settings tab must not stop the Bilibili WebSocket or queue engine.
    /// </summary>
    internal sealed class TrayHost : IDisposable
    {
        private readonly Action _openOverlay;
        private readonly Action _openSettings;
        private readonly Action _openLogs;
        private readonly Action _exit;
        private Thread? _thread;
        private NotifyIcon? _icon;
        private ContextMenuStrip? _menu;
        private Icon? _iconImage;

        public TrayHost(Action openOverlay, Action openSettings, Action openLogs, Action exit)
        {
            _openOverlay = openOverlay;
            _openSettings = openSettings;
            _openLogs = openLogs;
            _exit = exit;
        }

        public void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = false, Name = "BiliNCM Deck Tray" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void NotifyUpdateAvailable(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            try
            {
                _icon?.ShowBalloonTip(
                    5000,
                    "发现新版本 v" + version,
                    "打开设置页查看更新；更新会重启 Deck。",
                    ToolTipIcon.Info);
            }
            catch { }
        }

        private void Run()
        {
            try
            {
                Application.EnableVisualStyles();
                _menu = new ContextMenuStrip();
                _menu.Items.Add("打开浮层", null, (_, __) => SafeInvoke(_openOverlay));
                _menu.Items.Add("打开设置", null, (_, __) => SafeInvoke(_openSettings));
                _menu.Items.Add("打开日志目录", null, (_, __) => SafeInvoke(_openLogs));
                _menu.Items.Add(new ToolStripSeparator());
                _menu.Items.Add("退出 BiliNCM Deck", null, (_, __) => SafeInvoke(_exit));

                _iconImage = LoadIcon();
                _icon = new NotifyIcon
                {
                    Icon = _iconImage,
                    Text = "BiliNCM Deck",
                    Visible = true,
                    ContextMenuStrip = _menu
                };
                _icon.DoubleClick += (_, __) => SafeInvoke(_openSettings);
                try { _icon.ShowBalloonTip(2500, "BiliNCM Deck", "程序在托盘运行；右键托盘图标可退出。", ToolTipIcon.Info); } catch { }
                Application.Run();
            }
            catch (Exception ex)
            {
                Program.WriteLog("[托盘] 启动失败: " + ex.Message, ConsoleColor.Yellow);
            }
        }

        private static Icon LoadIcon()
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var icon = Icon.ExtractAssociatedIcon(path);
                    if (icon != null) return icon;
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        private static void SafeInvoke(Action action)
        {
            try { action(); }
            catch (Exception ex) { Program.WriteLog("[托盘] 操作失败: " + ex.Message, ConsoleColor.Yellow); }
        }

        public void Dispose()
        {
            try
            {
                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                    _icon = null;
                }
            }
            catch { }
            try
            {
                _menu?.Dispose();
                _menu = null;
            }
            catch { }
            try
            {
                if (_iconImage != null && !ReferenceEquals(_iconImage, SystemIcons.Application))
                    _iconImage.Dispose();
                _iconImage = null;
            }
            catch { }
        }
    }
}
