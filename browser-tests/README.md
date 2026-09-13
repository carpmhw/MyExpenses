# MyExpenses Browser Tests

這組測試使用 `uv` 管理 Python、pytest 與 Playwright Firefox，驗證 Vue frontend 的真實瀏覽器行為。

## 安裝

```bash
uv sync --project browser-tests
uv run --directory browser-tests --project . playwright install firefox
```

## Deterministic Tests

測試會由 session fixture 啟動 Vite 到固定的 `127.0.0.1:5199`，並由 Playwright 攔截 `/api/**`，因此不需要 backend：

```bash
cd frontend
npm run test:e2e
```

Vite 以 `--strictPort` 啟動。若 `5199` 已被任何程序占用，測試會明確失敗，不會自動換到 `5200` 或重用既有服務。啟動成功必須同時滿足本次程序輸出的 Local URL、程序仍存活，以及 `/login` 回應 HTTP 200。

涵蓋 Dashboard partial failure、中斷請求 recovery、分期付款 target state、分期消費 uncertain retry 與期間切換。

## Lifecycle Tests

生命週期回歸測試不載入瀏覽器測試的 autouse fixture，不會啟動 Firefox 或固定的 5199 伺服器：

```bash
uv run --directory browser-tests --project . pytest lifecycle-tests -q
```

目前程序群組管理使用 Linux/POSIX 的獨立 session 與 process group。正常 teardown、測試例外、啟動失敗、timeout 及可捕捉的 `KeyboardInterrupt` 會先送 `SIGTERM`，最多等待 5 秒，再必要時送 `SIGKILL`，最多再等待 5 秒。直接收到 `SIGKILL`、主機中斷或程序自行脫離群組時，Python `finally` 無法保證清理；遇到這些情況請依下節重新盤點。

## Historical Orphans

只清理由本 repository 完整命令建立、已確認為歷史孤兒的程序。先列出完整命令、PID、PPID、PGID、啟動時間與狀態，不要使用 `pkill node`，也不要只依 port 批次終止：

```bash
PATTERN='/srv/hermes_agent/home/workspace/Repo/MyExpenses/frontend/node_modules/.bin/vite --host 127.0.0.1 --port'
pgrep -af "$PATTERN"
ss -ltnp | rg '127\.0\.0\.1:(5199|52[0-9]{2})'
```

對每個已核對命令、PPID、啟動時間且確認為本次測試歷史殘留的 PID，先個別溫和終止：

```bash
ps -o pid=,ppid=,pgid=,sid=,lstart=,stat=,args= -p <PID>
kill -TERM <PID>
sleep 1
ps -o pid=,ppid=,pgid=,sid=,stat=,args= -p <PID>
```

只有在同一 PID 仍符合原本完整命令且仍存活時，才可對該 PID 使用 `SIGKILL`；不要因為 PID 已被重用而終止新程序：

```bash
kill -KILL <PID>
```

清理後重新執行 `pgrep -af "$PATTERN"` 與 `ss -ltnp`，確認目標程序及其 listener 消失，再開始 E2E。歷史 PID、程序數量與曾經使用過的 port 範圍都不是永久清理目標。

## Real-Stack Smoke

Real-stack smoke 不會自動啟停 Docker，也不會修改資料庫。執行前先啟動 backend：

```bash
docker compose up -d backend
cd frontend
E2E_REAL=1 npm run test:e2e:real
docker compose down
```

若未設定 `E2E_REAL=1`，real-stack test 會被明確 skip；一般 `npm run test:e2e` 只執行 deterministic tests。
