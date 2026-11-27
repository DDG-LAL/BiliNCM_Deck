# BiliNCM Deck

BiliNCM Deck 是一个**独立维护**的 B站直播点歌工具，基于 [BiliNCM-Bot](https://github.com/Enkianssus/BiliNCM-Bot) 的 MIT 源码 fork，并把原来分散的 Companion / Overlay / Launcher 能力合并进同一个程序。

> 版本：v1.0.0（首个公开版本）。Companion 队列状态机、配置迁移、浮层与日志功能已完成；Phase 2/3 核心回归通过。
>
> GitHub：<https://github.com/DDG-LAL/BiliNCM_Deck> ｜ 下载：<https://github.com/DDG-LAL/BiliNCM_Deck/releases/latest>

## 目标

- 一个进程、一个便携包
- 一个 5566 页面：OBS 浮层 + 登录/房间号 + 皮肤/背景设置
- 队列、网易云控制、B站弹幕在同一个 C# 程序内
- 不再需要 `BiliNCM-Bot.exe`、`BiliNCM-Companion.ps1`、`BiliNCM-Overlay.ps1`、`overlay-state.json`
- 已完全移除 5555；所有内部 API 统一到 5566
- 不提供强制插队/强制播放；切歌仅限主播和房管
- 与上游 bot 代码/更新完全分离；B站和网易云接口变化仍由本项目维护

## 目录

```text
src/BiliNCM-Deck/          主程序（上游源码 fork + 新功能）
src/BiliNCM-Deck/web/deck/ 5566 浮层与设置页资源
docs/                      架构、决策、POC 与交接文档
reference/                 旧 Companion/Overlay/Launcher 脚本，仅作移植参考
tools/                     构建辅助脚本
```

## 构建

需要 .NET 10 SDK（目标框架 `net10.0-windows`）。

```powershell
# 使用本地安装的 SDK
dotnet build .\src\BiliNCM-Deck\BiliNCM-Deck.csproj
```

> 运行便携包不需要安装 .NET；只有从源码构建时需要 .NET 10 SDK。

## 从旧版迁移

- 新 Deck 与旧 portable 放在同一目录时，首次启动会自动读取旧版 `current\config.json`，并复用根目录的皮肤/背景配置。
- 新 Deck 放在独立目录时，可用 `BiliNCM-Deck.exe --legacy-root "旧 portable 目录"` 导入 `current\config.json`、`overlay-config.json`、`overlay-assets\` 和 `ncm-path.txt`。
- 旧版的日志、`overlay-state.json` 不会迁移。

## 切歌与撤回权限

切歌、撤回当前播放歌曲只接受主播和房管。房管由 B站弹幕数据里的房管标记识别；主播通过机器人登录账号的 UID 识别，所以请用主播账号扫码登录。普通观众可以撤回自己的待播点歌，但不会打断当前歌曲。

`关闭点歌` 只拦截新的普通点歌；房管仍可通过弹幕重新开启，切歌/移除/撤回等管理命令也不受暂停影响。

## 弹幕指令

| 指令 | 可用身份 | 说明 |
|---|---|---|
| `点歌 关键词` | 普通观众 | 加入点歌队列；不会打断当前歌 |
| `置顶点歌 关键词` | 主播 / 房管 / SuperUser | 新请求插入当前歌下一首，不打断当前歌 |
| `撤回` / `取消` | 点歌人本人 | 撤回自己的待播；当前正在播放的歌只有主播/房管能撤回 |
| `移除 关键词` | 主播 / 房管 / SuperUser | 管理移除；按歌名或点歌人匹配待播歌曲 |
| `移除` | 主播 / 房管 / SuperUser | 移除自己最近的一条待播 |
| `置顶 关键词` / `优先 关键词` | 主播 / 房管 / SuperUser | 把指定待播移动到当前歌下一首；连续置顶以最后一次排最前 |
| `切歌` / `跳过` | 主播 / 房管 | 显式切到下一首；普通观众会被拒绝 |
| `关闭点歌` / `开启点歌` | 主播 / 房管 / SuperUser | 只影响新的普通点歌，不影响管理命令 |

`移除` 是管理员清理命令，不是普通观众的撤回入口；普通观众请使用 `撤回`。

> `config.json` 里的 `SkipPermission` 是旧配置遗留字段，当前不参与切歌判定；切歌固定只允许主播和房管，避免误配置放开普通观众。

## 端口

| 端口 | 用途 |
|---|---|
| 5566 | 唯一 HTTP API/页面：OBS 浮层、设置、登录/房间、皮肤/背景、内部兼容 API |
| 9222 | 网易云 CDP |

5555 已完全移除，不再监听、不再有网页入口。

## 托盘与退出

关闭浮层或设置页不会退出程序；B站弹幕、网易云队列和 5566 服务需要在后台继续运行。

- 右键任务栏托盘图标：打开浮层、打开设置、打开日志目录、退出程序
- 设置页底部：退出程序
- 便携包内 `Stop-BiliNCM-Deck.cmd` 仍可作为兜底
- 应用/托盘图标：深色圆角、白色音符、青色广播弧线、红色直播点、粉色外圈（`src/BiliNCM-Deck/Assets/`）

## 运行数据与日志

配置、背景图和日志都保存在 exe 同目录，便携包不会写入系统目录。

- `deck.log`：启动、弹幕命令、权限拒绝、队列引擎、CDP 和异常；10 MB × 3 轮转，敏感字段自动脱敏
- `danmaku.log`：运行期间收到的全部 `DANMU_MSG`，包含本地时间、B站时间、UID、用户名、房管/舰长/粉丝牌和原始内容；10 MB × 3 轮转
- `crash.log`：未处理异常的崩溃栈
- `/api/logs`：最近 1000 条内存日志，便于设置页或调试工具读取
- `/state` 包含 `liveStatus` / `liveStatusSource` / `liveStatusUpdated`；直播状态通过 WS `LIVE`/`PREPARING` 事件和 60 秒低频轮询刷新
- 内置 `--check` 环境诊断仍在计划中；当前可用 `tools/smoke-test.ps1` 做基础检查
- `tools/publish.ps1` 会删除并重建 `publish\`；覆盖已有便携包前先备份 `config.json`、`overlay-config.json`、`overlay-assets\` 和 `deck.log`

## 已知限制

- 重启 Deck 会清空浮层待播列表；NCM 中已存在的待播副本不会自动恢复跟踪。重启前请确认，或重新点歌。
- `置顶` / `优先` 只按歌名或点歌人昵称匹配，不匹配歌手名。
- 房管功能已实现，但仍建议在真实房管账号下复核。

## 许可

本项目沿用上游 MIT 许可与版权声明，详见 `LICENSE` 和 `NOTICE`。本项目与哔哩哔哩、网易云音乐无隶属关系。

## 开发文档

- [交接说明](docs/HANDOFF.md)
- [目标架构](docs/ARCHITECTURE.md)
- [关键决策](docs/DECISIONS.md)
- [POC 验收](docs/POC.md)
- [队列语义移植](docs/QUEUE-PORT.md)
- [Phase 2/3 回归清单](docs/REGRESSION.md)
