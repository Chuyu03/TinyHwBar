# TinyHwBar

[![Build](https://github.com/Chuyu03/TinyHwBar/actions/workflows/build.yml/badge.svg)](https://github.com/Chuyu03/TinyHwBar/actions/workflows/build.yml)

TinyHwBar 是一个面向 Windows 11 的单行硬件监控条。它只显示 CPU、物理内存和 NVIDIA 独立显卡数据，不监测网络速度、延迟或带宽。

> **English summary:** TinyHwBar is a tiny, dependency-free Windows hardware monitor bar for CPU, RAM, NVIDIA GPU usage, VRAM usage, and temperature. It does not create network connections, collect telemetry, or require administrator privileges.

![TinyHwBar monitoring bar](https://raw.githubusercontent.com/Chuyu03/TinyHwBar/main/docs/tinyhwbar.png)

## 下载

从 [GitHub Releases](https://github.com/Chuyu03/TinyHwBar/releases/latest) 下载最新的 `TinyHwBar-v*-win-x64.zip`，解压后运行 `TinyHwBar.exe`。

发布的 EXE 是本项目源码使用 Windows 自带 .NET Framework 编译器生成的未签名程序。Windows 11 的 [Smart App Control](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions) 可能直接阻止未知的未签名 EXE，且不提供单应用放行。请勿为了运行 TinyHwBar 关闭 SAC、Microsoft Defender 或添加安全排除项；若被阻止，请保留系统保护并暂不运行此版本。

> **Windows security note:** The v1.0.0 EXE is unsigned and may be blocked by Windows Smart App Control. Do not disable SAC, Microsoft Defender, or add security exclusions to run it.

下载后请核对 Release 中的 `SHA256SUMS.txt`，并保留 Microsoft Defender 保护。

## 显示内容

正常状态示例：

```text
CPU 5% · RAM 34% · GPU 6% · VR 18% · 44°
```

- `CPU`：全系统 CPU 使用率。
- `RAM`：物理内存使用率。
- `GPU`：NVIDIA GPU 核心使用率。
- `VR`：NVIDIA NVML v2 显存 `used / total` 百分比，与 `nvidia-smi` 的显存使用口径一致；系统保留显存由 NVML 单独统计。
- 温度：GPU 温度，单位为摄氏度。

电池或电源状态未知时显示：

```text
CPU 5% · RAM 34% · GPU ECO · VR -- · --°
```

进入 ECO 后，程序会关闭已经初始化的 NVML 会话，并停止后续 NVML 调用。重新接通电源后会自动恢复 GPU 数据。接电状态下 NVML 暂时不可用时显示 `GPU -- · VR -- · --°`，并每 30 秒重试。

## 系统要求

- Windows 11 64 位。
- .NET Framework 4.8。
- `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`。
- NVIDIA 驱动提供的 `%WINDIR%\System32\nvml.dll`。

本项目不需要 .NET SDK、MSBuild、NuGet、第三方包、自带驱动或其他下载。
TinyHwBar 不随附 NVIDIA 驱动；请仅通过设备制造商或 NVIDIA 官方渠道使用仍受支持且包含当前安全更新的驱动。

## 构建与运行

在项目根目录运行：

```powershell
.\build.cmd
.\outputs\TinyHwBar.exe
```

构建目标为 x64 Windows GUI 程序，成功后生成 `outputs\TinyHwBar.exe`。Manifest 使用 `asInvoker`，不会申请管理员权限。

程序默认显示在主屏工作区右上角，尺寸为 `330×36` 物理像素。它始终置顶，不进入任务栏或 Alt+Tab，默认不抢占焦点。命名 Mutex 会阻止重复运行。

## 操作

- 左键拖动：仅在未锁定且未启用鼠标穿透时有效。
- 右键：打开与托盘相同的菜单。
- 双击托盘图标：切换显示和隐藏。
- 锁定位置：禁止左键拖动。
- 鼠标穿透：鼠标事件传递给监控条下方的窗口；开启后必须通过托盘菜单关闭。
- 重置到右上角：恢复到主屏工作区右上角。
- 退出：停止采样、关闭 NVML 并退出进程。

## 配置

位置、锁定和鼠标穿透状态保存在：

```text
%LOCALAPPDATA%\TinyHwBar\settings.ini
```

显示/隐藏状态不会保存。配置损坏或保存位置不再位于任何当前屏幕时，程序会恢复默认位置和必要的默认状态。

如需完全恢复默认设置，请先退出 TinyHwBar，再删除 `settings.ini`。程序下次运行时会重新创建配置。

## 隐私与安全边界

TinyHwBar：

- 不创建网络连接，不检测网速、延迟或带宽。
- 不提供遥测、远程接口、日志上传或外部 API 调用。
- 不监控单个应用或用户数据。
- 不申请管理员权限。
- 不安装或加载自带内核驱动。
- 不提供开机启动、安装器、自动更新或后台服务。

项目不会上传 VirusTotal 或其他第三方扫描平台，也不应为程序添加 Defender 排除项。发现安全问题时，请阅读 [SECURITY.md](https://github.com/Chuyu03/TinyHwBar/blob/main/SECURITY.md) 并使用 GitHub 私密漏洞报告。

## 本地验证

生成 SHA-256：

```powershell
Get-FileHash .\outputs\TinyHwBar.exe -Algorithm SHA256
```

使用 Microsoft Defender 进行无处置扫描：

```powershell
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Scan -ScanType 3 -File "$(Resolve-Path .\outputs\TinyHwBar.exe)" -DisableRemediation
```

如 Defender 报警或扫描失败，应停止使用并分析原因；不要关闭 Defender 或添加白名单。

## 贡献与许可证

提交改进前请阅读 [CONTRIBUTING.md](https://github.com/Chuyu03/TinyHwBar/blob/main/CONTRIBUTING.md)。TinyHwBar 使用 [MIT License](LICENSE) 发布。
