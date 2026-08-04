import { vi } from 'vitest'

export interface Deferred<T> {
  promise: Promise<T>
  resolve: (value: T | PromiseLike<T>) => void
  reject: (reason?: unknown) => void
  settled: () => boolean
}

// Creates a manually controlled promise for deterministic async race tests.
export function deferred<T>(): Deferred<T> {
  let isSettled = false
  let resolvePromise: (value: T | PromiseLike<T>) => void = () => undefined
  let rejectPromise: (reason?: unknown) => void = () => undefined
  const promise = new Promise<T>((resolve, reject) => {
    resolvePromise = value => {
      isSettled = true
      resolve(value)
    }
    rejectPromise = reason => {
      isSettled = true
      reject(reason)
    }
  })

  return {
    promise,
    resolve: resolvePromise,
    reject: rejectPromise,
    settled: () => isSettled,
  }
}

// Creates an abort-shaped error for controlled cancellation tests.
export function abortError(): DOMException {
  return new DOMException('The operation was aborted.', 'AbortError')
}

// Creates a JSON response with predictable headers for API client tests.
export function jsonResponse<T>(body: T, status = 200, headers?: HeadersInit): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  })
}

// Creates a fetch mock that records every URL and request init value.
export function createFetchMock(
  implementation: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response> | Response,
) {
  const fetchMock = vi.fn(implementation)
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}
