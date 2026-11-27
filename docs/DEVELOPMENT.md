# 开发与实现记录

> 本文供开发和维护记录使用。普通用户请先看项目根目录的 `README.md`。

## 项目目标

- 一个进程、一个便携包，尽量降低直播时的部署和维护成本
- 只监听 `127.0.0.1:5566`，OBS 浮层、设置页和内部 API 统一入口
- 队列以网易云播放列表为准，当前歌曲不被打断
- 保留主播/房管的切歌、移除、置顶和暂停点歌能力
- 不提供强制插播、强制播放和自动更新

## 目录结构

```text
src/BiliNCM-Deck/           主程序
src/BiliNCM-Deck/web/deck/  5566 浮层与设置页
src/BiliNCM-Deck/web/ncm/   网易云 CDP 操作脚本
src/BiliNCM-Deck/LyricsService.cs  歌词抓取、LRC 解析、外部同步源与播放位置
docs/                       架构、决策、交接、回归与发布记录
packaging/                  便携包启动/停止脚本和说明
reference/                  旧 Companion/Overlay/Launcher 脚本，仅作移植参考
tools/                      构建、发布和冒烟测试脚本
```

## 构建与发布

需要 .NET 10 SDK（目标框架 `net10.0-windows`）。

```powershell
# Debug 构建
dotnet build .\src\BiliNCM-Deck\BiliNCM-Deck.csproj

# 生成 self-contained 便携包到 publish\
powershell -ExecutionPolicy Bypass -File .\tools\publish.ps1

# 冒烟测试
powershell -ExecutionPolicy Bypass -File .\tools\smoke-test.ps1 -Exe .\publish\BiliNCM-Deck.exe
```

发布前注意：

- `tools\publish.ps1` 会删除并重建 `publish\`；先备份 `config.json`、`overlay-config.json`、`overlay-assets\` 和日志
- GitHub Release 的 zip 不应包含 `config.json`、`overlay-config.json`、`lyrics-config.json`、`ncm-path.txt`、`overlay-assets\`、`deck.log`、`danmaku.log`、`crash.log` 或 PDB
- 版本号在 `src/BiliNCM-Deck/BiliNCM-Deck.csproj` 的 `<Version>` 中维护
- 发布后确认 `/health` 返回的 `version` 与 tag 一致
- 发布 zip 建议附带 SHA256 校验文件

## 当前技术结构

- 单进程 WinForms + WPF 混合；托盘由 `TrayHost.cs` 管理
- `DeckServer.cs` 提供 5566 HTTP API、设置页、浮层和内部兼容接口
- `CompanionQueueEngine.cs` 维护 bot 队列与 NCM 队列的状态机
- `NcmCdp.cs` 通过网易云 CDP 端口 `9222` 操作播放列表
- `DanmakuLog.cs` / `DeckLog.cs` 分别记录全量弹幕和运行日志
- `LyricsService.cs` 负责网易云/LRCLIB 歌词、LRC 解析和播放位置计算
- `web/deck/BiliNCM-Lyrics.html` 是独立歌词浮层，播放位置随网易云状态校正
- B 站弹幕 WebSocket 支持 Brotli 批量包，递归解析内层包和多条 JSON

## 已移除的旧内容

- 5555 端口和旧网页入口已完全移除，原 API 迁移到 5566
- `立即点歌`、`插队点歌`、`立即播放`、`强制播放`、`插队` 等强制插播/播放命令已删除
- `ForcePlaySongAsync` 和旧监控/雷达强制逻辑已删除
- Velopack 自动更新依赖已移除
- `SkipPermission` 是旧配置遗留字段，当前不参与切歌判定；切歌固定只允许主播和房管

## 队列与实现细节

- [目标架构](ARCHITECTURE.md)
- [关键决策](DECISIONS.md)
- [队列语义移植](QUEUE-PORT.md)
- [交接说明](HANDOFF.md)
- [POC 验收](POC.md)

## 回归与发布记录

- [Phase 2/3 回归清单](REGRESSION.md)
- [Phase 2/3 当前状态](PHASE2-PENDING.md)
- [v1.1.0 发布说明](RELEASE-v1.1.0.md)

## 已知限制

- 重启 Deck 会清空浮层待播列表；NCM 中已存在的待播副本不会自动恢复跟踪
- `置顶` / `优先` 只匹配歌名或点歌人昵称，不匹配歌手名
- 房管功能已实现，但发布前未使用真实房管账号完整复测
- 网易云音乐目标版本为 2.10.13；3.x 不兼容
