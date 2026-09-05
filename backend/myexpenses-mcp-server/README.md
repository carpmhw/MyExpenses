# MyExpenses MCP Server

這個 MCP server 使用 stdio transport 呼叫 MyExpenses REST API，支援固定日期、參考資料解析、冪等記帳及跨來源消費查詢。

## 啟動

```bash
npm ci
npm run build
export MYEXPENSES_API_URL=http://localhost
export MYEXPENSES_API_TOKEN='由 MyExpenses UI 建立的 token'
npm start
```

`MYEXPENSES_API_TOKEN` 不可提交到 repository 或寫入公開 log。`MYEXPENSES_API_URL` 必須是 reverse proxy origin，不要附加 `/api`。可選的 `MYEXPENSES_API_TIMEOUT_MS` 預設為 10000 毫秒。

## Scopes

依實際使用的工具授予權限；完整工具集使用下列 scopes，純查帳不需要 `transactions:write`：

- `agent-context:read`
- `categories:read`
- `credit-cards:read`
- `payment-methods:read`
- `reports:read`
- `transactions:read`
- `transactions:write`
- `transactions:undo`（只有使用 `undo_transaction` 才需要）

不需要 `transactions:delete`。API scope 是真正的安全邊界，MCP tool annotations 不能取代 API 授權。

## Write Flow

1. 呼叫 `prepare_bookkeeping_entry`，intent 使用 `ordinary`、`credit_card_purchase` 或 `credit_card_repayment`。
2. `ready` 時保存回傳的 `requestId`、固定日期及 `arguments`；`needs_input` 時只詢問缺少或歧義欄位。
3. 將 prepared `arguments` 原樣傳給指定的 create tool，不重新解析名稱、不重新計算日期。
4. `created` 與 `replayed` 都帶 canonical source ID；timeout、連線中斷或 5xx 會回 `outcome_unknown`，以原 requestId 重試。

`create_transaction` 沒有完整 prepared envelope 時只回 `needs_preparation`，不會呼叫未受保護的普通 API 路徑。信用卡消費使用 `create_credit_card_transaction`，只送 `/api/installments` 的 `cardId`、`totalAmount`、`periods`、`purchaseDate` 與 `description`。

## Query Semantics

- `search_transactions` 是普通原始帳目查詢；`repaymentOnly=true` 只篩 `Expense`、`living`、描述含 `信用卡帳單` 的資料。
- `search_consumption` 必須帶明確 `startDate`／`endDate`，信用卡消費按購買日及全額計入，卡費不重複計入。
- `get_financial_summary` 是既有普通月摘要，不是 consumption total。
- 月摘要保留 API 的 `totalIncome`、`totalExpense`、`totalBankBalance` 與匯率資訊；不捏造 API 未提供的 `balance` 或 `transactionCount`。
- 分類 consumption 只包含普通消費；信用卡目前沒有分類，不能用描述推測分類，`source=credit_card` 加 `categoryId` 會被拒絕。
- `get_transaction` 的 `sourceType` 是 `ordinary`；`get_credit_card_transaction` 的 `sourceType` 是 `credit_card`，兩者 numeric ID 不可混用。

## Example Traces

- 「午餐 150」：`prepare_bookkeeping_entry(ordinary)` → `create_transaction`，缺省日期、`other-expense` 與 `cash` 會在 ready envelope 中明列。
- 「買東西 500」：`prepare_bookkeeping_entry(ordinary)` → `create_transaction`，若未指定分類使用 `other-expense`。
- 「刷卡午餐 150」：`prepare_bookkeeping_entry(credit_card_purchase)` → 單卡直接 ready，多卡先回 `needs_input` 選卡。
- 「手機 24000 刷卡分期」：`prepare_bookkeeping_entry(credit_card_purchase, installmentRequested=true)`；缺少 periods 時先追問，不猜期數。
- 「繳卡費 3000」：`prepare_bookkeeping_entry(credit_card_repayment)` → `create_transaction`，使用 `living`、`信用卡帳單` 與現金，不會更新任何 installment payment。
- 「本月花多少」：以後端 context 展開本月月初至今天後呼叫 `search_consumption`；「本月刷卡多少」加 `source=credit_card`；「本月繳多少卡費」使用 `search_transactions` 的 `repaymentOnly=true`。

回答月份比較時，明列每段實際日期。預設「本月」是月初至今天，「上月」是完整上月；不要把不同長度的期間宣稱為同期，也不要把查無資料或 API 失敗說成零消費。

### 固定驗收範例

固定 context 為 `2026-09-05`、`Asia/Taipei`；參考資料假設飲食 `id=7, systemCode=meal`、其他支出 `id=8`、生活 `id=9`、現金 `id=3`、唯一信用卡 `id=4`。ID 僅供 fixture 使用，正式操作必須從 API 取得。每次新的記帳準備都產生不同 UUID。

| 使用者輸入 | 準備輸入或查詢 | 預期行為 |
| --- | --- | --- |
| 午餐 150 | `prepare_bookkeeping_entry`：`intent=ordinary, amount=150, description=午餐, category=飲食` | `ready`，日期 `2026-09-05`、分類 7、現金 3，直接呼叫回傳 targetTool |
| 買東西 500 | `intent=ordinary, amount=500, description=買東西` | `ready`，其他支出 8、現金 3 |
| 刷卡午餐 150 | `intent=credit_card_purchase, amount=150, description=午餐` | 唯一卡片 4、一期；多張卡則追問，沒有任何 POST |
| 手機 24000 刷卡分期 | `intent=credit_card_purchase, amount=24000, description=手機, installmentRequested=true` | `needs_input` 要求 `periods`；回答 12 後重新準備，使用總額 24000 |
| 繳卡費 3000 | `intent=credit_card_repayment, amount=3000` | 普通支出，生活 9、描述 `信用卡帳單`、現金 3；不更新付款狀態 |
| 本月花多少 | `search_consumption(startDate=2026-09-01, endDate=2026-09-05)` | 完整 consumption summary，不以單頁 items 相加 |
| 本月刷卡多少 | 同期間，`source=credit_card` | 購買日全額，不加本期應付款 |
| 本月繳多少卡費 | `search_transactions`，同期間、`repaymentOnly=true, type=Expense` | 普通卡費繳款 summary，不代表分期付款狀態 |
| 本月餐飲花多少 | `search_consumption`，同期間、`categoryId=7` | 只回普通餐飲，明說未涵蓋信用卡分類 |
| 比較本月與上月 | 分別查 `2026-09-01..2026-09-05` 與 `2026-08-01..2026-08-31` | 明說 5 天與 31 天不是同期；不在 MCP 本機重算今天 |

「上週」固定為上一個週一至週日，在此 context 下是 `2026-08-24..2026-08-30`。有明確日期時以使用者日期優先。範例是 agent 工具分派契約，不是對任意 LLM 語言理解的保證。

## 部署與回退

1. 先備份資料庫並部署 API，再部署以 `npm ci`、`npm test`、`npm run build` 驗證過的 MCP；此次沿用既有 receipt schema，不增加 migration。
2. 在 UI 建立具所需新 read scopes 的 token，透過 client 環境注入；核對 context、卡片列表及查帳，再執行測試記帳。
3. **Breaking**：舊 `create_transaction` 語意參數會得到 `needs_preparation`，必須先解析固定 ID；不得改走無 key API。`ready` 不是已提交，成功只依 API canonical 回應確認。
4. 回退時停用新 MCP 寫入工具或切換已驗證版本，保留相容的 API 與全部財務資料、receipt；不得刪除 receipt、重新產生 key 或還原舊備份來掩蓋不確定寫入。

結果為 `outcome_unknown` 時保存原命令再重試；若整個 envelope 已遺失，先查帳人工核對再決定是否建立新命令，不能只憑相同金額或描述判定同一筆。

## Test

```bash
npm test
```
