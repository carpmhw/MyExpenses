<div align="center">
  <img src="frontend/public/favicon.svg" width="96" alt="MyExpenses logo">

# MyExpenses

**單一 owner、自架式的個人財務管理系統**

[核心功能](#核心功能) · [快速開始](#快速開始) · [部署設定](#部署設定) · [MCP Server](#mcp-server) · [開發與驗證](#開發與驗證)
</div>

MyExpenses 集中管理日常收支、信用卡交易、多幣別帳戶、股票投資與財務快照，並可透過 MCP 讓已授權的 AI agent 安全查詢及記帳。

## 核心功能

- **收支與帳戶**：管理收入、支出、分類、付款方式、提款，以及 TWD、USD、JPY、CNY、HKD 帳戶與 TWD 換算。
- **信用卡交易**：建立一次付清或分期消費，追蹤每期應繳與付款狀態。
- **財務快照**：保存資產負債、換算匯率與歷史明細，並比較不同時間點的淨資產。
- **股票投資台帳**：記錄期初部位、買賣、費稅、現金股利與股票股利，支援台灣上市／上櫃行情。
- **分析報表**：檢視收支、分類、淨資產、信用卡應繳預測、持股結構、市場風險、TWR 與 XIRR。
- **自動化排程**：執行財務快照、即時股價更新與歷史行情同步，並保留執行狀態。
- **MCP 整合**：以具 scopes 的 API token 授權 AI agent 查詢資料、建立交易及復原交易。

信用卡交易新建與付款時程重建的期數限制為 1 至 60 期。變更前已存在的超過 60 期分期仍可查詢及標記既有付款，但不可重建超限時程。舊版 composite 命令建立的 `Transaction` 與 `Installment` 是獨立的歷史紀錄；兩者可分別編輯、刪除或還原，`TransactionId` 僅保留為歷史關聯，不會觸發同步或級聯生命週期。

## 技術架構

- **Backend**：.NET 10、ASP.NET Core Minimal APIs、EF Core 10、SQLite
- **Frontend**：Vue 3.5、TypeScript 6、Vite 8、Tailwind CSS 4、Chart.js
- **Authentication**：Session cookie、JWT、TOTP 2FA、recovery codes、API token scopes
- **MCP Server**：Node.js、TypeScript、Model Context Protocol SDK
- **Deployment**：Docker Compose、nginx reverse proxy
- **Testing**：xUnit、Node.js test runner、Vitest、pytest、Playwright Firefox

> [!IMPORTANT]
> Production 只能經 nginx 入口存取。Backend `5000` port 僅供容器或 process network 使用，不應發布到 host。

## 快速開始

需要 Docker Engine、Docker Compose v2 與 OpenSSL。先產生兩個不同且至少 32 字元的 secret：

```bash
umask 077
export MYEXPENSES_JWT_SECRET="$(openssl rand -hex 32)"
export MYEXPENSES_BOOTSTRAP_SECRET="$(openssl rand -hex 32)"
```

啟動雙容器版本：

```bash
docker compose up -d
```

或啟動 single-image 版本：

```bash
docker compose -f docker-compose.single.yml up -d
```

開啟 `http://localhost`，並確認服務狀態：

```bash
curl --fail http://localhost/health/live
curl --fail http://localhost/health/ready
```

### 首次 Owner Setup

1. 等待 `/health/ready` 成功後開啟網站。
2. 輸入 email、display name、password 與 bootstrap secret 建立唯一 owner。
3. 確認能重新登入後設定 2FA，並離線保存 recovery codes。
4. 從 runtime configuration、secret manager 與 shell 移除 bootstrap secret。

> [!WARNING]
> Owner 建立後 registration 會永久關閉。若既有資料庫包含多個 User，請先停機並建立 verified backup，再人工 reconciliation；系統不會自動刪除或選擇 User。

## 部署設定

| 變數 | 說明 |
| --- | --- |
| `MYEXPENSES_JWT_SECRET` | Production 必填，至少 32 字元且每個 deployment 獨立。 |
| `MYEXPENSES_BOOTSTRAP_SECRET` | 空資料庫首次建立 owner 時必填；完成後移除。 |
| `MYEXPENSES_DEPLOYMENT_MODE` | `Local`、`Lan` 或 `Remote`；預設 `Local`。 |
| `MYEXPENSES_BIND_ADDRESS` | nginx host bind；預設 `127.0.0.1`。 |
| `MYEXPENSES_PUBLIC_ORIGIN` | Canonical browser origin，不可包含 path 或 `/api`。 |
| `MYEXPENSES_COOKIE_SECURE` | HTTP 為 `false`；Remote HTTPS 必須為 `true`。 |
| `MYEXPENSES_HTTP_PORT` | nginx host port；預設 `80`。 |
| `MYEXPENSES_TRUSTED_EDGE_NETWORKS` | nginx 信任的 external edge 明確 CIDR/IP。 |
| `Deployment__TrustedProxies__0` | Backend 信任的直接上游 nginx 明確 IP。 |
| `Deployment__TrustedNetworks__0` | Backend 信任的直接上游 nginx 明確 CIDR。 |
| `TZ` | 初始時區；預設 `Asia/Taipei`，之後以資料庫中的系統時區為準。 |

| Mode | 入口與限制 |
| --- | --- |
| `Local` | 預設綁定 `127.0.0.1`，通常使用 `http://localhost`。 |
| `Lan` | 綁定明確 LAN address；plain HTTP 只適合可信 home network。 |
| `Remote` | 必須使用 HTTPS、Secure cookie 及明確的 proxy/network 信任範圍。 |

Remote 範例：

```bash
export MYEXPENSES_DEPLOYMENT_MODE=Remote
export MYEXPENSES_BIND_ADDRESS=127.0.0.1
export MYEXPENSES_PUBLIC_ORIGIN=https://expenses.example.com
export MYEXPENSES_COOKIE_SECURE=true
export MYEXPENSES_TRUSTED_EDGE_NETWORKS=192.0.2.10/32
export Deployment__TrustedNetworks__0=172.20.0.0/16
```

> [!WARNING]
> 上例 CIDR 僅為示意。部署前須換成實際且最小的信任範圍，不得使用 `0.0.0.0/0` 或 `::/0`。TLS、DNS、firewall、HTTP-to-HTTPS policy 與 forwarded headers sanitization 由 external edge 負責。

`MYEXPENSES_TRUSTED_EDGE_NETWORKS` 控制 external edge 到 nginx；`Deployment__TrustedProxies__0`／`Deployment__TrustedNetworks__0` 控制 nginx 到 backend。Single-image 版本透過 loopback 通訊，不需加入 Docker network。

Data Protection keys、SQLite database 與 backups 都使用 persistent volumes。遺失 keys 會讓既有 browser sessions 失效，但不會刪除財務資料。

## MCP Server

MCP Server 需要 Node.js 20 以上，以及在 MyExpenses UI 建立的 API token：

```bash
cd backend/myexpenses-mcp-server
npm ci
npm run build

export MYEXPENSES_API_URL=http://localhost
export MYEXPENSES_API_TOKEN=TOKEN_CREATED_IN_MYEXPENSES
npm start
```

`MYEXPENSES_API_URL` 必須是 reverse-proxy origin，且不可附加 `/api`；Remote 應使用 HTTPS。API token 只會在建立時顯示明文，不可提交至 repository 或寫入公開 log。

完整工具集使用的 scopes：`transactions:read`、`transactions:write`、`transactions:undo`、`categories:read`、`payment-methods:read`、`reports:read`、`credit-cards:read`、`agent-context:read`。不使用還原或月摘要時可省略 `transactions:undo` 或 `reports:read`；純查帳不需要寫入權限。MCP 不需要 `transactions:delete`。既有 token 不會自動取得新增 scopes，請在 UI 建立具所需 scopes 的新 token。

新增記帳採兩階段 envelope：先呼叫 `prepare_bookkeeping_entry`，再將回傳的 `arguments` 原樣交給 `create_transaction` 或 `create_credit_card_transaction`。準備階段會以後端 `agent-context` 固定系統時區日期、解析分類／付款方式／信用卡並產生 UUID `requestId`；`ready` 不代表已寫入。缺資料時回傳 `needs_input`，不會建立交易或 receipt。

工具口徑如下：

- `search_transactions` 查詢普通原始帳目，預設保留卡費；`repaymentOnly=true` 才套用 `Expense + living + Description 包含「信用卡帳單」`。
- `search_consumption` 查詢跨來源消費，信用卡依購買日計入總額，排除卡費並回傳完整 filtered summary、period、timezone、coverage 與 warnings。
- `get_financial_summary` 仍是普通財務月摘要，不可用來回答跨來源消費總額。
- 信用卡消費使用獨立 `credit_card_purchase` intent，不建立平行普通交易；卡費繳款使用 `credit_card_repayment` intent，僅建立普通 living 支出，不更新付款狀態。

重試必須保留整個 prepared envelope 與原 `requestId`。API 回覆 `replayed` 時可安全視為同一筆；連線中斷或 timeout 時 MCP 回傳 `outcome_unknown`，不得自動產生新 key。原 envelope 遺失時先查詢核對，不可用相同金額／描述猜測為同一筆。

支援 `mcpServers` 的 stdio client 設定範例（OpenClaw 請先核對所用版本的 MCP adapter；`${MYEXPENSES_API_TOKEN}` 是否展開由 client 決定，不是 Node 或 JSON 的功能）：

```json
{
  "mcpServers": {
    "myexpenses": {
      "command": "node",
      "args": ["/path/to/MyExpenses/backend/myexpenses-mcp-server/dist/index.js"],
      "env": {
        "MYEXPENSES_API_URL": "https://expenses.example.com",
        "MYEXPENSES_API_TOKEN": "${MYEXPENSES_API_TOKEN}"
      }
    }
  }
}
```

使用 secret manager 或 client 支援的環境變數注入 token，不要讓字面值 `${MYEXPENSES_API_TOKEN}` 被當成真實 token。完整的準備／執行、部署回退及固定日期範例見 [MCP 使用文件](backend/myexpenses-mcp-server/README.md)。

執行 MCP 驗證：

```bash
npm --prefix backend/myexpenses-mcp-server ci
npm --prefix backend/myexpenses-mcp-server test
```

## 備份與還原

Production database 預設位於 `/app/data/MyExpenses.db`，verified backups 位於 `/app/data/backups`。啟動時若有 pending migrations，application 會先建立 recovery point；備份失敗時不會套用 migration。

Compose 使用 named volumes；操作前須將 volumes 掛載到具備 repository scripts 與 `sqlite3` 的受控環境，並將以下路徑換成實際 mount path。

建立 verified backup：

```bash
MIGRATION_ID="$(sqlite3 /app/data/MyExpenses.db \
  'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;')"

sh scripts/backup-sqlite.sh \
  /app/data/MyExpenses.db \
  /app/data/backups \
  "$MIGRATION_ID" \
  7
```

還原前先停止 application，再執行：

```bash
sh scripts/restore-sqlite.sh <active-database> <verified-backup>
```

Restore script 會驗證 metadata 與 integrity、建立 rollback copy，再 atomic replacement active database。重新啟動後確認 health、Owner 登入、registration 狀態、代表性財務資料及 Browser/MCP 讀寫；確認完成前不要刪除 rollback copy。

> [!IMPORTANT]
> SQLite database、verified backups 與 Data Protection keys 都是敏感資料；off-host 保存前應加密並限制存取權限。

## 開發與驗證

本機開發需要 .NET 10 SDK、Node.js 24 與 OpenSSL。Backend 必須使用 Vite proxy 預期的 `5000` port：

```bash
cd backend/MyExpenses.Api
export Bootstrap__Secret="$(openssl rand -hex 32)"
dotnet run --urls http://localhost:5000
```

另一個 terminal 啟動 frontend：

```bash
cd frontend
npm ci
npm run dev
```

開啟 `http://localhost:5173`。執行核心驗證：

```bash
dotnet test backend/MyExpenses.Api.Tests/MyExpenses.Api.Tests.csproj

npm --prefix frontend ci
npm --prefix frontend run typecheck
npm --prefix frontend test
npm --prefix frontend run build

uv sync --project browser-tests
uv run --directory browser-tests --project . playwright install firefox
npm --prefix frontend run test:e2e

sh scripts/test-deployment-config.sh
sh scripts/test-smoke-deployment.sh
```

完整部署 smoke test 可執行 `scripts/smoke-deployment.sh local`、`lan` 或 `remote`；已有 images 時可設定 `SMOKE_SKIP_BUILD=1`。
