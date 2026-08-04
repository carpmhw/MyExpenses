from __future__ import annotations

import json
import os
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Iterator
from urllib.parse import urlparse

import pytest
from playwright.sync_api import Page, Route

ROOT = Path(__file__).resolve().parents[2]
FRONTEND = ROOT / "frontend"
VITE_PORT = "5199"
VITE_URL = f"http://127.0.0.1:{VITE_PORT}"


# 回傳 JSON API 內容，讓瀏覽器測試可以精確控制狀態碼與資料。
def route_json(route: Route, payload: Any, status: int = 200) -> None:
    route.fulfill(
        status=status,
        content_type="application/json",
        body=json.dumps(payload),
    )


# 建立認證成功時所有頁面共用的最小 API 回應。
def _default_api_response(route: Route) -> None:
    request = route.request
    path = urlparse(request.url).path
    if not path.startswith("/api/"):
        route.fallback()
        return

    if path.endswith("/auth/status"):
        route_json(route, {
            "authenticated": True,
            "user": {"id": 1, "email": "e2e@example.test", "displayName": "E2E"},
            "hasUsers": True,
        })
        return
    if path.endswith("/settings/timezone"):
        route_json(route, {"timeZoneId": "Asia/Taipei"})
        return
    if path.endswith("/categories"):
        route_json(route, {"items": [], "total": 0, "page": 1, "pageSize": 999})
        return
    if path.endswith("/payment-methods"):
        route_json(route, {"items": [], "total": 0, "page": 1, "pageSize": 999})
        return
    if path.endswith("/credit-cards"):
        route_json(route, {"items": [], "total": 0, "page": 1, "pageSize": 999})
        return
    if path.endswith("/credit-card-bills"):
        route_json(route, [])
        return
    if path.endswith("/withdrawals"):
        route_json(route, {
            "items": [],
            "total": 0,
            "page": 1,
            "pageSize": 50,
            "summary": {"totalAmount": 0, "count": 0, "averageAmount": 0, "maxAmount": 0},
        })
        return
    if path.endswith("/transactions"):
        route_json(route, {
            "items": [],
            "total": 0,
            "page": 1,
            "pageSize": 50,
            "summary": {
                "totalAmount": 0,
                "totalIncome": 0,
                "totalExpense": 0,
                "count": 0,
                "dailyAverage": 0,
                "maxAmount": 0,
            },
        })
        return
    if path.endswith("/installments"):
        route_json(route, {
            "items": [],
            "total": 0,
            "page": 1,
            "pageSize": 50,
            "summary": {"totalCount": 0, "activeCount": 0, "dueAmount": 0, "duePaymentCount": 0},
        })
        return
    if path.endswith("/reports/dashboard-summary"):
        route_json(route, {
            "totalWithdrawals": 0,
            "withdrawalCount": 0,
            "totalExpenses": 0,
            "expenseCount": 0,
            "disposableBalance": 0,
            "installmentDueAmount": 0,
            "installmentDuePaymentCount": 0,
            "activeInstallmentCount": 0,
            "previousDisposableBalance": 0,
        })
        return
    if path.startswith("/api/reports/") or path.startswith("/api/snapshots/"):
        route_json(route, [])
        return

    if request.method in {"POST", "PUT", "PATCH", "DELETE"}:
        route_json(route, {})
        return
    route_json(route, {})


# 安裝共用 API mock；個別測試之後註冊的 route 會覆寫這些預設回應。
def install_default_api_routes(page: Page) -> None:
    page.route("**/*", _default_api_response)


# 提供已安裝預設 API mock 的 Playwright page 給 deterministic browser tests。
@pytest.fixture
def mocked_page(page: Page) -> Page:
    install_default_api_routes(page)
    return page


# 啟動 Vite 開發伺服器，並在測試 session 結束時完整回收子程序。
@pytest.fixture(scope="session", autouse=True)
def vite_server() -> Iterator[None]:
    process = subprocess.Popen(
        ["npm", "run", "dev", "--", "--host", "127.0.0.1", "--port", VITE_PORT],
        cwd=FRONTEND,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )
    deadline = time.monotonic() + 30
    last_error: Exception | None = None
    try:
        while time.monotonic() < deadline:
            if process.poll() is not None:
                output = process.stdout.read() if process.stdout else ""
                raise RuntimeError(f"Vite exited before startup:\n{output}")
            try:
                with urllib.request.urlopen(f"{VITE_URL}/login", timeout=1) as response:
                    if response.status < 500:
                        break
            except (urllib.error.URLError, TimeoutError) as error:
                last_error = error
            time.sleep(0.25)
        else:
            raise RuntimeError(f"Vite did not start within 30 seconds: {last_error}")
        yield
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


# 只在明確要求 real-stack 時保留 Docker backend smoke test。
def pytest_collection_modifyitems(config: pytest.Config, items: list[pytest.Item]) -> None:
    if os.environ.get("E2E_REAL") == "1":
        return
    skip = pytest.mark.skip(reason="set E2E_REAL=1 to run Docker-backed smoke tests")
    for item in items:
        if item.get_closest_marker("real_stack"):
            item.add_marker(skip)
