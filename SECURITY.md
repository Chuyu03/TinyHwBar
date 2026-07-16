# Security Policy

## Supported versions

| Version | Supported |
| --- | --- |
| 2.x | Yes |
| 1.x | Yes |
| Earlier versions | No |

## Reporting a vulnerability

请使用 GitHub 的 [Private vulnerability reporting](https://github.com/Chuyu03/TinyHwBar/security/advisories/new) 私密报告安全问题，不要先创建公开 Issue。

报告时请只提供说明问题所需的最小信息：受影响版本、风险、复现前提和必要步骤。请先删除凭据、用户数据、私人路径、完整日志和其他无关信息。未经双方确认，请不要公开漏洞细节，也不要把项目文件上传到第三方扫描、粘贴或分析平台。

## 2.x security boundaries

TinyHwBar 2.x 遵循以下安全边界：

TinyHwBar 2.x 采用本地优先设计，但不是完全离线程序：唯一默认启用的网络活动是受限的本地网关 ICMP 检测；更新与遥测端点为空，其余外部通信功能默认关闭。

- 程序以当前用户和 `asInvoker` 运行，不需要管理员权限，不安装自带驱动或后台服务。
- 网络吞吐来自本机接口字节计数器。程序以固定文档保留地址只读查询 IPv4/IPv6 最佳接口，再从该接口报告的同地址族网关中稳定选择一个进行 ICMP 检测；它不宣称该网关是所有流量的全局出口或路由 next hop。检测默认开启，但只允许 RFC1918、CGNAT、IPv4/IPv6 link-local 和 IPv6 ULA，间隔 10 秒、超时 750 毫秒；公网 IPv4 或全局 IPv6 目标会在发包前被拒绝。
- 历史默认保存在 `%LOCALAPPDATA%\TinyHwBar\history.csv`，只含数值型采样，最多 900 点、24 小时、2 MiB，并可由用户清除。
- 开机启动、自动更新检查、回环 API 和遥测默认关闭；启用开机启动时只接受可确认卷类型的本地程序路径，拒绝 UNC 和映射网络盘。
- 更新清单地址和遥测地址默认为空。外部端点只接受经过预检且不含 query、userinfo 或 fragment 的 HTTPS 地址。公共 HTTPS DNS 预检只核对预检时解析出的地址，不固定随后实际连接的地址，因此不能彻底消除 DNS rebinding；真实端点必须使用可信、稳定的公开域名。
- 更新候选下载每次需要确认，最大 32 MiB；SHA-256 匹配后只暂存，不执行、安装或替换程序。清单哈希是完整性校验，不是发布者签名。
- API 只绑定 `127.0.0.1` 随机高端口，只提供 `/v1/status`，使用每次运行生成的 token，并只返回有界数值状态。启用状态会持久化；退出停止本次监听，但下次启动会恢复，持续关闭需取消勾选并应用。监听器使用单一后台工作线程，本机慢连接可使这个可选 API 单次暂时不可用至约 5 秒，但不会阻塞监控采样或界面。
- 遥测/诊断摘要会显示精确字段预览，每次发送都需要再次确认；没有静默后台上传。
- 当前用户安装/卸载脚本支持 `-WhatIf`。默认安装不启动程序，也不修改既有开机启动状态；默认卸载保留用户数据。`-RemoveUserData` 会在显式确认后递归删除整个用户数据目录且无法回滚，因此必须先退出所有已安装或便携版 TinyHwBar。安装器和卸载器都会在任何实际变更前持有按当前用户 SID 命名的跨会话 `Global\TinyHwBar.Maintenance.<SID>`，再持有与应用相同的当前会话 `Local\TinyHwBar.Singleton`，并在持锁后再次检查系统中正常命名的进程。跨会话 Mutex 会串行化同一用户的维护脚本，`Local\` Mutex 会阻止当前会话应用并发；进程快照仍不能阻止检查后从其他会话启动的应用或识别改名副本。同一账户下的恶意进程本来就可写当前用户安装路径，不属于这些脚本能够建立的安全边界。

## Signing and Windows application control

TinyHwBar 当前发布文件没有受信任的发布者签名。本机已复现 Smart App Control 阻止未签名、未建立云信任的临时测试 EXE；这不应通过关闭 SAC、Defender 或添加排除项来绕过。

SignPath Foundation 目前只有只读评估：没有申请资格、连接账号或仓库、上传产物、配置密钥或执行签名。因此不能声称 Smart App Control 问题已经解决。即使未来完成可信签名，也仍需保留源码审查、Defender 检查、发布包清单与哈希验证。
