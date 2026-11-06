[简体中文](README.md) | [English](README-EN.md) | **繁體中文**

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.png" alt="Logo" width="96" height="96">

# PCL SUPER

以 [PCL-CE](https://github.com/PCL-Community/PCL-CE) 為基底，移植 [PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork) 功能的個人建置。

**狀態**：已在 Windows x64 實機驗收並修正缺陷，目前版本 2.15.2；469 項遷移測試中 464 項通過（5 項因非管理員無法建立符號連結而失敗）。

</div>

## 這是什麼

PCL SUPER 不是另一套啟動器。介面、下載器、實例頁、崩潰分析等與 CE 重疊的部分全部保留 CE 實作；
LF 的能力依行為改寫到 CE 的型別與資源模型上，兩邊並存而非互相取代。

基線：PCL-CE `dev@16207f9e`；移植來源：LF `main` + `migration/csharp-modern-dotnet`。

## 已實作

**實例與環境**

- 還原點 / 環境快照：建立、還原、刪除，中斷日誌回滾
- 資源更新前自動留存（替換任何 Mod / 資源包 / 光影前）
- 實例複製與「建立測試副本 / 遷移測試環境」

**資源與依賴**

- 依賴檢查、啟動前依賴判定、依賴自動補全（含交易回滾）
- 依賴關係圖：必要依賴分列、未滿足項標紅
- 按鍵設定：讀取 options.txt、補全模組名稱、重複佔用分組
- 下載頁的版本與載入器篩選跟隨目前選取的實例
- 模組、資源包、光影包的下載列表與詳情頁標記「已安裝」（依檔案雜湊比對 Modrinth / CurseForge 專案）

**整合包**

- 更新：本機 `.zip` / `.mrpack`、下載直連、作者 HTTPS 清單檢查
- 差異預覽後交易提交，個人修改預設保留，失敗自動回滾，更新前自動建立還原點
- 整合包模式：資源頁收斂為檢視與搜尋，資源變動走整合包更新
- Minecraft 或載入器版本變更時拒絕原地更新

**存檔**

- 首頁「繼續上次的世界」
- 存檔壓縮包匯入：多選、單世界校驗、越界與連結拒絕、重名順延

**啟動器**

- 自更新從本倉庫的 GitHub Releases 取得，並以 Release 資產的 SHA256 `digest` 校驗
- 啟動器不在前景或正在啟動 / 執行遊戲時，更新、公告與錯誤提示一律暫緩，回到前景後再提示

**CE 側調整**

- 移除地區下載限制
- 匯出預設隔離存檔（不打包 `saves/`）

## 目前不做

- 全量資源線上搜尋（`ResourceSearcher/Project/Version`）與平台側資源新版本自動發現
- 已安裝判定會依檔案雜湊批次查詢 Modrinth / CurseForge 的專案 ID；啟動器更新只查詢本倉庫的 GitHub Releases

作者清單只接受 HTTPS 靜態 JSON，必填 `version` 與 `package`，格式見
[`docs/Living-Port-Matrix.md`](docs/Living-Port-Matrix.md) 第六節。

## 驗收狀態

已在 Windows x64 實機完成驗收，發現的問題已修正並進入目前建置。
逐項驗收清單見 [`docs/Windows-Acceptance.md`](docs/Windows-Acceptance.md)；
遷移測試見 `PCL.Living.Features.Tests`（469 項，其中 5 項受非管理員符號連結權限限制）。

## 建置

需要 .NET 10 SDK。

```bash
dotnet build "Plain Craft Launcher 2.slnx" -c Release
dotnet run --project PCL.Living.Features.Tests
```

Windows x64 自包含單檔發佈（無需預裝 .NET）：

```bash
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64 -o publish
```

## 發佈 Release

1. 更新 `Plain Craft Launcher 2/metadata.json` 的 `version.base`（必要時同步 csproj 中的版本號）。
2. 確認 `gh` 已登入，然後執行：

   ```powershell
   powershell -NoProfile -File release.ps1 -Version 2.15.2
   ```

3. 腳本會重新發佈 win-x64 單檔，重新命名為 `PCL-SUPER-<版本>-win-x64.exe` 並建立 GitHub Release。

啟動器讀取本倉庫的 Releases 進行自更新：正式通道取最新的正式 Release，測試通道取最新的預發佈 Release。
資產必須包含 `PCL-SUPER-<版本>-win-x64.exe`，SHA256 取自 Release 資產的 `digest`。

## 目錄

| 目錄 | 內容 |
|---|---|
| `PCL.Core` / `PCL.Core.Wpf` | CE 核心與 WPF 基礎層 |
| `PCL.Launcher.Core` | 啟動、實例、依賴補全等業務層 |
| `PCL.Living.Features` | 自 LF 移植的引擎（net10.0，無 WPF 相依） |
| `PCL.Living.Features.Tests` | 遷移測試，469 項 |
| `Plain Craft Launcher 2` | 應用與介面 |
| `docs` | 移植矩陣、驗收清單 |

## 授權與來源

- `Plain Craft Launcher 2/`：PCL《分發有限許可》，見 [`Plain Craft Launcher 2/LICENCE`](Plain%20Craft%20Launcher%202/LICENCE)。
  該許可只授予「原樣副本」的儲存與再分發權；本建置修改了該目錄，修改版的分發範圍受上游許可限制，需自行確認合規
- 其餘目錄：Apache-2.0，見 [`LICENSE`](LICENSE)
- LF 移植部分的署名、來源與授權說明：見 [`NOTICE-LIVING.md`](NOTICE-LIVING.md)
- 上游：[PCL](https://github.com/Meloong-Git/PCL) ｜ [PCL-CE](https://github.com/PCL-Community/PCL-CE) ｜ [PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork)

本倉庫公開用於個人建置的原始碼與自更新 Release，不代表上游立場，請勿向上游倉庫回報本建置的問題。
