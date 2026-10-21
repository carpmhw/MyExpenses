from __future__ import annotations

import re
from typing import Any
from urllib.parse import urlparse

import pytest
from playwright.sync_api import Browser, Page, Route, expect

from .conftest import install_default_api_routes, route_json


def _state() -> dict[str, Any]:
    """建立可由不同瀏覽器 context 共用的伺服器狀態替身，不依賴 localStorage。"""
    common = {"createdAt": "2026-08-01T00:00:00Z", "updatedAt": "2026-08-01T00:00:00Z"}
    return {
        "defaults": {"defaultBankAccountId": None, "defaultCreditCardId": None},
        "bank-accounts": [{**common, "id": i, "bankName": f"銀行{i}", "accountNumber": "12345", "currencyCode": "TWD", "balance": 100, "accountType": "活期", "convertedBalance": 100} for i in (1, 2)],
        "credit-cards": [{**common, "id": i, "bankName": f"銀行{i}", "lastFourDigits": "1234", "cardNetwork": "VISA", "statementDay": 1, "dueDay": 15, "creditLimit": 1000, "notes": None} for i in (1, 2)],
        "withdrawals": [],
        "installments": [],
    }


def _key(resource: str) -> str:
    """取得資源類別對應的固定預設欄位。"""
    return "defaultBankAccountId" if resource == "bank-accounts" else "defaultCreditCardId"


def _install_state(page: Page, state: dict[str, Any]) -> None:
    """安裝有狀態的資源、偏好與金融 API，其他請求沿用共用認證 fixture。"""
    def handle(route: Route) -> None:
        """依正式 API 路由保存目標狀態，讓 UI 誤寫偏好可由後續行為觀察。"""
        request = route.request
        path = urlparse(request.url).path
        if path == "/api/settings/transaction-defaults":
            route_json(route, state["defaults"])
            return
        parts = path.removeprefix("/api/").split("/")
        resource = parts[0]
        if resource in ("bank-accounts", "credit-cards"):
            if len(parts) == 3 and parts[2] == "default" and request.method == "PUT":
                identifier = int(parts[1])
                if not any(item["id"] == identifier for item in state[resource]):
                    route_json(route, {}, 404)
                    return
                state["defaults"][_key(resource)] = identifier
                route_json(route, state["defaults"])
                return
            if len(parts) == 2:
                identifier = int(parts[1])
                if request.method == "DELETE":
                    state[resource] = [item for item in state[resource] if item["id"] != identifier]
                    if state["defaults"][_key(resource)] == identifier:
                        state["defaults"][_key(resource)] = None
                    route.fulfill(status=204)
                    return
                item = next((item for item in state[resource] if item["id"] == identifier), None)
                route_json(route, item or {}, 200 if item else 404)
                return
            response = {"items": state[resource], "total": len(state[resource]), "page": 1, "pageSize": 100}
            if resource == "bank-accounts":
                response.update({"baseCurrency": "TWD", "totalBalanceInBaseCurrency": 100 * len(state[resource]), "exchangeRateUpdatedAt": None, "exchangeRateIsStale": False, "conversionAvailable": True})
            route_json(route, response)
            return
        if resource in ("withdrawals", "installments") and len(parts) == 1:
            if request.method == "POST":
                payload = request.post_data_json
                item = {**payload, "id": 7, "createdAt": "2026-08-01T00:00:00Z"}
                if resource == "withdrawals":
                    item["bankAccount"] = next(account for account in state["bank-accounts"] if account["id"] == payload["bankAccountId"])
                else:
                    item.update({"card": next(card for card in state["credit-cards"] if card["id"] == payload["cardId"]), "transaction": None, "remainingPeriods": payload["periods"], "perPeriod": payload["totalAmount"] / payload["periods"], "status": "Active", "payments": []})
                state[resource].append(item)
                route_json(route, item, 201)
                return
            summary = {"totalCount": len(state[resource]), "activeCount": len(state[resource]), "dueAmount": 100, "duePaymentCount": len(state[resource])}
            if resource == "withdrawals":
                summary = {"count": len(state[resource]), "totalAmount": 100, "averageAmount": 100, "maxAmount": 100, "baseCurrency": "TWD", "totalAmountInBaseCurrency": 100, "exchangeRateUpdatedAt": None, "exchangeRateIsStale": False, "conversionAvailable": True}
            route_json(route, {"items": state[resource], "total": len(state[resource]), "page": 1, "pageSize": 15, "summary": summary})
            return
        route.fallback()

    page.route("**/api/**", handle)


def _entry_path(resource: str) -> str:
    """回傳對應新增表單的現有頁面路徑。"""
    return "/withdrawals" if resource == "bank-accounts" else "/installments"


def _open_new(page: Page, resource: str) -> None:
    """透過實際頁面按鈕開啟表單，等待 modal 可見。"""
    page.get_by_role("button", name="+ 新增提款" if resource == "bank-accounts" else "+ 新增交易", exact=True).click()
    expect(page.get_by_role("dialog")).to_be_visible()


@pytest.mark.parametrize("resource", ["bank-accounts", "credit-cards"])
def test_management_default_prefill_manual_override_and_deletion(mocked_page: Page, resource: str) -> None:
    """驗證管理設定、當次改選、歷史編輯與刪除後留空的完整真實瀏覽器操作。"""
    state = _state()
    _install_state(mocked_page, state)
    mocked_page.goto(f"/{resource}")
    row = mocked_page.locator("tbody tr").nth(1)
    action = row.get_by_role("button", name=re.compile("設為預設"))
    expect(action).to_have_attribute("title", "設為預設")
    expect(action.locator("svg")).to_have_attribute("fill", "none")
    action.click()
    expect(row.get_by_test_id("default-indicator")).to_have_attribute("title", "目前預設")
    expect(row.get_by_role("img", name=re.compile("目前預設"))).to_be_visible()
    expect(row.get_by_test_id("default-indicator").locator("svg")).to_have_attribute("fill", "currentColor")
    mocked_page.goto(_entry_path(resource))
    _open_new(mocked_page, resource)
    dialog = mocked_page.get_by_role("dialog")
    expect(dialog.locator("select")).to_have_value("2")
    dialog.locator("select").select_option("1")
    dialog.locator('input[type="number"]').first.fill("100")
    if resource == "credit-cards":
        dialog.get_by_label("交易描述", exact=True).fill("測試消費")
        dialog.get_by_label("期數", exact=True).fill("1")
    dialog.get_by_role("button", name="儲存", exact=True).click()
    expect(dialog).to_have_count(0)
    assert state["defaults"][_key(resource)] == 2
    _open_new(mocked_page, resource)
    expect(dialog.locator("select")).to_have_value("2")
    dialog.get_by_role("button", name="取消", exact=True).click()
    if resource == "bank-accounts":
        mocked_page.locator("tbody tr").first.locator("button").first.click()
    else:
        mocked_page.get_by_role("button", name="編輯", exact=True).click()
    expect(dialog.locator("select")).to_have_value("1")
    dialog.get_by_role("button", name="取消", exact=True).click()

    mocked_page.goto(f"/{resource}")
    mocked_page.locator("tbody tr").nth(1).locator("button").nth(1).click()
    mocked_page.get_by_role("button", name="確認刪除", exact=True).click()
    expect(mocked_page.get_by_test_id("default-indicator")).to_have_count(0)
    mocked_page.goto(_entry_path(resource))
    _open_new(mocked_page, resource)
    expect(dialog.locator("select")).to_have_value("")
    dialog.locator('input[type="number"]').first.fill("100")
    if resource == "credit-cards":
        dialog.get_by_label("交易描述", exact=True).fill("缺少卡片")
    dialog.get_by_role("button", name="儲存", exact=True).click()
    expect(dialog).to_be_visible()
    expect(dialog.get_by_text("請選擇銀行帳戶" if resource == "bank-accounts" else "請選擇信用卡", exact=True).first).to_be_visible()
    assert len(state["withdrawals" if resource == "bank-accounts" else "installments"]) == 1


@pytest.mark.parametrize("resource", ["bank-accounts", "credit-cards"])
def test_manual_selection_can_submit_with_enter_when_default_query_fails(mocked_page: Page, resource: str) -> None:
    """驗證偏好讀取失敗時，Enter 提交明確選擇，不會被重試按鈕攔截。"""
    state = _state()
    _install_state(mocked_page, state)

    def fail_defaults(route: Route) -> None:
        """保持設定查詢失敗，單獨驗證金融表單仍可手動送出。"""
        route_json(route, {"title": "暫時無法讀取預設"}, 503)

    mocked_page.route("**/api/settings/transaction-defaults", fail_defaults)
    mocked_page.goto(_entry_path(resource))
    _open_new(mocked_page, resource)
    dialog = mocked_page.get_by_role("dialog")
    expect(dialog.get_by_role("button", name="重試預設設定", exact=True)).to_be_visible()
    dialog.locator("select").select_option("1")
    amount = dialog.locator('input[type="number"]').first
    amount.fill("100")
    if resource == "credit-cards":
        dialog.get_by_label("交易描述", exact=True).fill("鍵盤送出")
        dialog.get_by_label("期數", exact=True).fill("1")
    amount.press("Enter")
    expect(dialog).to_have_count(0)
    assert len(state["withdrawals" if resource == "bank-accounts" else "installments"]) == 1
    assert state["defaults"][_key(resource)] is None


@pytest.mark.parametrize("resource", ["bank-accounts", "credit-cards"])
def test_defaults_are_shared_across_browser_contexts(mocked_page: Page, browser: Browser, base_url: str, resource: str) -> None:
    """驗證隔離 localStorage 的另一個 context 在每次新增時讀取同一份伺服器設定。"""
    state = _state()
    _install_state(mocked_page, state)
    with browser.new_context(base_url=base_url) as context:
        second = context.new_page()
        install_default_api_routes(second)
        _install_state(second, state)
        second.goto(_entry_path(resource))
        _open_new(second, resource)
        expect(second.get_by_role("dialog").locator("select")).to_have_value("")
        second.get_by_role("dialog").get_by_role("button", name="取消", exact=True).click()
        mocked_page.goto(f"/{resource}")
        row = mocked_page.locator("tbody tr").nth(1)
        row.get_by_role("button", name=re.compile("設為預設")).click()
        expect(row.get_by_test_id("default-indicator")).to_be_visible()
        _open_new(second, resource)
        expect(second.get_by_role("dialog").locator("select")).to_have_value("2")
