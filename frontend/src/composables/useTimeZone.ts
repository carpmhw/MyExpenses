import { ref } from 'vue'
import { api } from '../api'
import {
  DEFAULT_SYSTEM_TIME_ZONE,
  formatSystemDateTime,
  getDateInputValue,
} from '../utils/timezone'

const timeZoneId = ref(DEFAULT_SYSTEM_TIME_ZONE)
const isReady = ref(false)
const loadError = ref(false)

// Provides the single configured system time zone to the authenticated frontend.
export function useTimeZone() {
  // Loads the authoritative system time zone from the backend.
  async function fetchTimeZone(): Promise<boolean> {
    loadError.value = false
    try {
      const result = await api.settings.getTimeZone()
      timeZoneId.value = result.timeZoneId
      isReady.value = true
      return true
    } catch {
      isReady.value = false
      loadError.value = true
      return false
    }
  }

  // Updates the local time zone state after the backend accepts a new value.
  function setTimeZone(newTimeZoneId: string): void {
    timeZoneId.value = newTimeZoneId
    isReady.value = true
    loadError.value = false
  }

  // Clears the cached system time zone when the authenticated session ends.
  function resetTimeZone(): void {
    timeZoneId.value = DEFAULT_SYSTEM_TIME_ZONE
    isReady.value = false
    loadError.value = false
  }

  // Returns today's date according to the configured system time zone.
  function getToday(instant: Date | string = new Date()): string {
    return getDateInputValue(instant, timeZoneId.value)
  }

  // Formats a UTC event timestamp using the configured system time zone.
  function formatDateTime(instant: Date | string): string {
    return formatSystemDateTime(instant, timeZoneId.value)
  }

  return { timeZoneId, isReady, loadError, fetchTimeZone, setTimeZone, resetTimeZone, getToday, formatDateTime }
}
