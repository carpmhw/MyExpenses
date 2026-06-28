// Creates a small request id guard so only the newest async response can update state.
export function createLatestRequestGuard() {
  let latestRequestId = 0

  // Starts a new request generation and returns its id.
  function next() {
    latestRequestId += 1
    return latestRequestId
  }

  // Checks whether the given request id is still the newest request.
  function isLatest(requestId: number) {
    return requestId === latestRequestId
  }

  return { next, isLatest }
}
