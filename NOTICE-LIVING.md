# PCL SUPER — PCL Living Fork 移植来源声明

本仓库（PCL SUPER）以 [PCL-Community/PCL-CE](https://github.com/PCL-Community/PCL-CE) 为基底，
移植 [DDZmumo/PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork) 的功能。

## 已移植代码

以下目录的 C# 代码源自 PCL Living Fork 的 `migration/csharp-modern-dotnet` 分支
（对应 `PCLCS/` 与 `tests/PCLLF.Migration.Tests/`），按 PCL《分发有限许可》与《存储库合理使用指南》
作为第三方二次创作保留原作者署名：

- `PCL.Living.Features/Minecraft/**` — 资源管理、还原点/环境历史、实例克隆、键位管理、
  个人选项、存档备份、模组依赖检查、世界元数据等引擎
- `PCL.Living.Features/Constants.cs` — 资源类型/来源平台枚举
- `PCL.Living.Features.Tests/**` — 上述引擎的原始测试用例（405 项）
- `PCL.Living.Features/LivingStrings.cs` — 为脱离 MeloongCore.Shared 而重写的字符串/日志兼容层

以下 CE 侧适配代码按 LF 的行为规则重写，不复制 LF 的资源模型：

- `PCL.Launcher.Core/Dependency/**`、`Plain Craft Launcher 2/Modules/Launcher/EnvironmentHistoryBridge.cs`、`InstanceCloneBridge.cs`；按键页复用 `InstanceKeybinding*` 引擎；依赖关系图复用 `ModDependencyInspection` — 启动前依赖检查、禁用前置启用、经 CE `Comp` 管线补全下载

移植方式：命名空间由 `PCLCS` 改为 `PCL.Living.Features`；去掉对 `MeloongCore.Shared` 的编译期依赖
（等价的字符串扩展与日志门面在本仓库内重写，语义保持一致，以保证 LF 的磁盘格式解析行为不变）。

## 版权与许可

- 原作：Plain Craft Launcher（PCL），作者龙腾猫跃及 PCL 原项目贡献者，
  Copyright © 成都瓜皮龙科技有限公司。
- 上游 PCL：https://github.com/Meloong-Git/PCL
- LF 分支：https://github.com/DDZmumo/PCL-Living-Fork
- 本项目沿用 PCL 的《分发有限许可》与《存储库合理使用指南》，并保留 CE 各目录原有许可。
