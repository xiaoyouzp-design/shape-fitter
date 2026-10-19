# 形态建拟合器 0.2.0-preview.1

[下载安装包与完整 ZIP](https://github.com/xiaoyouzp-design/shape-fitter/releases/tag/v0.2.0-preview.1) · [版本列表](https://github.com/xiaoyouzp-design/shape-fitter/releases)

升级旧版：先关闭 Unity，再运行 ZIP 内 `Install.ps1`。脚本会备份并将旧 `Assets/ModelCard` 迁移到 `Assets/形态建拟合器`，保留额外文件和 `.meta`。不要把新目录直接复制到旧目录旁；两者同时存在会导致重复程序集。若使用 unitypackage 升级，请先在 Unity Project 窗口将旧目录改名为“形态建拟合器”，待资源刷新后再导入。

适用于 Unity 编辑器的图片拟合、模型间拟合、模型坐标构建与拟合存档工具。当前版本为原型预览版，验证环境为 **Windows / Unity 2022.3.22f1**。

最方便的安装方式：导入同发行目录的 `形态建拟合器-0.2.0-preview.1.unitypackage`。也可以将本包 `Assets/形态建拟合器` 连同 `.meta` 文件复制到工程，或使用下方安装脚本。三种方式任选其一，勿在不同目录重复安装同一程序集。

```powershell
# 在解压目录执行；已有版本会先备份到工程根目录的 形态建拟合器备份。
.\Install.ps1 -ProjectPath '你的 Unity 工程目录'
# 包内文件校验（建议在导入 Unity 之前对解压包执行）
.\Verify-Package.ps1
```

菜单入口：

- `形态键拟合 / 图片拟合`
- `形态键拟合 / 模型拟合`
- `形态键拟合 / 构建模型坐标`
- `形态键拟合 / 拟合存档`

手工标注、图片拟合和模型拟合无需 Python。自动标点是可选功能，需自行安装 Python 3.12、检测依赖及模型文件；本包提供适配脚本和固定版本下载入口，**不包含检测权重、人物模型或参考图片**。

- [使用说明](Assets/形态建拟合器/Documentation/使用说明.zh-CN.md)
- [自动标点安装](Assets/形态建拟合器/Documentation/自动标点安装.zh-CN.md)
- [审计报告](Assets/形态建拟合器/Documentation/审计报告.zh-CN.md)
- [变更记录](Assets/形态建拟合器/Documentation/CHANGELOG.zh-CN.md)
- [来源与许可](Assets/形态建拟合器/Documentation/来源与许可.zh-CN.md)

本版本使用指定模型的坐标映射和可用形态键，不能保证任意 VRM 或任意真实照片都能可靠恢复。旧 `VRC Model Card` 卡片功能作为底层依赖一并保留。
