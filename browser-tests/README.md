# MyExpenses Browser Tests

這組測試使用 `uv` 管理 Python、pytest 與 Playwright Firefox，驗證 Vue frontend 的真實瀏覽器行為。

## 安裝

```bash
uv sync --project browser-tests
uv run --directory browser-tests --project . playwright install firefox
```

## Deterministic Tests

測試會啟動 Vite 到 `127.0.0.1:5199`，並由 Playwright 攔截 `/api/**`，因此不需要 backend：

```bash
cd frontend
npm run test:e2e
```

涵蓋 Dashboard partial failure、中斷請求 recovery、分期付款 target state、分期消費 uncertain retry 與期間切換。

## Real-Stack Smoke

Real-stack smoke 不會自動啟停 Docker，也不會修改資料庫。執行前先啟動 backend：

```bash
docker compose up -d backend
cd frontend
E2E_REAL=1 npm run test:e2e:real
docker compose down
```

若未設定 `E2E_REAL=1`，real-stack test 會被明確 skip；一般 `npm run test:e2e` 只執行 deterministic tests。
