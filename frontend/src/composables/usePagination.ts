import { ref, computed } from 'vue'

export function usePagination(initialPage = 1, initialPageSize = 20) {
  const page = ref(initialPage)
  const pageSize = ref(initialPageSize)
  const total = ref(0)

  const totalPages = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))

  const hasPrev = computed(() => page.value > 1)
  const hasNext = computed(() => page.value < totalPages.value)

  function prev() {
    if (hasPrev.value) page.value--
  }

  function next() {
    if (hasNext.value) page.value++
  }

  function goTo(p: number) {
    page.value = Math.max(1, Math.min(p, totalPages.value))
  }

  function reset() {
    page.value = 1
    total.value = 0
  }

  return { page, pageSize, total, totalPages, hasPrev, hasNext, prev, next, goTo, reset }
}
