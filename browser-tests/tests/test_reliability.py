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
    route.fallback()


# 驗證 Dashboard 的獨立區塊在分期 API 失敗時仍保留成功資料。
def test_dashboard_partial_failure(mocked_page: Page) -> None:
    def installments_failure(route: Route) -> None:
        route_json(route, {"title": "Credit card transactions unavailable"}, status=503)

    mocked_page.route("**/api/installments**", installments_failure)
    mocked_page.goto("/dashboard")

    expect(mocked_page.get_by_text("提款合計")).to_be_visible()
    expect(mocked_page.get_by_role("alert")).to_contain_text("Credit card transactions unavailable")


# 驗證活動卡片在指定視窗、長摘要與大額金額下維持版面與內容可用性。
def test_dashboard_activity_layout_and_large_amounts(mocked_page: Page) -> None:
    summary = {
        "totalWithdrawals": 500000000,
        "withdrawalCount": 1,
        "totalExpenses": 500000000,
        "expenseCount": 1,
        "disposableBalance": 0,
        "installmentDueAmount": 123456789,
        "installmentDuePaymentCount": 1,
        "activeInstallmentCount": 1,
        "previousDisposableBalance": 0,
        "baseCurrency": "TWD",
        "exchangeRateUpdatedAt": None,
        "exchangeRateIsStale": False,
        "conversionAvailable": True,
    }
    withdrawal = {
        "id": 1,
        "amount": 500000000,
        "date": "2026-08-01",
        "description": "薪資",
        "bankAccountId": 1,
        "bankAccount": {
            "id": 1,
            "bankName": "測試銀行",
            "accountNumber": "1234",
            "accountType": "活期",
            "balance": 500000000,
            "currencyCode": "TWD",
            "createdAt": "2026-08-01T00:00:00Z",
            "updatedAt": "2026-08-01T00:00:00Z",
        },
    }
    expense = {
        "id": 2,
        "type": "Expense",
        "amount": 500000000,
        "date": "2026-08-02",
        "description": "非常長的支出摘要用於驗證描述可以截斷但金額必須完整顯示且不會推擠相鄰欄位這是一段額外文字",
        "notes": None,
        "categoryId": 1,
        "paymentMethodId": None,
        "createdAt": "2026-08-02T00:00:00Z",
        "category": {"id": 1, "name": "餐飲", "type": "Expense", "icon": "", "color": "", "sortOrder": 1},
        "paymentMethod": None,
    }
    installment = {
        **INSTALLMENT,
        "totalAmount": 123456789,
        "periods": 1,
        "perPeriod": 123456789,
        "remainingPeriods": 1,
        "description": "非常長的信用卡交易摘要",
    }

    mocked_page.route("**/api/reports/dashboard-summary**", lambda route: route_json(route, summary))
    mocked_page.route("**/api/withdrawals**", lambda route: route_json(route, {"items": [withdrawal], "total": 1, "page": 1, "pageSize": 50}))
    mocked_page.route("**/api/transactions**", lambda route: route_json(route, {"items": [expense], "total": 1, "page": 1, "pageSize": 50}))
    mocked_page.route("**/api/installments**", lambda route: route_json(route, {"items": [installment], "total": 1, "page": 1, "pageSize": 50}))

    credit_modes: set[bool] = set()
    for theme, (width, height) in ((theme, viewport) for theme in ("light", "dark") for viewport in ((1920, 1080), (1704, 900), (1705, 900), (1605, 900), (1606, 900), (1536, 864), (1440, 900), (1366, 768), (1280, 800), (1279, 800), (1024, 768), (768, 800), (767, 800), (640, 800), (390, 844))):
        mocked_page.goto("/login")
        mocked_page.evaluate("theme => localStorage.setItem('darkMode', theme === 'dark' ? 'true' : 'false')", theme)
        mocked_page.set_viewport_size({"width": width, "height": height})
        mocked_page.goto("/dashboard")
        expect(mocked_page.get_by_text("提款合計", exact=True)).to_be_visible()
        expect(mocked_page.get_by_text("1 期（一次付清）", exact=True)).to_be_visible()
        activity_cards = mocked_page.get_by_test_id("dashboard-activity-card")
        expect(activity_cards.nth(0).locator('p[title="$500,000,000.00"]')).to_be_visible()
        expect(activity_cards.nth(1).locator('p[title="$500,000,000.00"]')).to_be_visible()
        expect(activity_cards.nth(2).locator('p[title="$123,456,789.00"]')).to_be_visible()
        expect(activity_cards.nth(0).locator('p[title="$500,000,000.00"]')).to_have_text("$500,000,000.00")
        expect(activity_cards.nth(1).locator('p[title="$500,000,000.00"]')).to_have_text("$500,000,000.00")
        expect(activity_cards.nth(2).locator('p[title="$123,456,789.00"]')).to_have_text("$123,456,789.00")
        withdrawal_row = activity_cards.nth(0).locator("div.cursor-pointer")
        expect(withdrawal_row.get_by_text("$500,000,000.00", exact=True)).to_be_visible()
        expense_row = activity_cards.nth(1).locator("div.cursor-pointer")
        expect(expense_row.get_by_text("NT$ 500,000,000", exact=True)).to_be_visible()
        credit_row = activity_cards.nth(2).locator("div.cursor-pointer")
        credit_row_amounts = credit_row.get_by_text("NT$ 123,456,789", exact=True)
        expect(credit_row_amounts).to_have_count(2)
        expect(credit_row_amounts.nth(0)).to_be_visible()
        expect(credit_row_amounts.nth(1)).to_be_visible()
        credit_card = activity_cards.nth(2)
        credit_card_box = credit_card.bounding_box()
        assert credit_card_box is not None
        credit_table = credit_card.get_by_test_id("dashboard-credit-table")
        credit_table_box = credit_table.bounding_box()
        assert credit_table_box is not None
        if credit_table_box["width"] <= 620:
            expect(credit_card.locator(".dashboard-credit-cell-label").first).to_be_visible()
        else:
            expect(credit_card.get_by_test_id("dashboard-credit-header").get_by_text("項目 / 摘要", exact=True)).to_be_visible()
        metrics = mocked_page.evaluate("""() => {
            // 讀取瀏覽器實際盒模型，讓視覺驗收不只依賴 DOM 文字存在。
            const box = node => {
                if (!node) return null;
                const rect = node.getBoundingClientRect();
                return {
                    left: rect.left,
                    right: rect.right,
                    top: rect.top,
                    bottom: rect.bottom,
                    width: rect.width,
                    height: rect.height,
                    scrollWidth: node.scrollWidth,
                    clientWidth: node.clientWidth,
                };
            };
            // 讀取目標文字的 computed typography，避免舊的極小字級回歸。
            const typography = node => {
                if (!node) return null;
                const computed = getComputedStyle(node);
                return {
                    fontSize: computed.fontSize,
                    fontWeight: computed.fontWeight,
                    lineHeight: computed.lineHeight,
                };
            };
            // 解析瀏覽器實際回傳的十六進位或 rgb 色彩值。
            const parseColor = value => {
                const hex = value.trim().match(/^#([0-9a-f]{6})$/i);
                if (hex) {
                    return {
                        red: Number.parseInt(hex[1].slice(0, 2), 16),
                        green: Number.parseInt(hex[1].slice(2, 4), 16),
                        blue: Number.parseInt(hex[1].slice(4, 6), 16),
                        alpha: 1,
                    };
                }
                const rgb = value.match(/rgba?\\(([^)]+)\\)/i);
                if (!rgb) return null;
                const channels = rgb[1].replaceAll(',', ' ').replaceAll('/', ' ').trim().split(/\\s+/);
                return {
                    red: Number.parseFloat(channels[0]),
                    green: Number.parseFloat(channels[1]),
                    blue: Number.parseFloat(channels[2]),
                    alpha: channels[3] ? Number.parseFloat(channels[3]) : 1,
                };
            };
            // 將半透明 gradient stop 合成到實際卡片背景上。
            const compositeColor = (foreground, background) => ({
                red: foreground.red * foreground.alpha + background.red * (1 - foreground.alpha),
                green: foreground.green * foreground.alpha + background.green * (1 - foreground.alpha),
                blue: foreground.blue * foreground.alpha + background.blue * (1 - foreground.alpha),
                alpha: 1,
            });
            // 找到文字所在卡片的第一個不透明背景色。
            const findSolidBackground = node => {
                let current = node;
                while (current) {
                    const color = parseColor(getComputedStyle(current).backgroundColor);
                    if (color && color.alpha > 0) return color;
                    current = current.parentElement;
                }
                return parseColor(getComputedStyle(document.documentElement).getPropertyValue('--color-bg-app'));
            };
            // 使用相對亮度計算 WCAG 對比值。
            const contrastRatio = (foreground, background) => {
                const luminance = color => {
                    const channels = [color.red, color.green, color.blue].map(channel => channel / 255).map(channel => channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
                    return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
                };
                const foregroundLuminance = luminance(foreground);
                const backgroundLuminance = luminance(background);
                return (Math.max(foregroundLuminance, backgroundLuminance) + 0.05) / (Math.min(foregroundLuminance, backgroundLuminance) + 0.05);
            };
            // 以文字的 solid ancestor 或 Header gradient 各 stop 計算最小實際對比。
            const contrastFor = node => {
                if (!node) return null;
                const foreground = parseColor(getComputedStyle(node).color);
                const header = node.closest('[data-testid="dashboard-activity-header"]');
                if (header) {
                    const base = findSolidBackground(header.parentElement);
                    const stops = [...getComputedStyle(header).backgroundImage.matchAll(/rgba?\\([^)]*\\)/gi)].map(match => parseColor(match[0])).filter(Boolean);
                    const backgrounds = stops.length ? stops.map(stop => compositeColor(stop, base)) : [base];
                    return Math.min(...backgrounds.map(background => contrastRatio(foreground, background)));
                }
                return contrastRatio(foreground, findSolidBackground(node));
            };
            const cards = [...document.querySelectorAll('[data-testid="dashboard-activity-card"]')];
            const activityGrid = cards[0]?.parentElement;
            const headers = cards.map(card => card.querySelector('[data-testid="dashboard-activity-header"]') ?? card.querySelector('[class*="from-color-"]'));
            const headerAmounts = headers.map(header => header?.querySelector('[data-testid="dashboard-header-amount"]') ?? header?.querySelector('p[title]'));
            const credit = cards[2];
            const creditTable = credit?.querySelector('[data-testid="dashboard-credit-table"]') ?? credit;
            const creditHeader = creditTable?.querySelector('[data-testid="dashboard-credit-header"]') ?? [...creditTable?.querySelectorAll('div') ?? []].find(element => element.classList.contains('uppercase'));
            const creditRow = creditTable?.querySelector('[data-testid="dashboard-credit-row"]') ?? credit?.querySelector('div.cursor-pointer');
            const creditTitle = [...headers[2]?.querySelectorAll('p') ?? []].find(p => p.textContent?.trim() === '信用卡交易');
            const creditSubtitle = [...headers[2]?.querySelectorAll('p') ?? []].find(p => p.textContent?.trim() === 'Credit Card Transactions');
            const creditSummary = creditRow?.querySelector('[data-credit-cell="description"]') ?? creditRow?.querySelector('div.flex-1');
            const creditSummaryText = creditSummary?.querySelector('p');
            const periodLabel = creditRow?.querySelector('[data-credit-value="period"]') ?? creditRow?.querySelector('span.bg-color-credit-bg');
            const periodCell = periodLabel?.parentElement;
            const creditDate = creditRow?.querySelector('[data-credit-role="date"]');
            const creditPaid = creditRow?.querySelector('[data-credit-role="paid"]');
            const creditTableHeader = creditHeader;
            const creditHeaderSummary = [...creditTableHeader?.children ?? []].find(child => child.textContent?.trim() === '項目 / 摘要');
            const creditHeaderNonSummaryCells = [...creditTableHeader?.children ?? []].filter(child => child.textContent?.trim() !== '項目 / 摘要').map(box);
            const creditNonSummaryCells = [...creditRow?.children ?? []].filter(child => child !== creditSummary).map(box);
            const withdrawalRow = cards[0]?.querySelector('div.cursor-pointer');
            const withdrawalAmount = cards[0]?.querySelector('[data-testid="dashboard-withdrawal-amount"]') ?? [...withdrawalRow?.querySelectorAll('span') ?? []].find(span => span.textContent?.trim() === '$500,000,000.00');
            const withdrawalRowCells = [...withdrawalRow?.children ?? []].map(box);
            const expenseRow = cards[1]?.querySelector('div.cursor-pointer');
            const expenseDescription = expenseRow?.querySelector('p.truncate');
            const expenseCategory = [...expenseRow?.querySelectorAll('p') ?? []].find(p => p.textContent?.trim() === '餐飲');
            const expenseAmount = cards[1]?.querySelector('[data-testid="dashboard-expense-amount"]') ?? [...expenseRow?.querySelectorAll('span') ?? []].find(span => span.textContent?.trim() === 'NT$ 500,000,000');
            const expenseRowCells = [...expenseRow?.children ?? []].map(box);
            const headerChildren = headers.map(header => [...header?.children ?? []].map(child => ({
                ...box(child),
                contentRight: child.getBoundingClientRect().left + child.scrollWidth,
            })));
            const overlaps = (first, second) => {
                const firstRight = Math.max(first.right, first.contentRight ?? first.right);
                const secondRight = Math.max(second.right, second.contentRight ?? second.right);
                return firstRight > second.left && secondRight > first.left && first.bottom > second.top && second.bottom > first.top;
            };
            const headerOverlaps = headerChildren.map(children => children.some((child, index) => children.slice(index + 1).some(other => overlaps(child, other))));
            const creditCells = [...creditRow?.children ?? []].map(box);
            const creditCellOverlaps = creditCells.some((cell, index) => creditCells.slice(index + 1).some(other => overlaps(cell, other)));
            const withdrawalRowOverlaps = withdrawalRowCells.some((cell, index) => withdrawalRowCells.slice(index + 1).some(other => overlaps(cell, other)));
            const expenseRowOverlaps = expenseRowCells.some((cell, index) => expenseRowCells.slice(index + 1).some(other => overlaps(cell, other)));
            const creditCellLabels = [...creditRow?.querySelectorAll('.dashboard-credit-cell-label') ?? []].map(label => ({
                role: label.parentElement?.getAttribute('data-credit-cell'),
                text: label.textContent?.trim(),
                display: getComputedStyle(label).display,
            }));
            const creditDataOrder = [...creditRow?.querySelectorAll('[data-credit-role]') ?? []].map(cell => cell.getAttribute('data-credit-role'));
            return {
                cards: cards.map(box),
                headers: headers.map(box),
                headerAmounts: headerAmounts.map(box),
                headerTypography: headerAmounts.map(typography),
                    withdrawalAmountTypography: typography(withdrawalAmount),
                    expenseAmountTypography: typography(expenseAmount),
                    expenseCategoryTypography: typography(expenseCategory),
                    expenseDescriptionTypography: typography(expenseDescription),
                    creditAmountTypography: {
                        total: typography(creditRow?.querySelector('[data-credit-role="total"]') ?? creditRow?.children[2]),
                        current: typography(creditRow?.querySelector('[data-credit-role="current"]') ?? creditRow?.children[5]),
                    },
                    creditDateTypography: typography(creditDate),
                    creditMetaTypography: {
                        label: typography(creditRow?.querySelector('.dashboard-credit-cell-label')),
                        period: typography(periodLabel),
                        paid: typography(creditPaid),
                    },
                    creditSummaryTypography: typography(creditSummaryText),
                amountContrast: {
                    headers: headerAmounts.map(contrastFor),
                    withdrawal: contrastFor(withdrawalAmount),
                    expense: contrastFor(expenseAmount),
                    creditTotal: contrastFor(creditRow?.querySelector('[data-credit-role="total"]')),
                    creditCurrent: contrastFor(creditRow?.querySelector('[data-credit-role="current"]')),
                },
                headerOverlaps,
                creditCellOverlaps,
                creditTitle: box(creditTitle),
                creditSubtitle: box(creditSubtitle),
                creditSummary: box(creditSummary),
                creditSummaryText: box(creditSummaryText),
                creditSummaryTextClass: creditSummaryText?.className ?? '',
                creditHeaderSummary: box(creditHeaderSummary),
                creditHeaderNonSummaryCells,
                creditNonSummaryCells,
                creditCellLabels,
                creditDataOrder,
                    creditHeaderDisplay: creditTableHeader ? getComputedStyle(creditTableHeader).display : '',
                    creditRowDisplay: creditRow ? getComputedStyle(creditRow).display : '',
                    creditNarrow: (creditTable?.getBoundingClientRect().width ?? 0) <= 620,
                    creditTableWidth: creditTable?.getBoundingClientRect().width ?? 0,
                    darkMode: document.documentElement.classList.contains('dark'),
                    activityGridWidth: activityGrid?.getBoundingClientRect().width ?? 0,
                withdrawalAmount: box(withdrawalAmount),
                withdrawalRowOverlaps,
                periodLabel: box(periodLabel),
                periodCell: box(periodCell),
                expenseDescription: box(expenseDescription),
                expenseDescriptionClass: expenseDescription?.className ?? '',
                expenseAmount: box(expenseAmount),
                expenseRowOverlaps,
                overflow: {
                    document: document.documentElement.scrollWidth,
                    body: document.body.scrollWidth,
                    container: (document.querySelector('main') ?? document.querySelector('div.flex-1.overflow-y-auto'))?.scrollWidth ?? 0,
                    containerClientWidth: (document.querySelector('main') ?? document.querySelector('div.flex-1.overflow-y-auto'))?.clientWidth ?? 0,
                },
            };
            }""")
        assert len(metrics["cards"]) == 3
        assert metrics["darkMode"] == (theme == "dark"), metrics
        credit_modes.add(metrics["creditNarrow"])
        assert metrics["overflow"]["document"] <= width
        assert metrics["overflow"]["body"] <= width
        assert metrics["overflow"]["containerClientWidth"] > 0
        assert metrics["overflow"]["container"] <= metrics["overflow"]["containerClientWidth"]
        assert all(card["scrollWidth"] <= card["clientWidth"] for card in metrics["cards"])
        assert all(header["height"] >= 104 for header in metrics["headers"])
        assert all(amount["scrollWidth"] <= amount["clientWidth"] for amount in metrics["headerAmounts"]), metrics
        assert all(float(amount["fontSize"].removesuffix("px")) == 18 for amount in metrics["headerTypography"]), metrics
        assert all(int(amount["fontWeight"]) == 700 for amount in metrics["headerTypography"]), metrics
        assert all(float(amount["lineHeight"].removesuffix("px")) == 22.5 for amount in metrics["headerTypography"]), metrics
        assert float(metrics["withdrawalAmountTypography"]["fontSize"].removesuffix("px")) == 18, metrics
        assert float(metrics["expenseAmountTypography"]["fontSize"].removesuffix("px")) == 18, metrics
        assert float(metrics["creditAmountTypography"]["total"]["fontSize"].removesuffix("px")) == 16, metrics
        assert float(metrics["creditAmountTypography"]["current"]["fontSize"].removesuffix("px")) == 16, metrics
        assert int(metrics["withdrawalAmountTypography"]["fontWeight"]) == 600, metrics
        assert int(metrics["expenseAmountTypography"]["fontWeight"]) == 600, metrics
        assert int(metrics["creditAmountTypography"]["total"]["fontWeight"]) == 600, metrics
        assert int(metrics["creditAmountTypography"]["current"]["fontWeight"]) == 600, metrics
        assert float(metrics["withdrawalAmountTypography"]["lineHeight"].removesuffix("px")) == 22.5, metrics
        assert float(metrics["expenseAmountTypography"]["lineHeight"].removesuffix("px")) == 22.5, metrics
        assert float(metrics["creditAmountTypography"]["total"]["lineHeight"].removesuffix("px")) == 20, metrics
        assert float(metrics["creditAmountTypography"]["current"]["lineHeight"].removesuffix("px")) == 20, metrics
        assert float(metrics["creditDateTypography"]["fontSize"].removesuffix("px")) == 14, metrics
        assert int(metrics["creditDateTypography"]["fontWeight"]) == 400, metrics
        assert float(metrics["creditDateTypography"]["lineHeight"].removesuffix("px")) == 20, metrics
        assert float(metrics["expenseCategoryTypography"]["fontSize"].removesuffix("px")) == 14, metrics
        assert int(metrics["expenseCategoryTypography"]["fontWeight"]) == 400, metrics
        assert float(metrics["expenseCategoryTypography"]["lineHeight"].removesuffix("px")) == 20, metrics
        assert float(metrics["expenseDescriptionTypography"]["fontSize"].removesuffix("px")) == 16, metrics
        assert int(metrics["expenseDescriptionTypography"]["fontWeight"]) == 600, metrics
        assert float(metrics["expenseDescriptionTypography"]["lineHeight"].removesuffix("px")) == 24, metrics
        assert float(metrics["creditSummaryTypography"]["fontSize"].removesuffix("px")) == 16, metrics
        assert int(metrics["creditSummaryTypography"]["fontWeight"]) == 600, metrics
        assert float(metrics["creditSummaryTypography"]["lineHeight"].removesuffix("px")) == 24, metrics
        for meta_typography in (metrics["creditMetaTypography"]["period"], metrics["creditMetaTypography"]["paid"]):
            assert float(meta_typography["fontSize"].removesuffix("px")) == 13, metrics
            assert int(meta_typography["fontWeight"]) == 500, metrics
            assert float(meta_typography["lineHeight"].removesuffix("px")) == 18.2, metrics
        if metrics["creditNarrow"]:
            assert float(metrics["creditMetaTypography"]["label"]["fontSize"].removesuffix("px")) == 13, metrics
            assert int(metrics["creditMetaTypography"]["label"]["fontWeight"]) == 500, metrics
            assert float(metrics["creditMetaTypography"]["label"]["lineHeight"].removesuffix("px")) == 18.2, metrics
        assert all(contrast >= 4.5 for contrast in metrics["amountContrast"]["headers"]), metrics["amountContrast"]
        assert metrics["amountContrast"]["withdrawal"] >= 4.5, metrics["amountContrast"]
        assert metrics["amountContrast"]["expense"] >= 4.5, metrics["amountContrast"]
        assert metrics["amountContrast"]["creditTotal"] >= 4.5, metrics["amountContrast"]
        assert metrics["amountContrast"]["creditCurrent"] >= 4.5, metrics["amountContrast"]
        assert metrics["headerOverlaps"] == [False, False, False], metrics
        assert metrics["creditCellOverlaps"] is False, metrics
        assert metrics["creditTitle"]["scrollWidth"] <= metrics["creditTitle"]["clientWidth"]
        assert metrics["creditSubtitle"]["scrollWidth"] <= metrics["creditSubtitle"]["clientWidth"]
        assert metrics["creditSummary"]["width"] >= 32, metrics
        assert "truncate" in metrics["creditSummaryTextClass"]
        assert [label["role"] for label in metrics["creditCellLabels"]] == ["date", "description", "total", "period", "paid", "current"], metrics
        assert [label["text"] for label in metrics["creditCellLabels"]] == ["日期", "項目 / 摘要", "總額", "期數", "已繳", "本期"], metrics
        if metrics["creditNarrow"]:
            assert all(cell["scrollWidth"] <= cell["clientWidth"] for cell in metrics["creditNonSummaryCells"]), metrics
        else:
            assert metrics["creditHeaderSummary"]["width"] >= 48, metrics
            assert metrics["creditHeaderSummary"]["scrollWidth"] <= metrics["creditHeaderSummary"]["clientWidth"], metrics
            assert all(cell["scrollWidth"] <= cell["clientWidth"] for cell in metrics["creditHeaderNonSummaryCells"] + metrics["creditNonSummaryCells"]), metrics
        assert metrics["withdrawalAmount"]["scrollWidth"] <= metrics["withdrawalAmount"]["clientWidth"], metrics
        assert metrics["withdrawalRowOverlaps"] is False, metrics
        assert metrics["expenseAmount"]["scrollWidth"] <= metrics["expenseAmount"]["clientWidth"], metrics
        assert metrics["expenseRowOverlaps"] is False, metrics
        assert len(metrics["creditHeaderNonSummaryCells"]) == 5
        assert len(metrics["creditNonSummaryCells"]) == 5
        if not metrics["creditNarrow"]:
            assert all(abs(header_cell["left"] - row_cell["left"]) <= 1 for header_cell, row_cell in zip(metrics["creditHeaderNonSummaryCells"], metrics["creditNonSummaryCells"])), metrics
        assert metrics["creditDataOrder"] == ["date", "description", "total", "period", "paid", "current"], metrics
        if metrics["creditNarrow"]:
            assert metrics["creditHeaderDisplay"] == "none", metrics
            assert len(metrics["creditCellLabels"]) == 6, metrics
            assert all(label["display"] != "none" for label in metrics["creditCellLabels"]), metrics
        else:
            assert metrics["creditHeaderDisplay"] == "grid", metrics
            assert all(label["display"] == "none" for label in metrics["creditCellLabels"]), metrics
        assert metrics["periodLabel"]["height"] <= 26, metrics
        assert metrics["periodLabel"]["right"] <= metrics["periodCell"]["right"] + 1, metrics
        assert "truncate" in metrics["expenseDescriptionClass"]
        if width != 1279:
            assert metrics["expenseDescription"]["scrollWidth"] > metrics["expenseDescription"]["clientWidth"], metrics
        if width < 640:
            non_summary_cells = metrics["creditNonSummaryCells"]
            assert abs(non_summary_cells[0]["top"] - non_summary_cells[1]["top"]) <= 4, metrics
            assert abs(non_summary_cells[3]["top"] - non_summary_cells[4]["top"]) <= 4, metrics
            assert metrics["creditSummary"]["top"] >= max(non_summary_cells[0]["bottom"], non_summary_cells[1]["bottom"]), metrics
            assert non_summary_cells[2]["top"] >= metrics["creditSummary"]["bottom"], metrics
            assert min(non_summary_cells[3]["top"], non_summary_cells[4]["top"]) >= non_summary_cells[2]["bottom"], metrics
        elif width >= 1280:
            expected_header_height = 148 if metrics["activityGridWidth"] <= 1100 else 104
            assert all(abs(header["height"] - expected_header_height) <= 1 for header in metrics["headers"]), metrics
        if width >= 1280:
            assert len({round(card["top"], 1) for card in metrics["cards"]}) == 1
            assert abs(metrics["cards"][0]["width"] - 340) < 1
            assert abs(metrics["cards"][1]["width"] / metrics["cards"][2]["width"] - 2 / 3) < 0.01
            assert len({round(header["height"], 1) for header in metrics["headers"]}) == 1
        else:
            assert len({round(card["left"], 1) for card in metrics["cards"]}) == 1
            assert metrics["cards"][0]["top"] < metrics["cards"][1]["top"] < metrics["cards"][2]["top"]
    assert credit_modes == {True, False}


# 驗證持股結構報表的延遲載入、組合篩選、空結果、快照缺少與行動版明細可存取。
def test_stock_structure_report_filters_and_mobile_layout(mocked_page: Page) -> None:
    structure_requests: list[str] = []
    structure_response = {
        "summary": {
            "holdingCount": 1,
            "totalEstimatedBuyCost": 9025,
            "totalGrossMarketValue": 10000,
            "totalEstimatedNetSellValue": 9960,
            "totalEstimatedGainLoss": 935,
            "estimatedGainLossPercentage": 10.36,
        },
        "insights": [{
            "code": "NoReminder",
            "severity": "Info",
            "message": "目前沒有觸發已設定的持股結構提醒。",
            "affectedName": None,
            "observedPercentage": None,
            "thresholdPercentage": None,
            "affectedCount": None,
            "amount": None,
        }],
        "symbolAllocations": [{"key": "AAA", "label": "AAA", "value": 9960, "percentage": 100}],
        "instrumentTypeAllocations": [{"key": "Stock", "label": "股票", "value": 9960, "percentage": 100}],
        "brokerAllocations": [{"key": "甲券商", "label": "甲券商", "value": 9960, "percentage": 100}],
        "marketAllocations": [{"key": "Twse", "label": "上市", "value": 9960, "percentage": 100}],
        "concentration": {
            "top1Percentage": 100,
            "top3Percentage": 100,
            "top5Percentage": 100,
            "hhi": 1,
            "effectiveHoldingCount": 1,
        },
        "dataQuality": {
            "holdingCount": 1,
            "positivePriceCount": 1,
            "missingLastPriceUpdateCount": 0,
            "stalePriceCount": 0,
            "positivePriceCoverage": 1,
            "oldestLastPriceUpdateUtc": "2026-08-06T00:00:00Z",
            "latestLastPriceUpdateUtc": "2026-08-06T00:00:00Z",
            "staleAfterHours": 72,
            "generatedAtUtc": "2026-08-06T00:00:00Z",
        },
        "holdings": [{
            "id": 1,
            "name": "標的一",
            "symbol": "AAA",
            "instrumentType": "Stock",
            "shares": 100,
            "buyPrice": 90,
            "currentPrice": 100,
            "broker": "甲券商",
            "grossMarketValue": 10000,
            "buyCommission": 25,
            "sellCommission": 25,
            "securitiesTransactionTax": 15,
            "estimatedBuyCost": 9025,
            "estimatedNetSellValue": 9960,
            "estimatedGainLoss": 935,
            "allocationPercentage": 100,
        }],
        "availableBrokers": ["甲券商", "乙券商"],
        "availableInstrumentTypes": ["Stock", "StockEtf"],
        "generatedAt": "2026-08-06T00:00:00Z",
    }
    empty_structure = {
        **structure_response,
        "summary": {**structure_response["summary"], "holdingCount": 0},
        "symbolAllocations": [],
        "instrumentTypeAllocations": [],
        "brokerAllocations": [],
        "marketAllocations": [],
        "holdings": [],
    }

    def stock_structure(route: Route) -> None:
        structure_requests.append(route.request.url)
        query = parse_qs(urlparse(route.request.url).query)
        if query.get("broker") == ["乙券商"]:
            route_json(route, empty_structure)
            return
        route_json(route, structure_response)

    mocked_page.route("**/api/reports/stock-structure**", stock_structure)
    mocked_page.route("**/api/reports/stock-value-trend**", lambda route: route_json(route, []))
    mocked_page.set_viewport_size({"width": 375, "height": 800})
    mocked_page.goto("/reports")

    expect(mocked_page.get_by_role("tab", name="持股結構")).to_be_visible()
    mocked_page.get_by_role("tab", name="持股結構").click()
    expect(mocked_page.get_by_text("標的一 (AAA)")).to_be_visible()
    expect(mocked_page.locator('input[type="date"]')).to_have_count(0)
    expect(mocked_page.get_by_text("尚無全部持股價值歷史")).to_be_visible()

    mocked_page.locator('[data-testid="broker-filter"]').select_option("甲券商")
    mocked_page.locator('[data-testid="instrument-type-filter"]').select_option("Stock")
    expect(mocked_page.get_by_text("標的一 (AAA)")).to_be_visible()
    assert any("broker=%E7%94%B2%E5%88%B8%E5%95%86" in url and "instrumentType=Stock" in url for url in structure_requests)

    mocked_page.locator('[data-testid="broker-filter"]').select_option("乙券商")
    expect(mocked_page.get_by_text("沒有符合篩選的持股")).to_be_visible()
    mocked_page.get_by_test_id("clear-stock-structure-filters").click()
    expect(mocked_page.get_by_text("標的一 (AAA)")).to_be_visible()
    assert mocked_page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")


# 驗證股票市場修正、風險期間切換、覆蓋不足與行動版相關矩陣可存取。
def test_stock_market_risk_market_edit_period_and_mobile_matrix(mocked_page: Page) -> None:
    stock_payload = {
        "items": [{
            "id": 1,
            "name": "台積電",
            "symbol": "2330",
            "market": "Unknown",
            "instrumentType": "Stock",
            "shares": 100,
            "buyPrice": 500,
            "currentPrice": 600,
            "broker": "測試券商",
            "lastPriceUpdate": None,
            "hasLedger": False,
            "grossMarketValue": 60000,
            "buyCommission": 0,
            "sellCommission": 0,
            "securitiesTransactionTax": 0,
            "estimatedNetSellValue": 60000,
            "estimatedGainLoss": 10000,
        }],
        "total": 1,
        "page": 1,
        "pageSize": 15,
        "totalEstimatedNetSellValue": 60000,
        "totalEstimatedGainLoss": 10000,
    }
    saved_market: str | None = None

    def stocks(route: Route) -> None:
        nonlocal saved_market
        if route.request.method == "PUT":
            body = json.loads(route.request.post_data or "{}")
            saved_market = body.get("market")
            route_json(route, {**stock_payload["items"][0], **body})
            return
        route_json(route, stock_payload)

    mocked_page.route("**/api/stocks**", stocks)
    mocked_page.goto("/stocks")
    expect(mocked_page.get_by_text("待辨識")).to_be_visible()
    mocked_page.get_by_test_id("stock-edit-1").click()
    dialog = mocked_page.get_by_role("dialog")
    dialog.locator("select").first.select_option("Tpex")
    dialog.get_by_role("button", name="儲存").click()
    expect(mocked_page.get_by_text("股票已更新")).to_be_visible()
    assert saved_market == "Tpex"

    complete_report = {
        "periodMonths": 12,
        "scenarioDescription": "目前持股歷史情境：以目前毛市值權重套用歷史還原日報酬",
        "calculationDate": "2026-08-07",
        "dataCutoffDate": "2026-08-06",
        "portfolioAnnualizedVolatility": {"value": 0.2, "unavailableReason": None},
        "portfolioMaximumDrawdown": {"value": -0.15, "unavailableReason": None},
        "eligibleMarketValueCoverage": 0.95,
        "eligibleMarketValueCoverageMetric": {"value": 0.95, "unavailableReason": None},
        "coverageThreshold": 0.9,
        "commonObservationCount": 200,
        "totalHoldingCount": 2,
        "includedInstruments": [],
        "excludedInstruments": [],
        "volatilityRanking": [{
            "name": "台積電", "symbol": "2330", "market": "Twse", "grossMarketValue": 60000,
            "weight": 0.6, "annualizedVolatility": 0.2, "observations": 200,
        }],
        "correlationMatrix": {
            "labels": [
                {"name": "台積電", "symbol": "2330", "market": "Twse"},
                {"name": "台灣50", "symbol": "0050", "market": "Twse"},
            ],
            "values": [[1, 0.42], [0.42, 1]],
            "commonObservationCount": 200,
            "unavailableReason": None,
        },
        "syncWarnings": [],
        "riskContributions": [],
    }
    coverage_report = {
        **complete_report,
        "periodMonths": 3,
        "eligibleMarketValueCoverage": 0.75,
        "eligibleMarketValueCoverageMetric": {"value": 0.75, "unavailableReason": None},
        "portfolioAnnualizedVolatility": {"value": None, "unavailableReason": "CoverageBelowThreshold"},
        "correlationMatrix": {**complete_report["correlationMatrix"], "unavailableReason": "InsufficientCommonDates"},
    }

    def market_risk(route: Route) -> None:
        query = parse_qs(urlparse(route.request.url).query)
        route_json(route, coverage_report if query.get("periodMonths") == ["3"] else complete_report)

    mocked_page.route("**/api/reports/stock-market-risk**", market_risk)
    mocked_page.set_viewport_size({"width": 375, "height": 800})
    mocked_page.goto("/reports")
    mocked_page.get_by_role("tab", name="市場風險").click()
    expect(mocked_page.get_by_text("95.0%")).to_be_visible()
    expect(mocked_page.get_by_text("相關性矩陣")).to_be_visible()
    expect(mocked_page.get_by_role("columnheader", name="0050")).to_be_visible()
    assert mocked_page.locator("table").count() >= 1
    assert mocked_page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")

    mocked_page.get_by_test_id("period-3").click()
    expect(mocked_page.get_by_text("覆蓋不足。系統不會以零波動代表缺少資料。", exact=True)).to_be_visible()


# 驗證股票 Ledger 初始化、Buy mutation 與投資績效 tab 可在瀏覽器中完成最小 smoke。
def test_stock_ledger_initialization_and_performance_smoke(mocked_page: Page) -> None:
    stock = {
        "id": 1,
        "name": "台積電",
        "symbol": "2330",
        "market": "Twse",
        "instrumentType": "Stock",
        "shares": 10,
        "buyPrice": 500,
        "currentPrice": 600,
        "broker": "測試券商",
        "lastPriceUpdate": None,
        "grossMarketValue": 6000,
        "buyCommission": 0,
        "sellCommission": 0,
        "securitiesTransactionTax": 0,
        "estimatedNetSellValue": 6000,
        "estimatedGainLoss": 1000,
        "hasLedger": False,
    }
    stock_list = {
        "items": [stock],
        "total": 1,
        "page": 1,
        "pageSize": 15,
        "totalEstimatedNetSellValue": 6000,
        "totalEstimatedGainLoss": 1000,
    }
    ledger_row = {
        "id": 1,
        "stockId": 1,
        "stockName": "台積電",
        "symbol": "2330",
        "market": "Twse",
        "broker": "測試券商",
        "type": "Buy",
        "tradeDate": "2026-08-01",
        "sequence": 1,
        "shares": 10,
        "price": 500,
        "fee": 0,
        "tax": 0,
        "cashAmount": None,
        "openingMarketValue": None,
        "notes": None,
        "grossAmount": 5000,
        "netCashFlow": -5000,
        "allocatedCostBasis": 5000,
        "realizedGainLoss": 0,
        "netDividend": 0,
        "remainingShares": 10,
        "remainingCostBasis": 5000,
        "executionAveragePrice": 500,
    }
    performance = {
        "dateStart": "2026-01-01",
        "dateEnd": "2026-08-25",
        "trackingStartDate": "2026-01-01",
        "hasSyntheticOpeningBalances": False,
        "terminalValuationSource": "CurrentGrossMarketValue",
        "ledgerCoverage": {"value": 1, "unavailableReason": "None"},
        "summary": {
            "currentGrossMarketValue": 6000,
            "remainingCostBasis": 5000,
            "realizedGainLoss": 0,
            "unrealizedGainLoss": 1000,
            "netDividendIncome": 0,
            "totalGainLoss": 1000,
        },
        "twr": {"value": 0.12, "unavailableReason": "None"},
        "xirr": {"value": 0.18, "unavailableReason": "None"},
        "xirrOpeningValue": 5000,
        "xirrOpeningValuationSource": "HistoricalRawClose",
        "monthlyPoints": [{
            "month": "2026-08",
            "endingMarketValue": 6000,
            "netContribution": 0,
            "realizedGainLoss": 0,
            "dividendIncome": 0,
            "cumulativeTwr": 0.12,
        }],
        "instrumentBreakdown": [{
            "stockId": 1,
            "name": "台積電",
            "symbol": "2330",
            "market": "Twse",
            "broker": "測試券商",
            "currentShares": 10,
            "grossMarketValue": 6000,
            "remainingCostBasis": 5000,
            "realizedGainLoss": 0,
            "unrealizedGainLoss": 1000,
            "dividendIncome": 0,
            "totalGainLoss": 1000,
            "isClosed": False,
        }],
        "dataQuality": {
            "activeInstrumentCount": 1,
            "ledgerManagedInstrumentCount": 1,
            "priceObservationCount": 10,
            "priceCoverage": 1,
            "trackingStartReason": "None",
            "hasIncompleteLedgerCoverage": False,
        },
    }
    created_operations: list[str] = []

    def stocks_and_ledger(route: Route) -> None:
        """回應股票、Ledger、費稅估算與交易 mutation 的 deterministic API route。"""
        path = urlparse(route.request.url).path
        if path == "/api/stocks" and route.request.method == "GET":
            route_json(route, stock_list)
            return
        if path == "/api/stocks/options" and route.request.method == "GET":
            route_json(route, [{
                "id": stock["id"],
                "name": stock["name"],
                "symbol": stock["symbol"],
                "broker": stock["broker"],
                "shares": stock["shares"],
                "hasLedger": True,
            }])
            return
        if path.endswith("/initialize"):
            route_json(route, {"initializedCount": 1, "skippedCount": 0, "blockingCount": 0, "totalCount": 1, "blockingStocks": []})
            return
        if path == "/api/stocks/positions" and route.request.method == "POST":
            created_operations.append("position")
            route_json(route, {"stock": stock, "transaction": ledger_row, "replay": {"projection": {}, "entries": []}}, status=201)
            return
        if path == "/api/stocks/ledger" and route.request.method == "GET":
            route_json(route, {"items": [ledger_row], "total": 1, "page": 1, "pageSize": 20})
            return
        if path == "/api/stocks/ledger/estimate-costs" and route.request.method == "POST":
            route_json(route, {"fee": 10, "tax": 1})
            return
        if path.endswith("/transactions") and route.request.method == "POST":
            created_operations.append("transaction")
            route_json(route, ledger_row, status=201)
            return
        route.fallback()

    mocked_page.route("**/api/stocks**", stocks_and_ledger)

    mocked_page.route("**/api/reports/stock-performance**", lambda route: route_json(route, performance))
    mocked_page.goto("/stocks")

    mocked_page.get_by_role("button", name="+ 新增股票").click()
    stock_dialog = mocked_page.get_by_role("dialog")
    stock_dialog.locator('input[placeholder="e.g. 台積電"]').fill("台積電")
    stock_dialog.locator('input[placeholder="e.g. 2330"]').fill("2330")
    stock_dialog.locator('input[type="number"][step="1"]').fill("10")
    stock_dialog.locator('input[type="number"][step="0.01"]').nth(0).fill("500")
    stock_dialog.locator('input[type="number"][step="0.01"]').nth(1).fill("600")
    stock_dialog.get_by_role("button", name="儲存").click()
    expect(mocked_page.get_by_text("股票已建立")).to_be_visible()

    expect(mocked_page.get_by_test_id("ledger-initialization")).to_be_visible()
    mocked_page.get_by_test_id("initialize-ledger").click()
    expect(mocked_page.get_by_text("Ledger 初始化完成")).to_be_visible()

    mocked_page.get_by_test_id("stock-tab-ledger").click()
    mocked_page.get_by_test_id("ledger-new-transaction").click()
    dialog = mocked_page.get_by_role("dialog")
    dialog.get_by_test_id("transaction-shares").fill("2")
    dialog.get_by_test_id("transaction-price").fill("610")
    expect(dialog.get_by_test_id("transaction-estimate-ready")).to_be_visible()
    dialog.get_by_test_id("transaction-save").click()
    expect(mocked_page.get_by_text("交易已建立").last).to_be_visible()

    mocked_page.get_by_test_id("ledger-new-transaction").click()
    dialog = mocked_page.get_by_role("dialog")
    dialog.get_by_test_id("transaction-type").select_option("Dividend")
    dialog.get_by_test_id("transaction-cash-amount").fill("100")
    dialog.get_by_test_id("transaction-fee").fill("0")
    dialog.get_by_test_id("transaction-tax").fill("0")
    dialog.get_by_test_id("transaction-save").click()
    expect(mocked_page.get_by_text("交易已建立").last).to_be_visible()

    mocked_page.get_by_test_id("ledger-new-transaction").click()
    dialog = mocked_page.get_by_role("dialog")
    dialog.get_by_test_id("transaction-type").select_option("Sell")
    dialog.get_by_test_id("transaction-shares").fill("2")
    dialog.get_by_test_id("transaction-price").fill("610")
    expect(dialog.get_by_test_id("transaction-estimate-ready")).to_be_visible()
    dialog.get_by_test_id("transaction-save").click()
    expect(mocked_page.get_by_text("交易已建立").last).to_be_visible()
    assert created_operations.count("position") == 1
    assert created_operations.count("transaction") == 3
    expect(mocked_page.get_by_text("已實現損益")).to_be_visible()
    expect(mocked_page.get_by_text("剩餘股數")).to_be_visible()

    mocked_page.goto("/reports")
    mocked_page.get_by_role("tab", name="投資績效").click()
    expect(mocked_page.get_by_test_id("performance-kpis")).to_contain_text("TWR")
    expect(mocked_page.get_by_test_id("performance-kpis")).to_contain_text("12.00%")


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

    expect(mocked_page.get_by_text("尚無信用卡交易資料")).to_be_visible()
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


# 驗證交易表單能以鍵盤完成基本輸入，並提供可程式化的標籤、錯誤與狀態。
def test_transaction_entry_keyboard_and_accessibility(mocked_page: Page) -> None:
    mocked_page.route("**/api/categories**", _ordinary_entry_options)
    mocked_page.route("**/api/payment-methods**", _ordinary_entry_options)
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

    expect(mocked_page.get_by_role("status").last).to_contain_text("交易已建立")


# 驗證普通交易入口不顯示信用卡控制項，也不載入信用卡參考資料。
def test_transaction_entry_excludes_credit_card_path(mocked_page: Page) -> None:
    credit_card_requests: list[str] = []

    # 回應含信用卡的支付方式清單，確認畫面只過濾而非依賴後端移除。
    def ordinary_options_with_credit_card(route: Route) -> None:
        path = urlparse(route.request.url).path
        if path.endswith("/payment-methods"):
            route_json(route, {
                "items": [
                    {"id": 2, "name": "信用卡", "systemCode": "credit-card", "icon": "", "color": "#4F759D"},
                    {"id": 11, "name": "現金", "systemCode": "cash", "icon": "", "color": "#4F759D"},
                ],
                "total": 2,
                "page": 1,
                "pageSize": 999,
            })
            return
        _ordinary_entry_options(route)

    # 將意外的信用卡資料請求記錄下來，讓測試失敗而非依賴未處理網路。
    def unexpected_credit_card_request(route: Route) -> None:
        credit_card_requests.append(route.request.url)
        route_json(route, {"items": [], "total": 0, "page": 1, "pageSize": 999})

    mocked_page.route("**/api/categories**", ordinary_options_with_credit_card)
    mocked_page.route("**/api/payment-methods**", ordinary_options_with_credit_card)
    mocked_page.route("**/api/credit-cards**", unexpected_credit_card_request)
    mocked_page.goto("/expenses")
    mocked_page.get_by_role("button", name=re.compile("新增")).first.click()

    form = mocked_page.locator("form").last
    expect(form.locator("#transaction-payment-method option").filter(has_text="信用卡")).to_have_count(0)
    expect(form.locator("#transaction-payment-mode")).to_have_count(0)
    expect(form.locator("#transaction-installment-card")).to_have_count(0)
    expect(form.locator("#transaction-installment-periods")).to_have_count(0)
    assert credit_card_requests == []


# 驗證窄螢幕交易表單採單欄、無水平溢位且主操作仍可觸及。
def test_transaction_entry_mobile_layout(mocked_page: Page) -> None:
    mocked_page.set_viewport_size({"width": 375, "height": 800})
    mocked_page.route("**/api/categories**", _ordinary_entry_options)
    mocked_page.route("**/api/payment-methods**", _ordinary_entry_options)
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
        if transaction_calls == 1:
            route_json(route, {"items": [old_transaction], "total": 1, "page": 1, "pageSize": 50, "summary": {"totalAmount": 10, "totalIncome": 0, "totalExpense": 10, "count": 1, "dailyAverage": 10, "maxAmount": 10}})
            return
        if transaction_calls == 2:
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
