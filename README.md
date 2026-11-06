**简体中文** | [English](README-EN.md) | [繁體中文](README-ZH_TW.md)

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.png" alt="Logo" width="96" height="96">

# PCL SUPER

以 [PCL-CE](https://github.com/PCL-Community/PCL-CE) 为基底，移植 [PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork) 功能的个人构建。

**状态**：已在 Windows x64 实机验收并修复缺陷，当前版本 2.15.2；469 项迁移测试中 464 项通过（5 项因非管理员无法创建符号链接而失败）。

</div>

## 这是什么

PCL SUPER 不是另起一套启动器。界面、下载器、实例页、崩溃分析等与 CE 重叠的部分全部保留 CE 实现；
LF 的能力按行为重写到 CE 的类型与资源模型上，两边并存而不是相互替换。

基线：PCL-CE `dev@16207f9e`；移植源：LF `main` + `migration/csharp-modern-dotnet`。

## 已实现

**实例与环境**

- 还原点 / 环境快照：创建、恢复、删除，中断日志回滚
- 资源更新前自动留档（替换任何 Mod / 资源包 / 光影前）
- 实例克隆与“创建测试副本 / 迁移测试环境”

**资源与依赖**

- 依赖检查、启动前依赖判定、依赖补全（含事务回滚）
- 依赖关系图：必需依赖分列、未满足项标红
- 按键设置：读取 options.txt、补全模组名称、重复占用分组
- 下载页的版本与加载器筛选跟随当前所选实例
- 模组、资源包、光影包的下载列表与详情页标记「已安装」（按文件哈希匹配 Modrinth / CurseForge 工程）

**整合包**

- 更新：本地 `.zip` / `.mrpack`、下载直链、作者 HTTPS 清单检查
- 差异预览后事务提交，个人修改默认保留，失败自动回滚，更新前自动保存还原点
- 整合包模式：资源页收敛为查看与搜索，资源变动走整合包更新
- Minecraft 或加载器版本变化时拒绝原地更新

**存档**

- 首页“继续上次的世界”
- 存档压缩包导入：多选、单世界校验、越界与链接拒绝、重名顺延

**启动器**

- 自更新从本仓库的 GitHub Releases 获取，并用 Release 资产的 SHA256 `digest` 校验
- 启动器不在前台或正在启动 / 运行游戏时，更新、公告与错误提示一律暂缓，回到前台后再提示

**CE 侧调整**

- 移除地区下载限制
- 导出默认隔离存档（不打包 `saves/`）

## 当前不做

- 全量资源在线搜索（`ResourceSearcher/Project/Version`）与平台侧资源新版本自动发现
- 已安装判定会按文件哈希批量查询 Modrinth / CurseForge 的工程 ID；启动器更新只查询本仓库的 GitHub Releases

作者清单只接受 HTTPS 静态 JSON，必填 `version` 与 `package`，格式见
[`docs/Living-Port-Matrix.md`](docs/Living-Port-Matrix.md) 第六节。

## 验收状态

已在 Windows x64 实机完成验收，发现的问题已修复并进入当前构建。
逐项验收清单见 [`docs/Windows-Acceptance.md`](docs/Windows-Acceptance.md)；
迁移测试见 `PCL.Living.Features.Tests`（469 项，其中 5 项受非管理员符号链接权限限制）。

## 构建

需要 .NET 10 SDK。

```bash
dotnet build "Plain Craft Launcher 2.slnx" -c Release
dotnet run --project PCL.Living.Features.Tests
```

Windows x64 自包含单文件发布（无需预装 .NET）：

```bash
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64 -o publish
```

## 发布 Release

1. 更新 `Plain Craft Launcher 2/metadata.json` 的 `version.base`（必要时同步 csproj 中的版本号）。
2. 确认 `gh` 已登录，然后运行：

   ```powershell
   powershell -NoProfile -File release.ps1 -Version 2.15.2
   ```

3. 脚本会重新发布 win-x64 单文件，重命名为 `PCL-SUPER-<版本>-win-x64.exe` 并创建 GitHub Release。

启动器读取本仓库的 Releases 进行自更新：正式通道取最新的正式 Release，测试通道取最新的预发布 Release。
资产必须包含 `PCL-SUPER-<版本>-win-x64.exe`，SHA256 取自 Release 资产的 `digest`。

## 目录

| 目录 | 内容 |
|---|---|
| `PCL.Core` / `PCL.Core.Wpf` | CE 核心与 WPF 基础层 |
| `PCL.Launcher.Core` | 启动、实例、依赖补全等业务层 |
| `PCL.Living.Features` | 从 LF 移植的引擎（net10.0，无 WPF 依赖） |
| `PCL.Living.Features.Tests` | 迁移测试，469 项 |
| `Plain Craft Launcher 2` | 应用与界面 |
| `docs` | 移植矩阵、验收清单 |

## 许可与来源

- `Plain Craft Launcher 2/`：PCL《分发有限许可》，见 [`Plain Craft Launcher 2/LICENCE`](Plain%20Craft%20Launcher%202/LICENCE)。
  该许可只授予“原样副本”的存储与再分发权；本构建修改了该目录，修改版的分发范围受上游许可限制，需自行确认合规
- 其余目录：Apache-2.0，见 [`LICENSE`](LICENSE)
- LF 移植部分的署名、来源与许可说明：见 [`NOTICE-LIVING.md`](NOTICE-LIVING.md)
- 上游：[PCL](https://github.com/Meloong-Git/PCL) ｜ [PCL-CE](https://github.com/PCL-Community/PCL-CE) ｜ [PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork)

本仓库公开用于个人构建的源码与自更新 Release，不代表上游立场，请勿向上游仓库反馈本构建的问题。
