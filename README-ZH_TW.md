[简体中文](README.md) | [English](README-EN.md) | **繁體中文**

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="Logo" width="80" height="80">

# PCL SUPER

以 [PCL-CE](https://github.com/PCL-Community/PCL-CE) 為基底，移植 [PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork) 功能的個人建置。

**預發布**：Linux 交叉編譯 0 error，415 項遷移測試通過；尚未在 Windows 實機執行驗收。

</div>

## 這是什麼

PCL SUPER 不是另一套啟動器。介面、下載器、實例頁、崩潰分析等與 CE 重疊的部分全部保留 CE 實作；
LF 的能力依行為改寫到 CE 的型別與資源模型上，兩邊並存而非互相取代。

基線：PCL-CE `dev@16207f9e`；移植來源：LF `main` + `migration/csharp-modern-dotnet`。

## 已實作

- **實例與環境**：還原點 / 環境快照、資源更新前自動留存、實例複製與測試副本 / 遷移
- **資源與依賴**：依賴檢查與啟動前判定、依賴自動補全（含交易回滾）、依賴關係圖、按鍵設定
- **整合包**：本機 `.zip` / `.mrpack`、下載直連、作者 HTTPS 清單檢查；差異預覽後交易提交、失敗回滾、更新前自動還原點；整合包模式
- **存檔**：首頁「繼續上次的世界」、地圖壓縮包匯入（單世界校驗、重名順延）
- **CE 側調整**：移除地區下載限制、匯出預設隔離存檔

## 目前不做

- 依雜湊反查平台專案、`ResourceSearcher/Project/Version` 線上搜尋、平台自動發現新版本
- 以上都需要平台 API 與憑證，目前口徑為全部暫緩，不接任何平台介面

作者清單只接受 HTTPS 靜態 JSON，必填 `version` 與 `package`，格式見
[`docs/Living-Port-Matrix.md`](docs/Living-Port-Matrix.md) 第六節。

## 驗收狀態

所有介面變更目前只經過 Linux 交叉編譯，**未在 Windows 實機執行**。
逐項清單見 [`docs/Windows-Acceptance.md`](docs/Windows-Acceptance.md)。

## 建置

需要 .NET 10 SDK。

```bash
dotnet build "Plain Craft Launcher 2.slnx" -c Release
dotnet PCL.Living.Features.Tests/bin/Release/net10.0/PCL.Living.Features.Tests.dll
```

Windows x64 框架相依發布：

```bash
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -r win-x64 --self-contained false -o publish
```

執行需要 .NET 10 Desktop Runtime（x64）。

## 目錄

| 目錄 | 內容 |
|---|---|
| `PCL.Core` / `PCL.Core.Wpf` | CE 核心與 WPF 基礎層 |
| `PCL.Launcher.Core` | 啟動、實例、依賴補全 |
| `PCL.Living.Features` | 自 LF 移植的引擎（net10.0，無 WPF 相依） |
| `PCL.Living.Features.Tests` | 415 項遷移測試 |
| `Plain Craft Launcher 2` | 應用與介面 |
| `docs` | 移植矩陣、驗收清單 |

## 授權與來源

- `Plain Craft Launcher 2/`：PCL《分發有限許可》，見 [`Plain Craft Launcher 2/LICENCE`](Plain%20Craft%20Launcher%202/LICENCE)。
  該許可只授予「原樣副本」的儲存與再分發權；本建置修改了該目錄，因此**不應公開分發**
- 其餘目錄：Apache-2.0，見 [`LICENSE`](LICENSE)
- LF 移植部分的署名、來源與授權說明：見 [`NOTICE-LIVING.md`](NOTICE-LIVING.md)
- 上游：[PCL](https://github.com/Meloong-Git/PCL) ｜ [PCL-CE](https://github.com/PCL-Community/PCL-CE) ｜ [PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork)

本專案為個人二次開發，不代表上游立場，請勿向上游倉庫回報本建置的問題。目前倉庫為私有預覽，未經許可不得公開分發。
