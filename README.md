# Steam × Xbox HDR Screenshot Helper / 截图助手

![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)

**English**

A portable Windows tray helper for Steam screenshots. Press the configured Steam screenshot key once: Steam saves its original JPG, then Xbox Game Bar takes an HDR screenshot. The helper validates and copies the original JPEG XR into the same per-game Steam `screenshots` folder. It never injects into games, converts images, uploads pictures, or needs administrator rights.

Experimental: real Game Bar JPEG XR samples and automated file checks pass. Verify shortcut capture with your own game and display before relying on it.

Download the self-contained Windows x64 ZIP from [Releases](../../releases/latest). See below for Chinese setup and troubleshooting. Development and privacy guidance are in [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md). Licensed under MIT.

---

Windows 11 x64 便携工具，版本 0.1.1。在 Steam 游戏中按截图键，保留 Steam 原截图，同时发送 Game Bar 截图快捷键，将实际生成的 HDR JXR 直接复制到该游戏的 `screenshots` 文件夹，与 Steam JPG 放在一起。已使用本机《巫师 3》真实 Game Bar 半浮点 JXR 验证解码、复制与哈希一致性；新版自动联动仍需在下一次游戏截图时确认。

## 开始使用

1. 解压 `SteamXboxHdr-win-x64.zip`，双击 `SteamXboxHdr.exe`。无需安装 .NET，无需管理员权限。
2. 查看窗口中的自动检测结果。Steam 安装路径和 Windows 捕获目录留空即可自动识别；若检测位置有误，可浏览选择。
3. 确认工具的截图键与 **Steam → 设置 → 游戏中 → 截图快捷键** 一致。默认 F12，目前支持无修饰键的 F1–F24。
4. 点击「启用截图联动」，进入已经安装的 Steam 游戏，确认游戏所在显示器已开启 Windows HDR，然后按一次截图键。
5. 等待约 6 秒收集图片，然后进行解码校验与复制。日志显示归档成功后，可点击「最近归档」。高分辨率图片可能额外耗时数秒，请等本轮完成后再截图。

窗口右上角关闭按钮会收起到托盘。退出请右键托盘图标 →「退出」。首次运行默认暂停；程序不会添加开机启动项。手动修改路径或截图键后，请暂停并重新启用。

## 文件去哪里

```text
Steam/userdata/<本次截图账号>/760/remote/<游戏ID>/screenshots/
├─ 原 Steam 截图.jpg
├─ 原截图名__XboxHDR__请求ID.jxr
├─ 原截图名__XboxHDR__请求ID.png  # 可在设置中关闭 SDR 副本归档
└─ .metadata/                  # 隐藏目录，包含来源、时间、游戏及 SHA-256
   └─ 原截图名__XboxHDR__请求ID.json
```

Steam 原图、缩略图、screenshots.vdf 和 Game Bar 源文件保持原样。工具在对应游戏的截图目录创建带 `__XboxHDR__` 标记的新副本；复制失败会清理本轮创建的副本，不删除源图片。不上传图片，无账号登录、网络遥测或运行时联网功能。

两个捕获过程有延迟，不能保证同一帧。请求 ID 用于避免重复归档覆盖已有图片。

## 自动识别和配对规则

- 从当前用户注册表读取 Steam 安装路径，从 `libraryfolders.vdf` 和游戏 manifest 读取库与安装目录。
- 识别当前前台进程所属 Steam 游戏，再在本轮新增截图中确认真实账号和截图目录；不选择「第一个账号」。
- 使用 Windows Known Folder API 获取捕获目录，支持系统已重定向的捕获文件夹。
- 只读检查当前用户 Game Bar 安装版本及捕获禁用设置。安装成功不代表特定游戏一定可捕获。
- 按键时检查游戏窗口所在显示器的 Windows HDR 状态。Windows 11 24H2 及更新系统使用 `activeColorMode` 区分 HDR 与普通广色域；无法判定时跳过联动。
- 原始截图按键始终传递给 Steam。松开截图键后，在前台窗口未变化的情况下发送 `Win + Alt + PrintScreen`。
- 只接受本轮唯一的 Steam 截图和唯一的 JXR，并核对 Steam 游戏 ID 与前台游戏。PNG 必须与 JXR 同名才归档。
- JXR 须通过文件签名、原生 WIC 像素格式 GUID 和 Windows 解码检查。支持 Game Bar 输出的半浮点 RGBA；即便 WPF 将格式显示为 Default 也能识别。校验只在内存解码，归档仍逐字节复制原文件，不把 SDR 改扩展名当 HDR。
- 图片大小和写入时间稳定后，以只读共享方式打开源文件复制，校验源与副本 SHA-256，元数据最后提交。

## 当前限制

- 已验证本机《巫师 3》17:18:15 和 17:22:00 的真实 Game Bar JXR，可解码并逐字节归档；新版快捷键自动联动全流程仍需在下一次截图时确认。
- 当前只识别 Steam 库 `steamapps/common` 中已登记的游戏。不支持添加到 Steam 的非 Steam 游戏、特殊启动器跨目录子进程、Steam 自定义截图目录或组合截图键；检测失败会记日志并跳过。
- 单次处理一组截图。连续按键、多个账号同时输出截图、多个 JXR 候选、目录扫描异常都会取消自动归档。Steam 自身截图继续正常工作。
- Game Bar 需要真正生成可解码的浮点 JXR。只有 PNG、输出很慢、编码格式不在支持列表或捕获不可用时，会保留原文件并记录原因。
- 当前只归档与 JXR 同名的 PNG；特殊版本若采用不同命名规则，不猜测配对。
- 目录链接或文件链接会被拒绝。复制显示模式若无法唯一判断游戏输出显示器，也会跳过。
- 权限更高的游戏、系统热键限制或第三方覆盖层可能阻止模拟快捷键。工具不会自动提权、注入游戏、修改 HDR 开关或系统策略。
- 游戏安装后首次启用即可读取库；工具运行期间安装或移动游戏后，暂停再启用以刷新。

## 排查和实测

配置和日志位于 `%LOCALAPPDATA%\SteamXboxHdr\`，窗口提供「打开日志」按钮。

首次实测时，请在一个已运行的 Steam HDR 游戏中按一次设置的截图键，确认以下四点：

1. Steam 原 JPG 仍正常生成。
2. Game Bar 实际捕获目录出现新 JXR，通常还有同名 PNG。
3. 收集图片并校验后，正确账号、正确游戏的 `screenshots` 中直接出现副本，与原 Steam JPG 在同一文件夹。
4. 日志显示归档成功，元数据记录源路径与 SHA-256。若只有 PNG，先确认游戏捕获和 HDR 环境；工具不会把 PNG 处理成 HDR。

只读诊断命令：`SteamXboxHdr.exe --diagnose <输出JSON绝对路径>`。这个命令不发截图快捷键。

手动补归档命令：`SteamXboxHdr.exe --archive <Steam参考截图绝对路径> <GameBar原始JXR绝对路径>`。参考截图用于确定游戏目录；不会宣称两张图片为同次截图。手动归档保留 Game Bar 原文件名并加上归档标记与 ID。

## 开发与验证

使用 .NET 10 SDK，在 Windows x64 上运行：

```powershell
dotnet build src/SteamXboxHdr/SteamXboxHdr.csproj -c Release
dotnet run --project tests/SteamXboxHdr.Tests/SteamXboxHdr.Tests.csproj -c Release
pwsh -File ./publish.ps1
```

`publish.ps1` 优先使用本目录 `.tools/dotnet` 下的 SDK，否则使用 PATH 中的 dotnet。源码无第三方 NuGet 库；首次恢复和自包含发布需要下载微软运行时包，发布后的应用不需要联网。

自动检查覆盖实际浮点 JXR 编解码、PNG 冒充 JXR 拒绝、唯一候选配对、账号与游戏识别、PNG 同名配对、路径排除、源文件保留、哈希一致性、冲突避免、失败回滚、库解析和 x64 Win32 结构布局。

实现参考：[微软 Windows SDK 头文件](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/wingdi.h)、[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)、[Windows JPEG XR 编解码器](https://learn.microsoft.com/en-us/windows/win32/wic/jpeg-xr-codec)。
