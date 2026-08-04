import { ref, type Ref } from 'vue'

export type AsyncMutationStatus = 'idle' | 'submitting' | 'success' | 'error'

export interface AsyncMutationOptions<TInput, TData> {
  mutate: (input: TInput, context: { signal?: AbortSignal }) => Promise<TData>
  classifyError?: (error: unknown) => { uncertain: boolean }
  onSuccess?: (data: TData) => void | Promise<void>
}

export interface AsyncMutationState<TInput, TData> {
  status: Ref<AsyncMutationStatus>
  data: Ref<TData | undefined>
  error: Ref<unknown | null>
  followUpError: Ref<unknown | null>
  uncertain: Ref<boolean>
  submit: (input: TInput) => Promise<TData>
  reset: () => void
}

// Owns one server-confirmed mutation and keeps follow-up refresh failures separate from command success.
export function useAsyncMutation<TInput, TData>(
  options: AsyncMutationOptions<TInput, TData>,
): AsyncMutationState<TInput, TData> {
  const status = ref<AsyncMutationStatus>('idle')
  const data = ref<TData | undefined>()
  const error = ref<unknown | null>(null)
  const followUpError = ref<unknown | null>(null)
  const uncertain = ref(false)
  let inFlight: Promise<TData> | null = null

  // Submits once per in-flight logical action and returns the canonical server result.
  function submit(input: TInput): Promise<TData> {
    if (inFlight) return inFlight
    status.value = 'submitting'
    error.value = null
    followUpError.value = null
    uncertain.value = false
    let mutationResult: Promise<TData>
    try {
      mutationResult = options.mutate(input, {})
    } catch (mutationError) {
      mutationResult = Promise.reject(mutationError)
    }
    let pending: Promise<TData>
    pending = mutationResult
      .then(async result => {
        data.value = result
        status.value = 'success'
        if (options.onSuccess) {
          try {
            await options.onSuccess(result)
          } catch (followUpFailure) {
            followUpError.value = followUpFailure
          }
        }
        return result
      })
      .catch(mutationError => {
        error.value = mutationError
        uncertain.value = options.classifyError ? options.classifyError(mutationError).uncertain : false
        status.value = 'error'
        throw mutationError
      })
      .finally(() => {
        if (inFlight === pending) inFlight = null
      })
    inFlight = pending
    return pending
  }

  // Returns the mutation to idle state after the owning form has been reset.
  function reset(): void {
    if (inFlight) return
    status.value = 'idle'
    data.value = undefined
    error.value = null
    followUpError.value = null
    uncertain.value = false
  }

  return { status, data, error, followUpError, uncertain, submit, reset }
}
