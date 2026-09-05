#!/usr/bin/env node

import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
  type CallToolResult,
} from '@modelcontextprotocol/sdk/types.js';
import { ApiClient, ApiError } from './api-client.js';
import { matchesSchema, outputSchema, arraySchema, pageSchema, categorySchema, paymentSchema, cardSchema, transactionSchema, creditSchema } from './schemas.js';

type JsonObject = Record<string, unknown>;
type ToolStatus = 'ok' | 'ready' | 'created' | 'replayed' | 'needs_input' | 'needs_preparation' | 'error' | 'outcome_unknown' | 'configuration_error';
type UUIDFactory = () => string;

interface ContextResponse {
  currentDate: string;
  timeZoneId: string;
}

interface CategoryReference {
  id: number;
  name: string;
  type: unknown;
  systemCode?: string | null;
  icon?: string | null;
}

interface PaymentMethodReference {
  id: number;
  name: string;
  systemCode?: string | null;
  icon?: string | null;
}

interface CreditCardReference {
  id: number;
  bankName: string;
  lastFourDigits: string;
  cardNetwork?: string | null;
}

interface PrepareInput extends JsonObject {
  intent?: unknown;
  amount?: unknown;
  totalAmount?: unknown;
  description?: unknown;
  date?: unknown;
  type?: unknown;
  category?: unknown;
  categoryCode?: unknown;
  categoryId?: unknown;
  paymentMethod?: unknown;
  paymentMethodCode?: unknown;
  paymentMethodId?: unknown;
  notes?: unknown;
  card?: unknown;
  cardId?: unknown;
  periods?: unknown;
  installmentRequested?: unknown;
}

interface ResolveResult<T> {
  value: T | null;
  result?: CallToolResult;
}

interface ResolvedDate {
  date: string;
  context: ContextResponse | null;
  defaulted: boolean;
}

class ToolInputError extends Error {
  public readonly code: string;

  /** 建立工具輸入驗證錯誤。 */
  public constructor(code: string, message: string) {
    super(message);
    this.name = 'ToolInputError';
    this.code = code;
  }
}

const WRITE_TOOLS = new Set(['create_transaction', 'create_credit_card_transaction', 'undo_transaction']);

/** 建立 MCP 工具輸入 JSON Schema。 */
function objectSchema(properties: JsonObject, required: string[] = []): JsonObject {
  return {
    type: 'object',
    properties,
    required,
    additionalProperties: false,
  };
}

/** 建立成功或正常追問的 MCP 回應，保留文字與結構化資料。 */
function toolResult(status: ToolStatus, data: JsonObject, text: string, isError = false): CallToolResult {
  return {
    ...(isError ? { isError: true } : {}),
    content: [{ type: 'text', text }],
    structuredContent: { ...data, status },
  };
}

/** 建立欄位追問結果，不執行任何寫入。 */
function needsInput(
  message: string,
  missingFields: string[] = [],
  fieldErrors: JsonObject = {},
  extra: JsonObject = {},
): CallToolResult {
  return toolResult('needs_input', {
    message,
    ...(missingFields.length > 0 ? { missingFields } : {}),
    ...(Object.keys(fieldErrors).length > 0 ? { fieldErrors } : {}),
    ...extra,
  }, message);
}

/** 建立設定缺失結果，明確區分於查無財務資料。 */
function configurationError(code: string, message: string, extra: JsonObject = {}): CallToolResult {
  return toolResult('configuration_error', { code, message, ...extra }, message, true);
}

/** 建立 MCP 工具的安全失敗結果。 */
function errorResult(status: Extract<ToolStatus, 'error' | 'needs_preparation' | 'outcome_unknown'>, code: string, message: string, extra: JsonObject = {}): CallToolResult {
  return toolResult(status, { code, message, ...extra }, message, true);
}

/** 判斷未知值是否為 JSON object。 */
function isRecord(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** 將 API 分頁或陣列格式統一為 reference items。 */
function listItems<T>(value: unknown, complete = false): T[] {
  if (Array.isArray(value)) return value as T[];
  if (isRecord(value) && Array.isArray(value.items)) {
    if (complete && value.total !== value.items.length) throw new ToolInputError('invalid_api_response', 'API 參考資料集合不完整');
    return value.items as T[];
  }
  throw new ToolInputError('invalid_api_response', 'API 回應缺少有效 items');
}

/** 取出一般正整數欄位，拒絕浮點數與字串猜測。 */
function positiveInteger(value: unknown, field: string): number {
  if (typeof value !== 'number' || !Number.isInteger(value) || value <= 0) {
    throw new ToolInputError('invalid_input', `${field} 必須是正整數`);
  }
  return value;
}

/** 取出有限正金額，拒絕 NaN、Infinity、零與負數。 */
function positiveAmount(value: unknown, field = 'amount'): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) {
    throw new ToolInputError('invalid_amount', `${field} 必須是有限且大於零的數字`);
  }
  return value;
}

/** 驗證 ISO DateOnly 字串且確認日期實際存在。 */
function validDate(value: unknown): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

/** 驗證 API 所需的 UUID requestId。 */
function validUuid(value: unknown): value is string {
  return typeof value === 'string'
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}

/** 將交易型別輸入正規化為 API 的 enum 名稱。 */
function transactionType(value: unknown): 'Income' | 'Expense' | null {
  if (typeof value === 'number') {
    if (value === 0) return 'Income';
    if (value === 1) return 'Expense';
  }
  if (typeof value !== 'string') return null;
  const normalized = value.trim().toLowerCase();
  if (normalized === 'income' || normalized === '收入') return 'Income';
  if (normalized === 'expense' || normalized === '支出') return 'Expense';
  return null;
}

/** 將分類 enum 輸入正規化為交易型別。 */
function categoryType(value: unknown): 'Income' | 'Expense' | null {
  return transactionType(value);
}

/** 將字串選擇器轉成不區分大小寫的比對值。 */
function selectorText(value: unknown): string | null {
  if (typeof value !== 'string' || value.trim() === '') return null;
  return value.trim().toLocaleLowerCase();
}

/** 將 API query 參數安全編碼為 URL path。 */
function queryPath(path: string, values: Record<string, unknown>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    if (value === undefined || value === null || value === '') continue;
    query.set(key, String(value));
  }
  const encoded = query.toString();
  return encoded ? `${path}?${encoded}` : path;
}

/** 取得時區 context 並驗證後端回應欄位。 */
async function loadContext(client: ApiClient): Promise<ContextResponse> {
  const value = await client.get<unknown>('/api/agent/context');
  if (!isRecord(value) || !validDate(value.currentDate) || typeof value.timeZoneId !== 'string' || !value.timeZoneId.trim()) {
    throw new ToolInputError('invalid_api_response', 'agent context 回應格式無效');
  }
  return { currentDate: value.currentDate, timeZoneId: value.timeZoneId };
}

/** 讀取所有分類參考資料。 */
async function loadCategories(client: ApiClient): Promise<CategoryReference[]> {
  const items = listItems<CategoryReference>(await client.get('/api/categories?all=true'), true);
  requireApiSchema(arraySchema(categorySchema), items);
  return items;
}

/** 讀取所有付款方式參考資料。 */
async function loadPaymentMethods(client: ApiClient): Promise<PaymentMethodReference[]> {
  const items = listItems<PaymentMethodReference>(await client.get('/api/payment-methods?all=true'), true);
  requireApiSchema(arraySchema(paymentSchema), items);
  return items;
}

/** 拒絕無效 API 資料，避免宣告成功或使用不完整參考資料。 */
function requireApiSchema(schema: JsonObject, value: unknown): void {
  if (!matchesSchema(schema, value)) throw new ToolInputError('invalid_api_response', 'API 回應格式無效，無法確認結果');
}

/** 讀取完整信用卡集合，避免單卡判斷受 API 分頁影響。 */
async function loadAllCreditCards(client: ApiClient): Promise<CreditCardReference[]> {
  let pageSize = 100;
  const cards: CreditCardReference[] = [];
  let page = 1;
  let total: number | undefined;
  do {
    const response = await client.get<unknown>(queryPath('/api/credit-cards', { page, pageSize }));
    requireApiSchema(pageSchema(cardSchema), response);
    const data = response as { items: CreditCardReference[]; total: number; page: number; pageSize: number };
    if (data.page !== page || (total !== undefined && (data.total !== total || data.pageSize !== pageSize))
      || data.items.length !== Math.min(data.pageSize, Math.max(0, data.total - (page - 1) * data.pageSize))) {
      throw new ToolInputError('invalid_api_response', '信用卡 API 回應格式無效');
    }
    total = data.total;
    pageSize = data.pageSize;
    cards.push(...data.items);
    if (new Set(cards.map(card => card.id)).size !== cards.length) throw new ToolInputError('invalid_api_response', '信用卡分頁包含重複資料');
    page += 1;
  } while (cards.length < total && page <= 100);
  if (cards.length !== total) throw new ToolInputError('invalid_api_response', '信用卡分頁資料不完整');
  return cards;
}

/** 將分類轉成不含多餘欄位的候選資料。 */
function categoryCandidate(category: CategoryReference): JsonObject {
  return {
    id: category.id,
    name: category.name,
    type: categoryType(category.type) ?? category.type,
    systemCode: category.systemCode ?? null,
    icon: category.icon ?? null,
  };
}

/** 將付款方式轉成不含多餘欄位的候選資料。 */
function paymentMethodCandidate(paymentMethod: PaymentMethodReference): JsonObject {
  return {
    id: paymentMethod.id,
    name: paymentMethod.name,
    systemCode: paymentMethod.systemCode ?? null,
    icon: paymentMethod.icon ?? null,
  };
}

/** 將信用卡轉成可供使用者選擇的候選資料。 */
function creditCardCandidate(card: CreditCardReference): JsonObject {
  return {
    id: card.id,
    bankName: card.bankName,
    lastFourDigits: card.lastFourDigits,
    cardNetwork: card.cardNetwork ?? null,
  };
}

/** 判斷分類是否符合指定 selector。 */
function categoryMatches(category: CategoryReference, selector: string): boolean {
  const value = selectorText(selector);
  return value !== null
    && (selectorText(category.name) === value || selectorText(category.systemCode) === value);
}

/** 判斷付款方式是否符合指定 selector。 */
function paymentMethodMatches(paymentMethod: PaymentMethodReference, selector: string): boolean {
  const value = selectorText(selector);
  return value !== null
    && (selectorText(paymentMethod.name) === value || selectorText(paymentMethod.systemCode) === value);
}

/** 判斷信用卡是否符合 ID、銀行名稱或末四碼 selector。 */
function creditCardMatches(card: CreditCardReference, selector: string): boolean {
  const value = selectorText(selector);
  return value !== null && [
    String(card.id).toLocaleLowerCase(),
    selectorText(card.bankName),
    selectorText(card.lastFourDigits),
  ].includes(value);
}

/** 解析分類 ID／名稱／systemCode，保留未知與歧義為明確追問。 */
function resolveCategory(categories: CategoryReference[], args: PrepareInput): ResolveResult<CategoryReference> {
  const categoryId = args.categoryId;
  if (categoryId !== undefined && (typeof categoryId !== 'number' || !Number.isInteger(categoryId) || categoryId <= 0)) {
    return { value: null, result: needsInput('categoryId 必須是正整數', [], { categoryId: 'invalid' }) };
  }
  const selectors = [args.category, args.categoryCode].filter(value => value !== undefined && value !== null);
  if (selectors.length > 1 && !categories.some(category => selectors.every(selector => typeof selector === 'string' && categoryMatches(category, selector)))) {
    return { value: null, result: needsInput('category 與 categoryCode 指向不同選擇器', [], { category: 'conflict' }) };
  }
  const selector = selectors[0];
  let matches: CategoryReference[];
  if (typeof categoryId === 'number') {
    matches = categories.filter(category => category.id === categoryId);
    if (matches.length === 0) return { value: null, result: needsInput('找不到指定分類', [], { categoryId: 'not_found' }) };
    if (!selectors.every(value => typeof value === 'string' && categoryMatches(matches[0], value))) {
      return { value: null, result: needsInput('categoryId 與分類 selector 不一致', [], { category: 'conflict' }) };
    }
  } else if (selector === undefined) {
    return { value: null };
  } else if (typeof selector !== 'string' || selector.trim() === '') {
    return { value: null, result: needsInput('分類 selector 不可為空', [], { category: 'invalid' }) };
  } else {
    matches = categories.filter(category => selectors.every(value => typeof value === 'string' && categoryMatches(category, value)));
    if (matches.length === 0) return { value: null, result: needsInput('找不到指定分類', [], { category: 'not_found' }) };
  }
  if (matches.length > 1) {
    return {
      value: null,
      result: needsInput('分類 selector 不唯一，請選擇一個分類', [], { category: 'ambiguous' }, {
        candidates: matches.map(categoryCandidate),
      }),
    };
  }
  return { value: matches[0] };
}

/** 解析付款方式 ID／名稱／systemCode，保留未知與歧義為明確追問。 */
function resolvePaymentMethod(paymentMethods: PaymentMethodReference[], args: PrepareInput): ResolveResult<PaymentMethodReference> {
  const paymentMethodId = args.paymentMethodId;
  if (paymentMethodId !== undefined && (typeof paymentMethodId !== 'number' || !Number.isInteger(paymentMethodId) || paymentMethodId <= 0)) {
    return { value: null, result: needsInput('paymentMethodId 必須是正整數', [], { paymentMethodId: 'invalid' }) };
  }
  const selectors = [args.paymentMethod, args.paymentMethodCode].filter(value => value !== undefined && value !== null);
  if (selectors.length > 1 && !paymentMethods.some(method => selectors.every(selector => typeof selector === 'string' && paymentMethodMatches(method, selector)))) {
    return { value: null, result: needsInput('paymentMethod 與 paymentMethodCode 指向不同選擇器', [], { paymentMethod: 'conflict' }) };
  }
  const selector = selectors[0];
  let matches: PaymentMethodReference[];
  if (typeof paymentMethodId === 'number') {
    matches = paymentMethods.filter(paymentMethod => paymentMethod.id === paymentMethodId);
    if (matches.length === 0) return { value: null, result: needsInput('找不到指定付款方式', [], { paymentMethodId: 'not_found' }) };
    if (!selectors.every(value => typeof value === 'string' && paymentMethodMatches(matches[0], value))) {
      return { value: null, result: needsInput('paymentMethodId 與付款方式 selector 不一致', [], { paymentMethod: 'conflict' }) };
    }
  } else if (selector === undefined) {
    return { value: null };
  } else if (typeof selector !== 'string' || selector.trim() === '') {
    return { value: null, result: needsInput('付款方式 selector 不可為空', [], { paymentMethod: 'invalid' }) };
  } else {
    matches = paymentMethods.filter(paymentMethod => selectors.every(value => typeof value === 'string' && paymentMethodMatches(paymentMethod, value)));
    if (matches.length === 0) return { value: null, result: needsInput('找不到指定付款方式', [], { paymentMethod: 'not_found' }) };
  }
  if (matches.length > 1) {
    return {
      value: null,
      result: needsInput('付款方式 selector 不唯一，請選擇一個付款方式', [], { paymentMethod: 'ambiguous' }, {
        candidates: matches.map(paymentMethodCandidate),
      }),
    };
  }
  return { value: matches[0] };
}

/** 解析信用卡 selector，支援 ID、銀行名稱及末四碼。 */
function resolveCreditCard(cards: CreditCardReference[], args: PrepareInput): ResolveResult<CreditCardReference> {
  const cardId = args.cardId;
  if (cardId !== undefined && (typeof cardId !== 'number' || !Number.isInteger(cardId) || cardId <= 0)) {
    return { value: null, result: needsInput('cardId 必須是正整數', [], { cardId: 'invalid' }) };
  }
  const selector = args.card;
  if (cardId !== undefined) {
    const matches = cards.filter(card => card.id === cardId);
    if (matches.length === 0) return { value: null, result: needsInput('找不到指定信用卡', [], { cardId: 'not_found' }) };
    if (selector !== undefined && (typeof selector !== 'string' || !creditCardMatches(matches[0], selector))) {
      return { value: null, result: needsInput('cardId 與信用卡 selector 不一致', [], { card: 'conflict' }) };
    }
    return { value: matches[0] };
  }
  if (selector === undefined) {
    if (cards.length === 1) return { value: cards[0] };
    if (cards.length === 0) return { value: null, result: configurationError('credit_card_not_configured', '目前沒有可用信用卡') };
    return {
      value: null,
      result: needsInput('目前有多張信用卡，請指定卡片', ['card'], {}, {
        candidates: cards.map(creditCardCandidate),
      }),
    };
  }
  if (typeof selector !== 'string') return { value: null, result: needsInput('信用卡 selector 必須是文字或 cardId', [], { card: 'invalid' }) };
  const matches = cards.filter(card => creditCardMatches(card, selector));
  if (matches.length === 0) return { value: null, result: needsInput('找不到指定信用卡', [], { card: 'not_found' }) };
  if (matches.length > 1) {
    return {
      value: null,
      result: needsInput('信用卡 selector 不唯一，請選擇一張卡', [], { card: 'ambiguous' }, {
        candidates: matches.map(creditCardCandidate),
      }),
    };
  }
  return { value: matches[0] };
}

/** 解析日期欄位，缺省時使用 API 回傳的系統時區日期。 */
async function resolveDate(args: PrepareInput, client: ApiClient): Promise<ResolvedDate | CallToolResult> {
  if (args.date !== undefined && args.date !== null) {
    return validDate(args.date)
      ? { date: args.date, context: null, defaulted: false }
      : needsInput('date 必須是有效的 YYYY-MM-DD 日期', [], { date: 'invalid' });
  }
  const context = await loadContext(client);
  return { date: context.currentDate, context, defaulted: true };
}

/** 判斷日期解析是否成功，協助 TypeScript 排除 MCP 回應型別。 */
function isResolvedDate(value: ResolvedDate | CallToolResult): value is ResolvedDate {
  return isRecord(value) && typeof value.date === 'string' && typeof value.defaulted === 'boolean';
}

/** 驗證準備輸入的 intent，避免未知意圖被靜默轉換。 */
function intentValue(value: unknown): 'ordinary' | 'credit_card_purchase' | 'credit_card_repayment' | null {
  if (value === 'ordinary' || value === 'credit_card_purchase' || value === 'credit_card_repayment') return value;
  return null;
}

/** 準備普通收入／支出的固定 canonical 命令。 */
async function prepareOrdinary(args: PrepareInput, client: ApiClient, uuid: UUIDFactory): Promise<CallToolResult> {
  const amountValue = args.amount;
  if (amountValue === undefined || amountValue === null) return needsInput('請提供金額', ['amount']);
  let amount: number;
  try {
    amount = positiveAmount(amountValue);
  } catch (error) {
    return needsInput((error as Error).message, [], { amount: 'invalid' });
  }
  const description = typeof args.description === 'string' ? args.description.trim() : '';
  if (!description) return needsInput('請提供交易描述', ['description']);
  const explicitType = args.type === undefined || args.type === null ? null : transactionType(args.type);
  if (args.type !== undefined && args.type !== null && explicitType === null) {
    return needsInput('type 必須是 income 或 expense', [], { type: 'invalid' });
  }
  const dateResult = await resolveDate(args, client);
  if (!isResolvedDate(dateResult)) return dateResult;
  const [categories, paymentMethods] = await Promise.all([loadCategories(client), loadPaymentMethods(client)]);
  const categoryResult = resolveCategory(categories, args);
  if (categoryResult.result) return categoryResult.result;
  let category = categoryResult.value;
  const defaults: string[] = [];
  if (!category) {
    if (explicitType === 'Income') return needsInput('收入交易需要明確分類', ['category']);
    const matches = categories.filter(item => selectorText(item.systemCode) === 'other-expense');
    category = matches.length === 1 && categoryType(matches[0].type) === 'Expense' ? matches[0] : null;
    if (!category) return configurationError('default_category_missing', '找不到預設分類 other-expense');
    defaults.push('category=other-expense');
  }
  const resolvedCategoryType = categoryType(category.type);
  if (!resolvedCategoryType) return configurationError('invalid_category_type', '分類參考資料的 type 無效');
  if (explicitType && explicitType !== resolvedCategoryType) {
    return needsInput('交易 type 與分類型別不一致', [], { type: 'category_conflict', category: 'type_conflict' });
  }
  const finalType = explicitType ?? resolvedCategoryType;
  const paymentResult = resolvePaymentMethod(paymentMethods, args);
  if (paymentResult.result) return paymentResult.result;
  let paymentMethod = paymentResult.value;
  if (!paymentMethod) {
    const matches = paymentMethods.filter(item => selectorText(item.systemCode) === 'cash');
    paymentMethod = matches.length === 1 ? matches[0] : null;
    if (!paymentMethod) return configurationError('default_payment_method_missing', '找不到預設付款方式 cash');
    defaults.push('paymentMethod=cash');
  }
  if (selectorText(paymentMethod.systemCode) === 'credit-card') {
    return needsInput('普通交易不可使用信用卡付款方式，請改用 credit_card_purchase', [], {
      paymentMethod: 'credit_card_workflow_required',
    });
  }
  if (dateResult.defaulted) defaults.push(`date=${dateResult.date}`);
  const requestId = uuid();
  const command = {
    requestId,
    amount,
    description,
    date: dateResult.date,
    type: finalType,
    categoryId: category.id,
    paymentMethodId: paymentMethod.id,
    ...(typeof args.notes === 'string' && args.notes.trim() ? { notes: args.notes.trim() } : {}),
  };
  return toolResult('ready', {
    requestId,
    targetTool: 'create_transaction',
    arguments: command,
    appliedDefaults: defaults,
    timeZoneId: dateResult.context?.timeZoneId ?? null,
  }, `已準備普通${finalType === 'Income' ? '收入' : '支出'}命令，requestId=${requestId}`);
}

/** 準備信用卡帳單繳款的 living 分類普通命令。 */
async function prepareCreditCardRepayment(args: PrepareInput, client: ApiClient, uuid: UUIDFactory): Promise<CallToolResult> {
  const amountValue = args.amount;
  if (amountValue === undefined || amountValue === null) return needsInput('請提供卡費繳款金額', ['amount']);
  let amount: number;
  try {
    amount = positiveAmount(amountValue);
  } catch (error) {
    return needsInput((error as Error).message, [], { amount: 'invalid' });
  }
  const explicitType = args.type === undefined || args.type === null ? null : transactionType(args.type);
  if (args.type !== undefined && args.type !== null && explicitType !== 'Expense') {
    return needsInput('卡費繳款必須是 expense', [], { type: 'conflict' });
  }
  const dateResult = await resolveDate(args, client);
  if (!isResolvedDate(dateResult)) return dateResult;
  const [categories, paymentMethods] = await Promise.all([loadCategories(client), loadPaymentMethods(client)]);
  const categoryResult = resolveCategory(categories, args);
  if (categoryResult.result) return categoryResult.result;
  let category = categoryResult.value;
  const defaults: string[] = [];
  if (!category) {
    const matches = categories.filter(item => selectorText(item.systemCode) === 'living');
    category = matches.length === 1 ? matches[0] : null;
    if (!category) return configurationError('repayment_category_missing', '找不到卡費繳款需要的 living 分類');
    defaults.push('category=living');
  }
  if (selectorText(category.systemCode) !== 'living') {
    return needsInput('卡費繳款必須使用 living 分類', [], { category: 'repayment_category_required' });
  }
  if (categoryType(category.type) !== 'Expense') return configurationError('invalid_category_type', 'living 分類必須是支出型別');
  const paymentResult = resolvePaymentMethod(paymentMethods, args);
  if (paymentResult.result) return paymentResult.result;
  let paymentMethod = paymentResult.value;
  if (!paymentMethod) {
    const matches = paymentMethods.filter(item => selectorText(item.systemCode) === 'cash');
    paymentMethod = matches.length === 1 ? matches[0] : null;
    if (!paymentMethod) return configurationError('default_payment_method_missing', '找不到預設付款方式 cash');
    defaults.push('paymentMethod=cash');
  }
  if (selectorText(paymentMethod.systemCode) === 'credit-card') {
    return needsInput('卡費繳款不可用信用卡付款方式', [], { paymentMethod: 'credit_card_repayment_conflict' });
  }
  const originalDescription = typeof args.description === 'string' ? args.description.trim() : '';
  const description = originalDescription.includes('信用卡帳單')
    ? originalDescription
    : originalDescription ? `信用卡帳單 ${originalDescription}` : '信用卡帳單';
  if (!originalDescription) defaults.push('description=信用卡帳單');
  if (dateResult.defaulted) defaults.push(`date=${dateResult.date}`);
  const requestId = uuid();
  const command = {
    requestId,
    amount,
    description,
    date: dateResult.date,
    type: 'Expense',
    categoryId: category.id,
    paymentMethodId: paymentMethod.id,
    ...(typeof args.notes === 'string' && args.notes.trim() ? { notes: args.notes.trim() } : {}),
  };
  return toolResult('ready', {
    requestId,
    targetTool: 'create_transaction',
    arguments: command,
    appliedDefaults: defaults,
    timeZoneId: dateResult.context?.timeZoneId ?? null,
  }, `已準備卡費繳款命令，requestId=${requestId}`);
}

/** 準備獨立信用卡消費的固定 canonical 命令。 */
async function prepareCreditCardPurchase(args: PrepareInput, client: ApiClient, uuid: UUIDFactory): Promise<CallToolResult> {
  const amountValue = args.amount ?? args.totalAmount;
  if (amountValue === undefined || amountValue === null) return needsInput('請提供信用卡消費總額', ['amount']);
  let amount: number;
  try {
    amount = positiveAmount(amountValue, 'totalAmount');
  } catch (error) {
    return needsInput((error as Error).message, [], { amount: 'invalid' });
  }
  const description = typeof args.description === 'string' ? args.description.trim() : '';
  if (!description) return needsInput('請提供信用卡消費描述', ['description']);
  const dateResult = await resolveDate(args, client);
  if (!isResolvedDate(dateResult)) return dateResult;
  const cards = await loadAllCreditCards(client);
  const cardResult = resolveCreditCard(cards, args);
  if (cardResult.result) return cardResult.result;
  if (!cardResult.value) return configurationError('credit_card_not_configured', '目前沒有可用信用卡');
  const requestedInstallment = args.installmentRequested;
  if (requestedInstallment !== undefined && typeof requestedInstallment !== 'boolean') {
    return needsInput('installmentRequested 必須是 boolean', [], { installmentRequested: 'invalid' });
  }
  let periods: number;
  const defaults: string[] = [];
  if (args.periods === undefined || args.periods === null) {
    if (requestedInstallment === true) return needsInput('已指定分期，請提供期數', ['periods']);
    periods = 1;
    defaults.push('periods=1');
  } else if (typeof args.periods !== 'number' || !Number.isInteger(args.periods) || args.periods < 1 || args.periods > 60) {
    return needsInput('periods 必須是 1 到 60 的整數', [], { periods: 'out_of_range' });
  } else {
    periods = args.periods;
  }
  if (dateResult.defaulted) defaults.push(`purchaseDate=${dateResult.date}`);
  if (args.card === undefined && args.cardId === undefined) defaults.push(`cardId=${cardResult.value.id}`);
  const requestId = uuid();
  const command = {
    requestId,
    cardId: cardResult.value.id,
    totalAmount: amount,
    periods,
    purchaseDate: dateResult.date,
    description,
  };
  return toolResult('ready', {
    requestId,
    targetTool: 'create_credit_card_transaction',
    arguments: command,
    appliedDefaults: defaults,
    timeZoneId: dateResult.context?.timeZoneId ?? null,
  }, `已準備信用卡消費命令，requestId=${requestId}`);
}

/** 準備三種記帳意圖，準備階段只讀取參考資料。 */
async function prepareBookkeepingEntry(args: PrepareInput, client: ApiClient, uuid: UUIDFactory): Promise<CallToolResult> {
  const intent = intentValue(args.intent);
  if (!intent) return needsInput('請指定 ordinary、credit_card_purchase 或 credit_card_repayment intent', ['intent']);
  const incompatible = intent === 'credit_card_purchase'
    ? ['category', 'categoryCode', 'categoryId', 'paymentMethod', 'paymentMethodCode', 'paymentMethodId', 'type', 'notes']
    : ['card', 'cardId', 'totalAmount', 'periods', 'installmentRequested', 'perPeriodAmount'];
  const fieldErrors: JsonObject = {};
  for (const field of incompatible) if (args[field] !== undefined) fieldErrors[field] = 'unsupported_for_intent';
  if (Object.keys(fieldErrors).length) return needsInput('指定欄位不適用於此記帳意圖', [], fieldErrors);
  if (intent === 'credit_card_purchase') {
    if (args.amount !== undefined && args.totalAmount !== undefined && args.amount !== args.totalAmount) return needsInput('amount 與 totalAmount 不一致，請確認總額', [], { totalAmount: 'conflict' });
    if (args.perPeriodAmount !== undefined && args.totalAmount === undefined) return needsInput('只有每期金額不足以確認總額，請提供 totalAmount', ['totalAmount']);
  }
  if (intent === 'ordinary') return prepareOrdinary(args, client, uuid);
  if (intent === 'credit_card_repayment') return prepareCreditCardRepayment(args, client, uuid);
  return prepareCreditCardPurchase(args, client, uuid);
}

/** 產生缺少準備 envelope 時的安全提示，不呼叫寫入 API。 */
function needsPreparation(targetTool: string, missingFields: string[]): CallToolResult {
  return errorResult(
    'needs_preparation',
    'needs_preparation',
    `請先呼叫 prepare_bookkeeping_entry 取得固定日期、參考資料與 requestId：${missingFields.join(', ')}`,
    { targetTool, missingFields, guidance: '準備完成後，原樣傳入 arguments 與 requestId。' },
  );
}

/** 取得普通交易 canonical response 中適合回傳的欄位。 */
function transactionData(value: unknown): JsonObject {
  requireApiSchema(transactionSchema, value);
  if (!isRecord(value)) throw new ToolInputError('invalid_api_response', '普通交易 API 回應格式無效');
  return {
    id: value.id,
    type: value.type,
    amount: value.amount,
    date: value.date,
    description: value.description ?? null,
    notes: value.notes ?? null,
    categoryId: value.categoryId,
    paymentMethodId: value.paymentMethodId ?? null,
    category: value.category ?? null,
    paymentMethod: value.paymentMethod ?? null,
  };
}

/** 執行固定普通交易命令，永遠使用 prepared requestId 作為冪等 key。 */
async function createTransaction(args: JsonObject, client: ApiClient): Promise<CallToolResult> {
  const requestId = args.requestId;
  const selectors = ['category', 'categoryCode', 'paymentMethod', 'paymentMethodCode'].filter(field => args[field] !== undefined);
  if (selectors.length > 0) {
    return errorResult('needs_preparation', 'needs_preparation', '語意選擇器必須在準備階段解析，不可與執行命令的固定 ID 混用。', {
      targetTool: 'create_transaction',
      ...(typeof requestId === 'string' ? { requestId } : {}),
      fieldErrors: Object.fromEntries(selectors.map(field => [field, 'requires_preparation'])),
      guidance: '新的記帳意圖請使用 prepare_bookkeeping_entry 解析所有選擇器，再原樣執行回傳的固定 ID arguments。既有命令重試請使用原始 prepared envelope，不要重新準備或產生新 requestId；原始命令遺失時先核對紀錄。',
    });
  }
  const missing: string[] = [];
  if (requestId === undefined) missing.push('requestId');
  if (args.date === undefined) missing.push('date');
  if (args.categoryId === undefined) missing.push('categoryId');
  if (args.paymentMethodId === undefined) missing.push('paymentMethodId');
  if (args.type === undefined) missing.push('type');
  if (missing.length > 0) return needsPreparation('create_transaction', missing);
  if (!validUuid(requestId)) return errorResult('error', 'invalid_request_id', 'requestId 必須是 UUID');
  if (!validDate(args.date)) return errorResult('error', 'invalid_date', 'date 必須是有效的 YYYY-MM-DD');
  let amount: number;
  try {
    amount = positiveAmount(args.amount);
  } catch (error) {
    return errorResult('error', 'invalid_amount', (error as Error).message, { requestId });
  }
  const type = transactionType(args.type);
  if (!type) return errorResult('error', 'invalid_type', 'type 必須是 Income 或 Expense', { requestId });
  let categoryId: number;
  let paymentMethodId: number;
  try {
    categoryId = positiveInteger(args.categoryId, 'categoryId');
    paymentMethodId = positiveInteger(args.paymentMethodId, 'paymentMethodId');
  } catch (error) {
    return errorResult('error', 'invalid_reference_id', (error as Error).message, { requestId });
  }
  if (typeof args.description !== 'string' || args.description.trim() === '') {
    return errorResult('error', 'invalid_description', 'description 不可為空', { requestId });
  }
  const body = {
    type,
    amount,
    date: args.date,
    description: args.description.trim(),
    categoryId,
    paymentMethodId,
    ...(typeof args.notes === 'string' && args.notes.trim() ? { notes: args.notes.trim() } : {}),
  };
  const response = await client.post<unknown>('/api/transactions', body, requestId);
  const transaction = transactionData(response.data);
  const status = response.replayed ? 'replayed' : 'created';
  const action = response.replayed ? '已重播' : '已記錄';
  const category = canonicalReferenceLabel(transaction.category, transaction.categoryId);
  const paymentMethod = canonicalReferenceLabel(transaction.paymentMethod, transaction.paymentMethodId);
  return toolResult(status, { requestId, transaction }, `${action} ${transaction.date} ${transaction.amount} 元：${transaction.description ?? ''}；分類：${category}；付款方式：${paymentMethod}`);
}

/** 優先顯示回應中的實際名稱，缺少名稱時明列回應 ID，不查詢或猜測參考資料。 */
function canonicalReferenceLabel(reference: unknown, id: unknown): string {
  if (isRecord(reference) && typeof reference.name === 'string' && reference.name.trim()) return reference.name.trim();
  return typeof id === 'number' ? `ID ${id}` : '未提供（ID 不可用）';
}

/** 取得信用卡交易 canonical response 中適合回傳的欄位。 */
function creditCardTransactionData(value: unknown): JsonObject {
  requireApiSchema(creditSchema, value);
  if (!isRecord(value)) throw new ToolInputError('invalid_api_response', '信用卡交易 API 回應格式無效');
  return {
    id: value.id,
    sourceType: 'credit_card',
    sourceId: value.id,
    transactionId: value.transactionId ?? null,
    cardId: value.cardId ?? null,
    totalAmount: value.totalAmount,
    periods: value.periods,
    perPeriod: value.perPeriod,
    purchaseDate: value.purchaseDate,
    description: value.description ?? null,
    card: value.card ?? null,
    payments: Array.isArray(value.payments) ? value.payments : [],
  };
}

/** 執行不帶 TransactionId 的獨立信用卡消費命令。 */
async function createCreditCardTransaction(args: JsonObject, client: ApiClient): Promise<CallToolResult> {
  const requestId = args.requestId;
  const missing: string[] = [];
  for (const field of ['requestId', 'cardId', 'totalAmount', 'periods', 'purchaseDate', 'description']) {
    if (args[field] === undefined) missing.push(field);
  }
  if (missing.length > 0) return needsPreparation('create_credit_card_transaction', missing);
  if (!validUuid(requestId)) return errorResult('error', 'invalid_request_id', 'requestId 必須是 UUID');
  if (!validDate(args.purchaseDate)) return errorResult('error', 'invalid_date', 'purchaseDate 必須是有效的 YYYY-MM-DD', { requestId });
  let cardId: number;
  let periods: number;
  let totalAmount: number;
  try {
    cardId = positiveInteger(args.cardId, 'cardId');
    periods = positiveInteger(args.periods, 'periods');
    totalAmount = positiveAmount(args.totalAmount, 'totalAmount');
  } catch (error) {
    return errorResult('error', 'invalid_input', (error as Error).message, { requestId });
  }
  if (periods > 60) return errorResult('error', 'periods_out_of_range', 'periods 必須介於 1 與 60', { requestId });
  if (typeof args.description !== 'string' || args.description.trim() === '') {
    return errorResult('error', 'invalid_description', 'description 不可為空', { requestId });
  }
  const body = {
    cardId,
    totalAmount,
    periods,
    purchaseDate: args.purchaseDate,
    description: args.description.trim(),
  };
  const response = await client.post<unknown>('/api/installments', body, requestId);
  const transaction = creditCardTransactionData(response.data);
  const status = response.replayed ? 'replayed' : 'created';
  const action = response.replayed ? '已重播' : '已記錄';
  return toolResult(status, { requestId, creditCardTransaction: transaction }, `${action} ${transaction.purchaseDate} ${transaction.totalAmount} 元：${transaction.description ?? ''}`);
}

/** 將交易列表摘要轉成簡短人類可讀文字。 */
function transactionListText(value: unknown): string {
  if (!isRecord(value)) return '已取得交易查詢結果';
  const summary = isRecord(value.summary) ? value.summary : {};
  return `找到 ${String(value.total ?? 0)} 筆交易，支出 ${String(summary.totalExpense ?? 0)} 元，收入 ${String(summary.totalIncome ?? 0)} 元`;
}

/** 列出分類並在 MCP 端套用可選的收入／支出篩選。 */
async function listCategories(args: JsonObject, client: ApiClient): Promise<CallToolResult> {
  const type = args.type;
  if (type !== undefined && type !== 'income' && type !== 'expense') {
    return errorResult('error', 'invalid_type', 'type 必須是 income 或 expense');
  }
  const items = (await loadCategories(client))
    .filter(item => type === undefined || categoryType(item.type)?.toLowerCase() === type)
    .map(categoryCandidate);
  return toolResult('ok', { items }, `取得 ${items.length} 個分類`);
}

/** 取得最近的普通交易，限制 API limit 在安全範圍內。 */
async function getRecentTransactions(args: JsonObject, client: ApiClient): Promise<CallToolResult> {
  const limit = args.limit === undefined ? 5 : args.limit;
  if (typeof limit !== 'number' || !Number.isInteger(limit) || limit < 1 || limit > 100) {
    return errorResult('error', 'invalid_limit', 'limit 必須是 1 到 100 的整數');
  }
  const data = await client.get<unknown>(queryPath('/api/transactions', { limit }));
  const items = Array.isArray(data) ? data.map(transactionData) : listItems<unknown>(data).map(transactionData);
  return toolResult('ok', { items }, `取得最近 ${items.length} 筆普通交易`);
}

/** 取得完整信用卡候選清單與分頁資訊。 */
async function listCreditCards(args: JsonObject, client: ApiClient): Promise<CallToolResult> {
  const page = args.page === undefined ? 1 : positiveInteger(args.page, 'page');
  const pageSize = args.pageSize === undefined ? 20 : positiveInteger(args.pageSize, 'pageSize');
  if (pageSize > 100) return errorResult('error', 'invalid_page_size', 'pageSize 不可超過 100');
  const data = await client.get<unknown>(queryPath('/api/credit-cards', { page, pageSize }));
  if (!isRecord(data)) throw new ToolInputError('invalid_api_response', '信用卡列表回應格式無效');
  requireApiSchema(pageSchema(cardSchema), data);
  const items = listItems<CreditCardReference>(data).map(creditCardCandidate);
  if (data.page !== page || items.length !== Math.min(data.pageSize as number, Math.max(0, (data.total as number) - (page - 1) * (data.pageSize as number)))
    || new Set(items.map(card => card.id)).size !== items.length) {
    throw new ToolInputError('invalid_api_response', '信用卡分頁資料不完整或不一致');
  }
  return toolResult('ok', {
    items,
    total: typeof data.total === 'number' ? data.total : items.length,
    page: typeof data.page === 'number' ? data.page : page,
    pageSize: typeof data.pageSize === 'number' ? data.pageSize : pageSize,
  }, `取得 ${items.length} 張信用卡候選`);
}

/** 還原已刪除的普通交易，清楚區分於取消新增命令。 */
async function undoTransaction(args: JsonObject, client: ApiClient): Promise<CallToolResult> {
  let id: number;
  try {
    id = positiveInteger(args.id, 'id');
  } catch (error) {
    return errorResult('error', 'invalid_id', (error as Error).message);
  }
  const response = await client.post<unknown>(`/api/transactions/${id}/undo`, {});
  const transaction = transactionData(response.data);
  return toolResult('ok', { transaction }, `已還原普通交易 #${transaction.id}：${transaction.description ?? ''}`);
}

/** 取得既有普通收支摘要，不冒充跨來源 consumption。 */
async function financialSummary(client: ApiClient): Promise<CallToolResult> {
  const summary = await client.get<unknown>('/api/reports/monthly-summary');
  return toolResult('ok', { summary, basis: 'ordinary_financial_summary' }, '已取得目前月份普通收支摘要');
}

/** 讀取普通交易明細。 */
async function getTransaction(args: JsonObject, client: ApiClient): Promise<CallToolResult> {
  let id: number;
  try {
    id = positiveInteger(args.id, 'id');
  } catch (error) {
    return errorResult('error', 'invalid_id', (error as Error).message);
  }
  const transaction = transactionData(await client.get<unknown>(`/api/transactions/${id}`));
  return toolResult('ok', { sourceType: 'ordinary', sourceId: id, transaction }, `取得普通交易 #${id}`);
}

/** 讀取獨立信用卡交易與付款時程。 */
async function getCreditCardTransaction(args: JsonObject, client: ApiClient): Promise<CallToolResult> {
  let id: number;
  try {
    id = positiveInteger(args.sourceId ?? args.id, 'sourceId');
  } catch (error) {
    return errorResult('error', 'invalid_source_id', (error as Error).message);
  }
  const transaction = creditCardTransactionData(await client.get<unknown>(`/api/installments/${id}`));
  return toolResult('ok', { sourceType: 'credit_card', sourceId: id, transaction }, `取得信用卡交易 #${id}`);
}

/** 執行普通交易原始帳目查詢並保留完整 filtered summary。 */
async function searchTransactions(args: JsonObject, client: ApiClient): Promise<CallToolResult> {
  const values: Record<string, unknown> = {};
  for (const field of ['startDate', 'endDate']) {
    if (args[field] !== undefined && !validDate(args[field])) return errorResult('error', 'invalid_date', `${field} 必須是有效的 YYYY-MM-DD`);
    values[field] = args[field];
  }
  if (args.categoryId !== undefined) {
    try { values.categoryId = positiveInteger(args.categoryId, 'categoryId'); } catch (error) { return errorResult('error', 'invalid_category_id', (error as Error).message); }
  }
  if (args.category !== undefined || args.categoryCode !== undefined) {
    const categories = await loadCategories(client);
    const categoryResult = resolveCategory(categories, args);
    if (categoryResult.result) return categoryResult.result;
    if (!categoryResult.value) return errorResult('error', 'invalid_category_id', '找不到指定分類');
    values.categoryId = categoryResult.value.id;
  }
  const searchValue = args.search ?? args.keyword;
  if (searchValue !== undefined && typeof searchValue !== 'string') return errorResult('error', 'invalid_search', 'search 必須是文字');
  if (args.type !== undefined) {
    const type = transactionType(args.type);
    if (!type) return errorResult('error', 'invalid_type', 'type 必須是 income 或 expense');
    values.type = type;
  }
  if (args.repaymentOnly !== undefined && typeof args.repaymentOnly !== 'boolean') return errorResult('error', 'invalid_repayment_only', 'repaymentOnly 必須是 boolean');
  for (const field of ['page', 'pageSize', 'repaymentOnly']) values[field] = args[field];
  values.search = searchValue;
  if (values.page !== undefined) {
    try { positiveInteger(values.page, 'page'); } catch (error) { return errorResult('error', 'invalid_page', (error as Error).message); }
  }
  if (values.pageSize !== undefined) {
    try {
      const pageSize = positiveInteger(values.pageSize, 'pageSize');
      if (pageSize > 100) return errorResult('error', 'invalid_page_size', 'pageSize 不可超過 100');
    } catch (error) { return errorResult('error', 'invalid_page_size', (error as Error).message); }
  }
  const data = await client.get<unknown>(queryPath('/api/transactions', values));
  if (!isRecord(data)) throw new ToolInputError('invalid_api_response', '交易查詢回應格式無效');
  return toolResult('ok', { ...data }, transactionListText(data));
}

/** 執行跨來源 consumption 查詢，保留期間與 coverage 說明。 */
async function searchConsumption(args: JsonObject, client: ApiClient): Promise<CallToolResult> {
  for (const field of ['startDate', 'endDate']) {
    if (!validDate(args[field])) return errorResult('error', 'invalid_date', `${field} 必須是有效的 YYYY-MM-DD`);
  }
  const values: Record<string, unknown> = {
    startDate: args.startDate,
    endDate: args.endDate,
    source: args.source,
    categoryId: args.categoryId,
    search: args.search,
    page: args.page,
    pageSize: args.pageSize,
  };
  if (args.source !== undefined && args.source !== 'all' && args.source !== 'ordinary' && args.source !== 'credit_card') {
    return errorResult('error', 'invalid_source', 'source 必須是 all、ordinary 或 credit_card');
  }
  if (args.categoryId !== undefined) {
    try { values.categoryId = positiveInteger(args.categoryId, 'categoryId'); } catch (error) { return errorResult('error', 'invalid_category_id', (error as Error).message); }
  }
  if (args.search !== undefined && typeof args.search !== 'string') return errorResult('error', 'invalid_search', 'search 必須是文字');
  if (args.page !== undefined) {
    try { values.page = positiveInteger(args.page, 'page'); } catch (error) { return errorResult('error', 'invalid_page', (error as Error).message); }
  }
  if (args.pageSize !== undefined) {
    try {
      values.pageSize = positiveInteger(args.pageSize, 'pageSize');
      if ((values.pageSize as number) > 100) return errorResult('error', 'invalid_page_size', 'pageSize 不可超過 100');
    } catch (error) { return errorResult('error', 'invalid_page_size', (error as Error).message); }
  }
  const data = await client.get<unknown>(queryPath('/api/consumption', values));
  if (!isRecord(data)) throw new ToolInputError('invalid_api_response', 'consumption 查詢回應格式無效');
  const period = isRecord(data.period) ? `${String(data.period.startDate)} 至 ${String(data.period.endDate)}` : '指定期間';
  const summary = isRecord(data.summary) ? data.summary : {};
  return toolResult('ok', { ...data }, `${period} consumption 總額 ${String(summary.totalAmount ?? 0)} 元（${String(summary.count ?? 0)} 筆）`);
}

/** 將工具名稱與輸入分派到對應的 MCP handler。 */
async function handleTool(name: string, args: JsonObject, client: ApiClient, uuid: UUIDFactory): Promise<CallToolResult> {
  switch (name) {
    case 'get_bookkeeping_context':
      return toolResult('ok', { context: await loadContext(client) }, '已取得記帳系統日期與時區');
    case 'prepare_bookkeeping_entry':
      return prepareBookkeepingEntry(args as PrepareInput, client, uuid);
    case 'create_transaction':
      return createTransaction(args, client);
    case 'create_credit_card_transaction':
      return createCreditCardTransaction(args, client);
    case 'list_credit_cards':
      return listCreditCards(args, client);
    case 'list_categories':
      return listCategories(args, client);
    case 'get_recent_transactions':
      return getRecentTransactions(args, client);
    case 'undo_transaction':
      return undoTransaction(args, client);
    case 'list_payment_methods': {
      const items = (await loadPaymentMethods(client)).map(paymentMethodCandidate);
      return toolResult('ok', { items }, `取得 ${items.length} 個付款方式；信用卡消費請使用獨立信用卡工具`);
    }
    case 'get_financial_summary':
      return financialSummary(client);
    case 'search_transactions':
      return searchTransactions(args, client);
    case 'get_transaction':
      return getTransaction(args, client);
    case 'get_credit_card_transaction':
      return getCreditCardTransaction(args, client);
    case 'search_consumption':
      return searchConsumption(args, client);
    default:
      return errorResult('error', 'unknown_tool', `未知工具：${name}`);
  }
}

/** 將未預期例外映射成不洩漏 raw body 的 MCP 結果。 */
function mapFailure(name: string, args: JsonObject, error: unknown): CallToolResult {
  const requestId = typeof args.requestId === 'string' ? { requestId: args.requestId } : {};
  if (WRITE_TOOLS.has(name) && ((error instanceof ApiError && error.retryable) || (error instanceof ToolInputError && error.code === 'invalid_api_response'))) {
    return errorResult('outcome_unknown', error.code, '無法確認命令結果；保留原命令，勿建立新 requestId。', {
      ...requestId, targetTool: name, arguments: args,
      guidance: name === 'undo_transaction' ? '請先查詢原交易核對還原結果。' : '使用完全相同的 requestId 與 arguments 重試；原命令遺失時先核對紀錄。',
    });
  }
  if (error instanceof ApiError) {
    return errorResult('error', error.code, error.message, { ...requestId, httpStatus: error.status || undefined });
  }
  if (error instanceof ToolInputError) return errorResult('error', error.code, error.message, requestId);
  return errorResult('error', 'internal_error', '工具執行失敗，未建立可確認的結果', requestId);
}

/** 建立 MCP server、註冊工具列表與安全的 tools/call handler。 */
export function createServer(client: ApiClient, uuid: UUIDFactory = randomUUID): Server {
  const server = new Server(
    { name: 'myexpenses-mcp-server', version: '2.0.0' },
    { capabilities: { tools: {} } },
  );
  // 冪等提示描述重送的副作用，不保證回應相同；還原會修改既有狀態，保守標示為破壞性操作。
  const definitions = TOOL_DEFINITIONS.map(tool => ({
    ...tool,
    outputSchema: outputSchema(tool.name),
    annotations: {
      readOnlyHint: !WRITE_TOOLS.has(tool.name),
      destructiveHint: tool.name === 'undo_transaction',
      idempotentHint: true,
    },
  }));
  server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: definitions }));
  server.setRequestHandler(CallToolRequestSchema, async request => {
    const name = request.params.name;
    const args = isRecord(request.params.arguments) ? request.params.arguments : {};
    try {
      const definition = definitions.find(tool => tool.name === name);
      if (definition && !matchesSchema(definition.inputSchema, args)) {
        return name === 'prepare_bookkeeping_entry'
          ? needsInput('輸入欄位不符合工具契約，請修正明確欄位', [], { arguments: 'invalid_schema' })
          : errorResult('error', 'invalid_input', '輸入欄位不符合工具契約');
      }
      const result = await handleTool(name, args, client, uuid);
      if (definition) requireApiSchema(definition.outputSchema, result.structuredContent);
      return result;
    } catch (error) {
      return mapFailure(name, args, error);
    }
  });
  return server;
}

const TOOL_DEFINITIONS = [
  {
    name: 'get_bookkeeping_context',
    description: '讀取後端系統的 currentDate 與 timeZoneId；相對日期必須以此 context 展開，不使用 MCP 主機日期。',
    inputSchema: objectSchema({}),
  },
  {
    name: 'prepare_bookkeeping_entry',
    description: '唯讀準備 ordinary、credit_card_purchase 或 credit_card_repayment 命令，解析參考資料、固定日期與 requestId；ready 後可直接執行，不需要額外確認。',
    inputSchema: objectSchema({
      intent: { type: 'string', enum: ['ordinary', 'credit_card_purchase', 'credit_card_repayment'] },
      amount: { type: 'number' },
      totalAmount: { type: 'number' },
      description: { type: 'string' },
      date: { type: 'string', pattern: '^\\d{4}-\\d{2}-\\d{2}$' },
      type: { type: 'string', enum: ['income', 'expense', 'Income', 'Expense'] },
      category: { type: 'string' },
      categoryCode: { type: 'string' },
      categoryId: { type: 'integer', minimum: 1 },
      paymentMethod: { type: 'string' },
      paymentMethodCode: { type: 'string' },
      paymentMethodId: { type: 'integer', minimum: 1 },
      notes: { type: 'string' },
      card: { type: 'string' },
      cardId: { type: 'integer', minimum: 1 },
      periods: { type: 'integer', minimum: 1, maximum: 60 },
      installmentRequested: { type: 'boolean' },
      perPeriodAmount: { type: 'number', exclusiveMinimum: 0, description: '每期金額不是總額；提供此欄位時必須另行確認 totalAmount。' },
    }),
  },
  {
    name: 'create_transaction',
    description: '執行已準備的普通收入或支出。必須使用 prepare_bookkeeping_entry 回傳的固定 requestId、date、categoryId 與 paymentMethodId；不接受信用卡消費。舊語意選擇器僅供引導準備，即使同時提供 ID 也不寫入、不重新解析參考資料。',
    inputSchema: objectSchema({
      requestId: { type: 'string', format: 'uuid' },
      amount: { type: 'number', exclusiveMinimum: 0 },
      description: { type: 'string', minLength: 1 },
      date: { type: 'string', pattern: '^\\d{4}-\\d{2}-\\d{2}$' },
      type: { type: 'string', enum: ['Income', 'Expense'] },
      category: { type: 'string', description: '舊分類名稱或代碼；需先呼叫 prepare_bookkeeping_entry。' },
      categoryCode: { type: 'string', description: '舊分類代碼；需先呼叫 prepare_bookkeeping_entry。' },
      paymentMethod: { type: 'string', description: '舊付款方式名稱或代碼；需先呼叫 prepare_bookkeeping_entry。' },
      paymentMethodCode: { type: 'string', description: '舊付款方式代碼；需先呼叫 prepare_bookkeeping_entry。' },
      categoryId: { type: 'integer', minimum: 1 },
      paymentMethodId: { type: 'integer', minimum: 1 },
      notes: { type: 'string' },
    }),
  },
  {
    name: 'create_credit_card_transaction',
    description: '執行已準備的獨立信用卡消費，使用 /api/installments 建立完整付款時程，不送 TransactionId，也不建立普通交易。',
    inputSchema: objectSchema({
      requestId: { type: 'string', format: 'uuid' },
      cardId: { type: 'integer', minimum: 1 },
      totalAmount: { type: 'number', exclusiveMinimum: 0 },
      periods: { type: 'integer', minimum: 1, maximum: 60 },
      purchaseDate: { type: 'string', pattern: '^\\d{4}-\\d{2}-\\d{2}$' },
      description: { type: 'string', minLength: 1 },
    }),
  },
  {
    name: 'list_credit_cards',
    description: '列出信用卡候選；卡片僅供信用卡消費準備與辨識，不提供卡片管理寫入。',
    inputSchema: objectSchema({
      page: { type: 'integer', minimum: 1 },
      pageSize: { type: 'integer', minimum: 1, maximum: 100 },
    }),
  },
  {
    name: 'list_categories',
    description: '取得收支分類；可依 income 或 expense 篩選，回傳 id、name、type、icon 與 systemCode。',
    inputSchema: objectSchema({ type: { type: 'string', enum: ['income', 'expense'] } }),
  },
  {
    name: 'get_recent_transactions',
    description: '查詢最近的普通交易，不是完整 consumption summary；limit 預設 5，最多 100。',
    inputSchema: objectSchema({ limit: { type: 'integer', minimum: 1, maximum: 100 } }),
  },
  {
    name: 'undo_transaction',
    description: '還原一筆已刪除的普通交易，不是取消剛新增的記帳。',
    inputSchema: objectSchema({ id: { type: 'integer', minimum: 1 } }, ['id']),
  },
  {
    name: 'list_payment_methods',
    description: '取得付款方式；普通信用卡付款會導向獨立 credit_card_purchase 工作流程。',
    inputSchema: objectSchema({}),
  },
  {
    name: 'get_financial_summary',
    description: '取得現有普通財務月摘要；不要用此工具回答跨來源消費，跨來源問題請使用 search_consumption。',
    inputSchema: objectSchema({}),
  },
  {
    name: 'search_transactions',
    description: '搜尋普通原始帳目，支援日期、type、categoryId、search、分頁與 repaymentOnly；summary 覆蓋完整篩選集合，卡費預設仍會保留。',
    inputSchema: objectSchema({
      startDate: { type: 'string', pattern: '^\\d{4}-\\d{2}-\\d{2}$' },
      endDate: { type: 'string', pattern: '^\\d{4}-\\d{2}-\\d{2}$' },
      categoryId: { type: 'integer', minimum: 1 },
      category: { type: 'string' },
      categoryCode: { type: 'string' },
      search: { type: 'string' },
      keyword: { type: 'string' },
      type: { type: 'string', enum: ['income', 'expense', 'Income', 'Expense'] },
      page: { type: 'integer', minimum: 1 },
      pageSize: { type: 'integer', minimum: 1, maximum: 100 },
      repaymentOnly: { type: 'boolean' },
    }),
  },
  {
    name: 'get_transaction',
    description: '依普通交易 ID 取得原始帳目明細；sourceType=ordinary。',
    inputSchema: objectSchema({ id: { type: 'integer', minimum: 1 } }, ['id']),
  },
  {
    name: 'get_credit_card_transaction',
    description: '依 credit_card sourceId 取得獨立信用卡消費與完整付款時程；不要用普通 transaction ID 代替。',
    inputSchema: objectSchema({ sourceId: { type: 'integer', minimum: 1 } }, ['sourceId']),
  },
  {
    name: 'search_consumption',
    description: '查詢跨來源 consumption；必須提供明確 startDate/endDate，信用卡消費按購買日全額計入並排除 living + 描述含信用卡帳單的卡費。',
    inputSchema: objectSchema({
      startDate: { type: 'string', pattern: '^\\d{4}-\\d{2}-\\d{2}$' },
      endDate: { type: 'string', pattern: '^\\d{4}-\\d{2}-\\d{2}$' },
      source: { type: 'string', enum: ['all', 'ordinary', 'credit_card'] },
      categoryId: { type: 'integer', minimum: 1 },
      search: { type: 'string' },
      page: { type: 'integer', minimum: 1 },
      pageSize: { type: 'integer', minimum: 1, maximum: 100 },
    }, ['startDate', 'endDate']),
  },
];

/** 以 stdio 啟動正式 MCP server，token 缺失時以非零狀態安全結束。 */
export async function main(): Promise<void> {
  const token = process.env.MYEXPENSES_API_TOKEN;
  if (!token) {
    console.error('MYEXPENSES_API_TOKEN environment variable is required');
    process.exitCode = 1;
    return;
  }
  const apiUrl = process.env.MYEXPENSES_API_URL || 'http://localhost:5000';
  const timeoutMs = Number(process.env.MYEXPENSES_API_TIMEOUT_MS || 10_000);
  const server = createServer(new ApiClient(apiUrl, token, Number.isFinite(timeoutMs) ? timeoutMs : 10_000));
  await server.connect(new StdioServerTransport());
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  main().catch(error => {
    console.error('MCP server error:', error instanceof Error ? error.message : 'unknown error');
    process.exitCode = 1;
  });
}
