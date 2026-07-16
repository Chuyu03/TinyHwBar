# SignPath Foundation 只读评估

评估日期：2026-07-15。

状态：**有条件适合作为未来方案，但当前不接入。** 本轮没有申请 SignPath Foundation、创建账号、安装 GitHub App、连接仓库、修改 CI、创建密钥、上传源码或产物，也没有执行签名。

## 结论

SignPath Foundation 可以为符合条件的开源项目提供免费、托管的 OV 级代码签名。对 TinyHwBar 的合理目标是降低 Windows 因“未知、未签名程序”而阻止执行的概率，并显示可信发布者；它不能保证消除 SmartScreen 提示，也不能绕过 Defender、Smart App Control 的恶意软件判断、WDAC、企业策略或第三方安全软件。

TinyHwBar 当前具备一些有利基础：MIT 许可证、已有公开版本、功能和隐私边界已有说明、构建不依赖第三方包、GitHub Actions 使用 GitHub 托管 runner、工作流权限为只读且 checkout 不保留凭据。不过，Foundation 是否认可项目声誉、单维护者角色安排、MFA 状态以及最终签名策略，仍需 SignPath 明确确认。因此不应把“看起来满足条件”写成“已经具备资格”。

## 资格与治理要求

[SignPath Foundation 条件](https://signpath.org/terms.html)要求项目使用 OSI 批准的开源许可证、没有商业双重许可或维护者提供的专有组件、已发布并持续维护、功能已有公开说明，且不包含恶意软件、PUA、漏洞利用或绕过安全措施功能。

使用 Foundation 证书时还需要：

- 所有源码仓库和 SignPath 成员启用 MFA。
- 公开 Authors、Reviewers 和 Approvers 角色。
- 每次签名请求经过人工批准。
- 在项目主页和下载页公开 `Code signing policy` 与隐私声明。
- 待签二进制由可验证的源码与构建流程产生，并满足文件元数据限制。

证书发布者会显示为 `SignPath Foundation`，不是 `TinyHwBar`。Foundation 保留接受、暂停和撤销证书的最终决定权；可执行程序还需要其认可的“可验证声誉”。

## GitHub、密钥与数据边界

[SignPath GitHub 集成文档](https://docs.signpath.io/trusted-build-systems/github)当前要求待签文件先保存为 GitHub Actions artifact；开源项目前置任务必须运行在 GitHub 托管 runner。提交动作需要：

- `GITHUB_TOKEN` 的 `actions: read` 与 `contents: read`。
- 仅对目标 signing policy 具有 Submitter 权限的 `SIGNPATH_API_TOKEN`。
- Foundation 要求的人工审批与来源验证。

GitHub App 被明确列为源码/构建策略验证的前置条件，但文档没有在该页列出完整 App 权限。接入前必须让 SignPath 书面确认基础 Foundation 流程是否需要 App；如需要，只能授权 TinyHwBar 单仓库，并在安装页逐项核对读写权限。当前文档仍使用长期 API secret，不能假设支持 GitHub OIDC 或无密钥流程。

[SignPath 服务条款](https://signpath.io/terms-of-service)把软件产物、证书、私钥、审计日志和组织配置都定义为服务中的 Content。删除或终止组织后，条款原则上要求 30 天内删除 Content，但加密备份可能继续保留；活跃组织的产物和审计日志保留期限未在该条款中给出固定上限。[隐私政策](https://signpath.io/privacy-policy)还说明会处理账号、IP、会话和请求时间，并使用位于欧洲或美国的子处理方。

如果未来接入，待签 artifact 应只包含最终 unsigned EXE 和必要发布文件，不包含 PDB、工作区、配置、日志或个人路径；GitHub artifact 应使用最短可行保留期，SignPath 的产物、审计日志和 token 保留策略需要先得到书面答复。

## Smart App Control 与 SmartScreen

### 本机复现结果

在当前 Windows 11 环境执行本地编译的临时测试 EXE 时，Code Integrity Operational 日志记录了事件 `3077`、`3033` 和 `3118`。其中 `3118` 明确对应 Smart App Control 阻止详情；组合证据表明该文件未签名且未建立云信任，并没有给出威胁名称。

这说明测试进程是在进入测试断言前被系统策略阻止，不能把该结果归因于测试代码失败。本记录只保留通用事件号和结论，不记录本机绝对路径、文件哈希或策略 ID。它也只是对当前阻止原因的复现，不代表 SAC 问题已经解决；项目不会通过关闭 SAC、关闭 Defender、加入排除项或自签证书绕过该边界。

微软已把 [SignPath Foundation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)列为符合资格的开源项目可用的免费 OV 级托管签名选项。

[Smart App Control 签名要求](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)当前只接受受信任提供商签发的 RSA 证书；ECC 签名尚不支持。未来必须确认 Foundation 证书链、RSA 算法和可信时间戳，并在签名后保持文件字节不变。

[SmartScreen 声誉说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)表明其同时评估发布者/证书声誉与文件哈希声誉。新的有效 OV/EV 签名文件仍可能显示“无法识别的应用”，EV 证书自 2024 年起也不再自动绕过首次下载警告。因此签名只能降低风险并积累一致发布者声誉，不能承诺零提示。

## 若以后决定接入

1. 先由 SignPath 确认项目资格、单维护者角色安排、GitHub App 完整权限、API token 生命周期、产物/日志保留、RSA 证书链和时间戳。
2. 把申请、账号连接、App 安装、CI/secret 修改、首次上传、首次签名、tag 和 Release 分成独立审批点。
3. 签名 workflow 只允许受保护的发布 ref 或人工触发，第三方 Actions 固定完整 commit SHA；fork PR 和 `pull_request_target` 不得接触签名 secret。
4. 对最终 PE 逐项验证 Authenticode 状态、发布者、证书链、RSA、时间戳和文件哈希，并分别在 Smart App Control 与 SmartScreen 下载场景实测。
5. 继续保留源码/diff 审查、Defender 检查、发布包清单与哈希验证；签名不能替代这些安全验证。

在上述条件得到书面答复并获得单独明确批准前，安全收口是：**不申请、不连接、不上传、不签名。**
