# MCP 記帳與消費查詢驗證

對應 OpenSpec change：`enhance-mcp-bookkeeping-and-consumption-query`。此紀錄區分自動化測試、程式審查與尚未執行的部署驗證，不以任務勾選取代測試證據。

## 自動化結果

| 命令與執行位置 | 結果 |
| --- | --- |
| repository root：`dotnet test backend/MyExpenses.Api.Tests/MyExpenses.Api.Tests.csproj` | 773 通過，0 失敗，0 略過 |
| MCP 目錄：`npm test` | 38 通過，0 失敗，包含 TypeScript 建置 |
| MCP 目錄：`npm run build` | 通過 |
| frontend 目錄：`npm test` | Node 74 通過；Vitest 256 通過，共 330 |
| frontend 目錄：`npm run build` | 型別檢查、Vite、設定及入口大小檢查通過 |
| repository root：`openspec validate enhance-mcp-bookkeeping-and-consumption-query --strict` | 通過 |
| repository root：`git diff --check` | 通過 |

## 證據對照

| 契約 | 測試或審查位置 |
| --- | --- |
| 後端日期、跨日及 read scopes | `MyExpenses.Api.Tests/Endpoints/TimeZoneDefaultsEndpointsTests.cs`、`ApiTokenScopeIntegrationTests.cs` |
| read scopes 不可管理卡片、設定或 token；撤銷後拒絕 ordinary replay | `ApiTokenScopeIntegrationTests.cs` |
| UUID、明確日期、型別及金額驗證，無 key 相容行為 | `TransactionCommandContractTests.cs`、`Services/TransactionCommandRollbackTests.cs` |
| 不同連線同時競爭、唯一收據核對與回滾 | `TransactionCommandContractTests.cs` 的同步屏障測試、`Services/FinancialCommandReplayBranchTests.cs` |
| ordinary／standalone／composite 的編輯、刪除原參考資料及 410 重播 | `Services/FinancialCommandReplayBranchTests.cs`、`InstallmentPurchaseContractTests.cs` |
| 1–60 期新增限制、付款排程與獨立生命週期 | `InstallmentPurchaseContractTests.cs`、`Services/InstallmentCommandRollbackTests.cs` |
| 卡費精確 predicate、分類改名、notes-only、付款狀態獨立性 | `ConsumptionQueryContractTests.cs`、`EndpointInputValidationTests.cs` |
| 歷史關聯去重、跨月份、刪除回補、多重關聯警告 | `ConsumptionQueryContractTests.cs` |
| 分類只涵蓋 ordinary、125 筆完整摘要、空集合與穩定分頁 | `ConsumptionQueryContractTests.cs`；讀模型在同一 Serializable transaction 內取得集合 |
| input／output schemas 與格式錯誤不冒充成功 | MCP `src/index.test.ts` 的 malformed read/write、unknown fields、selector 與 schema 測試 |
| 準備預設、收入、卡片歧義、卡費描述正規化 | MCP `src/index.test.ts` 的 preparation、defaults、selector 與 repayment 測試 |
| 固定 envelope、失敗保留命令、不確定寫入、真實 timeout | MCP `src/index.test.ts` 的 API error、malformed write、AbortSignal 測試 |
| MCP 子程序跨午夜重啟及 stdio 協定 | MCP `src/index.test.ts` 的 real stdio tests；HTTP API 使用 mock |
| 來源同 ID 區分、完整 query summary 與 coverage 轉送 | MCP `src/index.test.ts` 的 source IDs、ordinary search 與 consumption 測試 |
| 歷史 72 期可讀及回 replay，新建 72 期不可執行 | MCP `src/index.test.ts` 前兩項歷史期數測試 |
| 新 token 預設及既有 token 不自動擴權 | `frontend/test/mcp-token-scopes.test.ts` 原始碼契約測試；API 授權另以整合測試驗證 |
| 自然語言預期工具分派、月份比較及限制提示 | 本目錄 `README.md` 的固定 `2026-09-05` fixture 表；工具行為由上述 API／MCP 測試支撐 |

API 測試路徑以上均以 `backend/` 為起點。MCP 測試為 Node test runner，一個 test 可包含多組參數或 fixture。

## 審查結論

- 修正分類 consumption 混入信用卡總額、極大頁碼整數溢位、數字型別 enum 未定義值、composite 衝突後 replay indication 與 MCP 歷史超限期數讀取等問題。
- 無新增 migration、歷史資料改寫或既有報表計算變更；先前誤加的四個 `reports:read` endpoint 開放已移除，`ReportEndpoints.cs` 不再有此 change 的差異。
- MCP 只回 canonical API 資料；結果編輯後的 replay 不宣稱為原始 response snapshot，無效寫入回應保留 envelope 並標示 `outcome_unknown`。
- 月摘要保留實際 `totalBankBalance` 與匯率 metadata，已更正提案中不存在的 `balance`／`transactionCount` 欄位描述。
- 既有語意 selector 名稱仍被辨識，但在 write 工具只提供 `needs_preparation` 引導；固定 ID 命令不重新解析或預查參考資料。

## 驗證限制

- 未使用 production database、真實 token 或實際 OpenClaw 安裝執行寫入；不宣稱已完成部署 smoke test。不同 client 的 MCP adapter 與 secret 展開方式仍須部署時核對。
- 真實 stdio 子程序搭配 mock HTTP API；真實資料庫的冪等性、授權、回滾及重播由 .NET SQLite 整合測試獨立驗證，並非單一真實 MCP 到 production API 的端到端測試。
- 跨服務並行測試使用獨立 SQLite 連線與同步屏障；未進行獨立 OS 程序的資料庫壓力測試。
- 未執行瀏覽器 E2E 或任意 LLM 自然語言理解測試。固定 tool trace 是可檢查的分派契約，不是語言模型成功率保證。
- `openspec/` 被現有 `.gitignore` 忽略；spec、design、tasks 的修改留在本機。未自行變更忽略政策、強制加入 Git、提交或封存。
