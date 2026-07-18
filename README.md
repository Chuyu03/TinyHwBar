# TinyHwBar

[![Build](https://github.com/Chuyu03/TinyHwBar/actions/workflows/build.yml/badge.svg)](https://github.com/Chuyu03/TinyHwBar/actions/workflows/build.yml)

TinyHwBar 是一个开源、轻量、本地优先的 Windows 11 硬件监控工具。它用一条常驻监控条和一个简洁的控制中心，集中展示 CPU、内存、独立显卡/核显、网络吞吐与受限的本地网关延迟。

- **源码完整公开**：项目按 MIT License 发布，硬件采样、界面、历史、安装脚本和高级功能边界均可直接审阅。
- **单文件、超小体积**：当前 `3.0.0` 构建为一个约 `0.2 MiB` 的 x64 EXE；基于 Windows 11 的 .NET Framework 4.8，不捆绑第三方 DLL、自带内核驱动或后台服务。
- **本地优先**：核心采样与历史保存在本机，不记录进程、网址、数据包内容或用户文件。
- **默认克制的通信边界**：更新与遥测端点默认为空，自动更新、回环 API 和遥测默认关闭；回环 API 即使启用也只监听本机。
- **GPU 开箱即用，也可精确指定**：默认自动选择可用物理 GPU，独显休眠或数据不可用时回退核显；需要固定设备时，可在设置页直接从真实独显/核显列表中手动选择。
- **核心指标优先**：GPU 卡片先完整展示利用率、显存/共享内存及可用温度，设备型号只作辅助信息，过长时仅缩略型号。

> “本地优先”不等于“完全离线”：本地网关延迟检测默认开启，每 10 秒最多向所选接口报告的受限本地地址发送一次 ICMP Echo，可在设置中关闭；程序不会向公网目标执行延迟或带宽测试。

当前源码版本为 `3.0.0`。正式二进制只通过 [GitHub Releases](https://github.com/Chuyu03/TinyHwBar/releases) 发布；`v3.0.0` 正式发布完成前，GitHub 的 Latest 仍可能指向已经停止支持的历史版本，该标记不代表支持状态，此时尚没有受支持的正式 V3 二进制。不要把工作区或第三方转载的 EXE 当作正式 Release。

项目当前只维护最新的 `3.x` 主版本；`v1.x` 与 `v2.x` 作为历史 Release 保留，但已停止支持且不会继续接收修复，详见 [安全政策](SECURITY.md)。

> **English summary:** TinyHwBar is an open-source, local-first Windows 11 hardware monitor distributed as a single x64 EXE. The current source targets version 3.0.0 and builds to about 0.2 MiB using Windows/.NET Framework components without bundled third-party DLLs, drivers, or services. It is not offline-only: restricted local-gateway ICMP latency is enabled by default, while update and telemetry endpoints are empty and related external-service features are off by default. Only the latest 3.x line is supported. Earlier Releases remain downloadable as unsupported history, and GitHub's Latest label does not indicate support; until v3.0.0 is published, no supported official binary is available.

![TinyHwBar compact monitoring bar](docs/tinyhwbar.png)

上图展示始终保持紧凑的监控条；当前版本通过独立控制中心承载详细状态和设置，不会把复杂选项堆进主界面。

TinyHwBar 使用极简仪表盘图标：轮廓、单指针和少量刻度用最少元素直接表达“实时硬件状态”。确定性源文件位于 `assets/TinyHwBar.png` 和 `assets/TinyHwBar.ico`。

## 下载与 Windows 安全提示

从 [GitHub Releases](https://github.com/Chuyu03/TinyHwBar/releases) 下载最新可见的 `3.x` 正式版本，并核对同时发布的 `SHA256SUMS.txt`。如果 [`v3.0.0`](https://github.com/Chuyu03/TinyHwBar/releases/tag/v3.0.0) 尚不可见，说明 V3 仍处于发布准备阶段；上一条可见 Release 仅作为已停止支持的历史版本保留，此时尚没有受支持的正式二进制。

TinyHwBar 当前正式发布路径没有受信任的发布者签名，V3 也沿用这条已披露边界。Windows 11 的 [Smart App Control](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions) 可能直接阻止未知的未签名 EXE，且不提供单应用放行。本项目已在本机复现：临时测试 EXE 因未签名且未建立云信任而被 SAC 阻止；这不是测试断言失败，也不表示该问题已经解决。

请勿为了运行 TinyHwBar 关闭 SAC、Microsoft Defender 或添加安全排除项。若被阻止，请保留系统保护并暂不运行该构建。SignPath Foundation 目前仅完成只读评估：没有申请、接入、上传或签名，详见 [SignPath Foundation 评估](docs/signpath-foundation-assessment.md)。下载正式 Release 后仍应核对 `SHA256SUMS.txt`。

## 当前源码功能

以下内容描述当前源码；`CHANGELOG.md` 中标为 `Unreleased` 的后续改进不会包含在 `v3.0.0` 二进制中。

### 采样与历史

- 网络速率来自 Windows 本地接口累计字节计数器，只计算被动上下行吞吐，不执行互联网带宽测试。程序通过 Windows 路由表分别查询固定 IPv4 / IPv6 文档保留目的地址的最佳接口；查询只读本机路由表，不发包。路由指向 VPN Tunnel 时可采样该接口，也支持仅有 IPv6 路由的环境；若两个协议族指向不同适配器，会明确降级而不猜选。该结果代表这两个探测目的地址的路由，不宣称所有流量都有唯一“当前出口”。
- 所选路由接口报告的本地网关延迟检测默认开启：若接口报告多个同地址族网关，当前版本按稳定顺序选择其中一个，因此不把它表述为路由 next hop。每 10 秒最多发送一个 ICMP Echo，最多等待 750 毫秒；目标只允许 RFC1918、CGNAT、IPv4 link-local、IPv6 link-local 或 ULA，公网 IPv4 和全局 IPv6 地址会在发包前被拒绝。
- GPU 概览按角色分为“独立显卡”和“核显”，不按 NVIDIA、AMD、Intel 等厂商拆分卡片。程序枚举 DXGI 中的 NVIDIA、AMD、Intel 物理适配器，在能够可靠识别时排除远程、软件渲染和虚拟候选，并按 LUID 去重，再通过 DXCore `IsIntegrated` 分类；Windows GPU Engine / Adapter Memory 计数器提供对应角色的利用率与内存数据。NVIDIA NVML 只作为 NVIDIA 独立显卡温度的可选增强；不支持、类型未知或候选不唯一时会明确降级，不猜选设备或填入推测值。控制中心把利用率、显存/共享内存及可用温度作为大字号单行主信息；型号位于次行并可独立缩略，不会挤压核心指标。
- GPU 显示默认使用“自动选择（推荐）”，启动后无需配置；可用独显有有效数据时优先显示独显，独显处于 ECO、不可用或没有有效利用率而核显有数据时回退核显。设置页始终提供物理 GPU 下拉列表与刷新入口；手动选择保存设备 LUID 和名称，设备暂时不可用时保留偏好并临时回退自动选择。
- 历史默认持久化到本机，只保存数值型采样。内存与磁盘均最多保留 900 点，单条记录的最大保留年龄为 24 小时；以默认 2 秒间隔连续采样时，900 点实际约覆盖最近 30 分钟，并不代表始终存在 24 小时曲线。大于 2 MiB 的异常历史文件会被拒绝，写入失败会提示但不停止监控。关闭“保存历史”只停止后续磁盘写入：已有磁盘历史仍可查看且不会被删除，关闭期间的新点只进入本次会话曲线；重新启用后继续保存旧持久化历史和此后的新点。角色化 GPU 曲线沿用现有磁盘架构与列顺序，已有记录可兼容加载。用户仍可在控制中心主动清除历史。
- 设置或历史主文件损坏、缺失但仍有旧备份、版本不兼容或暂时无法读取时，程序会尝试只读加载有效备份，并保护异常主文件，不会用默认值或空历史自动覆盖。未通过完整验证的设置只恢复界面、位置、GPU 等无副作用偏好；历史写入、网关 ICMP、启动更新检查、本地 API 和遥测在本次会话保持关闭。用户重新勾选并应用后只会在本次会话启用，不会写回受保护的 `settings.ini`；按提示处理设置文件并重新启动后，持久化才恢复。只有明确确认“清除全部本地历史”才删除历史主文件和备份。
- 当前控制中心包含四页：`概览 / 历史曲线 / 设置 / 高级功能`。
- 控制中心显式定义 `Tab / Shift+Tab` 顺序、`Enter / Esc` 操作和关键控件辅助名称；浅色界面的文字及四条曲线均满足相应的对比度门槛。

### 默认关闭的高级功能

开机启动、自动更新检查、回环 API 和遥测均默认关闭；TinyHwBar 不会因仅运行程序而自动启用它们。

- **开机启动**：仅在用户明确操作后写入当前用户启动项；程序不会静默替换不匹配的既有值，并会拒绝 UNC、映射网络盘及无法确认卷类型的程序路径。
- **更新**：清单地址默认为空。手动检查只在用户点击后发起；启动后自动检查必须显式开启。发现新版本后，下载还需要再次确认。下载上限为 32 MiB，必须匹配清单中的 SHA-256，且只原子暂存为候选文件；程序不会执行、安装、替换当前 EXE 或自动回滚。SHA-256 只校验文件是否与清单一致，不代表发布者身份或代码签名。
- **本地 API**：只绑定 `127.0.0.1` 的随机高端口，只提供 `GET /v1/status`，使用每次运行重新生成的 Bearer token，并返回有界的数值型状态；不监听局域网或公网。状态响应使用 `schemaVersion: 2` 的 `discrete* / integrated*` 字段，同时只在厂商真实匹配时保留旧 `nvidia* / intel*` 数值兼容字段，避免把 AMD 数据错误标成 NVIDIA 或 Intel。启用状态会保存在本地设置中：退出会停止本次监听，下次启动会恢复；持续关闭需取消勾选并应用。
- **遥测/诊断摘要**：地址默认为空；端点只接受经预检的 HTTPS 地址，并拒绝 query、userinfo 和 fragment。发送前显示精确字段预览，每次发送都需要再次确认；不会后台自动上传。更新和遥测共用的公共 HTTPS DNS 预检只核对预检时解析出的地址，不固定随后实际连接的地址，因而不能彻底消除 DNS rebinding；真实端点必须使用可信、稳定的公开域名。

完整实现状态与安全边界见 [3.0.0 实施与发布边界](docs/v3-plan.md)。

## 显示内容

自动选择独立显卡时的示例：

```text
CPU 5% · RAM 34% · GPU 6% · VR 18% · 44°
```

- `CPU`：全系统 CPU 使用率。
- `RAM`：物理内存使用率。
- `GPU` / `iGPU`：当前实际显示的独立显卡或核显中，最忙物理引擎的利用率。
- `VR`：独立显卡使用专用显存占用比例；核显优先使用共享内存占用比例，并只在共享内存数据不可用时回退到可用的专用内存比例。
- 温度：仅在当前显示 NVIDIA 独立显卡且 NVML 可用时显示，单位为摄氏度；核显和其他无法可靠读取温度的设备显示 `--`，不填入推测值。

独立显卡处于 ECO、不可用或没有有效利用率而核显可用时，自动模式会直接回退到核显，例如：

```text
CPU 5% · RAM 34% · iGPU 12% · VR 9% · --°
```

只有没有可用核显可以回退、且当前仍需表达独立显卡 ECO 状态时，监控条才显示：

```text
CPU 5% · RAM 34% · GPU ECO · VR -- · --°
```

进入 ECO 后，程序会关闭已初始化的 NVML 会话并停止后续 NVML 调用；这不妨碍自动模式显示可用核显。重新接通电源后独立显卡采样自动恢复；接电状态下 NVML 暂时不可用时每 30 秒重试。

## 3.0.0 已知限制

- 每个 GPU 角色当前只展示一个适配器。自动模式遇到同一角色多个可靠候选时会显示歧义，用户可在设置中手动选择已列出的物理 GPU；仍无法可靠分类的候选不会按枚举顺序或型号名称猜选。
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

`test.cmd --compile-only` 会先运行不启动 TinyHwBar 的 PowerShell 安装、卸载和发布准备脚本安全断言，再验证 C# 测试程序可以编译，但不会执行未签名的测试 EXE。默认的无参数 `test.cmd` 会在上述检查后继续执行测试 EXE；在启用 Smart App Control 的机器上，该临时 EXE 可能被系统阻止，此时应保留 SAC，不要把“被策略阻止”误报成测试断言失败。脚本只接受无参数或唯一的 `--compile-only`，未知或额外参数会直接报错退出。

构建目标为 x64 Windows GUI 单文件程序，Manifest 使用 `asInvoker`，不会申请管理员权限。监控条默认显示在主屏工作区右上角；未锁定且未开启鼠标穿透时，可拖到任意当前显示器的完整边界内，包括任务栏预留但视觉上空闲的区域，并在重启后恢复该位置。“重置到右上角”仍回到主屏工作区，便于从覆盖系统界面的位置恢复。监控条始终置顶，不进入任务栏或 Alt+Tab；控制中心按普通窗口显示。命名 Mutex 会阻止重复运行。

## 可选的当前用户安装脚本

安装脚本只面向当前用户，并且会拒绝在管理员或提升权限的 PowerShell 中运行。请使用普通非管理员 PowerShell，先通过 `-WhatIf` 检查范围：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-TinyHwBar.ps1 -WhatIf
```

确认预览后，去掉 `-WhatIf` 才会实际安装。默认路径是 `%LOCALAPPDATA%\Programs\TinyHwBar`；默认还会创建当前用户开始菜单快捷方式和 Installed Apps 卸载注册。安装器会在写入前校验源文件的 x64、.NET、产品名、内部文件名、原始文件名和版本元数据，并在审批、暂存与复制阶段绑定同一 SHA-256；任一结构信息缺失、不符或文件在流程中变化都会拒绝安装。这些检查用于防止选错文件和中途替换，不是发布者身份认证；正式发布包仍应使用随版本提供的 SHA-256 或未来的代码签名核验来源。安装器不会启动 TinyHwBar，也不会新建、删除或强制关闭既有开机启动项，原有启动状态会保持不变。若同名开始菜单快捷方式不是安装器创建的完整属性组合，脚本会拒绝覆盖；可先人工核对，或使用 `-NoStartMenuShortcut` 保留它并只安装其余内容。

卸载也应先预览：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Uninstall-TinyHwBar.ps1 -WhatIf
```

安装后可从 Windows Installed Apps 卸载，也可先在普通非管理员 PowerShell 中运行 `powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\TinyHwBar\Uninstall-TinyHwBar.ps1" -WhatIf` 核对范围；卸载器同样拒绝提升权限运行。默认卸载会从带有效所有权标记的安装目录删除固定命名的 EXE、卸载脚本和标记，并删除仍精确匹配的启动项、安装器快捷方式和 Installed Apps 注册；外部项目所有权无法证明或属性已变化时会保留并警告。默认保留 `%LOCALAPPDATA%\TinyHwBar` 中的设置、历史和暂存更新；只有显式传入 `-RemoveUserData` 才申请递归删除整个用户数据目录，包括其中的未知文件。该不可回滚删除只会在可回滚的安装项卸载事务成功提交后作为最后一步执行；删除失败会保留并报告尚存内容，不会把安装项回滚成已安装状态。安装、卸载或删除用户数据都属于可见系统变更，应先退出所有副本并核对 `-WhatIf` 输出。安装器和卸载器在任何实际变更前，会依次持有按当前用户 SID 命名的跨会话维护锁 `Global\TinyHwBar.Maintenance.<SID>`、与新版应用相同的跨会话单例锁 `Global\TinyHwBar.Singleton.<SID>`，以及旧版当前会话兼容锁 `Local\TinyHwBar.Singleton`；持锁后只阻断当前用户 SID 的正常命名进程，并始终阻断正在占用当前用户安装路径的进程，其他 Windows 用户的同名程序不会误阻塞维护。Global 锁会串行化同一 Windows 用户各会话中的维护脚本并阻止新版应用跨会话并发；旧版 Local 锁只在当前会话可见，因此另一会话中的旧版仍须依赖进程复查且存在检查后启动的残余竞态。执行维护前仍必须人工退出所有副本；无法解析 Owner SID 的非安装路径进程只会触发警告，进程快照也不能识别改名副本。

## 本地发布资产准备

维护者可使用纯本地工具从一个明确的 40 位提交 ID 构建隔离候选。发布工具不从 `PATH` 猜测 Git，也拒绝 `cmd\git.exe` shim；只接受人工选定的真实 Git for Windows 二进制 `<Git安装根>\mingw64\bin\git.exe`。先在普通非管理员 PowerShell 中检查真实路径、其安装根到 EXE 的 ACL、精确 SHA-256 和有效 Authenticode 签名者指纹：

```powershell
$gitExe = 'C:\Program Files\Git\mingw64\bin\git.exe' # 替换为本机准备审阅的真实 Git 路径
$gitRoot = Split-Path (Split-Path (Split-Path $gitExe -Parent) -Parent) -Parent
$gitSha256 = (Get-FileHash -LiteralPath $gitExe -Algorithm SHA256).Hash
$gitSignature = Get-AuthenticodeSignature -LiteralPath $gitExe
$gitSignerThumbprint = if ($null -ne $gitSignature.SignerCertificate) {
  $gitSignature.SignerCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant()
} else {
  $null
}

[pscustomobject]@{
  GitExe = $gitExe
  Sha256 = $gitSha256
  SignatureStatus = $gitSignature.Status
  SignerSubject = $gitSignature.SignerCertificate.Subject
  SignerThumbprint = $gitSignerThumbprint
}
@(
  $gitRoot
  (Join-Path $gitRoot 'mingw64')
  (Join-Path $gitRoot 'mingw64\bin')
  $gitExe
) | ForEach-Object { Get-Acl -LiteralPath $_ | Format-List Path, Owner, AccessToString }
```

只有在确认路径确为 `mingw64\bin\git.exe`、签名状态为 `Valid`、哈希和签名者指纹符合预期，且上述四处没有向当前用户、`LocalSystem`、内置 `Administrators` 之外的主体授予写入、修改、删除、改权限或取得所有权后，才把已审阅值传给预览。脚本还会递归检查整个 Git 安装树的所有者、ACL 和 reparse point，并检查安装根的祖先目录是否允许其他主体替换该树；任一边界无法证明时都会拒绝。

这条校验只保护 Git 工具链，不能把一个允许其他本机账户写入的源码仓库变成可信发布源。正式资产准备必须在受保护的当前用户目录或其他隔离、可信的冻结提交副本中进行；如果仓库或其 `.git` 目录向其他本机主体开放写权限，应先停止并迁移到受保护副本，而不是把脚本的 Git 校验当作豁免。

发布命令还强制要求一个已经存在的专用 `TrustedWorkspaceRoot`。脚本不会创建该根或修改 ACL；调用者必须先人工确认根目录、祖先和现有内容的 Owner、ACL 与 reparse point 均受保护，并把带有真实 `.git` 目录的 TinyHwBar 仓库放在该根的严格子目录中。仓库不能与根下保留的 `work`、`staging` 目录重叠。Git archive、展开源码、构建输出、ZIP 回读和最终 staging 都留在同一个受验证根内；脚本还把它启动的 Git、测试与构建子进程使用的 `TEMP`/`TMP`/`TMPDIR` 重定向到该根。任何新建目录或产物继承出不可信写权限、Owner 无法证明或变成 reparse point 时都会停止。示例中的根只是占位，不能未经 ACL 审阅直接照抄：

所有生产 Git 调用都显式使用 `--no-replace-objects`、`--no-lazy-fetch`、`--no-optional-locks` 和 `--no-pager`。执行期间会先快照、清除并在结束后精确恢复继承的 `GIT_*` 环境，包括仓库/工作树/公共目录、对象库与 alternates、replace-ref 与 `grafts`、配置注入、属性覆盖、trace 和 test hook；`GIT_EXEC_PATH` 固定到已批准 Git 安装树内的 `libexec\git-core`，system/global config 与 attributes 指向信任根内经过 ACL 复核的空文件，每次 Git 调用还固定精确的 `safe.directory` 和空 attributes。脚本在第一次 Git 前及确认后再次原始检查 `.git/config` 与可选的 `config.worktree`，拒绝 `include` / `includeIf`、`fsck` 覆盖、可注册归档命令的 `tar.*` section、续行、NUL 和无效 UTF-8；同时拒绝 `.git/commondir`、对象 alternates、`.git/info/grafts`、`.git/info/attributes` 或 `.git/shallow`。解析出的 common directory 与 `--git-path objects` 必须分别精确等于根内 `.git` 和 `.git\objects`，对象库还要通过 `git fsck --full --strict`；归档显式使用 `--no-worktree-attributes`。这样冻结提交的 40 位 ID 与实际归档 tree 使用同一套本地、未替换且不受外部配置、属性或对象库污染的语义。

```powershell
$commit = '<40位提交ID>'
$trustedWorkspaceRoot = Join-Path $env:USERPROFILE 'TinyHwBar-ReleaseWorkspace'
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Prepare-Release.ps1 `
  -Version 3.0.0 `
  -Commit $commit `
  -GitExe $gitExe `
  -ApprovedGitSha256 $gitSha256 `
  -ApprovedGitSignerThumbprint $gitSignerThumbprint `
  -TrustedWorkspaceRoot $trustedWorkspaceRoot `
  -WhatIf
```

复核完整提交 ID、`-WhatIf` 范围以及冻结提交中的 `test.cmd`、`build.cmd` 后，再显式加入 `-ApproveFrozenCommitScripts`；实际运行仍会显示高影响确认：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Prepare-Release.ps1 `
  -Version 3.0.0 `
  -Commit $commit `
  -GitExe $gitExe `
  -ApprovedGitSha256 $gitSha256 `
  -ApprovedGitSignerThumbprint $gitSignerThumbprint `
  -TrustedWorkspaceRoot $trustedWorkspaceRoot `
  -ApproveFrozenCommitScripts
```

工具会保守检查 Git 安装树、其祖先目录、真实 `git.exe` 以及整个专用发布工作区的所有者、ACL 与 reparse point，并绑定人工批准的精确哈希与签名者指纹；高影响确认通过后，它锁定同一个真实 EXE、再次复核全部条件，随后才执行 Git。工具通过该 Git 和 `git archive` 把指定提交展开到受保护的 `work` 子树，在隔离副本中以当前普通用户权限运行 `test.cmd --compile-only` 与 `build.cmd`，随后在受保护的 `staging` 子树生成独立二进制 README、严格 allowlist ZIP、新的 SHA-256 文件和本地来源记录，并重新打开 ZIP 核对文件、哈希、版本、完整提交来源及 README 相对链接。目标 staging 目录必须不存在，工具会拒绝提升权限运行，不会修改 ACL、复用旧校验文件，也不会执行 `git add`、commit、tag、push、上传或 Release 发布。运行前仍应退出 TinyHwBar，并先冻结、复核准备公开的最终提交；本地候选不能替代该版本重新取得的 CI 和发布审批。

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

历史只保存数值型硬件和网络采样，不记录进程、网址、数据包内容或用户文件。可在控制中心清除历史。如需手动重置，请先从托盘彻底退出所有 TinyHwBar，再同时删除 `settings.ini`、`settings.ini.bak`、`history.csv` 和 `history.csv.bak`；只删除主文件时，有效备份会在下次启动继续用于恢复。这些文件只影响本地设置与历史。若已启用开机启动，应先在控制中心取消勾选并应用，因为删除本地文件不会删除 HKCU 启动项、安装文件或开始菜单快捷方式。

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
