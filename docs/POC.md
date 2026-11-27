# POC 验收与 Deck 1.1.1 状态

## 已通过

- [x] `dotnet build` 成功，0 errors
- [x] 单进程启动：不再启动 `BiliNCM-Bot.exe` / `BiliNCM-Companion.ps1`
- [x] 5555 已完全移除，所有原 API 迁移到 5566
- [x] 5566 `/health` 正常
- [x] 5566 `/settings` 返回当前设置页（含登录/房间/皮肤/背景）
- [x] 5566 `/state` 返回 Companion 状态机快照（当前歌 + 精确 pending d）
- [x] 5566 `/api/ncm/state` 返回真实网易云 `track_queue` / 当前索引
- [x] 设置页通过 5566 `/api/config` 读取，无控制台错误
- [x] 5566 `/api/bili/qrstart` + `/api/bili/qrstatus` 可扫码登录
- [x] 启动不自动打开旧后台；`--no-browser` 可用于测试
- [x] self-contained single-file 发布成功（无需系统 .NET 10）
- [x] 便携包含启动/关闭脚本
- [x] 完整 Companion 状态机移植：pending qid、升级、撤回、重试、NCM/bot 缺失判定
- [x] 网易云重启后按序恢复真正缺失的待播 d
- [x] 旧配置自动迁移（同目录或 `--legacy-root`）
- [x] 背景图 EXIF 旋转、4000 万像素限制、3840 长边缩放
- [x] 删除强制插队/强制播放；切歌限制为主播和房管
- [x] 持久化 `deck.log`：启动/命令/权限/引擎/CDP/异常，10 MB × 3 轮转，敏感字段脱敏
- [x] `关闭点歌` 只拦截新的普通点歌；暂停时仍可用 `开启点歌` 和管理命令
- [x] 撤回当前播放歌曲仅主播/房管；普通观众只能撤回待播
- [x] 5566 兼容 API 本地 Origin 校验；`crash.log` 固定写入程序根目录
- [x] 切歌/移除 CDP store 提取支持 `#portal_root`
- [x] 扫码登录兼容新版 B站流程：Set-Cookie + crossDomain/ticket + nav 校验（已在 Phase 2 真实扫码验证）
- [x] 房间连接校验 Cookie/UID/弹幕 token，并返回具体错误
- [x] 托盘图标 + 设置页退出按钮；关闭页面不退出后台
- [x] 自定义音符/直播图标应用到 exe 和托盘
- [x] 管理命令权限收紧：普通观众不能置顶点歌、切歌、关闭点歌或移除他人待播
- [x] 全部 `DANMU_MSG` 写入独立 `danmaku.log`
- [x] B站 Brotli 批量包解析支持多个内层包/多条 JSON（同时点歌不再丢失）
- [x] 重复点歌撤回只删除自己的请求，最后一条消失才删除 NCM 副本
- [x] `置顶`/`置顶点歌` 实际移动到当前歌下一位，连续置顶最后一次优先
- [x] `live_status` 初始读取 + WS LIVE/PREPARING + 60 秒低频刷新
- [x] CDP 已就绪时不重启网易云；缺少 CDP 时才按 `ncm-path.txt` 查找并启动

## 尚未完成

- [ ] 真实直播弹幕回归（权限、点歌、置顶、撤回、切歌）
- [ ] 手动网易云 UI 拖动/删除/换歌单现场回归
- [ ] 内置 `--check` 环境诊断（配置/登录/房间/CDP/素材/端口/日志）

## 当前数据流

```text
bot _targetQueue / _currentPlayingSong
        │
        ▼
CompanionQueueEngine（1 秒轮询，完整旧 Companion 状态机）
        │  ├─ bot 队列增量 / known 状态
        │  ├─ NCM track_queue / pending qid / 重启恢复
        │  └─ 每轮最多一次 add/move
        ▼
DeckServer :5566 /state → OBS 浮层
DeckServer :5566 /api/ncm/state → 调试/诊断
```

点歌/置顶/撤回仍通过 bot 原生命令修改 `_targetQueue`；引擎按 NCM 实际状态完成加入、合并、移动、重试和显示。详见 `QUEUE-PORT.md`。

## 日志与诊断差异

- 新项目已有 5566 `/health`、`/state`、`/api/ncm/state`，以及 `/api/logs` 的最近 1000 条内存日志。
- `Program.WriteLog` 和队列引擎日志现在统一持久化到程序根目录 `deck.log`：
  - 启动 banner、配置摘要、命令/权限拒绝、引擎 request/add/move/retry、CDP 失败、WebSocket 重连、未处理异常
  - 10 MB × 3 轮转；Cookie/SESSDATA/AuthJson/qrcode_key 等自动脱敏
- 全部 `DANMU_MSG` 写入独立 `danmaku.log`，包含本地时间、B站时间、UID、用户名、房管/舰长/粉丝牌和原文；10 MB × 3 轮转。
- B站 Brotli 批量包会解析压缩包内所有内层包和同一包内的多条 JSON，避免同时弹幕丢失。
- 旧项目额外有 `launcher.log`、`companion.log`、`overlay.log`、`bot-runtime.log`，以及 `-CheckOnly`、`-DebugLogin`、`Test-NcmCdp.ps1`、`Capture-BotLogs.ps1`。
- 新项目仍缺内置 `--check` 环境诊断；旧脚本不一定要逐个照搬。
