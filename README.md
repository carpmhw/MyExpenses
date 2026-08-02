# MyExpenses

個人記帳系統，追蹤提款、支出、信用卡分期、銀行帳戶、股票與財務快照。支援 MCP 協議，可透過 AI agent 直接記帳。

## 技術棧

- **Backend**: .NET 10、SQLite、Entity Framework Core、JWT 認證
- **Frontend**: Vue 3、TypeScript、Vite、Tailwind CSS v4、Chart.js
- **MCP Server**: Node.js、`@modelcontextprotocol/sdk`
- **部署**: Docker Compose、nginx reverse proxy

## 安全部署邊界

MyExpenses 是單一 owner 的個人系統，不是 multi-user 或 multi-tenant 服務。Production 的支援部署只有一個 reverse-proxy origin：兩容器 Compose 只發布 frontend/nginx，single-image Compose 只發布容器內的 nginx；ASP.NET Core backend 的 `5000` 只在內部容器或 process network 使用，不是瀏覽器或 MCP 的 Production 入口。

Production 預設是 localhost-only：reverse proxy bind 到 `127.0.0.1`。必須明確選擇 LAN 或 Remote，才會把入口暴露到其他網路。不要把 backend 的 `5000` 直接發布到 host，也不要用它繞過 TLS、security headers 或 proxy trust boundary。

## 快速開始：Local Compose

先在部署主機產生兩個彼此不同的隨機 secret。以下指令只會把隨機結果放入目前 shell 的環境，不會在文件中提供可重用的值：

```bash
umask 077
export MYEXPENSES_JWT_SECRET="$(openssl rand -hex 32)"
export MYEXPENSES_BOOTSTRAP_SECRET="$(openssl rand -hex 32)"
export MYEXPENSES_DEPLOYMENT_MODE=Local
export MYEXPENSES_BIND_ADDRESS=127.0.0.1
export MYEXPENSES_PUBLIC_ORIGIN=http://localhost
export MYEXPENSES_COOKIE_SECURE=false
```

確認 Docker Compose 已取得這些值後啟動兩容器部署：

```bash
docker compose up -d
```

或啟動 single-image 部署：

```bash
docker compose -f docker-compose.single.yml up -d
```

Local 的使用入口是 `http://localhost`。健康檢查也必須經過 reverse proxy：

```bash
curl --fail http://localhost/health/live
curl --fail http://localhost/health/ready
```

兩種 Compose 部署都會持久化 SQLite data、verified backups 與 Data Protection keys。兩容器版本的 backend service 使用 Compose internal network；single-image 版本則在同一容器內以 loopback 連到 backend。

### Deployment Smoke Verification

先執行不會啟動容器的 deployment contract checks：

```bash
sh scripts/test-deployment-config.sh
sh scripts/test-smoke-deployment.sh
```

Local smoke 會產生本次 process 專用的 JWT、bootstrap 與 owner password，使用獨立的 Compose project/volumes 啟動兩容器部署，驗證 fresh owner setup、health、`/api` routing、Bearer plus session-cookie authentication、restart persistence 與 backend host-port isolation，結束時只清除該 isolated project：

```bash
scripts/smoke-deployment.sh local
```

若已有對應 image，可使用 `SMOKE_SKIP_BUILD=1`。也可以指定未使用的 `COMPOSE_PROJECT_NAME`；script 會拒絕清理已有 containers、volumes 或 networks：

```bash
COMPOSE_PROJECT_NAME=myexpenses-smoke-check SMOKE_SKIP_BUILD=1 scripts/smoke-deployment.sh local
```

LAN smoke 只 render 明確的 non-loopback bind，並驗證 trusted home network 的 HTTP warning，不會要求 public network。Remote smoke 只 render HTTPS origin、Secure cookies、明確 trusted network 與 security-header/redirect contract；若本機已有可用 images，可用 `SMOKE_REMOTE_LIVE=1` 在 loopback edge 模擬 `X-Forwarded-Proto`，不需要真實 certificate 或 public internet：

```bash
scripts/smoke-deployment.sh lan
SMOKE_SKIP_BUILD=1 SMOKE_REMOTE_LIVE=1 scripts/smoke-deployment.sh remote
```

## Compose 環境變數契約

下列名稱是支援的 Compose deployment contract。`MYEXPENSES_JWT_SECRET` 在目前兩個 Compose 檔案中使用 `:?` fail-fast substitution，缺少時 `docker compose config`/啟動會失敗。Bootstrap secret 由 backend 依資料庫是否已初始化進行 fail-closed validation，因此 initialized installation 可以移除它後重新 render；其餘項目有 Local 預設值，但 LAN/Remote 必須按照下表明確設定。

| 變數 | 要求與用途 |
|------|------------|
| `MYEXPENSES_JWT_SECRET` | 必填。至少 32 字元、每個 deployment 獨立的隨機 JWT signing secret；不可使用 placeholder 或 Development fallback。 |
| `MYEXPENSES_BOOTSTRAP_SECRET` | 未初始化的 Production 安裝必須提供至少 32 字元的獨立 secret；owner 建立後可移除，backend 會永久關閉 registration。 |
| `MYEXPENSES_DEPLOYMENT_MODE` | `Local`、`Lan` 或 `Remote`；預設 `Local`。LAN/Remote 不應依賴預設值。 |
| `MYEXPENSES_BIND_ADDRESS` | reverse proxy 的 host bind；預設 `127.0.0.1`。LAN 要用明確的 non-loopback address；Remote 可依 TLS edge 是否同機選擇 loopback 或明確介面。 |
| `MYEXPENSES_PUBLIC_ORIGIN` | 瀏覽器與 MCP 看見的 origin，預設 `http://localhost`，不可包含 `/api` 或其他 path。Remote 必須是 absolute `https://` origin。 |
| `MYEXPENSES_COOKIE_SECURE` | 同時套用到 deployment 與 session cookie。Local/HTTP 必須為 `false`；Remote 必須為 `true`；LAN 依實際 HTTP/HTTPS transport 設定。 |
| `Deployment__TrustedProxies__0` | Remote 時可指定 reverse proxy 的明確 IP；不可填任意或 unrestricted address。這是 backend 的環境變數名稱，不是 `MYEXPENSES_` 前綴。 |
| `Deployment__TrustedNetworks__0` | `TrustedProxies` 的替代方案，Remote 時可指定明確 CIDR network，例如 operator 自己的 proxy network。不可使用 `0.0.0.0/0` 或 `::/0`。 |
| `MYEXPENSES_TRUSTED_EDGE_NETWORKS` | nginx 只有從這些明確 CIDR/IP 收到的連線才會採用 incoming `X-Forwarded-Proto`/`X-Forwarded-For`；預設只信任 loopback。Remote external edge 必須明確設定其 network。 |

Remote 至少要提供一個明確的 `Deployment__TrustedProxies__0` 或 `Deployment__TrustedNetworks__0`，以及 nginx 的 `MYEXPENSES_TRUSTED_EDGE_NETWORKS`。backend 只在 allowlist 內信任 `X-Forwarded-For` 與 `X-Forwarded-Proto`，並在 rate limiting、authentication 前處理；nginx 也不會從未列入 edge allowlist 的 client 直接採用 forwarded headers。TLS edge 必須覆寫客戶端提供的 forwarded headers，不能直接轉發未驗證的值。

`MYEXPENSES_HTTP_PORT` 可以改變 reverse proxy 的 host port，但不會讓 backend `5000` 變成公開入口。`SqliteBackup__RetentionLimit` 與 `SqliteBackup__BackupDirectory` 是可選的 backend 設定，詳見[備份與還原](#備份與還原)。

其他常用 backend 設定如下；Compose 會把 `MYEXPENSES_JWT_SECRET` 映射到 `Jwt:Secret`，並把 `MYEXPENSES_COOKIE_SECURE` 同步到 cookie 設定，不要讓兩組值互相衝突。

| 設定 | 說明 | 預設值 |
|------|------|--------|
| `ASPNETCORE_ENVIRONMENT` | 執行環境；Production 會啟用安全 secret 與 key preflight。 | `Production` |
| `ConnectionStrings__DefaultConnection` | SQLite database path。 | `Data Source=/app/data/MyExpenses.db`（Compose） |
| `Jwt__Issuer` / `Jwt__Audience` | JWT issuer 與 audience。 | `MyExpenses` |
| `Jwt__ExpiryMinutes` | JWT 有效時間（分鐘）。 | `1440` |
| `TimeZone__Default` / `TZ` | 應用程式與 container 的預設時區。 | `Asia/Taipei` |

### Secret 產生與保存

- JWT 與 bootstrap secret 必須分開產生、分開保存，不要把任何 secret 寫入 repository、`README`、Compose YAML、image layer、shell log、健康回應或 application log。
- 可用 `openssl rand -hex 32` 或等價的密碼管理器/secret manager 產生至少 32 字元的值；不要自己拼接可預測字串。
- 若使用 `.env` 或其他檔案，應限制為 owner-only 權限並確保不會被 commit；Production 優先使用部署平台的 secret injection。
- Development 可使用明確標示為 Development-only 的 JWT fallback，但該值只限 `ASPNETCORE_ENVIRONMENT=Development`，不可複製到 LAN、Remote 或任何 Production 環境。Production 仍必須提供外部 JWT secret。

## 首次 Owner Setup

1. 先在空資料庫啟動 Compose，等待 `http://localhost/health/ready` 成功；Remote 或 LAN 則改用其 reverse-proxy origin。
2. 用瀏覽器開啟該 origin。前端呼叫 `/api/auth/status`，發現 `hasUsers=false` 後顯示首次建立 owner 表單。
3. 輸入 email、display name、password 與 operator 產生的 bootstrap secret。前端只在當次 request 送出 `X-MyExpenses-Bootstrap-Secret` header，不會將它寫入 browser storage；成功後會清除表單中的值。
4. backend 會在資料庫 singleton constraint 下原子建立唯一 owner，成功後回傳一般 authenticated session。並發首次 setup 只有一個 request 能成功；後續 registration 永久關閉。
5. 確認 owner 可以重新登入、資料庫 readiness 正常，再設定 2FA 與保存 recovery codes。

### Setup 後移除 Bootstrap Secret

owner 建立完成後，應把 bootstrap secret 從 application runtime configuration、secret manager 的 deployment binding、shell environment 與臨時檔案移除。已初始化的 backend 不再需要 `Bootstrap:Secret` 才能啟動，且即使重新提供它也不會重新開放 registration。

注意：兩個 Compose 檔案允許 `MYEXPENSES_BOOTSTRAP_SECRET` 在 initialized installation 移除；若資料庫尚未有 owner，backend startup preflight 仍會拒絕缺少或不安全的 bootstrap configuration。不要把 bootstrap secret 保留成長期可用的 Production credential。

## Legacy Multi-user Migration Recovery

如果既有資料庫有超過一個 User，這是需要人工處理的 legacy state，不是可由升級自動猜測的資料清理工作。startup/migration 會在接受 request 前失敗並給出 actionable error；系統不會自動刪除、合併或選出某一個 User。

處理順序必須是：

1. 先停止舊版本與新版本 application，保留原始 SQLite database，並建立一份已通過 integrity/schema 檢查的 verified backup。不要先刪 User 再嘗試 migration。
2. 將 verified backup 與原始資料庫放到受控、加密的 recovery storage；reconciliation 只能在副本上進行，不能直接修改唯一的原始 recovery point。
3. 停機後依 operator 的資料保留政策完成 reconciliation，確認最後只剩一個合法 owner，並處理與被移除 User 關聯的 authentication/API-token 資料。Financial entities 不帶 `UserId`，不可因為 owner reconciliation 自動刪除 financial data。
4. 先在副本上執行 integrity check、migration 與 owner login/financial record verification；任何不確定都恢復原始 verified backup，保留舊 image，暫停升級。
5. 只有在結果與 recovery point 都已驗證後，才將已 reconciliation 的 database 部署回持久化 volume，重新啟動並確認 `/health/ready`。

這個流程是停機 reconciliation，不是 registration endpoint、API 或 README 提供的自動 multi-user migration command。遇到多 User 時，保留資料並停機比讓應用程式自動刪除資料安全。

## Deployment Modes

| Mode | Bind 與入口 | Cookie/transport | 安全邊界與限制 |
|------|-------------|------------------|----------------|
| `Local` | 預設 reverse proxy bind `127.0.0.1`，origin 通常是 `http://localhost`。 | HTTP；`MYEXPENSES_COOKIE_SECURE=false`。 | 只適合同一台主機的 browser/MCP。不要把 bind 改成 LAN address 後仍宣稱是 Local。 |
| `Lan` | 必須明確設定 non-loopback `MYEXPENSES_BIND_ADDRESS`，例如 trusted home network 的指定介面。 | HTTP LAN 時 cookie `Secure=false`；若由可信 TLS edge 提供 HTTPS，才可按實際 origin 設為 `true`。 | Plain HTTP 只適合完全信任的 home network，會受到同網路竊聽或惡意主機影響，不適合 internet。VPN 或 HTTPS 優先。 |
| `Remote` | 由外部 HTTPS reverse proxy、VPN gateway 或 tunnel edge 進入；`MYEXPENSES_PUBLIC_ORIGIN` 必須是 `https://`。 | 必須 `MYEXPENSES_COOKIE_SECURE=true`；HTTP 必須 redirect 到 HTTPS 或被拒絕。 | 必須設定 `Deployment__TrustedProxies__0` 或 `Deployment__TrustedNetworks__0`，並由 TLS edge 管理有效 certificate 與 forwarded headers。 |

LAN 範例只示範非敏感的文件保留位址，實際值應換成 operator 的介面與 origin：

```bash
export MYEXPENSES_DEPLOYMENT_MODE=Lan
export MYEXPENSES_BIND_ADDRESS=192.0.2.20
export MYEXPENSES_PUBLIC_ORIGIN=http://192.0.2.20
export MYEXPENSES_COOKIE_SECURE=false
```

Remote 若 TLS edge 與 Compose 在同一台主機，可讓 Compose 入口維持 loopback，再由 edge proxy 到該入口；若 edge 需要直接連到 Compose，必須明確設定可達的 bind address。Remote 範例：

```bash
export MYEXPENSES_DEPLOYMENT_MODE=Remote
export MYEXPENSES_BIND_ADDRESS=127.0.0.1
export MYEXPENSES_PUBLIC_ORIGIN=https://expenses.example.com
export MYEXPENSES_COOKIE_SECURE=true
export MYEXPENSES_TRUSTED_EDGE_NETWORKS=192.0.2.10/32
export Deployment__TrustedProxies__0=192.0.2.10
```

Remote edge 的 TLS certificate、HTTP-to-HTTPS redirect/rejection、DNS、firewall 與 private network access 是 operator 的責任。容器 nginx 以 HTTP 80 接收來自 edge 的流量，依 `X-Forwarded-Proto` 執行 Remote HTTP policy；不要把這視為容器自己提供了 TLS。Remote responses 會有 HSTS（由 edge/nginx policy）、`Content-Security-Policy: frame-ancestors 'none'`、`X-Content-Type-Options: nosniff` 與明確的 `Referrer-Policy`。

## Browser、API 與 MCP Origin

Production browser 一律開啟 reverse-proxy origin：

- Local：`http://localhost`
- LAN：operator 設定的 `http://<lan-host>` 或 HTTPS origin
- Remote：`https://expenses.example.com`

frontend 會以同源 `/api/...` 呼叫 backend。Production 不直接連到 backend 的 `5000` port，因為該 port 沒有 host port publication；直接打內部 port 也會繞過預期的 reverse-proxy security boundary。

MCP server 需要 Node.js `>=20` 與 owner 建立後由 UI 產生的 API token。`MYEXPENSES_API_URL` 填 reverse-proxy origin，不要加 `/api`，MCP 會自行附加 `/api/...`：

```bash
MYEXPENSES_API_URL=http://localhost
MYEXPENSES_API_TOKEN=TOKEN_CREATED_IN_MYEXPENSES
```

Remote MCP 使用 `MYEXPENSES_API_URL=https://expenses.example.com`；LAN 則使用 LAN reverse-proxy origin。API token 是 credential，不要寫入 repository 或公開 log。

OpenCode 設定範例：

```json
{
  "mcpServers": {
    "myexpenses": {
      "command": "node",
      "args": ["/path/to/backend/myexpenses-mcp-server/dist/index.js"],
      "env": {
        "MYEXPENSES_API_URL": "http://localhost",
        "MYEXPENSES_API_TOKEN": "TOKEN_CREATED_IN_MYEXPENSES"
      }
    }
  }
}
```

MCP server 提供以下工具：

| 工具 | 說明 |
|------|------|
| `create_transaction` | 記帳（收入 / 支出） |
| `list_categories` | 查詢收支分類 |
| `list_payment_methods` | 查詢支付方式 |
| `get_recent_transactions` | 查詢最近交易紀錄 |
| `get_financial_summary` | 取得本月收支摘要 |
| `undo_transaction` | 復原已刪除的交易 |

## 備份與還原

### Backup 建立與 retention

Production SQLite 路徑預設是 `/app/data/MyExpenses.db`，verified backups 預設在 `/app/data/backups`，兩個 Compose 檔案都把 backup directory 放在 `myexpenses-backups` volume。operator 可使用 repository 的 `scripts/backup-sqlite.sh`，或由整合的 `SqliteBackupService` 呼叫相同的 SQLite backup primitive：

```bash
sh scripts/backup-sqlite.sh \
  /app/data/MyExpenses.db \
  /app/data/backups \
  20260802132902_AddSingleOwnerInvariant \
  7
```

script 需要 `sqlite3`，並要求提供的 migration identity 與 active database history 相符；停止 application 可降低 operator 操作風險，但 SQLite backup primitive 也能處理線上 WAL database。若 active database 在 container volume 內，請在受控的 operator environment 掛載該 volume 後執行，不要直接假設 host 上存在 `/app/data`。

應用程式在 startup 發現「既有 database 且有 pending migrations」時，會先用 SQLite consistent backup primitive 建立 temporary backup，寫入建立時間、source migration identity/schema version，執行 `PRAGMA integrity_check`，再以 atomic publication 發布。backup 失敗或 integrity verification 失敗時，migration 不會套用，application 也不會報 readiness。空資料庫首次 migration，以及 schema 已 current 的一般 restart，不會產生不必要的 pre-migration backup。

每次 verified backup 成功後才執行 retention cleanup。預設保留 7 個 verified backups，可用 backend 設定覆寫：

```text
SqliteBackup__RetentionLimit=7
SqliteBackup__BackupDirectory=/app/data/backups
```

新的 backup 無效、目的地不可用、空間不足或 cleanup 失敗時，不得刪除既有 verified recovery point。backup 檔案不是應用程式加密格式；存到不受信任的主機、object storage 或 off-host location 前，必須先用 operator 管理的加密方案加密，並限制檔案權限。不要把 plaintext financial database 上傳到公開或未加密的位置。

### Restore、rollback 與驗證

既有還原 wrapper 是 `scripts/restore-sqlite.sh`，實際參數只有 active database 與 verified backup：

```bash
sh scripts/restore-sqlite.sh <active-database> <verified-backup>
```

執行前必須停止 application，並且執行環境必須有 `sqlite3` 指令。script 不會安裝 `sqlite3`、不會啟動 Compose、不會執行 migration，也不會修改 application configuration。Docker named volume 的 `/app/data` 不是必然可直接由 host path 讀取；請將 volume 在受控且安裝 `sqlite3` 的 operator environment 中掛載後，再使用上面的實際檔案路徑。

wrapper 會驗證 selected backup 存在、`PRAGMA integrity_check` 為 `ok`、`__MyExpensesBackupMetadata` 的 `IntegrityCheck` 為 `ok`、timestamp/schema metadata 完整，並確認 `MigrationIdentity` 與 `__EFMigrationsHistory` 一致。選定 backup 必須不同於 active database。驗證失敗時不會替換 active database。

通過驗證後，script 會先建立目前 database 的 owner-only rollback copy，處理 SQLite `-wal`/`-shm` sidecar，再以 atomic replacement 替換 active database；失敗時保留 active database。它成功後會印出 rollback path，請在完成驗證前保留該檔案。rollback copy 是當次 restore 的安全副本，不要未經驗證就宣稱它是帶有 verified metadata 的 backup；若需要回滾，先停止 application，再依同一檔案系統上的 SQLite/operator restore 程序放回 rollback copy，或改用另一個已驗證的 backup。

還原後啟動正常 Compose，讓 startup 只套用比 restored migration identity 更新的 migrations。至少完成以下驗證後才刪除 rollback copy：

1. 由 reverse-proxy origin 檢查 `/health/ready` 成功，並確認 liveness/readiness 沒有暴露 secret 或內部資料。
2. 用原 owner 登入，確認 session/cookie 與 registration-closed 狀態正常。
3. 檢查代表性的 transactions、accounts、installments、snapshots 或 dashboard totals，與 recovery point 的預期筆數/金額一致。
4. 完成一次 browser 與 MCP 讀取/寫入的最小驗證，確認都使用 reverse-proxy origin 而非 `5000`。

## Data Protection Keys

Production 使用穩定的 `DataProtection__ApplicationName=MyExpenses`，並把 key ring 寫到獨立的 `DataProtection__KeyDirectory=/app/keys`。兩個 Compose deployment 都掛載 `myexpenses-dataprotection:/app/keys`；該 volume 必須與 `/app/data` 分開持久化，key files 只能讓 application identity 讀取，不能寫入 log 或 health response。

保留 key volume 後重建 container，仍可解密尚未過期的 `mx_session` cookie。若 key volume 遺失、損壞或無法讀寫，Production startup 會 fail closed；既有 browser sessions 會失效，需要重新登入，但這不會刪除或損失 `/app/data/MyExpenses.db` 的任何 financial data，也不代表需要 restore database。Database backup 與 Data Protection key backup 應分開管理；若要備份 key volume，必須把它視為另一組敏感 secret 並使用受控加密 storage。

## Development

直接使用 backend port、Vite dev server 或 Development-only secret 只適合本機開發，不是 Production deployment contract：

```bash
# Backend
cd backend/MyExpenses.Api
dotnet run

# Frontend
cd frontend
npm install
npm run dev
```

開發環境需要 .NET 10 SDK 與 Node.js。Production 請使用 Compose reverse-proxy origin、外部 JWT secret、明確 deployment mode 與持久化 volumes。

## License

MIT
