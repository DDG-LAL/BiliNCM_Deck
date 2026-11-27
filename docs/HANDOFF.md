# BiliNCM Deck 交接说明

## 目标

把原来的多进程方式：

```text
BiliNCM-Bot.exe + BiliNCM-Companion.ps1 + BiliNCM-Overlay.ps1 + Launcher
```

合并成一个独立工具：

```text
BiliNCM-Deck.exe
  ├─ B站登录/弹幕/房间
  ├─ 网易云 CDP/播放列表/队列
  ├─ 5566 浮层 + 设置 + 登录 + 房间 + 皮肤/背景
  └─ 单实例/配置/日志
```

与上游 BiliNCM-Bot 的代码和发布流程完全分离；不依赖上游自动更新。

## 当前基线

- 项目目录：仓库根目录（clone 后即可）
- 版本：`1.0.0`（首个公开版本，提交以 `git log -1` 为准）
- 工作区：Phase 2/3 核心回归已完成；重启 Deck 不保留待播列表为已知限制
- GitHub：`https://github.com/DDG-LAL/BiliNCM_Deck`
- 便携包：`publish\`（self-contained 单文件）
- 新会话请先读：`docs/HANDOFF.md`、`docs/QUEUE-PORT.md`、`docs/POC.md`；回归前读 `docs/REGRESSION.md`
- 旧 `BiliNCM_Companion` 仓库和旧便携版仅作回退参考，本项目不修改它们

## 当前架构

- 单进程，只监听 `127.0.0.1:5566`
- 5555 已完全移除，不再监听，也不再提供旧网页入口
- 原 5555 的配置、登录、二维码、房间号、日志、退出、队列兼容 API 已迁移到 5566
- 网易云通过 CDP `9222` 操作
- 设置页：`http://127.0.0.1:5566/settings`
- OBS 浮层：`http://127.0.0.1:5566/`

## 已实现

- 上游源码已 fork 到 `src/BiliNCM-Deck/`，项目名 `BiliNCM-Deck.csproj`
- 5566 浮层/设置页已使用旧项目当前版本
- `CompanionQueueEngine.cs` 已完整移植旧 Companion 状态机：
  - `Legacy / New / Upgrade / Pending / Playing / Done`
  - pending qid、FIFO、重复合并、已播升级
  - bot 队列增量、撤回、缺失判定
  - 每轮最多一次 add/move，失败 2/5/10 秒重试
  - NCM 重启后等待稳定并按 `Seq` 恢复缺失待播 d
- 旧配置迁移：
  - 新 Deck 与旧 portable 同目录时自动读取 `current\config.json`
  - 独立目录使用 `--legacy-root "旧目录"`
  - 迁移 `overlay-config.json`、`overlay-assets\`、`ncm-path.txt`
- 背景图处理：
  - JPEG EXIF 方向修正
  - 4000 万像素限制
  - 3840 长边缩放
  - PNG 透明通道
  - JPEG 质量 88 重编码
- 已删除强制插队/强制播放：
  - `立即点歌`、`插队点歌`、`立即播放`、`强制播放`、`插队`
  - 5555/5566 的 `play_now`、`push_current_to_queue`、`skip_current`
  - `ForcePlaySongAsync` 和旧监控/雷达强制逻辑
- 切歌只允许主播和房管：
  - 房管使用 B站 `DANMU_MSG.info[2][2]` 的房管标记
  - 主播通过弹幕 UID 与登录账号 `DedeUserID`（`_config.BiliUid`）比对
  - 不再允许“当前歌点歌人”或 `SuperUsers` 绕过切歌权限
- 持久化日志：
  - `deck.log` 记录启动、配置、命令、权限拒绝、队列引擎、CDP、WebSocket、未处理异常
  - 10 MB × 3 轮转；`SESSDATA`、`Cookie`、`AuthJson`、`qrcode_key` 等自动脱敏
  - `/api/logs` 内存日志保留最近 1000 条
- `关闭点歌` 只拦截新的普通点歌；`开启点歌`、管理命令和撤回在暂停时仍可用
- `撤回` 当前播放歌曲只允许主播/房管；普通观众只能撤回自己的待播歌曲
- 5566 兼容 API 增加本地 Origin 校验；`crash.log` 固定写入程序根目录
- 切歌/移除的 CDP store 提取增加 `#portal_root` 兜底，降低网易云页面加载时序问题
- B站扫码登录兼容新版流程：
  - 从轮询响应 `Set-Cookie` 和 crossDomain/ticket 地址获取登录态
  - 校验 `SESSDATA`，用 nav 接口确认 `isLogin` / `mid`
  - 失败时不再保存空 Cookie 或误报登录成功
- 房间连接增加 Cookie/UID/弹幕 token 校验和具体错误日志
- 托盘图标：打开浮层/设置/日志目录、退出；设置页增加“退出程序”按钮
- 关闭页面不会退出，程序在托盘后台运行
- B站弹幕包解析兼容压缩包内多个内层包和单包内多条 JSON；同时弹幕不再丢失
- `danmaku.log` 记录全部 `DANMU_MSG`（本地时间、B站时间、UID、用户名、房管/舰长/粉丝牌、原文）
- 同一首歌被多人重复点歌时，撤回/移除只删除自己的 bot 队列条目；只有最后一条请求消失时才删除 NCM 副本
- `置顶` / `置顶点歌` 使用 NCM `current+1` 实际移动，连续置顶以最后一次排最前
- `live_status` 采用初始读取 + WebSocket LIVE/PREPARING 事件 + 60 秒低频轮询刷新
- 管理命令权限收紧：
  - 主播/房管/SuperUser 才能 `置顶点歌`、`置顶`、`移除`、`关闭点歌`
  - 普通观众只能点歌和撤回自己的待播
  - 旧配置中 `PriorityPermission` / `ToggleAcceptPermission` / `ForceControlPermission` 的“所有人”默认值会在启动时自动收紧并保存
- `移除` 是管理员清理命令，不是普通观众的自助撤回；普通观众使用 `撤回`
- 应用/托盘图标：`Assets\BiliNCM-Deck.ico`（深色圆角、白色音符、青色广播弧线、红色直播点、粉色外圈）

## 关键文件

```text
src/BiliNCM-Deck/Program.cs                  bot 主逻辑、5566 API、登录/房间、权限
src/BiliNCM-Deck/CompanionQueueEngine.cs     Companion 完整队列状态机
src/BiliNCM-Deck/NcmCdp.cs                   CDP 目标发现 / state / add / move
src/BiliNCM-Deck/DeckServer.cs               5566 HTTP、浮层、设置、背景图处理
src/BiliNCM-Deck/DeckLog.cs                  持久化 deck.log、轮转与脱敏
src/BiliNCM-Deck/DanmakuLog.cs               全部 DANMU_MSG 的 danmaku.log
src/BiliNCM-Deck/TrayHost.cs                 托盘图标与退出菜单
src/BiliNCM-Deck/Assets/BiliNCM-Deck.ico     exe / 托盘应用图标
src/BiliNCM-Deck/web/deck/                   5566 浮层与设置页
src/BiliNCM-Deck/web/ncm/                    Companion JS 表达式
tools/publish.ps1                            self-contained 发布
tools/smoke-test.ps1                         5566/5555 关闭/API/日志 smoke test
docs/REGRESSION.md                           Phase 2/3 现场回归清单
```

## 构建与验证

```powershell
# Debug 构建
dotnet build .\src\BiliNCM-Deck\BiliNCM-Deck.csproj

# portable 发布
powershell -ExecutionPolicy Bypass -File .\tools\publish.ps1

# smoke test
powershell -ExecutionPolicy Bypass -File .\tools\smoke-test.ps1 -Exe .\publish\BiliNCM-Deck.exe
```

自动 smoke test 会检查：

- 5566 `/health`
- 5566 `/settings`
- 5566 `/state`
- 5566 `/api/ncm/state`
- 5566 `/api/config`
- 5555 已关闭

## 尚未完成

- 真实直播弹幕的最终回归（权限、点歌、置顶、撤回、切歌）
- 手动在网易云 UI 拖动/删除/换歌单现场回归
- 内置 `--check` 环境诊断（配置/登录/房间/CDP/素材/端口/日志）
- 旧便携版替换为新 Deck 的切换步骤

## 已知注意点

- 主播识别依赖机器人登录账号就是主播账号；如果使用其他账号登录，主播弹幕默认不会被识别，除非 B站同时给了房管标记。
- 普通点歌不会打断当前歌；`切歌/跳过` 仍是显式打断入口，但现在只有主播/房管可用。
- bot 队列撤回时，Companion 不强制删除 NCM 副本；这是旧 Companion 的原始语义。
- 5555 已移除；任何旧书签/旧 UI 都不应再使用。
- 已持久化 `deck.log`（10 MB × 3 轮转、敏感字段脱敏），排查问题优先看它；`/api/logs` 只保留最近 1000 条内存日志。
- 全部 `DANMU_MSG` 写入独立 `danmaku.log`，用于排查“同时发弹幕丢失”、权限和点歌人问题。
- `ToggleAcceptPermission` 的代码默认值已收紧为房管/SuperUser；已有 `config.json` 若仍是 `MinGuardType=0`、`MinMedalLevel=0`，启动时会记录警告，需要手动收紧。
- `--check` 尚未实现；当前用 `tools/smoke-test.ps1` 和 `deck.log` 做回归前检查。
- `SkipPermission` 是旧配置遗留字段，当前不参与切歌判定；切歌固定看主播/房管，避免误配置放开普通观众。
- 连接直播间时会记录 `live_status`；只有 `1` 表示直播中，`0/2` 可能代表未开播或轮播，最终直播回归建议使用 `1`。
- 关闭浮层/设置页不会退出程序，这是后台常驻语义；请用托盘菜单、设置页“退出程序”或 `Stop-BiliNCM-Deck.cmd` 退出。

## 下一步

1. 在真实直播间做一轮点歌/撤回/切歌/房管权限回归
2. 手动在网易云 UI 拖动、删除、换歌单，确认 5566 浮层同步且不被改回
3. 补内置 `--check` 环境诊断；持久化 `deck.log` 已可用
4. 通过后再考虑用新 Deck 替换旧便携版；通过前保留旧版回退
5. Phase 2/3 通过后提交并上传 GitHub；上传前确认 `config.json`、`deck.log`、`publish\`、`ncm-path.txt` 等运行数据没有入库
