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

// Creates the snapshot page default range from one year before today through today.
export function createDefaultSnapshotDateRange(today = new Date()): SnapshotDateRange {
  const dateEnd = formatDateInput(today)

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

  return formatDateInput(new Date(targetYear, month - 1, clampedDay))
}

// Formats a Date as YYYY-MM-DD for native date inputs and API query parameters.
function formatDateInput(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}
