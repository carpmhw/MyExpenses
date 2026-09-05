import { spawn } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { createServer as createHttpServer } from 'node:http';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { ApiClient } from './api-client.js';
import { createServer } from './index.js';
import { matchesSchema } from './schemas.js';
/** 建立不需啟動 HTTP server 的 fetch mock，並記錄請求供契約斷言。 */
function mockFetch(handler, requests) {
    return (async (input, init = {}) => {
        const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url);
        requests.push({ url, init });
        return handler(url, init);
    });
}
/** 建立一次 MCP in-memory client/server 呼叫並回傳工具結果。 */
async function callTool(handler, name, args, uuid = '11111111-1111-4111-8111-111111111111') {
    const requests = [];
    const apiClient = new ApiClient('http://api.test', 'test-token', 500, mockFetch(handler, requests));
    const server = createServer(apiClient, uuid === null ? undefined : () => uuid);
    const client = new Client({ name: 'contract-test', version: '1.0.0' }, { capabilities: {} });
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    const result = await client.callTool({ name, arguments: args });
    await client.close();
    await server.close();
    return { result: result, requests };
}
/** 讀取 MCP structuredContent，讓測試不依賴人類可讀文字。 */
function structured(result) {
    assert.ok(result.structuredContent);
    return result.structuredContent;
}
const ordinaryCommand = {
    requestId: '11111111-1111-4111-8111-111111111111', amount: 150,
    description: '午餐', date: '2026-09-05', type: 'Expense', categoryId: 7, paymentMethodId: 3,
};
const creditCommand = {
    requestId: ordinaryCommand.requestId, cardId: 4, totalAmount: 24000,
    periods: 12, purchaseDate: '2026-09-05', description: '手機',
};
// 驗證歷史明細與編輯後重播允許 72 期 canonical 資料，完整保留付款時程。
test('historical 72-period credit details and edited canonical replays retain all payments', async () => {
    const payments = Array.from({ length: 72 }, (_, index) => ({
        id: index + 1, installmentId: 9, period: index + 1, amount: 1000,
        isPaid: false, paidDate: null, dueDate: null,
    }));
    const canonical = { ...creditCommand, id: 9, periods: 72, totalAmount: 72000, perPeriod: 1000, payments };
    for (const replayed of [false, true]) {
        const { result, requests } = await callTool((url, init) => {
            assert.equal(url.pathname, replayed ? '/api/installments' : '/api/installments/9');
            assert.equal(init.method, replayed ? 'POST' : 'GET');
            if (replayed)
                assert.equal(JSON.parse(String(init.body)).periods, 12);
            return Response.json(canonical, { headers: { 'X-Idempotent-Replay': String(replayed) } });
        }, replayed ? 'create_credit_card_transaction' : 'get_credit_card_transaction', replayed ? creditCommand : { sourceId: 9 });
        const data = structured(result);
        assert.equal(data.status, replayed ? 'replayed' : 'ok');
        const transaction = (replayed ? data.creditCardTransaction : data.transaction);
        assert.equal(transaction.periods, 72);
        assert.deepEqual(transaction.payments, payments);
        assert.equal(requests.length, 1);
    }
});
// 驗證歷史回應的相容性不放寬新命令與準備輸入的 60 期上限。
test('72-period create and preparation inputs remain rejected without API calls', async () => {
    const created = await callTool(references, 'create_credit_card_transaction', { ...creditCommand, periods: 72 });
    assert.equal(structured(created.result).status, 'error');
    assert.equal(structured(created.result).code, 'invalid_input');
    assert.equal(created.result.isError, true);
    assert.equal(created.requests.length, 0);
    const prepared = await callTool(references, 'prepare_bookkeeping_entry', {
        intent: 'credit_card_purchase', amount: 72000, description: '歷史期數不能用於新命令', periods: 72,
    });
    assert.equal(structured(prepared.result).status, 'needs_input');
    assert.equal(prepared.requests.length, 0);
});
// 驗證新增與重播確認使用實際回應，而非送出的日期、金額或舊參考資料。
test('ordinary confirmation includes canonical category and payment names for create and replay', async () => {
    for (const replayed of [false, true]) {
        const { result, requests } = await callTool(() => Response.json({
            ...ordinaryCommand, id: 42, amount: 999, date: '2026-09-06', categoryId: 90, paymentMethodId: 91,
            category: { id: 90, name: '實際分類' }, paymentMethod: { id: 91, name: '實際付款方式' },
        }, { headers: { 'X-Idempotent-Replay': String(replayed) } }), 'create_transaction', ordinaryCommand);
        assert.equal(structured(result).status, replayed ? 'replayed' : 'created');
        const content = JSON.stringify(result.content);
        assert.match(content, /2026-09-06 999/);
        assert.match(content, /分類：實際分類/);
        assert.match(content, /付款方式：實際付款方式/);
        assert.equal(requests.length, 1);
        assert.equal(requests[0].init.method, 'POST');
    }
});
// 驗證缺少名稱時明列 canonical ID，連 ID 也缺少時不猜測原送出值。
test('ordinary confirmation explicitly falls back to canonical IDs when names are unavailable', async () => {
    for (const reference of [undefined, null, { name: '' }, { name: ' ' }, { name: 123 }]) {
        const { result } = await callTool(() => Response.json({
            ...ordinaryCommand, id: 42, categoryId: 90, paymentMethodId: 91, category: reference, paymentMethod: reference,
        }), 'create_transaction', ordinaryCommand);
        assert.equal(structured(result).status, 'created');
        assert.match(JSON.stringify(result.content), /分類：ID 90/);
        assert.match(JSON.stringify(result.content), /付款方式：ID 91/);
    }
    const { result } = await callTool(() => Response.json({ ...ordinaryCommand, id: 42, paymentMethodId: null }), 'create_transaction', ordinaryCommand);
    assert.match(JSON.stringify(result.content), /付款方式：未提供（ID 不可用）/);
});
// 驗證舊語意欄位被辨識但不直接解析或寫入，混用 ID 也必須先釐清。
test('legacy create selectors require preparation without any API calls, including mixed IDs', async () => {
    for (const field of ['category', 'categoryCode', 'paymentMethod', 'paymentMethodCode']) {
        for (const withIds of [false, true]) {
            const args = withIds ? { ...ordinaryCommand, [field]: 'unknown-or-conflicting' }
                : { amount: 150, description: '午餐', [field]: '現有名稱' };
            const { result, requests } = await callTool(references, 'create_transaction', args);
            const data = structured(result);
            assert.equal(data.status, 'needs_preparation', `${field}, withIds=${withIds}`);
            assert.equal(data.code, 'needs_preparation');
            assert.equal(result.isError, true);
            assert.match(String(data.guidance), /prepare_bookkeeping_entry/);
            assert.equal(requests.length, 0);
            if (withIds) {
                assert.equal(data.requestId, ordinaryCommand.requestId);
                assert.match(String(data.guidance), /原始/);
            }
        }
    }
});
/** 提供有效的參考資料，讓驗證測試聚焦於指定欄位。 */
function references(url) {
    if (url.pathname === '/api/agent/context')
        return Response.json({ currentDate: '2026-09-05', timeZoneId: 'Asia/Taipei' });
    if (url.pathname === '/api/categories')
        return Response.json([{ id: 7, name: '飲食', type: 'Expense', systemCode: 'food' }, { id: 8, name: '其他', type: 'Expense', systemCode: 'other-expense' }]);
    if (url.pathname === '/api/payment-methods')
        return Response.json([{ id: 3, name: '現金', systemCode: 'cash' }]);
    return Response.json({ items: [{ id: 4, bankName: '銀行', lastFourDigits: '1234' }], total: 1, page: 1, pageSize: 100 });
}
// 驗證兩次相同消費的獨立準備使用正式 UUID 產生器，而非共用請求識別碼。
test('independent preparations generate distinct real UUIDs without financial writes', async () => {
    const args = { intent: 'ordinary', amount: 150, description: '午餐' };
    const first = await callTool(references, 'prepare_bookkeeping_entry', args, null);
    const second = await callTool(references, 'prepare_bookkeeping_entry', args, null);
    const a = structured(first.result);
    const b = structured(second.result);
    assert.equal(a.status, 'ready');
    assert.equal(b.status, 'ready');
    assert.match(String(a.requestId), /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    assert.notEqual(a.requestId, b.requestId);
    const { requestId: firstId, ...firstPayload } = a.arguments;
    const { requestId: secondId, ...secondPayload } = b.arguments;
    assert.equal(firstId, a.requestId);
    assert.equal(secondId, b.requestId);
    assert.deepEqual(firstPayload, secondPayload);
    assert.ok([...first.requests, ...second.requests].every(request => request.init.method === 'GET'));
});
// 驗證缺少預設參考資料時明確回設定錯誤，不自動建立參考資料。
test('missing default category, cash, living or cards produces configuration errors', async () => {
    for (const [path, intent, code] of [
        ['/api/categories', 'ordinary', 'default_category_missing'],
        ['/api/payment-methods', 'ordinary', 'default_payment_method_missing'],
        ['/api/categories', 'credit_card_repayment', 'repayment_category_missing'],
        ['/api/credit-cards', 'credit_card_purchase', 'credit_card_not_configured'],
    ]) {
        const { result, requests } = await callTool(url => url.pathname === path
            ? Response.json(path === '/api/credit-cards' ? { items: [], total: 0, page: 1, pageSize: 100 } : []) : references(url), 'prepare_bookkeeping_entry', { intent, amount: 150, description: '午餐' });
        assert.equal(structured(result).status, 'configuration_error', code);
        assert.equal(structured(result).code, code);
        assert.equal(result.isError, true);
        assert.ok(requests.every(request => request.init.method === 'GET'));
    }
});
// 驗證缺金額、收入分類或分期期數時回指定追問，不套用不適當預設。
test('missing bookkeeping fields and duplicate card names request clarification', async () => {
    for (const [args, field] of [
        [{ intent: 'ordinary', description: '午餐' }, 'amount'],
        [{ intent: 'ordinary', amount: 150, description: '薪資', type: 'income' }, 'category'],
        [{ intent: 'credit_card_purchase', amount: 24000, description: '手機', installmentRequested: true }, 'periods'],
    ]) {
        const { result, requests } = await callTool(references, 'prepare_bookkeeping_entry', args);
        assert.equal(structured(result).status, 'needs_input');
        assert.deepEqual(structured(result).missingFields, [field]);
        assert.ok(requests.every(request => request.init.method === 'GET'));
    }
    const { result, requests } = await callTool(url => url.pathname === '/api/credit-cards'
        ? Response.json({ items: [{ id: 1, bankName: '同名銀行', lastFourDigits: '1111' }, { id: 2, bankName: '同名銀行', lastFourDigits: '2222' }], total: 2, page: 1, pageSize: 100 }) : references(url), 'prepare_bookkeeping_entry', { intent: 'credit_card_purchase', amount: 150, description: '午餐', card: '同名銀行' });
    assert.equal(structured(result).status, 'needs_input');
    assert.deepEqual(structured(result).fieldErrors, { card: 'ambiguous' });
    assert.equal(structured(result).candidates.length, 2);
    assert.ok(requests.every(request => request.init.method === 'GET'));
});
// 驗證錯誤碼與 HTTP 狀態一致，annotations 不會繞過後端拒絕或洩漏內容。
test('401, 403, 409 and 410 return explicit safe codes for both write tools', async () => {
    for (const [status, code] of [[401, 'unauthorized'], [403, 'forbidden'], [409, 'conflict'], [410, 'result_unavailable']]) {
        for (const [name, command] of [['create_transaction', ordinaryCommand], ['create_credit_card_transaction', creditCommand]]) {
            const { result, requests } = await callTool(() => Response.json({ detail: 'private SQL and secret token' }, { status }), name, command);
            const data = structured(result);
            assert.equal(data.status, 'error');
            assert.equal(data.code, code);
            assert.equal(data.httpStatus, status);
            assert.equal(data.requestId, command.requestId);
            assert.equal(result.isError, true);
            assert.equal(JSON.stringify(result).includes('private SQL'), false);
            assert.equal(requests.length, 1);
        }
    }
});
// 驗證普通搜尋轉送篩選與跨頁完整摘要，不把目前頁面誤當完整集合。
test('ordinary search forwards filters and preserves the complete filtered summary', async () => {
    const args = { startDate: '2026-09-01', endDate: '2026-09-05', type: 'expense', categoryId: 7, search: '信用卡帳單', repaymentOnly: true, page: 2, pageSize: 1 };
    const payload = { items: [{ ...ordinaryCommand, id: 8 }], total: 125, page: 2, pageSize: 1,
        summary: { totalAmount: 50000, totalIncome: 0, totalExpense: 50000, count: 125, dailyAverage: 10000, maxAmount: 24000 } };
    const { result, requests } = await callTool(url => {
        assert.equal(url.pathname, '/api/transactions');
        assert.deepEqual(Object.fromEntries(url.searchParams), Object.fromEntries(Object.entries({ ...args, type: 'Expense' }).map(([key, value]) => [key, String(value)])));
        return Response.json(payload);
    }, 'search_transactions', args);
    assert.equal(structured(result).status, 'ok');
    assert.deepEqual(structured(result).summary, payload.summary);
    assert.equal(structured(result).total, 125);
    assert.equal(requests.length, 1);
});
// 驗證信用來源消費搜尋保留 API 的完整摘要、期間與分類涵蓋限制。
test('credit consumption forwards source filters and preserves complete summary and coverage', async () => {
    const args = { startDate: '2026-09-01', endDate: '2026-09-05', source: 'credit_card', search: '手機', page: 2, pageSize: 1 };
    const payload = {
        items: [{ sourceType: 'credit_card', sourceId: 7, date: '2026-09-05', amount: 24000, description: '手機' }],
        total: 125, page: 2, pageSize: 1, basis: 'consumption', period: { startDate: args.startDate, endDate: args.endDate }, timeZoneId: 'Asia/Taipei',
        filters: { source: 'credit_card', categoryId: null, search: '手機' },
        summary: { totalAmount: 50000, ordinaryAmount: 0, creditCardAmount: 50000, count: 125 },
        coverage: { creditCardCategoriesAvailable: false, categoryNote: '信用卡無分類', recognitionNote: '購買日全額', completenessNote: '僅已記錄資料' }, warnings: [],
    };
    const { result, requests } = await callTool(url => {
        assert.equal(url.pathname, '/api/consumption');
        assert.deepEqual(Object.fromEntries(url.searchParams), Object.fromEntries(Object.entries(args).map(([key, value]) => [key, String(value)])));
        return Response.json(payload);
    }, 'search_consumption', args);
    assert.deepEqual(structured(result), { ...payload, status: 'ok' });
    assert.equal(requests.length, 1);
});
// 驗證相同數字 ID 仍依來源呼叫不同明細 API，不互相覆蓋身份。
test('equal numeric source IDs route to distinct ordinary and credit details', async () => {
    for (const [name, args, path, source] of [
        ['get_transaction', { id: 7 }, '/api/transactions/7', 'ordinary'],
        ['get_credit_card_transaction', { sourceId: 7 }, '/api/installments/7', 'credit_card'],
    ]) {
        const { result, requests } = await callTool(url => {
            assert.equal(url.pathname, path);
            return Response.json(source === 'ordinary' ? { ...ordinaryCommand, id: 7 }
                : { ...creditCommand, id: 7, perPeriod: 2000, payments: [] });
        }, name, args);
        assert.equal(structured(result).status, 'ok');
        assert.equal(structured(result).sourceType, source);
        assert.equal(structured(result).sourceId, 7);
        assert.equal(requests.length, 1);
    }
});
// 驗證真實 MCP 子程序重啟與後端跨日後，兩種寫入仍重送原始 envelope。
test('real stdio process restart across midnight preserves ordinary and credit envelopes', async () => {
    let currentDate = '2026-09-05';
    const writes = [];
    const reads = [];
    const receipts = new Map();
    const http = createHttpServer(async (request, response) => {
        const url = new URL(request.url, 'http://api.test');
        response.setHeader('Content-Type', 'application/json');
        if (request.method === 'GET') {
            reads.push(url.pathname);
            response.end(url.pathname === '/api/agent/context'
                ? JSON.stringify({ currentDate, timeZoneId: 'Asia/Taipei' }) : await references(url).text());
            return;
        }
        const chunks = [];
        for await (const chunk of request)
            chunks.push(Buffer.from(chunk));
        const body = Buffer.concat(chunks).toString();
        const key = request.headers['idempotency-key'];
        writes.push({ path: url.pathname, key, body });
        const receiptKey = `${url.pathname}:${key}`;
        const existing = receipts.get(receiptKey);
        const canonical = existing ?? { ...JSON.parse(body), id: url.pathname === '/api/transactions' ? 8 : 9,
            ...(url.pathname === '/api/installments' ? { perPeriod: 2000, payments: [] } : {}) };
        receipts.set(receiptKey, canonical);
        response.setHeader('X-Idempotent-Replay', String(Boolean(existing)));
        response.end(JSON.stringify(canonical));
    });
    await new Promise(resolve => http.listen(0, '127.0.0.1', resolve));
    const address = http.address();
    assert.ok(address && typeof address !== 'string');
    const envelopes = [];
    const results = [];
    try {
        for (const restarted of [false, true]) {
            if (restarted)
                currentDate = '2026-09-06';
            const transport = new StdioClientTransport({ command: process.execPath,
                args: [join(dirname(fileURLToPath(import.meta.url)), 'index.js')],
                env: { MYEXPENSES_API_URL: `http://127.0.0.1:${address.port}`, MYEXPENSES_API_TOKEN: 'restart-test-token' }, stderr: 'pipe' });
            const client = new Client({ name: 'restart-test', version: '1.0.0' });
            try {
                await client.connect(transport);
                if (!restarted) {
                    for (const args of [{ intent: 'ordinary', amount: 150, description: '午餐' },
                        { intent: 'credit_card_purchase', amount: 24000, periods: 12, description: '手機' }]) {
                        const prepared = (await client.callTool({ name: 'prepare_bookkeeping_entry', arguments: args })).structuredContent;
                        assert.equal(prepared.status, 'ready');
                        envelopes.push({ name: prepared.targetTool, arguments: prepared.arguments });
                    }
                }
                else {
                    const context = (await client.callTool({ name: 'get_bookkeeping_context' })).structuredContent;
                    assert.equal(context.context.currentDate, '2026-09-06');
                }
                const readCount = reads.length;
                for (const envelope of envelopes) {
                    const data = (await client.callTool(envelope)).structuredContent;
                    assert.equal(data.status, restarted ? 'replayed' : 'created');
                    assert.equal(data.requestId, envelope.arguments.requestId);
                    results.push(data.transaction ?? data.creditCardTransaction);
                }
                assert.equal(reads.length, readCount, '執行固定 ID 命令不可重新讀取日期或參考資料');
            }
            finally {
                await client.close();
                await transport.close();
            }
        }
        assert.equal(writes.length, 4);
        assert.deepEqual(writes.slice(0, 2), writes.slice(2));
        assert.deepEqual(results.slice(0, 2), results.slice(2));
        assert.equal(JSON.parse(writes[2].body).date, '2026-09-05');
        assert.equal(JSON.parse(writes[3].body).purchaseDate, '2026-09-05');
    }
    finally {
        await new Promise((resolve, reject) => http.close(error => error ? reject(error) : resolve()));
    }
});
// 驗證已送出的命令不因不完整回應而被誤報成功。
test('malformed successful writes retain the envelope as outcome_unknown', async () => {
    for (const [name, command, valid] of [
        ['create_transaction', ordinaryCommand, { ...ordinaryCommand, id: 9 }],
        ['create_credit_card_transaction', creditCommand, { ...creditCommand, id: 8, perPeriod: 2000, payments: [] }],
    ]) {
        for (const payload of [{}, null, ...['id', name === 'create_transaction' ? 'date' : 'purchaseDate', name === 'create_transaction' ? 'amount' : 'totalAmount'].map(field => Object.fromEntries(Object.entries(valid).filter(([key]) => key !== field)))]) {
            const { result, requests } = await callTool(() => Response.json(payload), name, command);
            assert.equal(structured(result).status, 'outcome_unknown');
            assert.equal(result.isError, true);
            assert.equal(structured(result).requestId, command.requestId);
            assert.deepEqual(structured(result).arguments, command);
            assert.equal(requests.length, 1);
        }
    }
});
// 驗證無效讀取資料不能套用零值或空集合。
test('malformed read payloads fail closed', async () => {
    for (const name of ['search_consumption', 'search_transactions', 'get_financial_summary', 'get_transaction', 'get_credit_card_transaction', 'list_credit_cards', 'list_categories', 'list_payment_methods']) {
        for (const payload of [{}, { items: [{}], total: 1 }, { items: [], total: 0, summary: {} }]) {
            if (['list_categories', 'list_payment_methods'].includes(name) && payload.total === 0)
                continue;
            const { result } = await callTool(() => Response.json(payload), name, name === 'get_transaction' ? { id: 1 } : name === 'get_credit_card_transaction' ? { sourceId: 1 } : name.startsWith('search_') ? { startDate: '2026-09-01', endDate: '2026-09-05' } : {});
            assert.equal(structured(result).status, 'error', name);
            assert.equal(structured(result).code, 'invalid_api_response', name);
        }
    }
});
// 驗證名稱與代碼以解析後的實體識別碼比較。
test('reference names and codes resolve the same identity', async () => {
    const { result } = await callTool(references, 'prepare_bookkeeping_entry', {
        intent: 'ordinary', amount: 150, description: '午餐', category: '飲食', categoryCode: 'food', paymentMethod: '現金', paymentMethodCode: 'cash',
    });
    assert.equal(structured(result).status, 'ready');
    assert.equal(structured(result).arguments.categoryId, 7);
});
// 驗證明確輸入不被靜默捨棄或誤套預設。
test('unknown fields, null selectors and per-period ambiguity cannot prepare or write', async () => {
    for (const extra of [{ typo: 1 }, { category: null }, { date: null }, { notes: 123 }, { card: '' }, { perPeriodAmount: 2000 }, { amount: 2000, totalAmount: 24000 }, { category: 'food' }]) {
        const { result, requests } = await callTool(references, 'prepare_bookkeeping_entry', {
            intent: 'credit_card_purchase', amount: 24000, description: '手機', ...extra,
        });
        assert.notEqual(structured(result).status, 'ready', JSON.stringify(extra));
        assert.ok(requests.every(request => request.init.method !== 'POST'));
    }
    const { result, requests } = await callTool(references, 'create_transaction', { ...ordinaryCommand, category: 'unknown' });
    assert.equal(result.isError, true);
    assert.equal(requests.length, 0);
});
// 驗證不完整或重複分頁不會變成單卡自動選擇。
test('card pagination rejects missing totals, stalled pages, changing totals and duplicates', async () => {
    for (const mode of ['missing', 'stalled', 'changing', 'duplicate', 'cap']) {
        const { result, requests } = await callTool(url => {
            if (url.pathname !== '/api/credit-cards')
                return references(url);
            const page = Number(url.searchParams.get('page'));
            return Response.json({ items: mode === 'stalled' && page > 1 ? [] : [{ id: mode === 'duplicate' ? 4 : page, bankName: '銀行', lastFourDigits: '1234' }],
                ...(mode === 'missing' ? {} : { total: mode === 'cap' ? 101 : mode === 'changing' && page > 1 ? 3 : 2 }), page, pageSize: 1 });
        }, 'prepare_bookkeeping_entry', { intent: 'credit_card_purchase', amount: 150, description: '午餐' });
        assert.equal(structured(result).status, 'error', mode);
        assert.ok(requests.length <= 101);
    }
});
// 驗證多個選擇器不能藉由第一個欄位掩蓋衝突。
test('all supplied selectors must agree even with explicit IDs and during searches', async () => {
    for (const args of [
        { categoryId: 7, category: 'food', categoryCode: 'other-expense' },
        { paymentMethodId: 3, paymentMethod: 'cash', paymentMethodCode: 'unknown' },
    ]) {
        const { result } = await callTool(references, 'prepare_bookkeeping_entry', { intent: 'ordinary', amount: 150, description: '午餐', ...args });
        assert.equal(structured(result).status, 'needs_input');
    }
    const { result, requests } = await callTool(references, 'search_transactions', { categoryId: 7, categoryCode: 'other-expense' });
    assert.equal(structured(result).status, 'needs_input');
    assert.ok(requests.every(request => request.url.pathname !== '/api/transactions'));
});
// 驗證準備固定識別碼之後，執行不查詢已刪除的舊參考資料。
test('prepared commands forward IDs without reads and report edited canonical replay data', async () => {
    for (const name of ['create_transaction', 'create_credit_card_transaction']) {
        const command = name === 'create_transaction' ? ordinaryCommand : creditCommand;
        const { result, requests } = await callTool((_url, init) => {
            assert.equal(init.method, 'POST');
            return Response.json(name === 'create_transaction'
                ? { ...ordinaryCommand, id: 42, categoryId: 99, date: '2026-09-06', amount: 999 }
                : { ...creditCommand, id: 42, cardId: 99, purchaseDate: '2026-09-06', totalAmount: 999, perPeriod: 83.25, payments: [] }, { headers: { 'X-Idempotent-Replay': 'true' } });
        }, name, command);
        assert.equal(structured(result).status, 'replayed');
        assert.equal(requests.length, 1);
        assert.match(JSON.stringify(result.content), /999/);
    }
});
// 驗證既有月摘要實際契約，不捏造 API 沒有提供的收支餘額與筆數。
test('financial summary accepts actual report fields without inventing values', async () => {
    const summary = { totalIncome: 1000, totalExpense: 150, totalBankBalance: 9999, baseCurrency: 'TWD', exchangeRateUpdatedAt: null, exchangeRateIsStale: false, conversionAvailable: true };
    const { result } = await callTool(() => Response.json(summary), 'get_financial_summary');
    assert.equal(structured(result).status, 'ok');
    assert.deepEqual(structured(result).summary, summary);
});
// 驗證不完整參考集合及錯誤預設型別不會產生可執行命令。
test('incomplete references and ambiguous or incompatible defaults cannot prepare', async () => {
    for (const mode of ['partial', 'duplicate_category', 'duplicate_payment', 'income_repayment']) {
        const { result } = await callTool(url => {
            if (url.pathname === '/api/categories') {
                const category = { id: 8, name: '其他', type: mode === 'income_repayment' ? 'Income' : 'Expense', systemCode: mode === 'income_repayment' ? 'living' : 'other-expense' };
                if (mode === 'partial')
                    return Response.json({ items: [category], total: 2 });
                return Response.json(mode === 'duplicate_category' ? [category, { ...category, id: 9 }] : [category]);
            }
            if (mode === 'duplicate_payment' && url.pathname === '/api/payment-methods')
                return Response.json([{ id: 1, name: '現金', systemCode: 'cash' }, { id: 2, name: '現金2', systemCode: 'cash' }]);
            return references(url);
        }, 'prepare_bookkeeping_entry', { intent: mode === 'income_repayment' ? 'credit_card_repayment' : 'ordinary', amount: 150, description: '午餐' });
        assert.notEqual(structured(result).status, 'ready', mode);
    }
});
// 驗證授權、衝突、刪除收據與非 JSON 回應的安全映射。
test('write errors preserve safe status and never leak backend bodies', async () => {
    for (const status of [401, 403, 404, 409, 410, 422, 500, 200]) {
        const { result, requests } = await callTool(() => new Response('secret-token SQL failure', { status }), 'create_transaction', ordinaryCommand);
        assert.equal(structured(result).status, status === 500 || status === 200 ? 'outcome_unknown' : 'error');
        assert.equal(JSON.stringify(result).includes('secret-token'), false);
        assert.equal(requests.length, 1);
        if (status === 410)
            assert.equal(structured(result).code, 'result_unavailable');
    }
});
// 驗證完整分頁的候選清單不遺漏後續卡片。
test('complete card pagination includes every candidate without exposing notes', async () => {
    const { result, requests } = await callTool(url => {
        if (url.pathname !== '/api/credit-cards')
            return references(url);
        const page = Number(url.searchParams.get('page'));
        return Response.json({ items: [{ id: page, bankName: '銀行', lastFourDigits: '1234', notes: 'private-note' }], total: 2, page, pageSize: 1 });
    }, 'prepare_bookkeeping_entry', { intent: 'credit_card_purchase', amount: 150, description: '午餐' });
    assert.equal(structured(result).status, 'needs_input');
    assert.equal(structured(result).candidates.length, 2);
    assert.equal(requests.filter(request => request.url.pathname === '/api/credit-cards').length, 2);
    assert.equal(JSON.stringify(result).includes('private-note'), false);
});
// 驗證完整摘要不以目前頁面重新加總，且錯誤的巢狀資料不會通過驗證。
test('consumption preserves complete summary and coverage and rejects malformed nested fields', async () => {
    const payload = {
        items: [{ sourceType: 'credit_card', sourceId: 7, date: '2026-09-05', amount: 24000, description: '手機' }],
        total: 125, page: 1, pageSize: 1, basis: 'consumption',
        period: { startDate: '2026-09-01', endDate: '2026-09-05' }, timeZoneId: 'Asia/Taipei',
        filters: { source: 'all', categoryId: null, search: null },
        summary: { count: 125, totalAmount: 50000, ordinaryAmount: 26000, creditCardAmount: 24000 },
        coverage: { creditCardCategoriesAvailable: false, categoryNote: '信用卡無分類', recognitionNote: '購買日全額', completenessNote: '僅已記錄資料' }, warnings: [],
    };
    for (const extra of [{}, { summary: { ...payload.summary, totalAmount: '50000' } }, { items: [{ ...payload.items[0], sourceId: null }] }, { coverage: {} }, { period: { startDate: '2026-02-30', endDate: '2026-09-05' } }]) {
        const { result } = await callTool(() => Response.json({ ...payload, ...extra }), 'search_consumption', payload.period);
        assert.equal(structured(result).status, Object.keys(extra).length ? 'error' : 'ok');
        if (!Object.keys(extra).length)
            assert.deepEqual(structured(result).summary, payload.summary);
    }
});
// 驗證真正的子程序 stdio 協定、Bearer 認證與公開輸出 schema。
test('real stdio tools/list and tools/call validate output and reject invalid input', async () => {
    let requests = 0;
    const http = createHttpServer((request, response) => {
        requests += 1;
        assert.equal(request.headers.authorization, 'Bearer stdio-test-token');
        response.setHeader('Content-Type', 'application/json');
        response.end(JSON.stringify({ currentDate: '2026-09-05', timeZoneId: 'Asia/Taipei' }));
    });
    await new Promise(resolve => http.listen(0, '127.0.0.1', resolve));
    const address = http.address();
    assert.ok(address && typeof address !== 'string');
    const transport = new StdioClientTransport({
        command: process.execPath,
        args: [join(dirname(fileURLToPath(import.meta.url)), 'index.js')],
        env: { MYEXPENSES_API_URL: `http://127.0.0.1:${address.port}`, MYEXPENSES_API_TOKEN: 'stdio-test-token' },
        stderr: 'pipe',
    });
    const client = new Client({ name: 'stdio-test', version: '1.0.0' });
    try {
        await client.connect(transport);
        const { tools } = await client.listTools();
        assert.equal(tools.length, 14);
        const context = await client.callTool({ name: 'get_bookkeeping_context' });
        assert.equal(context.structuredContent.status, 'ok');
        for (const tool of tools) {
            assert.equal(matchesSchema(tool.outputSchema, { status: 'ok' }), false, tool.name);
            assert.deepEqual(tool.annotations, {
                readOnlyHint: !['create_transaction', 'create_credit_card_transaction', 'undo_transaction'].includes(tool.name),
                destructiveHint: tool.name === 'undo_transaction',
                idempotentHint: true,
            }, tool.name);
        }
        const createSchema = tools.find(tool => tool.name === 'create_transaction').inputSchema;
        for (const field of ['category', 'categoryCode', 'paymentMethod', 'paymentMethodCode']) {
            assert.equal(matchesSchema(createSchema, { amount: 150, description: '午餐', [field]: '現有名稱' }), true, field);
        }
        const preparationSchema = tools.find(tool => tool.name === 'prepare_bookkeeping_entry').outputSchema;
        assert.equal(matchesSchema(preparationSchema, { status: 'ready', requestId: ordinaryCommand.requestId, targetTool: 'create_transaction', arguments: {}, appliedDefaults: [] }), false);
        const invalid = await client.callTool({ name: 'create_transaction', arguments: { ...ordinaryCommand, notes: 42 } });
        assert.equal(invalid.isError, true);
        const missing = await client.callTool({ name: 'create_transaction' });
        assert.equal(missing.structuredContent.status, 'needs_preparation');
        assert.equal(requests, 1);
    }
    finally {
        await client.close();
        await transport.close();
        await new Promise((resolve, reject) => http.close(error => error ? reject(error) : resolve()));
    }
});
// 驗證公開卡片分頁工具不接受頁碼錯置或資料數量不足。
test('list_credit_cards rejects inconsistent page contents', async () => {
    for (const payload of [
        { items: [], total: 2, page: 1, pageSize: 20 },
        { items: [], total: 0, page: 2, pageSize: 20 },
        { items: [{ id: 4, bankName: '銀行', lastFourDigits: '1234' }, { id: 4, bankName: '銀行', lastFourDigits: '1234' }], total: 2, page: 1, pageSize: 20 },
    ]) {
        const { result } = await callTool(() => Response.json(payload), 'list_credit_cards');
        assert.equal(structured(result).status, 'error');
    }
});
// 驗證日期預設需要有效後端時區，不使用空白 context 繼續準備。
test('preparation rejects a context without a usable timezone', async () => {
    const { result } = await callTool(url => url.pathname === '/api/agent/context'
        ? Response.json({ currentDate: '2026-09-05', timeZoneId: ' ' }) : references(url), 'prepare_bookkeeping_entry', { intent: 'ordinary', amount: 150, description: '午餐' });
    assert.equal(structured(result).status, 'error');
});
// 驗證實際 AbortSignal 逾時有界，且原命令仍可供核對與重試。
test('bounded request timeout aborts the write and preserves its envelope', async () => {
    const { result, requests } = await callTool((_url, init) => new Promise((_resolve, reject) => {
        init.signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true });
    }), 'create_transaction', ordinaryCommand);
    assert.equal(structured(result).status, 'outcome_unknown');
    assert.equal(structured(result).code, 'timeout');
    assert.deepEqual(structured(result).arguments, ordinaryCommand);
    assert.equal(requests.length, 1);
});
test('tools/list exposes the enhanced tool contract and output schemas', async () => {
    const requests = [];
    const apiClient = new ApiClient('http://api.test', 'test-token', 500, mockFetch(() => new Response('{}'), requests));
    const server = createServer(apiClient);
    const client = new Client({ name: 'contract-test', version: '1.0.0' }, { capabilities: {} });
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    const response = await client.listTools();
    const names = response.tools.map(tool => tool.name);
    assert.ok(names.includes('prepare_bookkeeping_entry'));
    assert.ok(names.includes('search_consumption'));
    assert.ok(names.includes('create_credit_card_transaction'));
    assert.ok(response.tools.every(tool => tool.outputSchema));
    await client.close();
    await server.close();
});
test('prepare_bookkeeping_entry fixes backend date and reference IDs without writing', async () => {
    const { result, requests } = await callTool((url) => {
        if (url.pathname === '/api/agent/context') {
            return Response.json({ currentDate: '2026-09-05', timeZoneId: 'Asia/Taipei' });
        }
        if (url.pathname === '/api/categories') {
            return Response.json([{ id: 7, name: '其他', type: 'Expense', systemCode: 'other-expense' }]);
        }
        if (url.pathname === '/api/payment-methods') {
            return Response.json([{ id: 3, name: '現金', systemCode: 'cash' }]);
        }
        return Response.json({ error: 'unexpected' }, { status: 500 });
    }, 'prepare_bookkeeping_entry', { intent: 'ordinary', amount: 150, description: '午餐' });
    const data = structured(result);
    assert.equal(data.status, 'ready');
    const command = data.arguments;
    assert.equal(command.requestId, '11111111-1111-4111-8111-111111111111');
    assert.equal(command.date, '2026-09-05');
    assert.equal(command.categoryId, 7);
    assert.equal(command.paymentMethodId, 3);
    assert.equal(requests.filter(request => request.init.method === 'POST').length, 0);
});
test('create_transaction refuses unprepared input and uses requestId for write', async () => {
    const missing = await callTool(() => Response.json({}), 'create_transaction', {
        amount: 150,
        description: '午餐',
    });
    assert.equal(structured(missing.result).status, 'needs_preparation');
    assert.equal(missing.requests.length, 0);
    const executed = await callTool((url, init) => {
        assert.equal(url.pathname, '/api/transactions');
        assert.equal(init.headers && init.headers['Idempotency-Key'], '11111111-1111-4111-8111-111111111111');
        return Response.json({
            id: 9,
            type: 'Expense',
            amount: 150,
            date: '2026-09-05',
            description: '午餐',
            categoryId: 7,
            paymentMethodId: 3,
            category: { id: 7, name: '其他' },
            paymentMethod: { id: 3, name: '現金' },
        }, { status: 201 });
    }, 'create_transaction', {
        requestId: '11111111-1111-4111-8111-111111111111',
        amount: 150,
        description: '午餐',
        date: '2026-09-05',
        type: 'Expense',
        categoryId: 7,
        paymentMethodId: 3,
    });
    assert.equal(structured(executed.result).status, 'created');
    assert.equal(structured(executed.result).transaction.id, 9);
});
test('credit purchase preparation asks for a card when several cards exist', async () => {
    const { result } = await callTool((url) => {
        if (url.pathname === '/api/agent/context')
            return Response.json({ currentDate: '2026-09-05', timeZoneId: 'Asia/Taipei' });
        if (url.pathname === '/api/credit-cards') {
            return Response.json({
                items: [
                    { id: 1, bankName: '甲銀行', lastFourDigits: '1111' },
                    { id: 2, bankName: '乙銀行', lastFourDigits: '2222' },
                ],
                total: 2,
                page: 1,
                pageSize: 100,
            });
        }
        return Response.json({}, { status: 500 });
    }, 'prepare_bookkeeping_entry', {
        intent: 'credit_card_purchase',
        amount: 24000,
        description: '手機',
    });
    const data = structured(result);
    assert.equal(data.status, 'needs_input');
    assert.deepEqual(data.missingFields, ['card']);
    assert.equal(data.candidates.length, 2);
});
test('credit purchase execution sends only standalone installment fields and preserves replay status', async () => {
    const { result, requests } = await callTool((url, init) => {
        assert.equal(url.pathname, '/api/installments');
        const body = JSON.parse(String(init.body));
        assert.deepEqual(body, {
            cardId: 4,
            totalAmount: 24000,
            periods: 12,
            purchaseDate: '2026-09-05',
            description: '手機',
        });
        assert.equal('transactionId' in body, false);
        return new Response(JSON.stringify({
            id: 8,
            cardId: 4,
            totalAmount: 24000,
            periods: 12,
            perPeriod: 2000,
            purchaseDate: '2026-09-05',
            description: '手機',
            payments: [],
        }), { status: 201, headers: { 'Content-Type': 'application/json', 'X-Idempotent-Replay': 'true' } });
    }, 'create_credit_card_transaction', {
        requestId: '11111111-1111-4111-8111-111111111111',
        cardId: 4,
        totalAmount: 24000,
        periods: 12,
        purchaseDate: '2026-09-05',
        description: '手機',
    });
    assert.equal(structured(result).status, 'replayed');
    assert.equal(requests.length, 1);
});
test('repayment preparation normalizes living category and avoids duplicate marker', async () => {
    const { result } = await callTool((url) => {
        if (url.pathname === '/api/agent/context')
            return Response.json({ currentDate: '2026-09-05', timeZoneId: 'Asia/Taipei' });
        if (url.pathname === '/api/categories')
            return Response.json([{ id: 8, name: '生活改名', type: 'Expense', systemCode: 'living' }]);
        if (url.pathname === '/api/payment-methods')
            return Response.json([{ id: 3, name: '現金', systemCode: 'cash' }]);
        return Response.json({}, { status: 500 });
    }, 'prepare_bookkeeping_entry', {
        intent: 'credit_card_repayment',
        amount: 3000,
        description: '信用卡帳單 9 月',
    });
    const data = structured(result);
    assert.equal(data.status, 'ready');
    const command = data.arguments;
    assert.equal(command.description, '信用卡帳單 9 月');
    assert.equal(command.categoryId, 8);
    assert.equal(command.date, '2026-09-05');
});
test('write timeout returns outcome_unknown with the original command identity', async () => {
    const { result } = await callTool(() => {
        throw new Error('connection reset');
    }, 'create_transaction', {
        requestId: '11111111-1111-4111-8111-111111111111',
        amount: 10,
        description: 'timeout',
        date: '2026-09-05',
        type: 'Expense',
        categoryId: 1,
        paymentMethodId: 1,
    });
    const data = structured(result);
    assert.equal(data.status, 'outcome_unknown');
    assert.equal(data.requestId, '11111111-1111-4111-8111-111111111111');
});
test('search reads never turn API failures into empty success', async () => {
    const { result } = await callTool(() => Response.json({ detail: 'secret raw body' }, { status: 503 }), 'search_consumption', {
        startDate: '2026-09-01',
        endDate: '2026-09-30',
    });
    const data = structured(result);
    assert.equal(result.isError, true);
    assert.equal(data.status, 'error');
    assert.equal(data.code, 'backend_unavailable');
    assert.equal(data.message, '記帳 API 暫時無法使用');
    assert.equal(JSON.stringify(result).includes('secret raw body'), false);
});
test('stdio entry point rejects missing token with a non-zero exit', async () => {
    const script = join(dirname(fileURLToPath(import.meta.url)), 'index.js');
    const child = spawn(process.execPath, [script], {
        env: { ...process.env, MYEXPENSES_API_TOKEN: '' },
        stdio: ['pipe', 'pipe', 'pipe'],
    });
    let stderr = '';
    child.stderr.on('data', chunk => { stderr += String(chunk); });
    const exitCode = await new Promise(resolve => child.on('exit', resolve));
    assert.equal(exitCode, 1);
    assert.match(stderr, /MYEXPENSES_API_TOKEN/);
});
