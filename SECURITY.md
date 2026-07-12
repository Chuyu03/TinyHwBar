# Security Policy

## Supported versions

| Version | Supported |
| --- | --- |
| 1.x | Yes |
| Earlier versions | No |

## Reporting a vulnerability

请使用 GitHub 的 [Private vulnerability reporting](https://github.com/Chuyu03/TinyHwBar/security/advisories/new) 私密报告安全问题，不要先创建公开 Issue。

报告时请提供能够说明问题的最小信息：受影响版本、风险、复现前提和必要步骤。请先删除凭据、用户数据、私人路径、完整日志和其他与复现无关的信息。

维护者会先确认报告范围，再决定修复和披露方式。未经双方确认，请不要公开漏洞细节或上传项目文件到第三方扫描、粘贴或分析平台。

## Security boundaries

TinyHwBar 不需要管理员权限，不创建网络连接，不包含遥测、自动更新、自带驱动或后台服务。发布的 Windows EXE 为未签名二进制；请核对 Release 提供的 SHA-256，并保留 Microsoft Defender 保护。
