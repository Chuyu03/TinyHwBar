# TinyHwBar

[![Build](https://github.com/Chuyu03/TinyHwBar/actions/workflows/build.yml/badge.svg)](https://github.com/Chuyu03/TinyHwBar/actions/workflows/build.yml)

TinyHwBar 是一个开源、轻量、本地优先的 Windows 11 硬件监控工具。它用一条常驻监控条和一个简洁的控制中心，集中展示 CPU、内存、独立显卡/核显、网络吞吐与受限的本地网关延迟。

- **源码完整公开**：项目按 MIT License 发布，硬件采样、界面、历史、安装脚本和高级功能边界均可直接审阅。
- **单文件、超小体积**：当前 `2.0.0` 构建为一个约 `0.2 MiB` 的 x64 EXE；基于 Windows 11 的 .NET Framework 4.8，不捆绑第三方 DLL、自带内核驱动或后台服务。
- **本地优先**：核心采样与历史保存在本机，不记录进程、网址、数据包内容或用户文件。
- **默认克制的通信边界**：更新与遥测端点默认为空，自动更新、回环 API 和遥测默认关闭；回环 API 即使启用也只监听本机。
- **核心指标优先**：GPU 卡片先完整展示利用率、显存/共享内存及可用温度，设备型号只作辅助信息，过长时仅缩略型号。

> “本地优先”不等于“完全离线”：本地网关延迟检测默认开启，每 10 秒最多向所选接口报告的受限本地地址发送一次 ICMP Echo，可在设置中关闭；程序不会向公网目标执行延迟或带宽测试。

当前稳定版本为 `2.0.0`。正式二进制只通过 [GitHub Releases](https://github.com/Chuyu03/TinyHwBar/releases/tag/v2.0.0) 发布；不要把工作区或第三方转载的 EXE 当作正式 Release。

> **English summary:** TinyHwBar is an open-source, local-first Windows 11 hardware monitor distributed as a single x64 EXE. Version 2.0.0 is about 0.2 MiB and uses Windows/.NET Framework components without bundled third-party DLLs, drivers, or services. It is not offline-only: restricted local-gateway ICMP latency is enabled by default, while update and telemetry endpoints are empty and related external-service features are off by default. Official binaries are published only through the GitHub Release for v2.0.0.

![TinyHwBar compact monitoring bar](docs/tinyhwbar.png)

上图展示始终保持紧凑的监控条；`2.0.0` 在此基础上增加控制中心，而不会把复杂设置堆进主界面。

`2.0.0` 使用正式的极简仪表盘图标：轮廓、单指针和少量刻度用最少元素直接表达“实时硬件状态”。确定性源文件位于 `assets/TinyHwBar.png` 和 `assets/TinyHwBar.ico`。

## 下载与 Windows 安全提示

从 [`v2.0.0` GitHub Release](https://github.com/Chuyu03/TinyHwBar/releases/tag/v2.0.0) 下载正式版本，并核对同时发布的 `SHA256SUMS.txt`。

当前 `2.0.0` 发布文件没有受信任的发布者签名。Windows 11 的 [Smart App Control](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions) 可能直接阻止未知的未签名 EXE，且不提供单应用放行。本项目已在本机复现：临时测试 EXE 因未签名且未建立云信任而被 SAC 阻止；这不是测试断言失败，也不表示该问题已经解决。

请勿为了运行 TinyHwBar 关闭 SAC、Microsoft Defender 或添加安全排除项。若被阻止，请保留系统保护并暂不运行该构建。SignPath Foundation 目前仅完成只读评估：没有申请、接入、上传或签名，详见 [SignPath Foundation 评估](docs/signpath-foundation-assessment.md)。下载正式 Release 后仍应核对 `SHA256SUMS.txt`。

## 2.0.0 功能

### 采样与历史

- 网络速率来自 Windows 本地接口累计字节计数器，只计算被动上下行吞吐，不执行互联网带宽测试。程序通过 Windows 路由表分别查询固定 IPv4 / IPv6 文档保留目的地址的最佳接口；查询只读本机路由表，不发包。路由指向 VPN Tunnel 时可采样该接口，也支持仅有 IPv6 路由的环境；若两个协议族指向不同适配器，会明确降级而不猜选。该结果代表这两个探测目的地址的路由，不宣称所有流量都有唯一“当前出口”。
- 所选路由接口报告的本地网关延迟检测默认开启：若接口报告多个同地址族网关，当前版本按稳定顺序选择其中一个，因此不把它表述为路由 next hop。每 10 秒最多发送一个 ICMP Echo，最多等待 750 毫秒；目标只允许 RFC1918、CGNAT、IPv4 link-local、IPv6 link-local 或 ULA，公网 IPv4 和全局 IPv6 地址会在发包前被拒绝。
- GPU 概览按角色分为“独立显卡”和“核显”，不按 NVIDIA、AMD、Intel 等厂商拆分卡片。程序枚举 DXGI 中的 NVIDIA、AMD、Intel 物理适配器，在能够可靠识别时排除远程、软件渲染和虚拟候选，并按 LUID 去重，再通过 DXCore `IsIntegrated` 分类；Windows GPU Engine / Adapter Memory 计数器提供对应角色的利用率与内存数据。NVIDIA NVML 只作为 NVIDIA 独立显卡温度的可选增强；不支持、类型未知或候选不唯一时会明确降级，不猜选设备或填入推测值。控制中心把利用率、显存/共享内存及可用温度作为大字号单行主信息；型号位于次行并可独立缩略，不会挤压核心指标。
- 历史默认持久化到本机，只保存数值型采样。内存与磁盘均最多保留 900 点和最近 24 小时；大于 2 MiB 的异常历史文件会被拒绝，写入失败会提示但不停止监控。角色化 GPU 曲线沿用现有磁盘架构与列顺序，已有记录可兼容加载；角色命名变更不会主动清空历史，后续正常持久化仍按既有原子保存流程更新文件。用户仍可在控制中心主动清除历史。
- `2.0.0` 的控制中心包含四页：`概览 / 历史曲线 / 设置 / 高级功能`。
- 控制中心显式定义 `Tab / Shift+Tab` 顺序、`Enter / Esc` 操作和关键控件辅助名称；浅色界面的文字及四条曲线均满足相应的对比度门槛。

### 默认关闭的高级功能

开机启动、自动更新检查、回环 API 和遥测均默认关闭；TinyHwBar 不会因仅运行程序而自动启用它们。

- **开机启动**：仅在用户明确操作后写入当前用户启动项；程序不会静默替换不匹配的既有值，并会拒绝 UNC、映射网络盘及无法确认卷类型的程序路径。
- **更新**：清单地址默认为空。手动检查只在用户点击后发起；启动后自动检查必须显式开启。发现新版本后，下载还需要再次确认。下载上限为 32 MiB，必须匹配清单中的 SHA-256，且只原子暂存为候选文件；程序不会执行、安装、替换当前 EXE 或自动回滚。SHA-256 只校验文件是否与清单一致，不代表发布者身份或代码签名。
- **本地 API**：只绑定 `127.0.0.1` 的随机高端口，只提供 `GET /v1/status`，使用每次运行重新生成的 Bearer token，并返回有界的数值型状态；不监听局域网或公网。状态响应使用 `schemaVersion: 2` 的 `discrete* / integrated*` 字段，同时只在厂商真实匹配时保留旧 `nvidia* / intel*` 数值兼容字段，避免把 AMD 数据错误标成 NVIDIA 或 Intel。启用状态会保存在本地设置中：退出会停止本次监听，下次启动会恢复；持续关闭需取消勾选并应用。
- **遥测/诊断摘要**：地址默认为空；端点只接受经预检的 HTTPS 地址，并拒绝 query、userinfo 和 fragment。发送前显示精确字段预览，每次发送都需要再次确认；不会后台自动上传。更新和遥测共用的公共 HTTPS DNS 预检只核对预检时解析出的地址，不固定随后实际连接的地址，因而不能彻底消除 DNS rebinding；真实端点必须使用可信、稳定的公开域名。

完整实现状态与安全边界见 [2.0.0 实施与发布状态](docs/v2-plan.md)。

## 显示内容

正常状态示例：

```text
CPU 5% · RAM 34% · GPU 6% · VR 18% · 44°
```

- `CPU`：全系统 CPU 使用率。
- `RAM`：物理内存使用率。
- `GPU`：所选独立显卡的最忙物理引擎利用率。
- `VR`：所选独立显卡的专用显存占用比例。
- 温度：仅在所选独立显卡为 NVIDIA 且 NVML 可用时显示，单位为摄氏度；其他情况显示 `--`，不填入推测值。

电池或电源状态未知时显示：

```text
CPU 5% · RAM 34% · GPU ECO · VR -- · --°
```

进入 ECO 后，程序会关闭已初始化的 NVML 会话并停止后续 NVML 调用。重新接通电源后自动恢复；接电状态下 NVML 暂时不可用时每 30 秒重试。

## 2.0.0 已知限制

- 每个 GPU 角色当前只展示一个适配器。同一角色出现多个可靠候选，或同时存在无法可靠分类的候选时，为避免误报，程序会显示歧义状态，不会按枚举顺序或型号名称猜选。
- Manifest 已启用 Per-Monitor-V2，控制中心按 DPI 缩放；顶部监控条仍固定为 `330×36` 物理像素，在高 DPI 屏幕上可能显得更小。不同缩放比例的多显示器组合尚未完成系统性人工覆盖。
- 当前构建没有受信任的发布者签名，Smart App Control 仍可能阻止执行。

## 系统要求

- Windows 11 64 位。
- .NET Framework 4.8。
- `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`。
- NVIDIA 独立显卡温度可选依赖 NVIDIA 驱动提供的 `%WINDIR%\System32\nvml.dll`；没有 NVIDIA GPU 或 NVML 时不影响 Windows GPU 计数器提供的其他指标。

项目不需要 .NET SDK、MSBuild、NuGet、第三方包或自带内核驱动。

## 构建、测试与运行

先从托盘菜单彻底退出正在运行的 TinyHwBar，再在项目根目录运行：

```powershell
.\test.cmd --compile-only
.\build.cmd
.\outputs\TinyHwBar.exe
```

`test.cmd --compile-only` 只验证测试程序可以编译。默认的 `test.cmd` 还会执行测试 EXE；在启用 Smart App Control 的机器上，未签名临时 EXE 可能被系统阻止，此时应保留 SAC，不要把“被策略阻止”误报成测试断言失败。

构建目标为 x64 Windows GUI 单文件程序，Manifest 使用 `asInvoker`，不会申请管理员权限。监控条默认显示在主屏工作区右上角；未锁定且未开启鼠标穿透时，可拖到任意当前显示器的完整边界内，包括任务栏预留但视觉上空闲的区域，并在重启后恢复该位置。“重置到右上角”仍回到主屏工作区，便于从覆盖系统界面的位置恢复。监控条始终置顶，不进入任务栏或 Alt+Tab；控制中心按普通窗口显示。命名 Mutex 会阻止重复运行。

## 可选的当前用户安装脚本

安装脚本只面向当前用户。先使用 `-WhatIf` 检查范围：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TinyHwBar.ps1 -WhatIf
```

确认预览后，去掉 `-WhatIf` 才会实际安装。默认路径是 `%LOCALAPPDATA%\Programs\TinyHwBar`；默认还会创建当前用户开始菜单快捷方式和 Installed Apps 卸载注册。安装器不会启动 TinyHwBar，也不会新建、删除或强制关闭既有开机启动项，原有启动状态会保持不变。若同名开始菜单快捷方式不是安装器创建的完整属性组合，脚本会拒绝覆盖；可先人工核对，或使用 `-NoStartMenuShortcut` 保留它并只安装其余内容。

卸载也应先预览：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Uninstall-TinyHwBar.ps1 -WhatIf
```

安装后可从 Windows Installed Apps 卸载，也可先运行 `powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\TinyHwBar\Uninstall-TinyHwBar.ps1" -WhatIf` 核对范围。默认卸载会从带有效所有权标记的安装目录删除固定命名的 EXE、卸载脚本和标记，并删除仍精确匹配的启动项、安装器快捷方式和 Installed Apps 注册；外部项目所有权无法证明或属性已变化时会保留并警告。默认保留 `%LOCALAPPDATA%\TinyHwBar` 中的设置、历史和暂存更新；只有显式传入 `-RemoveUserData` 才申请递归删除整个用户数据目录，包括其中的未知文件，而且该删除没有回滚。安装、卸载或删除用户数据都属于可见系统变更，应先退出所有副本并核对 `-WhatIf` 输出。安装器和卸载器在任何实际变更前，会先持有按当前用户 SID 命名的跨会话 `Global\TinyHwBar.Maintenance.<SID>`，再持有与应用相同的当前会话 `Local\TinyHwBar.Singleton`，并在持锁后再次检查系统中正常命名的进程。前者串行化同一 Windows 用户各会话中的维护脚本，后者阻止当前会话应用与维护并发；进程快照仍不能阻止检查后从其他会话启动的应用，也不能识别改名副本。

## 操作

- 左键拖动：仅在未锁定且未启用鼠标穿透时有效。
- 右键：打开与托盘相同的菜单。
- 双击托盘图标：切换显示和隐藏；隐藏不等于退出。
- 锁定位置：禁止左键拖动。
- 鼠标穿透：开启后必须通过托盘菜单关闭。
- 透明度：`50%`–`100%`，默认 `90%`。
- 打开控制中心：查看四页本地状态与显式高级功能。
- 退出：停止采样、关闭高级服务和 NVML，并退出进程。

## 本地数据

设置与历史位于：

```text
%LOCALAPPDATA%\TinyHwBar\settings.ini
%LOCALAPPDATA%\TinyHwBar\history.csv
```

历史只保存数值型硬件和网络采样，不记录进程、网址、数据包内容或用户文件。可在控制中心清除历史。如需手动重置，请先从托盘彻底退出所有 TinyHwBar，再删除 `settings.ini` 和 `history.csv`；这只会重置本地设置与历史。若已启用开机启动，应先在控制中心取消勾选并应用，因为删除本地文件不会删除 HKCU 启动项、安装文件或开始菜单快捷方式。

## 隐私与安全边界

- 不监控单个应用、网址、数据包内容或用户文件。
- 不申请管理员权限，不安装或加载自带内核驱动，不创建后台服务。
- 默认启用的网络活动仅为受本地地址范围限制的“路由探测所选接口所报告网关”ICMP 延迟检测；该网关不宣称是所有流量的全局出口或路由 next hop，公网/全局目标会被拒绝。
- 更新和遥测没有内置默认端点，相关网络请求与持久化设置必须由用户显式配置或触发。
- 回环 API 仅限本机，不是“远程接口”。
- 项目不会把构建产物上传到 VirusTotal 或其他第三方扫描平台，也不应为程序添加 Defender 排除项。

发现安全问题时，请阅读 [SECURITY.md](https://github.com/Chuyu03/TinyHwBar/blob/main/SECURITY.md) 并使用 GitHub 私密漏洞报告。

## 本地验证

```powershell
Get-FileHash .\outputs\TinyHwBar.exe -Algorithm SHA256
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Scan -ScanType 3 -File "$(Resolve-Path .\outputs\TinyHwBar.exe)" -DisableRemediation
```

如 Defender 报警或扫描失败，应停止使用并分析原因；不要关闭 Defender 或添加白名单。
Defender 是否查询云端或自动提交样本取决于本机或组织策略；在执行自定义扫描前，应先核对 `Get-MpPreference | Select-Object MAPSReporting, SubmitSamplesConsent`，无法接受外部样本提交时不要运行该扫描命令。

## 贡献与许可证

提交改进前请阅读 [CONTRIBUTING.md](https://github.com/Chuyu03/TinyHwBar/blob/main/CONTRIBUTING.md)。TinyHwBar 使用 [MIT License](LICENSE) 发布。
