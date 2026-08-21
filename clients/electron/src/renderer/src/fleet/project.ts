import type { DeviceState } from '@shared/contract'

export type SortColumn =
  | 'device'
  | 'site'
  | 'status'
  | 'temp'
  | 'voltage'
  | 'rssi'
  | 'seq'
  | 'gaps'

export interface Filters {
  search: string
  site: string | null
  onlineOnly: boolean
  alertingOnly: boolean
  sort: SortColumn
  descending: boolean
}

export const DEFAULT_FILTERS: Filters = {
  search: '',
  site: null,
  onlineOnly: false,
  alertingOnly: false,
  sort: 'device',
  descending: false,
}

/** A device is alerting when its last event was serious enough to warrant attention. */
export function isAlerting(device: DeviceState): boolean {
  return device.last_event_severity === 'warning' || device.last_event_severity === 'critical'
}

/**
 * Filters and sorts the fleet for display.
 *
 * A pure function of `(devices, filters)`, deliberately outside React. That keeps it
 * testable without rendering anything, and it makes the memoisation in `useFleetView`
 * obviously correct: the output depends on the two arguments and nothing else.
 *
 * The semantics match `FleetView` in the Blazor client and `FleetViewModel` in the XAML
 * clients, down to the comparison rules — device ids and sites are machine identifiers, so
 * they sort by code unit rather than by locale, which is both faster and free of the
 * surprises locale-aware collation produces on identifiers.
 */
export function projectDevices(devices: readonly DeviceState[], filters: Filters): DeviceState[] {
  let result = devices as DeviceState[]

  const term = filters.search.trim().toLowerCase()
  if (term.length > 0) {
    result = result.filter(
      (d) =>
        d.device_id.toLowerCase().includes(term) ||
        d.site.toLowerCase().includes(term) ||
        (d.fw_version ?? '').toLowerCase().includes(term),
    )
  }

  if (filters.site) result = result.filter((d) => d.site === filters.site)
  if (filters.onlineOnly) result = result.filter((d) => d.online)
  if (filters.alertingOnly) result = result.filter(isAlerting)

  // Copy before sorting: the input is the store's cached array, and sorting in place would
  // reorder state the store still owns and hands to everything else.
  const sorted = result === devices ? [...result] : result
  const direction = filters.descending ? -1 : 1
  sorted.sort((a, b) => direction * compare(a, b, filters.sort))
  return sorted
}

function compare(a: DeviceState, b: DeviceState, sort: SortColumn): number {
  switch (sort) {
    case 'site':
      return ordinal(a.site, b.site) || ordinal(a.device_id, b.device_id)
    case 'status':
      return Number(a.online) - Number(b.online) || ordinal(a.device_id, b.device_id)
    case 'temp':
      return numeric(a.metrics?.temp_c, b.metrics?.temp_c) || ordinal(a.device_id, b.device_id)
    case 'voltage':
      return numeric(a.metrics?.voltage_v, b.metrics?.voltage_v) || ordinal(a.device_id, b.device_id)
    case 'rssi':
      return numeric(a.metrics?.rssi_dbm, b.metrics?.rssi_dbm) || ordinal(a.device_id, b.device_id)
    case 'seq':
      return a.seq - b.seq || ordinal(a.device_id, b.device_id)
    case 'gaps':
      return a.gaps - b.gaps || ordinal(a.device_id, b.device_id)
    default:
      return ordinal(a.device_id, b.device_id)
  }
}

/**
 * Ordinal string comparison.
 *
 * `Array.prototype.sort` is stable, but stability only preserves the order of the *input*,
 * and the store's array is a Map iteration order that changes whenever a snapshot arrives.
 * Every comparison therefore falls back to the device id, so a re-sort on equal keys does not
 * shuffle rows under the reader.
 */
function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0
}

/** Missing readings sort as the lowest value, matching the .NET clients. */
function numeric(a: number | undefined, b: number | undefined): number {
  return (a ?? Number.NEGATIVE_INFINITY) - (b ?? Number.NEGATIVE_INFINITY)
}

export function sitesOf(devices: readonly DeviceState[]): string[] {
  return [...new Set(devices.map((d) => d.site))].sort(ordinal)
}
