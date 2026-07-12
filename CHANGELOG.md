# Changelog

TinyHwBar 的重要版本变化记录在此文件中。版本号遵循语义化版本格式。

## [1.0.0] - 2026-07-12

### Added

- `330×36` 物理像素的单行 Windows 硬件监控条。
- CPU、物理内存、NVIDIA GPU 占用、显存比例和温度采样。
- 电池模式 `GPU ECO`，停止 NVML 调用以避免主动保持独显唤醒。
- 拖动、位置锁定、鼠标穿透、托盘显示/隐藏和位置恢复。
- Per-Monitor-V2 DPI、单实例保护和本地 INI 设置持久化。
- 无依赖的 .NET Framework 4.8 x64 构建脚本。
- 只读、无密钥且不保存产物的 GitHub Actions 编译检查。

[1.0.0]: https://github.com/Chuyu03/TinyHwBar/releases/tag/v1.0.0
