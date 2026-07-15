export interface SnapshotDateRange {
  dateStart: string
  dateEnd: string
}

export type SnapshotDateRangeCorrectionReason = 'end-before-start' | 'range-too-long' | null

export interface CoercedSnapshotDateRange extends SnapshotDateRange {
  changed: boolean
  reason: SnapshotDateRangeCorrectionReason
}

const MAX_SNAPSHOT_RANGE_YEARS = 5

// Creates the snapshot page default range from one year before the system-local today through today.
export function createDefaultSnapshotDateRange(today = new Date(), timeZone = 'Asia/Taipei'): SnapshotDateRange {
  const dateEnd = formatDateInput(today, timeZone)

  return {
    dateStart: subtractYearsFromDateInput(dateEnd, 1),
    dateEnd,
  }
}

// Corrects invalid snapshot date ranges before they are sent to the API.
export function coerceSnapshotDateRange(range: SnapshotDateRange): CoercedSnapshotDateRange {
  if (range.dateStart && range.dateEnd && range.dateEnd < range.dateStart) {
    return {
      dateStart: range.dateStart,
      dateEnd: range.dateStart,
      changed: true,
      reason: 'end-before-start',
    }
  }

  if (range.dateStart && range.dateEnd) {
    const earliestStart = subtractYearsFromDateInput(range.dateEnd, MAX_SNAPSHOT_RANGE_YEARS)
    if (range.dateStart < earliestStart) {
      return {
        dateStart: earliestStart,
        dateEnd: range.dateEnd,
        changed: true,
        reason: 'range-too-long',
      }
    }
  }

  return {
    ...range,
    changed: false,
    reason: null,
  }
}

// Subtracts calendar years from a YYYY-MM-DD string and returns another date input value.
function subtractYearsFromDateInput(dateInput: string, years: number): string {
  const [year, month, day] = dateInput.split('-').map(Number)
  const targetYear = year - years
  const lastDayOfTargetMonth = new Date(targetYear, month, 0).getDate()
  const clampedDay = Math.min(day, lastDayOfTargetMonth)

  return `${targetYear}-${String(month).padStart(2, '0')}-${String(clampedDay).padStart(2, '0')}`
}

// Formats a Date as YYYY-MM-DD in the requested time zone for date inputs and API query parameters.
function formatDateInput(date: Date, timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(date)
  const year = parts.find(part => part.type === 'year')?.value ?? ''
  const month = parts.find(part => part.type === 'month')?.value ?? ''
  const day = parts.find(part => part.type === 'day')?.value ?? ''
  return `${year}-${month}-${day}`
}
