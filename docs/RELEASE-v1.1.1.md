# BiliNCM Deck v1.1.1

新增独立 Updater 和主程序内置更新。

## 从 1.0.0 升级

1. 到 Releases 下载 `BiliNCM-Deck-Updater.exe`
2. 放到 `BiliNCM-Deck.exe` 同目录
3. 建议先下播，然后退出 Deck
4. 双击 Updater，按提示更新
5. 完成后自动重启

Updater 会校验 SHA256、备份旧版本；失败时自动回滚。登录、配置、背景、歌词设置和日志都会保留。

## 以后版本

设置页 →「关于 / 更新」→「检查更新」→「下载并更新」。

- 自动检查每 48 小时一次，只提示，不自动安装
- 「当前版本不再提示」只忽略当前新版本，后续新版本会再次提示
- 有待播或正在直播时会先提示：更新必须关闭整个 Deck，待播列表会清空
- 更新失败自动回滚，当前版本不受影响
- GitHub 无法访问时提供发布页手动下载链接，不阻塞当前版本使用

## 设置与数据

- 内置更新会退出并重启 Deck
- 更新只替换程序文件，不覆盖 `config.json`、`overlay-config.json`、`lyrics-config.json`、`ncm-path.txt`、`overlay-assets/` 和日志
- 旧版本备份保存在程序目录 `backup-v旧版本-时间/`

## 已知限制

- 更新期间 Deck 会短暂停止，直播浮层和点歌会中断
- 当前待播列表会在重启后清空
- 未签名 Updater 可能触发 Windows SmartScreen 提示
- 网易云音乐只支持 2.10.13；3.x 不兼容
