export type ApiFieldErrors = Record<string, string[]>

export interface ApiTypedErrorDetails {
  reason?: string
  availableShares?: number
  requestedShares?: number
  tradeDate?: string
}

export interface ApiErrorDetails {
  status: number | null
  code?: string | null
  title: string | null
  detail: string | null
  fieldErrors?: ApiFieldErrors
  traceId?: string | null
  details?: ApiTypedErrorDetails | null
  userMessage?: string
}

export class ApiError extends Error {
  readonly status: number | null
  readonly code: string | null
  readonly title: string | null
  readonly detail: string | null
  readonly fieldErrors: ApiFieldErrors
  readonly traceId: string | null
  readonly details: ApiTypedErrorDetails | null
  readonly userMessage: string

  // Creates a safe structured API error without exposing raw response content.
  constructor(details: ApiErrorDetails) {
    const userMessage = details.userMessage ?? details.detail ?? details.title ?? '要求失敗'
    super(userMessage)
    this.name = 'ApiError'
    this.status = details.status
    this.code = details.code ?? null
    this.title = details.title ?? null
    this.detail = details.detail ?? null
    this.fieldErrors = details.fieldErrors ?? {}
    this.traceId = details.traceId ?? null
    this.details = details.details ?? null
    this.userMessage = userMessage
  }
}

export class RequestCancelledError extends Error {
  readonly kind = 'cancelled'

  // Creates a distinct error type for intentional request cancellation.
  constructor() {
    super('Request cancelled')
    this.name = 'RequestCancelledError'
  }
}

// Identifies errors caused by replacing or disposing an owned request.
export function isRequestCancelled(error: unknown): error is RequestCancelledError {
  return error instanceof RequestCancelledError
}

// Returns a safe user-facing message for an HTTP status without response details.
export function safeStatusMessage(status: number | null): string {
  if (status === 400) return '請檢查輸入內容'
  if (status === 401) return '登入狀態已失效，請重新登入'
  if (status === 403) return '您沒有執行此操作的權限'
  if (status === 404) return '找不到要求的資料'
  if (status === 409) return '資料已變更，請重新整理後再試'
  if (status === 422) return '資料內容不符合要求'
  if (status !== null && status >= 500) return '伺服器目前無法處理要求'
  return '要求失敗，請稍後再試'
}
