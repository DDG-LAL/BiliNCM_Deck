# 目标架构

## 运行形态

一个 `BiliNCM-Deck.exe` 进程：

```text
Bilibili WebSocket / 弹幕
        │
        ▼
  CommandService ──► QueueService ──► Netease CDP ──► 网易云播放列表
        │                 │
        │                 ├─ 当前歌曲/待播队列（内存唯一状态）
        │                 └─ 历史/撤回/去重规则
        ▼
  DeckServer :5566
    ├─ /                 OBS 浮层
    ├─ /settings         登录/房间/皮肤/背景
    ├─ /state            浮层状态
    ├─ /api/overlay-*    配置与上传
    └─ /api/bili-*       扫码登录/登录状态
```

## 端口

- **5566**：唯一 HTTP 地址，包含浮层、设置、登录/房间和内部兼容 API
- **9222**：网易云 CDP

5555 已完全移除，不再监听。

## 模块建议

```text
Core/
  AppConfig.cs        配置模型、读写、迁移
  DeckPaths.cs        根目录、数据目录、日志目录
  Logging.cs          文件/控制台日志
Bilibili/
  BiliLoginService.cs 扫码登录流程（移植 BiliLoginFix 的修复）
  LiveClient.cs       房间连接、弹幕 WebSocket
  CommandService.cs   点歌/置顶/插队/撤回等指令与权限
Netease/
  NcmCdp.cs           CDP 目标发现、注入、重连
  NcmQueue.cs         播放列表读取/添加/移动/删除
  NcmState.cs         当前歌曲/队列快照
Queue/
  CompanionQueueEngine.cs  旧 Companion 完整状态机（pending qid / 重试 / 恢复）
DeckServer/
  HttpServer.cs       5566 HttpListener
  StaticFiles.cs      web/deck 静态资源
  OverlayApi.cs       config/upload/assets/state
  BiliApi.cs          登录/房间接口
  Security.cs         loopback、origin、文件名白名单
Hosting/
  SingleInstance.cs   互斥体
  TrayHost.cs         托盘/退出/打开设置
  BrowserLauncher.cs  只打开 5566
```

## 队列设计（关键）

旧 Companion 的核心语义必须保留：

- 不打断当前正在播放的歌曲
- 网易云播放列表是最终顺序
- 主播在网易云里手动拖动/删除/插入优先
- 重复点歌合并，不重复入队
- 当前歌曲结束后才轮到下一首
- 点歌/置顶/撤回按权限；切歌仅主播和房管；不提供强制插队

实现上不再采用“bot 队列 + Companion 轮询 + NCM 队列”三份状态，而是：

- 网易云 `track_queue` 是唯一播放顺序
- `Program` 只保存点歌人/歌曲元数据，不再决定顺序
- 通过网易云 CDP 读取/修改 NCM 播放列表
- 5566 每次读 NCM 快照，手动拖拽/删除自然优先
- 点歌元数据用于把“已知点歌”从 NCM 完整队列中筛出来显示

## 配置与数据

建议：

```text
DeckData/
  config.json             B站 Cookie/UID/房间号/权限
  overlay-config.json     皮肤/背景参数
  assets/                 背景图
  logs/
    deck.log
```

迁移来源：

- `current/config.json`：B站登录/房间/权限
- `overlay-config.json`：皮肤/背景
- `overlay-assets/`：背景图

迁移时必须保留原文件，复制而非移动。

## 安全

- 只监听 127.0.0.1
- 5566 API 只允许本机 Origin
- 图片上传限制类型、大小、像素、文件名
- 设置页永远不返回 Cookie
- 日志对 SESSDATA/BiliCookie 脱敏
- 不把配置、日志、背景图放进入库目录
