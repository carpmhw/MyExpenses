from __future__ import annotations

import json
import re
from typing import Any

from playwright.sync_api import Page, Route, expect


def _route_json(route: Route, payload: Any, status: int = 200) -> None:
    """回傳 JSON API 內容供瀏覽器測試使用。"""
    route.fulfill(
        status=status,
        content_type="application/json",
        body=json.dumps(payload),
    )


def _bank_response(*, available: bool, stale: bool) -> dict[str, Any]:
    """建立銀行帳戶頁測試使用的混合幣別 response。"""
    return {
        "items": [
            {
                "id": 1,
                "bankName": "台灣銀行",
                "accountNumber": "12345",
                "balance": 100000,
                "accountType": "活期",
                "currencyCode": "TWD",
                "createdAt": "2026-08-01T00:00:00Z",
                "updatedAt": "2026-08-01T00:00:00Z",
                "convertedBalance": 100000 if available else None,
            },
            {
                "id": 2,
                "bankName": "美元銀行",
                "accountNumber": "23456",
                "balance": 310,
                "accountType": "活期",
                "currencyCode": "USD",
                "createdAt": "2026-08-02T00:00:00Z",
                "updatedAt": "2026-08-02T00:00:00Z",
                "convertedBalance": 10000 if available else None,
            },
        ],
        "total": 2,
        "page": 1,
        "pageSize": 15,
        "baseCurrency": "TWD",
        "totalBalanceInBaseCurrency": 110000 if available else None,
        "exchangeRateUpdatedAt": "2026-08-01T00:00:00Z" if available else None,
        "exchangeRateIsStale": stale,
        "conversionAvailable": available,
    }


def _install_bank_routes(page: Page, response: dict[str, Any], posted: list[dict[str, Any]]) -> None:
    """安裝銀行列表、建立帳戶與 snapshot 的 deterministic API route。"""
    def bank_accounts(route: Route) -> None:
        """回應銀行帳戶列表與建立請求，並記錄新增 payload。"""
        if route.request.method == "POST":
            posted.append(json.loads(route.request.post_data or "{}"))
            _route_json(route, {
                "id": 3,
                "bankName": "新增銀行",
                "accountNumber": "34567",
                "balance": 100,
                "accountType": "活期",
                "currencyCode": "USD",
                "createdAt": "2026-08-03T00:00:00Z",
                "updatedAt": "2026-08-03T00:00:00Z",
            }, status=201)
            return
        _route_json(route, response)

    page.route("**/api/bank-accounts**", bank_accounts)
    page.route("**/api/snapshots", lambda route: _route_json(route, {"name": "混合幣別快照"}, status=201))


def test_bank_account_mixed_currency_form_and_stale_total(mocked_page: Page) -> None:
    """驗證新增外幣帳戶、混合幣別總額與 stale 狀態的瀏覽器流程。"""
    posted: list[dict[str, Any]] = []
    _install_bank_routes(mocked_page, _bank_response(available=True, stale=True), posted)
    mocked_page.goto("/bank-accounts")

    expect(mocked_page.get_by_text("USD", exact=True).first).to_be_visible()
    expect(mocked_page.get_by_text("110,000").first).to_be_visible()
    expect(mocked_page.get_by_text("使用過期匯率")).to_be_visible()

    mocked_page.get_by_role("button", name="+ 新增帳戶").click()
    dialog = mocked_page.get_by_role("dialog")
    expect(dialog.locator("select")).to_have_value("TWD")
    dialog.locator("input").nth(0).fill("新增銀行")
    dialog.locator("input").nth(1).fill("34567")
    dialog.locator("input").nth(2).fill("310")
    dialog.locator("input").nth(3).fill("活期")
    dialog.locator("select").select_option("USD")
    dialog.get_by_role("button", name="儲存").click()

    expect(mocked_page.get_by_text("帳戶已建立")).to_be_visible()
    assert len(posted) == 1
    assert posted[0]["currencyCode"] == "USD"

    mocked_page.get_by_role("button", name="📷 拍照").click()
    expect(mocked_page.get_by_text("快照已建立: 混合幣別快照")).to_be_visible()


def test_bank_account_unavailable_total_keeps_original_amount(mocked_page: Page) -> None:
    """驗證匯率不可用時保留原幣餘額且不顯示直接相加總額。"""
    _install_bank_routes(mocked_page, _bank_response(available=False, stale=False), [])
    mocked_page.goto("/bank-accounts")

    expect(mocked_page.get_by_text("匯率不可用，保留原幣資料")).to_be_visible()
    expect(mocked_page.get_by_text("不可用").first).to_be_visible()
    expect(mocked_page.get_by_text("310").first).to_be_visible()
    expect(mocked_page.get_by_text("110,000")).to_have_count(0)
    assert mocked_page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")


def test_dashboard_displays_twd_summary_and_original_withdrawal_currency(mocked_page: Page) -> None:
    """驗證 Dashboard summary 使用 TWD，近期提款維持關聯帳戶原幣。"""
    mocked_page.set_viewport_size({"width": 390, "height": 844})
    mocked_page.route("**/api/reports/dashboard-summary**", lambda route: _route_json(route, {
        "totalWithdrawals": 10000,
        "withdrawalCount": 1,
        "totalExpenses": 0,
        "expenseCount": 0,
        "disposableBalance": 10000,
        "installmentDueAmount": 0,
        "installmentDuePaymentCount": 0,
        "activeInstallmentCount": 0,
        "previousDisposableBalance": 0,
        "baseCurrency": "TWD",
        "exchangeRateUpdatedAt": None,
        "exchangeRateIsStale": False,
        "conversionAvailable": True,
    }))
    mocked_page.route("**/api/withdrawals**", lambda route: _route_json(route, {
        "items": [{
            "id": 1,
            "amount": 310,
            "date": "2026-08-01",
            "description": "美元提款",
            "bankAccountId": 1,
            "bankAccount": {
                "id": 1,
                "bankName": "美元銀行",
                "accountNumber": "12345",
                "accountType": "活期",
                "balance": 0,
                "currencyCode": "USD",
                "createdAt": "2026-08-01T00:00:00Z",
                "updatedAt": "2026-08-01T00:00:00Z",
            },
        }],
        "total": 1,
        "page": 1,
        "pageSize": 50,
        "summary": {
            "totalAmount": 10000,
            "count": 1,
            "averageAmount": 10000,
            "maxAmount": 10000,
            "baseCurrency": "TWD",
            "exchangeRateUpdatedAt": None,
            "exchangeRateIsStale": False,
            "conversionAvailable": True,
            "totalAmountInBaseCurrency": 10000,
        },
    }))
    mocked_page.goto("/dashboard")

    expect(mocked_page.get_by_text(re.compile(r"10,000")).first).to_be_visible()
    expect(mocked_page.get_by_text(re.compile(r"US\$310")).first).to_be_visible()
    assert mocked_page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")
    hero_summary = mocked_page.get_by_test_id("dashboard-hero-summary").bounding_box()
    hero_details = mocked_page.get_by_test_id("dashboard-hero-details").bounding_box()
    activity_cards = mocked_page.get_by_test_id("dashboard-activity-card")
    assert hero_summary is not None and hero_details is not None
    assert hero_details["y"] >= hero_summary["y"] + hero_summary["height"]
    assert activity_cards.count() == 3
    card_boxes = [activity_cards.nth(index).bounding_box() for index in range(3)]
    assert all(box is not None for box in card_boxes)
    assert card_boxes[1]["y"] >= card_boxes[0]["y"] + card_boxes[0]["height"]
    assert card_boxes[2]["y"] >= card_boxes[1]["y"] + card_boxes[1]["height"]


def test_withdrawal_initial_failure_does_not_display_zero_summary(mocked_page: Page) -> None:
    """驗證提款初始失敗顯示重試而非零值成功狀態。"""
    mocked_page.route("**/api/withdrawals**", lambda route: _route_json(route, {
        "title": "Service Unavailable",
        "detail": "匯率服務目前無法使用",
        "status": 503,
    }, status=503))
    mocked_page.goto("/withdrawals")

    expect(mocked_page.get_by_text("匯率服務目前無法使用")).to_be_visible()
    expect(mocked_page.get_by_role("button", name="重試")).to_be_visible()
    expect(mocked_page.get_by_text("總提款金額", exact=True)).to_have_count(0)
