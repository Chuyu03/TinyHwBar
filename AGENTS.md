# TinyHwBar 项目约定

## 项目定位与命名

- 将本仓库视为 TinyHwBar Windows 桌面应用的真实项目根。
- 用户可见的应用、可执行文件、进程、窗口、托盘和安装项名称保持为 `TinyHwBar`；版本号只放在元数据、变更记录、Git tag 与 Release 中。
- `outputs/` 与 `release/` 是生成产物目录，不是源码。

## 构建与验证

- 项目面向 Windows 11 x64，使用系统 .NET Framework 4.8 C# 编译器；除非任务明确要求，不增加 SDK、包、服务、驱动或第三方依赖。
- 替换或启动可执行文件前，先退出所有 TinyHwBar 实例。
- 本地编译检查使用 `test.cmd --compile-only`，应用构建使用 `build.cmd`。
- `test.cmd --compile-only` 不执行测试断言。完整 `test.cmd` 会运行未签名的临时测试 EXE，可能被 Smart App Control 阻止；保留 Windows 保护，并把策略阻止与测试失败明确区分。`2.0.0` 的完整测试发布证据是成功的 GitHub Actions Build；后续版本必须重新取得对应 CI 证据。

## 产品与安全边界

- 将 TinyHwBar 描述为“本地优先”，不要描述为“完全离线”：受限的本地网关 ICMP 延迟默认开启，更新与遥测端点为空，其他外部通信功能默认关闭。
- 独显和核显卡片优先完整显示占用率、显存或共享内存以及可用温度；设备名称仅作辅助，空间不足时可以缩略。
- 不得为运行或测试而关闭 Smart App Control、Defender 或添加排除项。`2.0.0` 是已明确接受的未签名发布；SignPath 仅为未来选项，除非用户另行重启该决策。

## Release 维护

- 发布前冻结并复核最终公开提交，再从该提交在隔离 staging 目录中重建资产。
- 只发布已批准的 ZIP 及其新生成的 SHA-256 校验文件；验证 ZIP allowlist，绝不复用旧版 `release/SHA256SUMS.txt`。
- 如实记录尚未覆盖的 DPI、安装生命周期、签名和平台策略限制，不夸大验证结论。
- `git add`、commit、push、tag、资产上传、Release 发布及其他公开写入必须逐层取得明确批准；发布后执行无需身份的公开回读。
