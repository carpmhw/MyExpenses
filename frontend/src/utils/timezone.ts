export const DEFAULT_SYSTEM_TIME_ZONE = 'Asia/Taipei'

export interface SystemDateParts {
  year: number
  month: number
  day: number
}

// Returns the numeric calendar parts for an instant in the requested time zone.
export function getSystemDateParts(
  instant: Date | string = new Date(),
  timeZone = DEFAULT_SYSTEM_TIME_ZONE,
): SystemDateParts {
  const date = instant instanceof Date ? instant : new Date(instant)
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(date)

  return {
    year: Number(parts.find(part => part.type === 'year')?.value),
    month: Number(parts.find(part => part.type === 'month')?.value),
    day: Number(parts.find(part => part.type === 'day')?.value),
  }
}

// Converts an instant to the YYYY-MM-DD value expected by native date inputs.
export function getDateInputValue(
  instant: Date | string = new Date(),
  timeZone = DEFAULT_SYSTEM_TIME_ZONE,
): string {
  const { year, month, day } = getSystemDateParts(instant, timeZone)
  return `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`
}

// Formats a date-only value without interpreting it as a timestamp.
export function formatDateOnly(dateInput: string, separator = '/'): string {
  const [year, month, day] = dateInput.slice(0, 10).split('-')
  if (!year || !month || !day) return dateInput
  return [year, month, day].join(separator)
}

// Compares two YYYY-MM-DD values without converting either calendar date to an instant.
export function isDateOnlyBefore(dateInput: string, otherDateInput: string): boolean {
  return dateInput.slice(0, 10) < otherDateInput.slice(0, 10)
}

// Adds calendar years to a date-only value and clamps invalid leap-day targets.
export function addCalendarYears(dateInput: string, years: number): string {
  const [year, month, day] = dateInput.slice(0, 10).split('-').map(Number)
  const targetYear = year + years
  const lastDay = new Date(targetYear, month, 0).getDate()
  const targetDay = Math.min(day, lastDay)
  return `${targetYear}-${String(month).padStart(2, '0')}-${String(targetDay).padStart(2, '0')}`
}

// Adds calendar days to a date-only value without converting through UTC.
export function addCalendarDays(dateInput: string, days: number): string {
  const [year, month, day] = dateInput.slice(0, 10).split('-').map(Number)
  const date = new Date(year, month - 1, day)
  date.setDate(date.getDate() + days)
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

// Formats an event timestamp as a system-local YYYY/MM/DD HH:mm string.
export function formatSystemDateTime(
  instant: Date | string,
  timeZone = DEFAULT_SYSTEM_TIME_ZONE,
): string {
  const date = instant instanceof Date ? instant : new Date(instant)
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).formatToParts(date)
  const value = (type: string) => parts.find(part => part.type === type)?.value ?? ''
  return `${value('year')}/${value('month')}/${value('day')} ${value('hour')}:${value('minute')}`
}

// Returns the first and last date of the current system-local month.
export function getCurrentMonthRange(
  instant: Date | string = new Date(),
  timeZone = DEFAULT_SYSTEM_TIME_ZONE,
): { start: string; end: string } {
  const { year, month } = getSystemDateParts(instant, timeZone)
  const lastDay = new Date(year, month, 0).getDate()
  return {
    start: `${year}-${String(month).padStart(2, '0')}-01`,
    end: `${year}-${String(month).padStart(2, '0')}-${String(lastDay).padStart(2, '0')}`,
  }
}

// Returns the first and last date of the current system-local year.
export function getCurrentYearRange(
  instant: Date | string = new Date(),
  timeZone = DEFAULT_SYSTEM_TIME_ZONE,
): { start: string; end: string } {
  const { year } = getSystemDateParts(instant, timeZone)
  return { start: `${year}-01-01`, end: `${year}-12-31` }
}
