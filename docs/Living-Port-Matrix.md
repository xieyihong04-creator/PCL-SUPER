# PCL SUPER 移植矩阵（第 1 批完成后的状态）

基线：`PCL-CE/dev@16207f9e`（含 1 处 Release 编译修复）
移植源：`DDZmumo/PCL-Living-Fork`（main 为 VB 主体 + `migration/csharp-modern-dotnet` 的 121 个 C# 文件）
命名：PCL SUPER　｜　范围：全量移植　｜　重叠功能：保留 CE 实现

## 一、已移植（逻辑层，随主工程编译，415 项测试通过）

| 功能 | 移植内容 | 备注 |
|---|---|---|
| 还原点 / 环境快照 | `EnvironmentHistory*`、`RecentEnvironmentChanges` | 与 CE 崩溃分析并存，不替换 |
| 测试副本 / 实例克隆 | `InstanceClone*`（含来源分组、导出隔离） | CE 无对应实现 |
| 按键 / 个人游戏习惯 | `InstanceKeybinding*`、`PersonalOptions*` | 按版本设置、同键占用检查 |
| 世界元数据 | `WorldMetadata*`、`LocalGameDataStore` | 存档列表、图标与信息 |
| 资源管理引擎 | `InstanceResource*`（目录、在线匹配、事务、回收站文件） | 界面待接 |
| 存档备份引擎 | `WorldBackup*` | CE 已有备份功能，本轮只入库引擎，界面按“保留 CE”处理 |
| 模组依赖引擎 | `ModDependency*`（索引、解析、冲突、版本） | 依赖补全与关系图已接界面 |
| 映射下载 | `MapDownloadFiles`（下载目录与安装交接） | 安装细节由 `MapDownloadInstaller` 承接；界面见“存档压缩包导入” |
| 资源/版本模型 | `Constants`、`McVersion`、`McInstallManifest` | 与 CE 同名类型隔离在不同命名空间 |
| 整合包更新 | `ModpackUpdateEngine`（清单解析、差异分类、事务提交、回滚恢复、作者清单） | 第 2 批；保留 LF 的 `PCL/ModpackOrigin.json` 与备份目录语义 |
| 地图下载安装 | `MapDownloadInstaller`（单世界校验、重名顺延、越界与链接拒绝） | 第 2 批；默认目录 `PCLLFWorldDownload` |
| 存档压缩包导入 | `InstanceMapImportBridge` + 存档页“导入存档压缩包”：多选 `.zip`、逐个校验单个 `level.dat`、拒绝越界路径与符号链接，重名自动顺延编号 | 引擎沿用 `MapDownloadInstaller`；只解压，不移动原压缩包 |
| 导出隔离存档 | `Config.Export.VersionIsolation`（默认开）+ 导出页“导出时隔离存档”开关：开启时隐藏并取消存档选项，规则收集与配置覆盖都过滤 `saves/`，保证导出的整合包不含存档 | 关闭后恢复 CE 的存档选项，仍默认不勾选 |
| 启动前依赖判定 | `DependencyLaunchGate`（指纹、按版本目录的“本次运行已确认”缓存） | 报告视图由宿主填充，不依赖 LF 的资源模型 |
| 依赖关系图 | `ModDependencyGraphService` + `PageInstanceModGraph`：按必需依赖分列绘制，未满足的边与节点标红，点击查看 Mod ID / 文件 / 禁用状态 | 只读本地声明，不联网 |
| 按键界面 | `PageInstanceKeybinding`：读取 options.txt、补全模组名称与归属、重复占用分组、按格式修改并写回 | 修改前检查游戏是否在运行 |
| 测试副本界面 | `InstanceCloneBridge` + 实例管理卡片“创建测试副本 / 迁移测试环境” | 来源记录仍为 `PCL/CloneOrigin.json`；迁移前自动保存原环境还原点 |
| 还原点界面 | `PageInstanceHistory` + `EnvironmentHistoryBridge`：实例页新增“还原点”入口，创建 / 恢复 / 删除、中断日志回滚；引擎沿用 `EnvironmentHistoryEngine` | 与 CE 崩溃分析并存；不自动清理旧还原点 |
| 更新前自动留档 | `EnvironmentHistoryBridge.CaptureBeforeUpdate` 挂在资源更新的第一个加载步骤：替换任何 Mod / 资源包 / 光影前先保存“更新模组前”或“修改游戏组件前”还原点 | 留档失败只提示，不阻断更新 |
| 依赖补全（甲） | `PCL.Launcher.Core/Dependency/*`：`CeDependencyInspection`（LF 检查器绑定 CE 类型）、`CeDependencyProbe`（上下文/视图/缓存）、`DependencyAutoFill`（启用禁用文件 + `CompDependency` 补下载 + 事务回滚）、`DependencyLaunchCheck`（接到 `McLaunchStart`） | 平台记录只认 `LocalComp.json` 中“SHA1 + 版本 + 加载器”精确命中的条目；下载后仍用加载器元数据复核 |
| 首页继续上次的世界 | `ContinueWorldService` + `PageLaunchRight` 首页卡片：读取当前实例 `saves` 中最后游玩的存档，沿用 CE `SaveManager` 解析名称与时间；只读扫描，不入库 | 使用 CE 的存档解析与 `--quickPlaySingleplayer` 语义；版本不支持时隐藏卡片 |
| 整合包更新界面 | `InstanceModpackUpdateBridge` + 实例管理卡片“整合包更新”：选本地 `.zip`/`.mrpack` 或填写下载直链，按 LF 作者清单比较差异、下载校验、预览后事务提交；个人修改默认保留，更新前自动保存“整合包更新前”还原点 | 不猜测平台接口：直链更新由用户提供地址；Minecraft 或加载器版本变化会拒绝原地更新 |
| 作者清单检查更新 | 每个实例可填一个作者清单 URL（`ModpackManifestUrl`，默认空=关闭，仅接受 HTTPS 静态 JSON）：读取 `version`，与本地作者清单/`ModpackVersion` 相同则提示已是最新；否则经清单里的 `package` 直链下载，走同一套差异预览与事务提交。清单失败只提示，手动更新入口不受影响；确认前不写任何文件 | 清单跳转到非 HTTPS、内容超过 512 KB、JSON 非法或字段缺失都会中止检查；不自动提交，仍以预览确认框为准 |
| 整合包模式 | 按实例保存的 `ModpackMode` 标记（`VersionModpackId` 同组配置）：实例管理卡片一键开关；开启后资源页隐藏“安装本地资源/下载新资源”并显示管理说明，资源变动统一走整合包更新；整合包更新成功后自动开启 | 只影响该版本；关闭即恢复原状，不移动或删除任何文件 |
| 本地资源扫描与搜索 | 直接复用 CE 资源页：本地文件扫描、搜索框、启用/禁用/可更新/重复筛选、排序、安装本地文件、下载新资源 | 按“重叠保留 CE”不另建页面；LF 的 `ResourceSearcher/Project/Version` 仍未移植 |

## 一点五、本轮同时完成的 CE 侧改动

- **移除地区下载限制**：删除 `PCL.Core/App/Region/RegionPolicy.cs`、`RestrictedFeaturePolicy`、
  `IRestrictedFeatures`/`WpfRestrictedFeatures` 及其全部 10 处调用点与本地化文案。
  现在处在任何国家或地区都可以正常下载 Minecraft、补全文件、安装整合包与加载器。
  注意：这同时移除了“启动时缺少客户端文件则不下载”的分支，启动补全行为恢复为始终下载。

## 二、待移植（第 2 批剩余）

- `ModDependencyOnline/Index` 的“按哈希批量反查平台项目”索引：当前只复用 CE 已缓存的精确记录，
  不主动按 Mod ID 猜测项目；未命中的前置只给出说明
- `LocalResourceFile/Loaders`、`ResourceSearcher/Project/Version`（本地资源扫描与搜索）

## 三、待移植（第 3 批：界面与体验）

- 平台自动发现新版本（CurseForge / Modrinth 项目 ID）：作者清单检查已完成，接入平台接口需要凭据与产品取舍，未做

### 与 CE 重叠、按约定保留 CE 的部分

- 深色主题与不透明度：CE 已有 `Config.UiDarkMode` / `ThemeService` / `ColorTheme`，不再移植 `ModTheme`
- 资源回收站、存档备份、QuickPlay：CE 已有完整实现，LF 仅引擎入库
- `ModernTooltipTheme`：CE 已有 `ModernTooltip` 主题资源

## 四、不移植（保留 CE）

- 下载器、实例页、崩溃分析、QuickPlay、备份、回收站的**现有实现**（按“重叠保留 CE”）
- 更新与发布链路（CNB、预发布通道、自有更新清单）——自用且涉及凭据
- Client ID / CurseForge API Key / 联机密钥——一律不进仓库，由宿主环境变量提供
- LF 的品牌与赞助文案：仅在 `NOTICE-LIVING.md` 中保留署名与来源

## 五、验证

- `dotnet build "Plain Craft Launcher 2.slnx" -c Release` → 0 error（Linux 交叉编译）
- `dotnet PCL.Living.Features.Tests/bin/Release/net10.0/PCL.Living.Features.Tests.dll` → 415/415 通过
- 运行期验证仍需 Windows 实机

## 六、作者更新清单格式

实例管理卡片“更新清单”里填写的地址必须是以 `https://` 开头的静态 JSON（不超过 512 KB），跳转到非 HTTPS 会中止。
只有 `version` 与 `package` 必填，其余字段可省略（`name` 仅用于日志与预览）：

```json
{
  "name": "Example Pack",
  "version": "2.1.0",
  "package": "https://example.com/pack-2.1.0.zip"
}
```

检查流程：读取 `version` → 与本地 `PCL/ModpackOrigin.json` 中的版本或 `ModpackVersion` 比较 → 不同则下载 `package`
直链 → 与本地作者清单比对 → 弹出差异预览 → 玩家确认后才走原有事务提交（更新前自动保存还原点）。
任何一步失败都只弹出提示，不会修改实例文件，也不影响“整合包更新”的手动入口。
