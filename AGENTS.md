# MyExpenses 開發代理指南

本文件適用於整個 repository。若子目錄有更具體的 `AGENTS.md`，修改該目錄時一併遵循。實作細節以目前程式碼、測試與設定檔為準；調整架構或開發指令時同步更新本文件。

## 專案定位與目錄

MyExpenses 是單一 owner、自架式的個人財務管理系統，涵蓋收支、信用卡、多幣別帳戶、股票台帳、財務快照與排程，並透過 MCP 提供查帳及記帳工具。

| 路徑 | 職責 |
| --- | --- |
| `backend/MyExpenses.Api/` | .NET 10、ASP.NET Core Minimal APIs、EF Core 10、SQLite |
| `backend/MyExpenses.Api/Program.cs` | DI、認證、middleware、啟動流程與 endpoint 註冊 |
| `backend/MyExpenses.Api/Endpoints/` | HTTP 路由、輸入驗證、授權與回應契約 |
| `backend/MyExpenses.Api/Services/` | 財務命令、計算器、外部行情、排程及資料庫啟動服務 |
| `backend/MyExpenses.Api/Models/` | 資料模型與 request／response 型別 |
| `backend/MyExpenses.Api/Data/`、`backend/MyExpenses.Api/Migrations/` | EF Core mapping、資料正規化與 schema 演進 |
| `backend/MyExpenses.Api.Tests/` | xUnit 服務、資料庫、API 契約與整合測試 |
| `frontend/src/` | Vue 3、TypeScript、Vite、Tailwind CSS 4、Chart.js |
| `frontend/src/api/`、`frontend/src/types/` | 共用 HTTP client、錯誤處理及 API 型別 |
| `frontend/src/pages/`、`frontend/src/components/` | 路由頁面、領域元件及共用 UI |
| `frontend/src/composables/`、`frontend/src/utils/` | 非同步狀態、認證、時區及可重用邏輯 |
| `frontend/test/` | Node.js test runner、Vitest 單元與元件測試 |
| `backend/myexpenses-mcp-server/` | 獨立 Node.js／TypeScript stdio MCP Server |
| `browser-tests/` | uv、pytest、Playwright Firefox 瀏覽器驗證 |
| `scripts/` | 部署驗證、smoke test、SQLite 備份還原及映像來源檢查 |
| `docker-compose*.yml`、`Dockerfile.single`、`nginx*.conf` | 雙容器與 single-image 部署 |

## 協作與程式碼規範

- 回覆、文件與新增註解使用繁體中文；程式識別字沿用英文及周邊命名方式。
- 所有新增或修改的函數／方法都要有繁體中文註解，說明用途及必要的契約或副作用。C# 優先使用 XML `<summary>`；TypeScript／Vue 使用 JSDoc 或鄰近註解；Python 使用 docstring。
- 修改前先執行 `git status --short`，辨識既有使用者變更；不要覆寫、回退或清理與任務無關的內容。
- 優先沿用現有服務、composable、元件與測試模式，保持變更聚焦。避免為單一功能加入新的框架或平行的基礎設施。
- C# 沿用 nullable、file-scoped namespace、非同步 I/O 與既有 DI 模式；TypeScript／Vue 沿用 `<script setup lang="ts">`、Composition API 及周邊格式。
- API 契約變更須一起檢查 backend DTO／endpoint、`frontend/src/types/index.ts`、`frontend/src/api/index.ts`，以及受影響的 MCP schema／client／測試。
- 依賴變更同步更新所屬 `package-lock.json` 或 `browser-tests/uv.lock`。兩個 Node.js 專案各有自己的依賴與指令，根目錄沒有共用 `package.json`。
- `backend/myexpenses-mcp-server/dist/` 目前有納入 Git；應修改 `src/` 並由 build 產生對應結果，不直接手改編譯輸出。
- `.opencode/`、`.superpowers/`、`openspec/` 與多數 `docs/` 內容目前被忽略；不要假設其中的本機規格已受版本控制。新增文件前確認 `.gitignore`，避免無意使用強制加入。

## 必須維持的領域契約

### 財務資料與查詢

- 後端金額、股數與匯率沿用 `decimal` 及各欄位既有精度／捨入規則；不要改用二進位浮點數保存金額，也不要把所有數值一律四捨五入成兩位。
- 幣別以 `CurrencyPolicy` 為準，目前為 TWD、USD、JPY、CNY、HKD，基準幣別是 TWD。前端 `utils/currency.ts` 與相關型別必須一致；原幣金額、換算金額與匯率資訊不可混用。
- 財務總額使用後端完整篩選結果的 summary；不要把目前分頁的 items 加總當成整體總額，也不要把 API 失敗或匯率缺失顯示成零。
- 保留 `FinancialSnapshotBuilder` 與快照保存的歷史明細、匯率及淨資產基準；歷史快照不能被目前行情或帳戶狀態默默重算。
- 普通帳目、信用卡消費、卡費繳款與分期付款狀態具有不同語意。消費查詢按信用卡購買日及全額計入，不能再把卡費重複算成消費。

### 寫入、分期與股票台帳

- 財務複合寫入沿用 `TransactionCommandService`、`InstallmentCommandService` 等既有交易邊界，維持原子性、rollback 與冪等回放契約。
- 對要求 `Idempotency-Key`／`requestId` 的操作，結果不確定時使用相同 key 與原 payload 重試；不得另產新 key 或退回無冪等保護的路徑。
- 分期付款 API 傳送明確的 `isPaid` 目標狀態，不改為 toggle。新建與付款時程重建限定 1–60 期；既有超限資料仍可查詢及標記付款。
- 舊 composite 命令的 `Transaction` 與 `Installment` 是獨立歷史紀錄；`TransactionId` 僅為歷史關聯，不能新增隱含同步刪除、還原或級聯生命週期。
- 股票持倉與衍生結果由 `StockLedgerService`／`StockLedgerCalculator` 回放台帳計算。交易新增、修改及刪除均需驗證剩餘歷史，不能只手動調整持股總數來繞過台帳。
- 外部行情沿用現有 provider 與 workflow；報表使用既有本機資料及資料品質契約，避免在讀取報表時另發不可控的行情請求。

### 時間與持久化

- 區分業務日期與時間點：日期沿用 `DateOnly`／日期字串契約，時間點以 UTC 保存；本地日期、顯示及排程透過 `TimeZoneService` 與前端共用 timezone 工具轉換。
- 系統時區以資料庫設定為準，`TZ`／`Asia/Taipei` 用於初始設定；不要改成依賴瀏覽器或主機的本地時區。可測試的目前時間沿用 `TimeProvider`。
- 本地日期篩選 UTC 時間點時，沿用 `ConvertLocalDateRangeToUtc` 的半開區間 `[start, endExclusive)`，避免自行組出一天最後一秒。
- schema 變更新增 EF Core migration，並同步 model snapshot；不要靠修改已套用的 migration 或重建資料庫來升級。
- 保留 `DatabaseStartupCoordinator` 的 migration 前 verified backup、完整性檢查與 readiness 流程；測試資料庫行為使用隔離的 SQLite 資料庫。
- `AppDbContext` 有字串 allowlist 正規化及 UTC conversion；新增欄位時檢查是否需要納入，而非對所有字串直接 Trim。
- 非 Production 的 EF Core Query 10103 會升級為例外。查詢應表達正確的條件、唯一性或排序，不以關閉警告解決問題。

## 各層實作注意事項

### Backend 與授權

- 使用現有 Minimal API endpoint group 與 service 分層。共用計算及多步驟寫入放入服務／計算器，避免在多個 endpoint 各自複製。
- 維持唯一 owner invariant、首次 bootstrap secret 驗證及註冊關閉規則；不能為方便測試而開放額外註冊。
- 新增 endpoint 時明確檢查認證及 API token scope metadata。API scopes 是真正的授權邊界，MCP tool annotations 不能取代它。
- 沿用既有錯誤碼、ProblemDetails、欄位驗證與 HTTP status 契約；列表沿用 `PaginationPolicy` 及穩定排序。

### Frontend

- API 呼叫集中於 `src/api/index.ts`，沿用 `ApiError`、取消請求與 session 過期協調；頁面不要自行實作另一套 fetch／401 處理。
- 查詢／寫入優先使用 `useAsyncQuery`、`useAsyncMutation` 與 `QueryState`。保留 loading、empty、refreshing、stale、error 的差異，以及過期回應保護和防止重複送出的行為。
- 重用 `components/ui/`、`style.css` 的語意色彩與共用 `Icon`／icon registry；維持暗色模式、行動版與可存取的表單操作。
- 路由沿用 lazy import。build 已包含設定檢查及入口大小預算，不要以放寬門檻取代解決 bundle 回歸。

### MCP Server

- 先讀 `backend/myexpenses-mcp-server/README.md` 與 `VERIFICATION.md`，維持 prepare → create／replay 的寫入流程。
- `MYEXPENSES_API_URL` 是 reverse-proxy origin，不含 `/api`；token 由環境注入。
- 保留固定日期、已解析 ID、prepared arguments、canonical source ID 與 `outcome_unknown` 契約；不同 `sourceType` 的 numeric ID 不能混用。
- stdio 的 stdout 保留給協定訊息；診斷資訊寫入 stderr，避免破壞 MCP transport。

## 開發與驗證指令

本機完整開發使用 .NET 10 SDK、Node.js 24、Python 3.12+、uv 與 OpenSSL；部署驗證另需 Docker Compose。MCP package 的最低 Node.js 版本為 20，但前端開發及 CI 以 24 為準。

以下指令除另有註明外，都在 repository 根目錄執行。

### 安裝與啟動

```bash
npm --prefix frontend ci
npm --prefix backend/myexpenses-mcp-server ci
uv sync --project browser-tests
uv run --directory browser-tests --project . playwright install firefox
```

在 `backend/MyExpenses.Api/` 啟動 API，空資料庫首次註冊需要 bootstrap secret：

```bash
export Bootstrap__Secret="$(openssl rand -hex 32)"
dotnet run --urls http://localhost:5000
```

另一個 terminal 在 repository 根目錄執行 `npm --prefix frontend run dev`，開啟 `http://localhost:5173`。Vite 將 `/api` 代理至 `http://localhost:5000`，不要依賴 launch profile 的其他預設 port。

### 依變更範圍選擇檢查

| 變更範圍 | 指令 |
| --- | --- |
| Backend | `dotnet test backend/MyExpenses.Api.Tests/MyExpenses.Api.Tests.csproj` |
| Frontend 型別 | `npm --prefix frontend run typecheck` |
| Frontend 單元及元件 | `npm --prefix frontend test` |
| Frontend production build／入口大小 | `npm --prefix frontend run build` |
| MCP 編譯及測試 | `npm --prefix backend/myexpenses-mcp-server test` |
| 瀏覽器互動 | `npm --prefix frontend run test:e2e` |
| 部署設定 | `sh scripts/test-deployment-config.sh` |
| Smoke script 回歸 | `sh scripts/test-smoke-deployment.sh` |

- `frontend test` 包含 `test:node`（`test/*.test.ts`）與 `test:vitest`（`test/unit/`、`test/component/`）；只執行其中一個不等於通過全部前端測試。目前沒有 `npm run lint`。
- 前端 CI 另執行 `npm audit --audit-level=high`；修改前端依賴時一併檢查。
- 一般 E2E 使用 `127.0.0.1:5199` 的 Vite 與攔截 API，不需 backend。Real-stack 需另行啟動 Docker backend，並以 `E2E_REAL=1 npm --prefix frontend run test:e2e:real` 明確啟用；詳見 `browser-tests/README.md`。
- Browser test lifecycle、port 占用與程序清理依 `browser-tests/README.md` 處理，避免終止其他開發程序。
- 完整部署 smoke 使用 `sh scripts/smoke-deployment.sh local`、`lan` 或 `remote`，先確認所選模式的環境設定；已有映像可設定 `SMOKE_SKIP_BUILD=1`。
- 修正缺陷及修改財務／授權／資料契約時，補上可重現問題的回歸測試；外部行情與時間使用 fixture 或可注入 provider，避免依賴即時網路結果。
- 純文件變更核對路徑、指令及 `git diff --check` 即可。完成回報列出實際執行的檢查與結果，未執行或被環境阻擋的項目明確說明。

## 部署與資料保護

- Production 經 nginx 入口存取；backend `5000` 僅供容器或 process network 使用。維持 `Local`、`Lan`、`Remote` 的現有入口與信任邊界。
- Remote 需要 HTTPS、Secure cookie 及明確的 trusted proxies／networks。變更部署時一起核對雙容器與 single-image 設定，以及 `/health/live`、`/health/ready`。
- 不將 `.env`、JWT／bootstrap secret、API token、recovery codes、SQLite 資料庫、備份或 Data Protection keys 寫入版本控制、測試 fixture 或公開 log。
- 不以刪除資料庫、volume、冪等 receipt 或 keys 解決啟動／測試問題。需要資料還原時依 `README.md` 的 verified backup／restore 流程處理。
- 映像來源與 Query 10103 排查參考 `docs/image-provenance-query-10103.md`；使用 `scripts/` 既有工具驗證，不只依 image tag 推斷版本。

## 完成前

1. 核對需求、相關呼叫端及受影響的領域契約，必要時同步 README／MCP 文件。
2. 執行與變更範圍相符的測試及 build，檢查實際輸出。
3. 檢查 `git diff --check`、`git diff` 與 `git status --short`，確認沒有非預期檔案或敏感資料。
4. 使用繁體中文摘要修改檔案、行為與驗證結果；只有使用者明確要求時才 commit、push 或建立 PR。
