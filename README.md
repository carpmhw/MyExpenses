# MyExpenses

個人記帳系統，可管理收支、提款、信用卡分期、銀行帳戶、股票與財務快照；並支援 MCP，讓 AI agent 直接查詢及記帳。

## 技術棧

- Backend：.NET 10、SQLite、Entity Framework Core、JWT
- Frontend：Vue 3、TypeScript、Vite、Tailwind CSS v4、Chart.js
- MCP Server：Node.js、Model Context Protocol SDK
- 部署：Docker Compose、nginx reverse proxy

## 核心功能

- **收支與帳戶管理**：管理收入、支出、分類、付款方式、銀行帳戶與提款紀錄。
- **信用卡分期**：建立分期消費、追蹤每期應繳與付款狀態。
- **財務快照與比較**：建立資產負債快照、保留歷史明細並比較不同時間點的淨資產變化。
- **股票持倉**：管理持股、券商、商品類型與估值，支援台灣上市／上櫃市場辨識及價格同步。
- **報表分析**：查看收支趨勢、分類分布、淨資產、分期預估、持股結構、股價趨勢與市場風險。
- **自動化排程**：檢視自動化快照與作業的排程、執行歷史及狀態。
- **MCP 整合**：讓已授權的 AI agent 透過 API token 查詢資料與建立交易。

## 部署邊界

MyExpenses 是單一 owner 的個人系統。Production 只能從 nginx 入口存取；backend `5000` port 僅供容器或 process network 使用，不應發布到 host。

預設 Local 模式只綁定 `127.0.0.1`。LAN 或 Remote 必須明確設定 bind address、public origin 與安全選項。

## 快速開始

產生兩個不同的隨機 secret：

```bash
umask 077
export MYEXPENSES_JWT_SECRET="$(openssl rand -hex 32)"
export MYEXPENSES_BOOTSTRAP_SECRET="$(openssl rand -hex 32)"
```

啟動兩容器版本：

```bash
docker compose up -d
```

或啟動 single-image 版本：

```bash
docker compose -f docker-compose.single.yml up -d
```

開啟 `http://localhost`，並透過 reverse proxy 檢查服務：

```bash
curl --fail http://localhost/health/live
curl --fail http://localhost/health/ready
```

### 首次 Owner Setup

1. 等待 `/health/ready` 成功後開啟網站。
2. 輸入 email、display name、password 與 bootstrap secret 建立唯一 owner。
3. 確認能重新登入後設定 2FA 並保存 recovery codes。
4. 從 runtime configuration、secret manager 與 shell 移除 bootstrap secret。

Registration 在 owner 建立後會永久關閉。若既有資料庫包含多個 User，應停止服務、建立 verified backup，再人工 reconciliation；系統不會自動刪除或選擇 User。

## 部署設定

| 變數 | 說明 |
|------|------|
| `MYEXPENSES_JWT_SECRET` | 必填，至少 32 字元且每個 deployment 獨立。 |
| `MYEXPENSES_BOOTSTRAP_SECRET` | 空資料庫首次建立 owner 時必填；完成後可移除。 |
| `MYEXPENSES_DEPLOYMENT_MODE` | `Local`、`Lan` 或 `Remote`；預設 `Local`。 |
| `MYEXPENSES_BIND_ADDRESS` | nginx 的 host bind；預設 `127.0.0.1`。 |
| `MYEXPENSES_PUBLIC_ORIGIN` | Browser 與 MCP 使用的 origin；不可包含 `/api`。 |
| `MYEXPENSES_COOKIE_SECURE` | HTTP 為 `false`；Remote HTTPS 必須為 `true`。 |
| `MYEXPENSES_HTTP_PORT` | nginx host port；預設 `80`。 |
| `MYEXPENSES_TRUSTED_EDGE_NETWORKS` | nginx 可信任 forwarded headers 的明確 CIDR/IP。 |
| `Deployment__TrustedProxies__0` | Remote backend 信任的明確 proxy IP。 |
| `Deployment__TrustedNetworks__0` | Remote backend 信任的明確 proxy CIDR。 |

| Mode | 入口與限制 |
|------|------------|
| `Local` | `127.0.0.1`、通常使用 `http://localhost`。 |
| `Lan` | 明確的 LAN address；Plain HTTP 只適合可信的 home network，不可暴露到 internet。 |
| `Remote` | 必須使用 HTTPS、Secure cookie；HTTP 必須 redirect 到 HTTPS 或被拒絕，並設定可信 proxy/network。 |

Remote 範例：

```bash
export MYEXPENSES_DEPLOYMENT_MODE=Remote
export MYEXPENSES_BIND_ADDRESS=127.0.0.1
export MYEXPENSES_PUBLIC_ORIGIN=https://expenses.example.com
export MYEXPENSES_COOKIE_SECURE=true
export MYEXPENSES_TRUSTED_EDGE_NETWORKS=192.0.2.10/32
export Deployment__TrustedProxies__0=192.0.2.10
```

TLS certificate、DNS、firewall、HTTP-to-HTTPS policy 與 forwarded headers sanitization 由 external edge 負責。不可使用 `0.0.0.0/0`、`::/0` 或直接公開 backend `5000`。

Data Protection keys 位於獨立的 persistent volume。遺失 keys 會讓既有 session 失效，但不會刪除 SQLite 財務資料。

### Deployment Verification

```bash
sh scripts/test-deployment-config.sh
sh scripts/test-smoke-deployment.sh
scripts/smoke-deployment.sh local
```

其他模式可執行 `scripts/smoke-deployment.sh lan` 或 `scripts/smoke-deployment.sh remote`；已有 images 時可設定 `SMOKE_SKIP_BUILD=1`。

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

`MYEXPENSES_API_URL` 必須是 reverse-proxy origin，不要附加 `/api`。Remote 使用 HTTPS origin；API token 不可寫入 repository 或公開 log。

可用工具：`create_transaction`、`list_categories`、`list_payment_methods`、`get_recent_transactions`、`get_financial_summary`、`undo_transaction`。

## 備份與還原

Production database 預設位於 `/app/data/MyExpenses.db`，verified backups 位於 `/app/data/backups`。兩種 Compose 部署都使用 named volumes 持久化 data、backups 與 Data Protection keys；host 不一定存在 `/app/data`，application images 也未包含這些 scripts 或 sqlite3。請先將 volumes 掛載到具備 repository scripts 與 `sqlite3` 的受控 operator environment，再把範例路徑換成實際 mount path。

建立 verified backup：

```bash
sh scripts/backup-sqlite.sh \
  /app/data/MyExpenses.db \
  /app/data/backups \
  20260802132902_AddSingleOwnerInvariant \
  7
```

Migration identity 必須與 active database 一致。Script 需要 `sqlite3`，會執行 integrity check，且只在新 backup 驗證成功後清理超過 retention limit 的舊 backup。

還原前先停止 application：

```bash
sh scripts/restore-sqlite.sh <active-database> <verified-backup>
```

Restore script 會驗證 backup metadata 與 integrity、建立 rollback copy，再 atomic replacement active database。還原後重新啟動服務，確認：

1. `/health/ready` 成功。
2. Owner 可以登入且 registration 維持關閉。
3. 代表性的交易、帳戶、分期、快照與 totals 正確。
4. Browser 與 MCP 都透過 reverse-proxy origin 讀寫。

確認完成前不要刪除 rollback copy。SQLite database 與 Data Protection keys 都是敏感資料，off-host 保存前應加密並限制權限。

## Development

本機開發需要 .NET 10 SDK 與 Node.js：

```bash
cd backend/MyExpenses.Api
dotnet run

cd ../../frontend
npm install
npm run dev
```

直接使用 backend port、Vite dev server 或 Development-only secret 只適合本機開發。

### 核心驗證

前端驗證涵蓋 TypeScript/Vue typecheck、既有 Node/Vitest tests 與 production build；部署 scripts 驗證 Compose、nginx 與 smoke-test 設定。

```bash
cd frontend
npm run typecheck
npm test
npm run build

cd ..
sh scripts/test-deployment-config.sh
sh scripts/test-smoke-deployment.sh
```

## Frontend Dependency Security

`frontend/package-lock.json` 是前端可重現安裝的基準。前端相依安全 workflow 會在相關 pull request 與 `main` push 先執行 `npm ci`，再以 `npm audit --audit-level=high` 阻擋 High 或 Critical advisory，並通過 typecheck、test 與 build。Dependabot 僅管理 `/frontend` npm 相依，採每週更新，一般版本更新最多同時開啟五個 pull request；security update PR 依 Dependabot 的安全更新機制處理，且不會自動合併。

相依更新後執行完整驗證：

```bash
cd frontend
npm ci
npm ls nanoid postcss radix-vue vite
npm audit --omit=dev --audit-level=high
npm audit --audit-level=high
npm run typecheck
npm test
npm run build
cd ..
docker build -t myexpenses-frontend:cve-fix ./frontend
```

不得 unattended 執行 `npm audit fix --force`、忽略 audit failure 或降低安全門檻。若標準 resolver 無法取得相容修補版，應先審查上游 semver 約束與 lockfile diff，再採用限定 parent dependency 的最小 override；不相關的直接相依與 major 版本應保持不變。

## License

MIT
