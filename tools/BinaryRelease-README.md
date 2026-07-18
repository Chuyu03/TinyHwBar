# TinyHwBar {{VERSION}}（Windows x64 便携版）

TinyHwBar 是一个开源、轻量、本地优先的 Windows 11 硬件监控工具。本压缩包是便携版，不含安装器、后台服务、驱动或第三方 DLL。

## 包内文件

- `TinyHwBar.exe`：Windows x64 主程序。
- [`LICENSE`](LICENSE)：MIT License。
- `README.md`：本说明。

与压缩包同时发布的 `{{SHA256_FILE}}` 位于 Release 资产列表中，不在 ZIP 内。请用它核对 `{{ASSET_NAME}}` 的 SHA-256；不要复用其他版本的校验文件。

本 ZIP 对应的冻结源码提交是 [{{COMMIT}}]({{REPOSITORY_URL}}/tree/{{COMMIT}})。版本标签可能移动或尚未创建；核对源码和安装脚本时，请以这个完整提交 ID 为准。

本资产的本地准备流程要求源码仓库、Git archive、展开源码、构建及子进程 `TEMP`/`TMP`/`TMPDIR`、ZIP 回读和最终 staging 全部位于同一个经过 Owner、ACL 与 reparse point 校验的专用信任根中；脚本会把它启动的 Git、测试和构建子进程临时目录重定向到该信任根，不修改 ACL、不执行 Git 写入或自动发布。这个流程降低其他本机账户替换构建输入或产物的风险，但不能替代完整提交审阅、CI 证据、SHA-256 核对或可信发布者签名。

准备流程还对所有 Git 调用启用 `--no-replace-objects`、`--no-lazy-fetch`、`--no-optional-locks` 和 `--no-pager`；继承的 `GIT_*` 覆盖会被隔离并在结束后恢复，`GIT_EXEC_PATH` 固定到已批准 Git 的 `libexec\git-core`，system/global config 与 attributes 固定到信任根内空文件，每次调用绑定精确 `safe.directory` 与空 attributes。流程拒绝本地 config 的 `include` / `includeIf`、`fsck` 覆盖、可注册自定义归档命令的 `tar.*` section、续行、NUL 与无效 UTF-8，也拒绝 `.git/commondir`、对象 alternates、`.git/info/grafts`、`.git/info/attributes` 和 `.git/shallow`；common directory 与 `--git-path objects` 必须留在根内，对象库须通过 `git fsck --full --strict`，归档使用 `--no-worktree-attributes`，确保上方冻结提交 ID 与实际 tree 不受外部对象、配置或属性污染。

## 运行

1. 解压完整 ZIP。
2. 双击 `TinyHwBar.exe`。
3. 通过通知区域的 TinyHwBar 托盘图标打开控制中心或退出。

TinyHwBar 面向 Windows 11 x64，并使用系统 .NET Framework 4.8。无需 .NET SDK、MSBuild、NuGet 或管理员权限。

本便携包不包含仓库中的安装/卸载脚本。需要当前用户安装项或开始菜单快捷方式时，请从[对应冻结提交源码]({{REPOSITORY_URL}}/tree/{{COMMIT}})取得脚本并先使用 `-WhatIf` 核对范围；不要从不明来源下载同名脚本。

## Windows 安全提示

本版本没有受信任的发布者签名。Smart App Control 或组织策略可能阻止未知的未签名 EXE。请勿为了运行 TinyHwBar 关闭 Smart App Control、Microsoft Defender 或添加排除项；若被阻止，请保留系统保护并暂不运行该构建。

## 本地数据与网络边界

设置与历史默认保存在 `%LOCALAPPDATA%\TinyHwBar`。TinyHwBar 不记录进程、网址、数据包内容或用户文件。

“本地优先”不等于“完全离线”：受限的本地网关 ICMP 延迟检测默认开启，可在设置中关闭；更新与遥测端点默认为空，自动更新、回环 API 和遥测默认关闭。

项目源码、完整功能边界、已知限制和安全报告方式请查看 [TinyHwBar 仓库]({{REPOSITORY_URL}}) 与[安全策略]({{REPOSITORY_URL}}/security/policy)。

---

**English summary:** TinyHwBar {{VERSION}} is an unsigned portable Windows 11 x64 build from frozen source commit [{{COMMIT}}]({{REPOSITORY_URL}}/tree/{{COMMIT}}). Extract the ZIP and run `TinyHwBar.exe`; no installer, service, driver, or third-party DLL is bundled. Keep Windows security protections enabled if execution is blocked. Restricted local-gateway ICMP latency is enabled by default, while update, loopback API, and telemetry features are off or unconfigured by default.
