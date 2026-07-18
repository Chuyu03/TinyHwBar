# Security Policy

## Supported versions

| Version | Status |
| --- | --- |
| 3.x | Supported — current maintained line |
| 2.x | Unsupported — historical release |
| 1.x | Unsupported — historical release |
| Earlier versions | Unsupported |

TinyHwBar 当前只维护最新主版本。历史版本会保留在 Git 历史、Tag 和 Release 中供查阅，但不设置安全维护期、EOL 截止日期、长期支持分支、补丁回移或多版本兼容承诺，也不会继续接收修复。GitHub 的 `Latest` 标记只表示发布页选择，不代表支持状态；`v3.0.0` 尚未正式发布时，当前没有受支持的正式二进制；待 `3.x` Release 可见后，用户应使用其中最新版本。

## Reporting a vulnerability

请使用 GitHub 的 [Private vulnerability reporting](https://github.com/Chuyu03/TinyHwBar/security/advisories/new) 私密报告安全问题，不要先创建公开 Issue。

报告时请只提供说明问题所需的最小信息：受影响版本、风险、复现前提和必要步骤。请先删除凭据、用户数据、私人路径、完整日志和其他无关信息。未经双方确认，请不要公开漏洞细节，也不要把项目文件上传到第三方扫描、粘贴或分析平台。

## 3.x security boundaries

TinyHwBar 3.x 遵循以下安全边界：

TinyHwBar 3.x 采用本地优先设计，但不是完全离线程序：唯一默认启用的网络活动是受限的本地网关 ICMP 检测；更新与遥测端点为空，其余外部通信功能默认关闭。

- 程序以当前用户和 `asInvoker` 运行，不需要管理员权限，不安装自带驱动或后台服务。
- 网络吞吐来自本机接口字节计数器。程序以固定文档保留地址只读查询 IPv4/IPv6 最佳接口，再从该接口报告的同地址族网关中稳定选择一个进行 ICMP 检测；它不宣称该网关是所有流量的全局出口或路由 next hop。检测默认开启，但只允许 RFC1918、CGNAT、IPv4/IPv6 link-local 和 IPv6 ULA，间隔 10 秒、超时 750 毫秒；公网 IPv4 或全局 IPv6 目标会在发包前被拒绝。
- 历史默认保存在 `%LOCALAPPDATA%\TinyHwBar\history.csv`，只含数值型采样，最多 900 点、24 小时、2 MiB，并可由用户清除。
- 设置主文件损坏、不兼容、不可读，或主文件缺失但只剩旧备份时，备份只用于恢复本地界面与 GPU 等无副作用偏好；历史写入、网关 ICMP、启动更新、本地 API 和遥测均在该会话 fail closed。重新勾选并应用只会在本次会话启用，不会写回受保护的 `settings.ini`；处理设置文件并重新启动后才恢复持久化。
- 开机启动、自动更新检查、回环 API 和遥测默认关闭；启用开机启动时只接受可确认卷类型的本地程序路径，拒绝 UNC 和映射网络盘。
- 更新清单地址和遥测地址默认为空。外部端点只接受经过预检且不含 query、userinfo 或 fragment 的 HTTPS 地址。公共 HTTPS DNS 预检只核对预检时解析出的地址，不固定随后实际连接的地址，因此不能彻底消除 DNS rebinding；真实端点必须使用可信、稳定的公开域名。
- 更新候选下载每次需要确认，最大 32 MiB；SHA-256 匹配后只暂存，不执行、安装或替换程序。清单哈希是完整性校验，不是发布者签名。
- API 只绑定 `127.0.0.1` 随机高端口，只提供 `/v1/status`，使用每次运行生成的 token，并只返回有界数值状态。启用状态会持久化；退出停止本次监听，但下次启动会恢复，持续关闭需取消勾选并应用。监听器使用单一后台工作线程，本机慢连接可使这个可选 API 单次暂时不可用至约 5 秒，但不会阻塞监控采样或界面。
- 遥测/诊断摘要会显示精确字段预览，每次发送都需要再次确认；没有静默后台上传。
- 当前用户安装/卸载脚本支持 `-WhatIf`，并拒绝管理员或提升权限运行。默认安装不启动程序，也不修改既有开机启动状态；默认卸载保留用户数据。`-RemoveUserData` 会在显式确认后递归删除整个用户数据目录且无法回滚，因此必须先退出所有已安装或便携版 TinyHwBar。安装器和卸载器都会在任何实际变更前持有按当前用户 SID 命名的跨会话 `Global\TinyHwBar.Maintenance.<SID>`，再持有与新版应用相同的 `Global\TinyHwBar.Singleton.<SID>`；为兼容旧版，还会检查当前会话的 `Local\TinyHwBar.Singleton`。持锁后只阻断 Owner SID 为当前用户的正常命名进程，并始终阻断占用当前用户安装路径的进程；其他用户的同名进程不会误阻塞。跨会话 Mutex 会串行化同一用户的维护脚本并阻止同一用户从其他 Windows 会话并发运行新版应用；旧版兼容锁只覆盖当前会话。无法解析 Owner SID 且路径不匹配时只警告，因此另一会话旧版仍有残余不确定性；进程快照也不能阻止检查后启动或识别改名副本。同一账户下的恶意进程本来就可写当前用户安装路径，不属于这些脚本能够建立的安全边界。
- 本地发布准备工具不从 `PATH` 解析 Git，也拒绝 `cmd\git.exe` shim；只接受人工审阅的真实 `mingw64\bin\git.exe`，并递归校验整个 Git 安装树及其祖先目录的所有者、ACL、reparse point，同时绑定精确 SHA-256、有效 Authenticode 签名和签名者指纹。它还强制要求调用者提供已存在且人工预置的 `TrustedWorkspaceRoot`：源码仓库必须是该根的严格子目录并使用根内 `.git`，Git archive、展开源码、构建、子进程 `TEMP`/`TMP`/`TMPDIR`、ZIP 回读和 staging 全部只能位于同一个经递归复核的根内。脚本不创建信任根、不修改 ACL；现有或新建路径只要 Owner、写权限或 reparse point 无法证明安全即拒绝。所有生产 Git 调用均使用 `--no-replace-objects`、`--no-lazy-fetch`、`--no-optional-locks`、`--no-pager`，继承的 `GIT_*` 覆盖会先快照并清除，`GIT_EXEC_PATH` 固定到已批准 Git 的 `libexec\git-core`，system/global config 与 attributes 固定到信任根内经 ACL 复核的空文件，每次调用还绑定精确 `safe.directory` 与空 attributes。脚本在任何 Git 前及确认后复查 `.git/config` / `config.worktree`，拒绝 `include` / `includeIf`、`fsck` 覆盖、可注册自定义归档命令的 `tar.*` section、续行、NUL 与无效 UTF-8；也拒绝 `.git/commondir`、对象 alternates、`.git/info/grafts`、`.git/info/attributes` 和 `.git/shallow`。解析出的 common directory 与 `--git-path objects` 必须分别等于根内 `.git` 和 `.git\objects`，对象库须通过 `git fsck --full --strict`，归档显式使用 `--no-worktree-attributes`，避免冻结 ID 与实际 tree 受到 replace-ref、`grafts`、外部对象、配置或属性污染。高影响确认通过后才锁定、复核并执行 Git；结束后精确恢复原 Git 环境。工具只生成本地候选，不执行 Git 写入、上传或 Release 发布。
- `Global\TinyHwBar.*.<SID>` 名称可预测。应用会为自己创建的全局单例锁设置当前用户与 LocalSystem ACL 并回读校验，但另一名本机用户仍可能抢先创建同名内核对象，造成应用或维护流程安全拒绝运行。这是共享/RDP 主机上的本地可用性风险，不会授予代码执行或读取用户数据的能力；彻底消除需要后续改用 Windows private namespace 或受当前用户 ACL 保护的跨会话文件锁。

## Signing and Windows application control

TinyHwBar 当前发布文件没有受信任的发布者签名。本机已复现 Smart App Control 阻止未签名、未建立云信任的临时测试 EXE；这不应通过关闭 SAC、Defender 或添加排除项来绕过。

SignPath Foundation 目前只有只读评估：没有申请资格、连接账号或仓库、上传产物、配置密钥或执行签名。因此不能声称 Smart App Control 问题已经解决。即使未来完成可信签名，也仍需保留源码审查、Defender 检查、发布包清单与哈希验证。
