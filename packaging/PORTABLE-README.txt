BiliNCM Deck 便携版
===================

启动：
  双击 Start-BiliNCM-Deck.cmd

页面：
  OBS / 浮层：http://127.0.0.1:5566/
  设置/登录/房间：http://127.0.0.1:5566/settings
  所有内部 API 也在 5566；5555 已移除

关闭：
  右键任务栏托盘图标 -> 退出 BiliNCM Deck
  或设置页底部“退出程序”
  或双击 Stop-BiliNCM-Deck.cmd

说明：
  - 关闭浮层/设置页面不会退出程序；程序在托盘继续运行，OBS 和点歌队列不受影响。
  - 歌词浮层：http://127.0.0.1:5566/lyrics；在 5566/settings 的“歌词浮层”里调整行数、皮肤、翻译和纯歌词字色。
  - 从 1.0.0 升级：下载 BiliNCM-Deck-Updater.exe 放到本目录，退出 Deck 后运行；以后可在设置页“关于 / 更新”检查更新。
  - 重启 Deck 会清空浮层待播列表；NCM 中已存在的待播副本不会自动恢复跟踪。重启前请确认，或重新点歌。
  - 本包已自包含 .NET 运行时，不需要另外安装 .NET 10。
  - 配置、日志、背景图会保存在本目录，不要放进 git。
  - 运行日志：deck.log（10 MB × 3 轮转，敏感字段脱敏）；全部弹幕：danmaku.log（10 MB × 3 轮转）；崩溃栈：crash.log。
  - 队列以网易云播放列表为准：手动拖动/删除优先，当前歌曲不被打断，重复点歌合并。
  - 把本包解压到旧 portable 目录时，首次启动会自动导入 current\config.json；独立目录可用 --legacy-root 指定旧目录。
  - 首次使用若网易云不在常见安装目录，请在 BiliNCM-Deck.exe 同目录建立 ncm-path.txt，写入 cloudmusic.exe 的完整路径；也可先手动启动网易云。
  - B站/网易云接口仍需维护；网易云目标版本 2.10.13。
