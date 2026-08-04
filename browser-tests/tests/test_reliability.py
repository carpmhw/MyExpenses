from __future__ import annotations

import json
import re
from typing import Any
from urllib.parse import parse_qs, urlparse

import pytest
from playwright.sync_api import Page, Route, expect


EMPTY_INSTALLMENTS = {
    "items": [],
    "total": 0,
    "page": 1,
    "pageSize": 15,
    "summary": {"totalCount": 0, "activeCount": 0, "dueAmount": 0, "duePaymentCount": 0},
}

CARD = {
    "id": 3,
    "bankName": "測試銀行",
    "lastFourDigits": "1234",
    "cardNetwork": "Visa",
    "statementDay": 15,
    "dueDay": 23,
    "creditLimit": 10000,
    "notes": None,
    "createdAt": "2026-08-01T00:00:00Z",
    "updatedAt": "2026-08-01T00:00:00Z",
}

INSTALLMENT = {
    "id": 7,
    "transactionId": None,
    "cardId": 3,
    "totalAmount": 300,
    "periods": 3,
    "perPeriod": 100,
    "remainingPeriods": 3,
    "status": "Active",
    "purchaseDate": "2026-08-01",
    "createdAt": "2026-08-01T00:00:00Z",
    "description": "測試分期",
    "transaction": None,
    "card": CARD,
    "payments": [{
        "id": 71,
        "installmentId": 7,
        "period": 1,
        "amount": 100,
        "paidDate": None,
        "dueDate": "2026-08-23",
        "isPaid": False,
    }],
}


# 回傳 JSON API 內容，讓個別瀏覽器案例可控制 response body 與 status。
def route_json(route: Route, payload: Any, status: int = 200) -> None:
    route.fulfill(
        status=status,
        content_type="application/json",
        body=json.dumps(payload),
    )


# 回傳交易頁建立分期所需的最小類別、支付方式與信用卡資料。
def _installment_purchase_options(route: Route) -> None:
    path = urlparse(route.request.url).path
    if path.endswith("/categories"):
        route_json(route, {
            "items": [{"id": 1, "name": "餐飲", "type": "Expense", "icon": "", "color": "#4F759D", "sortOrder": 1}],
            "total": 1,
            "page": 1,
            "pageSize": 999,
        })
        return
    if path.endswith("/payment-methods"):
        route_json(route, {
            "items": [{"id": 2, "name": "信用卡", "systemCode": "credit-card", "icon": "", "color": "#4F759D"}],
            "total": 1,
            "page": 1,
            "pageSize": 999,
        })
        return
    if path.endswith("/credit-cards"):
        route_json(route, {"items": [CARD], "total": 1, "page": 1, "pageSize": 999})
        return
    route.fallback()


# 回傳一般交易建立所需的最小分類與支付方式資料。
def _ordinary_entry_options(route: Route) -> None:
    path = urlparse(route.request.url).path
    if path.endswith("/categories"):
        route_json(route, {
            "items": [{"id": 1, "name": "餐飲", "type": "Expense", "icon": "", "color": "#4F759D", "sortOrder": 1}],
            "total": 1,
            "page": 1,
            "pageSize": 999,
        })
        return
    if path.endswith("/payment-methods"):
        route_json(route, {
            "items": [{"id": 11, "name": "現金", "systemCode": "cash", "icon": "", "color": "#4F759D"}],
            "total": 1,
            "page": 1,
            "pageSize": 999,
        })
        return
    if path.endswith("/credit-cards"):
        route_json(route, {"items": [], "total": 0, "page": 1, "pageSize": 999})
        return
    route.fallback()


# 驗證 Dashboard 的獨立區塊在分期 API 失敗時仍保留成功資料。
def test_dashboard_partial_failure(mocked_page: Page) -> None:
    def installments_failure(route: Route) -> None:
        route_json(route, {"title": "Installments unavailable"}, status=503)

    mocked_page.route("**/api/installments**", installments_failure)
    mocked_page.goto("/dashboard")

    expect(mocked_page.get_by_text("提款合計")).to_be_visible()
    expect(mocked_page.get_by_role("alert")).to_contain_text("Installments unavailable")


# 驗證中斷的初始請求可以透過 inline retry 恢復成真正的空成功狀態。
def test_interrupted_installment_request_recovers(mocked_page: Page) -> None:
    attempts = 0

    def installments_request(route: Route) -> None:
        nonlocal attempts
        attempts += 1
        if attempts == 1:
            route.abort("failed")
            return
        route_json(route, EMPTY_INSTALLMENTS)

    mocked_page.route("**/api/installments**", installments_request)
    mocked_page.goto("/installments")
    mocked_page.get_by_role("button", name="重試").first.click()

    expect(mocked_page.get_by_text("尚無分期資料")).to_be_visible()
    assert attempts == 2


# 驗證付款操作送出明確的 isPaid target state，而不是依賴 toggle。
def test_explicit_payment_state(mocked_page: Page) -> None:
    paid_request: dict[str, Any] = {}
    updated_installment = {
        **INSTALLMENT,
        "payments": [{**INSTALLMENT["payments"][0], "isPaid": True, "paidDate": "2026-08-02"}],
        "remainingPeriods": 2,
    }

    def list_installments(route: Route) -> None:
        route_json(route, {**EMPTY_INSTALLMENTS, "items": [INSTALLMENT], "total": 1, "summary": {"totalCount": 1, "activeCount": 1, "dueAmount": 100, "duePaymentCount": 1}})

    def get_installment(route: Route) -> None:
        route_json(route, INSTALLMENT if not paid_request else updated_installment)

    def mark_payment(route: Route) -> None:
        paid_request.update(json.loads(route.request.post_data or "{}"))
        route_json(route, updated_installment)

    mocked_page.route("**/api/installments**", list_installments)
    mocked_page.route("**/api/installments/7", get_installment)
    mocked_page.route("**/api/installments/7/payments/71", mark_payment)
    mocked_page.goto("/installments")
    mocked_page.get_by_role("button", name="檢視時程").click()
    mocked_page.get_by_role("button", name="標記已繳").click()
    mocked_page.get_by_role("button", name="確認").click()

    expect(mocked_page.get_by_text("已繳", exact=True)).to_be_visible()
    assert paid_request["isPaid"] is True
    assert paid_request["paidDate"]


# 驗證 unchanged uncertain retry 會重用同一個 installment purchase key。
def test_installment_purchase_retry_reuses_idempotency_key(mocked_page: Page) -> None:
    keys: list[str | None] = []
    attempts = 0

    mocked_page.route("**/api/categories**", _installment_purchase_options)
    mocked_page.route("**/api/payment-methods**", _installment_purchase_options)
    mocked_page.route("**/api/credit-cards**", _installment_purchase_options)

    def purchase_request(route: Route) -> None:
        nonlocal attempts
        attempts += 1
        keys.append(route.request.headers.get("idempotency-key"))
        if attempts == 1:
            route.abort("failed")
            return
        route_json(route, {"transaction": {}, "installment": {}})

    mocked_page.route("**/api/installment-purchases**", purchase_request)
    mocked_page.goto("/expenses")
    mocked_page.get_by_role("button", name=re.compile("新增")).first.click()

    form = mocked_page.locator("form").last
    form.locator("#transaction-amount").fill("300")
    form.locator("#transaction-description").fill("測試分期")
    form.locator("#transaction-category").select_option("1")
    form.locator("#transaction-payment-method").select_option("2")
    form.locator("#transaction-payment-mode").select_option("installment")
    form.locator("#transaction-installment-periods").fill("3")
    form.locator("#transaction-installment-card").select_option("3")
    form.get_by_role("button", name="建立支出與分期").click()
    assert attempts == 1

    form.get_by_role("button", name="使用相同資料重試").click()
    expect(mocked_page.get_by_text("交易與分期已建立")).to_be_visible()
    assert attempts == 2
    assert keys[0] is not None
    assert keys[0] == keys[1]


# 驗證交易表單能以鍵盤完成基本輸入，並提供可程式化的標籤、錯誤與狀態。
def test_transaction_entry_keyboard_and_accessibility(mocked_page: Page) -> None:
    mocked_page.route("**/api/categories**", _ordinary_entry_options)
    mocked_page.route("**/api/payment-methods**", _ordinary_entry_options)
    mocked_page.route("**/api/credit-cards**", _ordinary_entry_options)
    mocked_page.goto("/expenses")
    mocked_page.get_by_role("button", name=re.compile("新增")).first.click()

    form = mocked_page.locator("form").last
    expect(form.locator("#transaction-date")).to_be_focused()
    expect(form.locator('label[for="transaction-date"]')).to_have_text("交易日期")
    form.locator("#transaction-type").focus()
    expect(form.locator("#transaction-type")).to_be_focused()

    form.get_by_role("button", name="建立支出").press("Enter")
    expect(form.get_by_role("alert")).to_contain_text("請修正表單中的錯誤")
    expect(form.locator("#transaction-amount")).to_have_attribute("aria-invalid", "true")

    form.locator("#transaction-date").fill("2026-08-03")
    form.locator("#transaction-amount").fill("1280")
    form.locator("#transaction-category").select_option("1")
    form.locator("#transaction-description").fill("鍵盤測試")
    form.locator("#transaction-payment-method").select_option("11")
    form.get_by_role("button", name="建立支出").press("Enter")

    expect(mocked_page.get_by_role("status")).to_contain_text("交易已建立")


# 驗證窄螢幕交易表單採單欄、無水平溢位且主操作仍可觸及。
def test_transaction_entry_mobile_layout(mocked_page: Page) -> None:
    mocked_page.set_viewport_size({"width": 375, "height": 800})
    mocked_page.route("**/api/categories**", _ordinary_entry_options)
    mocked_page.route("**/api/payment-methods**", _ordinary_entry_options)
    mocked_page.route("**/api/credit-cards**", _ordinary_entry_options)
    mocked_page.goto("/expenses")
    mocked_page.get_by_role("button", name=re.compile("新增")).first.click()

    dialog = mocked_page.get_by_role("dialog")
    expect(dialog).to_be_visible()
    assert mocked_page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")
    primary = dialog.get_by_role("button", name="建立支出")
    expect(primary).to_be_visible()
    box = primary.bounding_box()
    assert box is not None
    assert box["height"] >= 44


# 驗證 Dashboard period identity 改變時會先清除舊月份資料。
def test_dashboard_period_switch_clears_old_rows(mocked_page: Page) -> None:
    old_transaction = {
        "id": 1,
        "type": "Expense",
        "amount": 10,
        "date": "2026-08-01",
        "description": "八月資料",
        "notes": None,
        "categoryId": 1,
        "paymentMethodId": None,
        "createdAt": "2026-08-01T00:00:00Z",
        "category": {"id": 1, "name": "餐飲", "type": "Expense", "icon": "", "color": "#4F759D", "sortOrder": 1},
        "paymentMethod": None,
    }
    previous_transaction = {**old_transaction, "id": 2, "date": "2026-07-01", "description": "七月資料"}
    new_transaction = {**old_transaction, "id": 3, "date": "2026-08-01", "description": "八月新資料"}
    pending_route: Route | None = None
    transaction_calls = 0

    def transactions_by_period(route: Route) -> None:
        nonlocal pending_route, transaction_calls
        transaction_calls += 1
        query = parse_qs(urlparse(route.request.url).query)
        if query.get("startDate") == ["2026-08-01"] and transaction_calls == 1:
            route_json(route, {"items": [old_transaction], "total": 1, "page": 1, "pageSize": 50, "summary": {"totalAmount": 10, "totalIncome": 0, "totalExpense": 10, "count": 1, "dailyAverage": 10, "maxAmount": 10}})
            return
        if query.get("startDate") == ["2026-07-01"]:
            route_json(route, {"items": [previous_transaction], "total": 1, "page": 1, "pageSize": 50, "summary": {"totalAmount": 10, "totalIncome": 0, "totalExpense": 10, "count": 1, "dailyAverage": 10, "maxAmount": 10}})
            return
        pending_route = route

    mocked_page.route("**/api/transactions**", transactions_by_period)
    mocked_page.goto("/dashboard")
    expect(mocked_page.get_by_text("八月資料")).to_be_visible()

    mocked_page.get_by_role("button", name="上一個月").click()
    expect(mocked_page.get_by_text("七月資料")).to_be_visible()
    mocked_page.get_by_role("button", name="下一個月").click()
    expect(mocked_page.get_by_text("七月資料")).not_to_be_visible()
    assert pending_route is not None
    route_json(pending_route, {"items": [new_transaction], "total": 1, "page": 1, "pageSize": 50, "summary": {"totalAmount": 10, "totalIncome": 0, "totalExpense": 10, "count": 1, "dailyAverage": 10, "maxAmount": 10}})
    expect(mocked_page.get_by_text("八月新資料")).to_be_visible()


# 驗證 real-stack smoke 只有在明確啟用時才檢查 browser 到 backend 的連線。
@pytest.mark.real_stack
def test_real_stack_login_page(page: Page) -> None:
    with page.expect_response("**/api/auth/status") as status_response:
        page.goto("/login")

    assert status_response.value.ok
    expect(page.get_by_text("MyExpenses")).to_be_visible()
    expect(page.locator('input[type="email"]')).to_be_visible()
