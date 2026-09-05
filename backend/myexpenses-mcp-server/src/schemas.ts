import { AjvJsonSchemaValidator } from '@modelcontextprotocol/sdk/validation/ajv';

type Schema = Record<string, unknown>;
const validator = new AjvJsonSchemaValidator();
export const idSchema = { type: 'integer', minimum: 1 };
const count = { type: 'integer', minimum: 0 };
const text = { type: 'string', minLength: 1 };
const nullableText = { type: ['string', 'null'] };
const amount = { type: 'number', exclusiveMinimum: 0 };
const money = { type: 'number' };
const date = { type: 'string', format: 'date' };
const transactionType = { enum: ['Income', 'Expense', 0, 1] };

/** 建立必填欄位明確的 API 資料契約，容許後端新增非必要欄位。 */
export function dataSchema(properties: Schema, required = Object.keys(properties)): Schema {
  return { type: 'object', properties, required, additionalProperties: true };
}

/** 建立同質清單的資料契約。 */
export function arraySchema(items: Schema): Schema {
  return { type: 'array', items };
}

/** 使用 SDK 的 JSON Schema 驗證器驗證執行期資料，不回傳原始資料或錯誤內容。 */
export function matchesSchema(schema: Schema, value: unknown): boolean {
  return validator.getValidator(schema)(value).valid;
}

export const categorySchema = dataSchema({ id: idSchema, name: text, type: transactionType, systemCode: nullableText, icon: nullableText }, ['id', 'name', 'type']);
export const paymentSchema = dataSchema({ id: idSchema, name: text, systemCode: nullableText, icon: nullableText }, ['id', 'name']);
export const cardSchema = dataSchema({ id: idSchema, bankName: text, lastFourDigits: { type: 'string', pattern: '^\\d{4}$' }, cardNetwork: nullableText }, ['id', 'bankName', 'lastFourDigits']);
export const transactionSchema = dataSchema({
  id: idSchema, type: transactionType, amount, date, description: nullableText,
  categoryId: idSchema, paymentMethodId: { anyOf: [idSchema, { type: 'null' }] },
}, ['id', 'type', 'amount', 'date', 'description', 'categoryId']);
export const creditSchema = dataSchema({
  id: idSchema, cardId: { anyOf: [idSchema, { type: 'null' }] }, totalAmount: amount,
  // 歷史或編輯後的 canonical 資料可超過 60 期；上限僅限制新命令輸入。
  periods: { type: 'integer', minimum: 1 }, perPeriod: money,
  purchaseDate: date, description: nullableText,
  payments: arraySchema(dataSchema({ id: idSchema, installmentId: idSchema, period: idSchema, amount: money, isPaid: { type: 'boolean' }, paidDate: { anyOf: [date, { type: 'null' }] }, dueDate: { anyOf: [date, { type: 'null' }] } })),
});

/** 建立分頁資料的結構契約。 */
export function pageSchema(item: Schema): Schema {
  return dataSchema({ items: arraySchema(item), total: count, page: idSchema, pageSize: { type: 'integer', minimum: 1, maximum: 100 } });
}

const financialSummary = dataSchema({ totalIncome: money, totalExpense: money, totalBankBalance: money, baseCurrency: text, exchangeRateUpdatedAt: nullableText, exchangeRateIsStale: { type: 'boolean' }, conversionAvailable: { type: 'boolean' } });
export const transactionPageSchema = dataSchema({
  ...(pageSchema(transactionSchema).properties as Schema),
  summary: dataSchema({ totalIncome: money, totalExpense: money }),
});
export const consumptionSchema = dataSchema({
  ...(pageSchema(dataSchema({ sourceType: { enum: ['ordinary', 'credit_card'] }, sourceId: idSchema, date, amount, description: nullableText })).properties as Schema),
  basis: { const: 'consumption' }, period: dataSchema({ startDate: date, endDate: date }), timeZoneId: text,
  filters: dataSchema({ source: { enum: ['all', 'ordinary', 'credit_card'] }, categoryId: { anyOf: [idSchema, { type: 'null' }] }, search: nullableText }),
  summary: dataSchema({ totalAmount: money, ordinaryAmount: money, creditCardAmount: money, count }),
  coverage: dataSchema({ creditCardCategoriesAvailable: { const: false }, categoryNote: text, recognitionNote: text, completenessNote: text }),
  warnings: arraySchema(text),
});

const successSchemas: Record<string, Schema> = {
  get_bookkeeping_context: dataSchema({ context: dataSchema({ currentDate: date, timeZoneId: text }) }),
  prepare_bookkeeping_entry: {
    ...dataSchema({ requestId: { type: 'string', format: 'uuid' }, targetTool: { enum: ['create_transaction', 'create_credit_card_transaction'] }, arguments: { type: 'object' }, appliedDefaults: arraySchema(text) }),
    oneOf: [
      dataSchema({ targetTool: { const: 'create_transaction' }, arguments: dataSchema({ requestId: { type: 'string', format: 'uuid' }, amount, date, description: text, type: { enum: ['Income', 'Expense'] }, categoryId: idSchema, paymentMethodId: idSchema }) }),
      dataSchema({ targetTool: { const: 'create_credit_card_transaction' }, arguments: dataSchema({ requestId: { type: 'string', format: 'uuid' }, cardId: idSchema, totalAmount: amount, periods: { type: 'integer', minimum: 1, maximum: 60 }, purchaseDate: date, description: text }) }),
    ],
  },
  create_transaction: dataSchema({ requestId: { type: 'string', format: 'uuid' }, transaction: transactionSchema }),
  create_credit_card_transaction: dataSchema({ requestId: { type: 'string', format: 'uuid' }, creditCardTransaction: creditSchema }),
  list_categories: dataSchema({ items: arraySchema(categorySchema) }),
  list_payment_methods: dataSchema({ items: arraySchema(paymentSchema) }),
  list_credit_cards: pageSchema(cardSchema),
  get_recent_transactions: dataSchema({ items: arraySchema(transactionSchema) }),
  undo_transaction: dataSchema({ transaction: transactionSchema }),
  get_transaction: dataSchema({ sourceType: { const: 'ordinary' }, sourceId: idSchema, transaction: transactionSchema }),
  get_credit_card_transaction: dataSchema({ sourceType: { const: 'credit_card' }, sourceId: idSchema, transaction: creditSchema }),
  get_financial_summary: dataSchema({ summary: financialSummary, basis: { const: 'ordinary_financial_summary' } }),
  search_transactions: transactionPageSchema,
  search_consumption: consumptionSchema,
};

/** 讓公開輸出契約與執行期成功驗證使用相同 schema。 */
export function outputSchema(name: string): Schema {
  return {
    type: 'object', required: ['status'],
    properties: { status: { enum: ['ok', 'ready', 'created', 'replayed', 'needs_input', 'needs_preparation', 'error', 'outcome_unknown', 'configuration_error'] } },
    allOf: [{ if: { properties: { status: { enum: ['ok', 'ready', 'created', 'replayed'] } } }, then: successSchemas[name], else: dataSchema({ message: text }) }],
  };
}
