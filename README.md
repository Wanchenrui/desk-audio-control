# 桌面音频 · DeskAudio

Windows 托盘音频面板。接入或拔出 USB、蓝牙设备时，面板会刷新列表，并把同一外设的播放和录音端口归为一项。遇到同时提供输入和输出的耳机、话筒或声卡，可以指定实际用途，供下次接入时沿用。

## 下载与更新

1. 用有权访问这个私有仓库的账号登录 GitHub，在 [最新正式版](https://github.com/Wanchenrui/desk-audio-control/releases/latest) 的 **Assets** 下下载 **DeskAudio.exe**。
2. 将 EXE 放在准备长期使用的文件夹里，双击即可运行。程序适用于 64 位 Windows 和 .NET Framework 4.8 或更高版本。换电脑时会重新识别该机的设备。
3. 打开面板的 **更新** 页，可以立即检查或关闭每天一次的后台检查。检测到新版本后，在发布页下载新版；从托盘退出旧版，再替换同名 EXE。

浏览器登录用于手动下载。要让程序检查这个私有仓库，在 **更新** 页勾选 **允许使用本机 GitHub 登录检查更新**，即可使用已登录的 Git for Windows 或 GitHub CLI。如果新电脑只有这个 EXE，在 **设置更新凭据** 保存一枚细粒度令牌即可。创建时将资源所有者设为 `Wanchenrui`，只选 `desk-audio-control` 仓库，仓库权限选择 **Contents → Read-only**。Windows 使用当前用户的账户加密保存该令牌，可从同一入口移除。[GitHub 令牌说明](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)

| 操作 | 入口 |
| --- | --- |
| 轮换当前可用的日常播放设备 | `Ctrl + Alt + F8` |
| 将当前默认话筒静音或取消静音 | `Ctrl + Alt + F7` |
| 调音量、左右平衡，或打开驱动提供的声音增强 | 在面板中选中设备 |
| 查看板载、显示器或虚拟线路 | 打开“显示其他接口”开关 |

关闭面板后，快捷键继续在托盘中工作。要退出程序，右键单击托盘图标并选择“退出”。

## 构建与运行

已在 Windows 10 x64 验证。构建使用 Windows 自带的 .NET Framework 4.x C# 编译器。在工程根目录打开 PowerShell 并运行

```powershell
.\Build.ps1
.\DeskAudio.exe
```

构建生成的 `DeskAudio.exe` 可独立运行。首次直接启动会打开设备面板；如需桌面入口，可为该文件创建快捷方式。在“切换与设置”中打开“登录 Windows 后启用托盘和快捷键”开关，即可启用登录启动。移动程序后，在新位置打开程序并把该开关关掉再打开，可更新登录启动路径。

设备列表会随连接状态刷新。“接入一个新的可用播放设备时，自动将它设为默认输出”可在设置中单独开启。有独立设备设置的通话或播放应用，可在应用内选择“系统默认”来跟随切换。

## 离线测试

在工程根目录运行

```powershell
.\tests\Run.ps1
```

测试通过模拟设备数据检查新设备接入、同型号并存、USB 换口、蓝牙多配置、模拟插孔和设备用途偏好。测试可使用普通用户权限运行，编译产物写入当前用户的临时目录，并在完成后清理。

## 文档

设计方案、路线图和维护约定见 [docs/](docs/README.md)。

## 参考项目

托盘中的设备与音量交互参考了 [EarTrumpet](https://github.com/File-New-Project/EarTrumpet)，全局快捷键切换参考了 [SoundSwitch](https://github.com/Belphemur/SoundSwitch)。
