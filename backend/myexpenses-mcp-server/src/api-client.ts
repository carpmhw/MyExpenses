export type ApiOperation = 'read' | 'write';

export interface ApiRequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  body?: unknown;
  idempotencyKey?: string;
  operation?: ApiOperation;
}

export interface ApiResponse<T> {
  data: T;
  replayed: boolean;
}

const SAFE_ERROR_CODES = new Set([
  'result_unavailable',
  'validation_error',
  'not_found',
  'conflict',
  'forbidden',
  'unauthorized',
  'timeout',
]);

/** 將 HTTP 狀態轉成不洩漏後端內容的穩定錯誤碼。 */
function statusCode(status: number): string {
  if (status === 401) return 'unauthorized';
  if (status === 403) return 'forbidden';
  if (status === 404) return 'not_found';
  if (status === 409) return 'conflict';
  if (status === 410) return 'result_unavailable';
  if (status >= 400 && status < 500) return 'validation_error';
  return 'backend_unavailable';
}

/** 取得 ProblemDetails 中允許暴露的錯誤碼，其他值一律忽略。 */
function safeBodyCode(body: unknown): string | undefined {
  if (!isRecord(body) || typeof body.code !== 'string') return undefined;
  return SAFE_ERROR_CODES.has(body.code) ? body.code : undefined;
}

/** 判斷未知資料是否為一般 JSON object。 */
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** API 呼叫失敗的安全結構，不包含 raw response body。 */
export class ApiError extends Error {
  public readonly status: number;
  public readonly code: string;
  public readonly retryable: boolean;

  /** 建立供 MCP 工具映射的 API 錯誤。 */
  public constructor(status: number, code: string, message: string, retryable: boolean) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.retryable = retryable;
  }
}

export type FetchImplementation = typeof fetch;

/** 封裝 Bearer 認證、timeout、冪等 header 與安全錯誤處理。 */
export class ApiClient {
  private readonly baseUrl: string;
  private readonly token: string;
  private readonly timeoutMs: number;
  private readonly fetchImplementation: FetchImplementation;

  /** 建立連往 MyExpenses API 的 client。 */
  public constructor(
    baseUrl: string,
    token: string,
    timeoutMs = 10_000,
    fetchImplementation: FetchImplementation = fetch,
  ) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.token = token;
    this.timeoutMs = timeoutMs;
    this.fetchImplementation = fetchImplementation;
  }

  /** 執行唯讀 GET 請求。 */
  public async get<T>(path: string): Promise<T> {
    const response = await this.request<T>(path, { operation: 'read' });
    return response.data;
  }

  /** 執行帶有 optional 冪等 key 的 POST 請求。 */
  public async post<T>(path: string, body: unknown, idempotencyKey?: string): Promise<ApiResponse<T>> {
    return this.request<T>(path, {
      method: 'POST',
      body,
      idempotencyKey,
      operation: 'write',
    });
  }

  /** 建立有限時間的 API 請求並映射成安全回應。 */
  private async request<T>(path: string, options: ApiRequestOptions): Promise<ApiResponse<T>> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);
    try {
      const headers: Record<string, string> = {
        Accept: 'application/json',
        Authorization: `Bearer ${this.token}`,
      };
      if (options.body !== undefined) headers['Content-Type'] = 'application/json';
      if (options.idempotencyKey) headers['Idempotency-Key'] = options.idempotencyKey;

      const response = await this.fetchImplementation(`${this.baseUrl}${path}`, {
        method: options.method ?? 'GET',
        headers,
        body: options.body === undefined ? undefined : JSON.stringify(options.body),
        signal: controller.signal,
      });
      const text = await response.text();
      let body: unknown = undefined;
      if (text.trim()) {
        try {
          body = JSON.parse(text);
        } catch {
          body = undefined;
        }
      }

      if (!response.ok) {
        const code = safeBodyCode(body) ?? statusCode(response.status);
        const retryable = options.operation === 'write' && response.status >= 500;
        throw new ApiError(
          response.status,
          code,
          retryable ? '無法確認命令是否已提交，請使用原 requestId 重試' : safeMessage(response.status, code),
          retryable,
        );
      }

      return {
        data: body as T,
        replayed: response.headers.get('X-Idempotent-Replay')?.toLowerCase() === 'true',
      };
    } catch (error) {
      if (error instanceof ApiError) throw error;
      if (isAbortError(error)) {
        throw new ApiError(
          0,
          'timeout',
          options.operation === 'write'
            ? '無法確認命令是否已提交，請使用原 requestId 重試'
            : '讀取 API 逾時，請稍後重試',
          true,
        );
      }
      throw new ApiError(
        0,
        'network_error',
        options.operation === 'write'
          ? '無法確認命令是否已提交，請使用原 requestId 重試'
          : '無法連線到記帳 API，請稍後重試',
        options.operation === 'write',
      );
    } finally {
      clearTimeout(timer);
    }
  }
}

/** 將狀態碼與安全錯誤碼轉成可顯示的固定訊息。 */
function safeMessage(status: number, code: string): string {
  if (code === 'result_unavailable') return '原命令已提交，但目前結果已不存在';
  if (code === 'conflict') return 'requestId 已被不同命令使用，請勿改寫原命令';
  if (code === 'forbidden') return 'API token 缺少執行此操作的 scope';
  if (code === 'unauthorized') return 'API token 無效或已失效';
  if (code === 'not_found') return '找不到指定的記帳資料';
  if (status >= 400 && status < 500) return '記帳 API 拒絕了這個請求，請檢查輸入資料';
  return '記帳 API 暫時無法使用';
}

/** 判斷例外是否為 AbortController 觸發的 timeout。 */
function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}
