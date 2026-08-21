import { describe, expect, it } from 'vitest'
import type { DeviceState, TelemetryPoint } from '../src/shared/contract'
import { DEFAULT_FILTERS, projectDevices, sitesOf, type Filters } from '../src/renderer/src/fleet/project'
import { buildSpark } from '../src/renderer/src/fleet/spark'

function device(id: string, overrides: Partial<DeviceState> = {}): DeviceState {
  return {
    device_id: id,
    site: 'site-00',
    boot_id: 'aaaaaaaaaaaaaaaa',
    online: true,
    seq: 1,
    gaps: 0,
    last_seen: '2026-08-21T12:00:00Z',
    fw_version: '1.4.2',
    metrics: { temp_c: 20, humidity_pct: 40, voltage_v: 12, rssi_dbm: -60, uptime_s: 100 },
    ...overrides,
  }
}

const filters = (patch: Partial<Filters> = {}): Filters => ({ ...DEFAULT_FILTERS, ...patch })

describe('projectDevices', () => {
  const fleet = [
    device('dev-3', { site: 'site-02', metrics: undefined, online: false, offline_reason: 'lwt' }),
    device('dev-1', { site: 'site-01', fw_version: '2.0.0', metrics: { temp_c: 40, humidity_pct: 1, voltage_v: 11, rssi_dbm: -80, uptime_s: 1 } }),
    device('dev-2', { site: 'site-01', last_event_severity: 'critical', last_event: 'brownout' }),
  ]

  it('sorts by device id by default', () => {
    expect(projectDevices(fleet, filters()).map((d) => d.device_id)).toEqual(['dev-1', 'dev-2', 'dev-3'])
  })

  it('reverses when descending', () => {
    expect(projectDevices(fleet, filters({ descending: true })).map((d) => d.device_id)).toEqual([
      'dev-3',
      'dev-2',
      'dev-1',
    ])
  })

  it('searches device id, site and firmware', () => {
    expect(projectDevices(fleet, filters({ search: 'site-02' })).map((d) => d.device_id)).toEqual(['dev-3'])
    expect(projectDevices(fleet, filters({ search: '2.0.0' })).map((d) => d.device_id)).toEqual(['dev-1'])
    expect(projectDevices(fleet, filters({ search: 'DEV-2' })).map((d) => d.device_id)).toEqual(['dev-2'])
  })

  it('filters by site, online and alerting', () => {
    expect(projectDevices(fleet, filters({ site: 'site-01' }))).toHaveLength(2)
    expect(projectDevices(fleet, filters({ onlineOnly: true }))).toHaveLength(2)
    expect(projectDevices(fleet, filters({ alertingOnly: true })).map((d) => d.device_id)).toEqual(['dev-2'])
  })

  /** A device with no metrics yet must sort somewhere defined, not throw and not float to the top. */
  it('sorts devices without readings below those with them', () => {
    const order = projectDevices(fleet, filters({ sort: 'temp' })).map((d) => d.device_id)
    expect(order[0]).toBe('dev-3')
    expect(order.at(-1)).toBe('dev-1')
  })

  /**
   * The store's array is a Map iteration order, which changes whenever a snapshot arrives.
   * Sorting on equal keys must therefore be decided by something stable, or rows shuffle under
   * the reader between frames for no reason.
   */
  it('breaks ties by device id so equal keys never reorder', () => {
    const tied = [device('dev-c'), device('dev-a'), device('dev-b')]
    const forward = projectDevices(tied, filters({ sort: 'seq' })).map((d) => d.device_id)
    const reversedInput = projectDevices([...tied].reverse(), filters({ sort: 'seq' })).map((d) => d.device_id)

    expect(forward).toEqual(['dev-a', 'dev-b', 'dev-c'])
    expect(reversedInput).toEqual(forward)
  })

  /** The input is the store's own cached array; sorting it in place would reorder shared state. */
  it('does not mutate the array it is given', () => {
    const input = [device('dev-b'), device('dev-a')]
    const before = input.map((d) => d.device_id)
    projectDevices(input, filters())

    expect(input.map((d) => d.device_id)).toEqual(before)
  })

  it('lists sites in order without duplicates', () => {
    expect(sitesOf(fleet)).toEqual(['site-01', 'site-02'])
  })
})

describe('buildSpark', () => {
  const point = (temp: number | null): TelemetryPoint => ({
    bucket: '2026-08-21T12:00:00Z',
    samples: 1,
    temp_c_avg: temp,
  })

  it('normalises to the unit box with hot at the top', () => {
    const spark = buildSpark([point(10), point(15), point(20)])

    expect(spark.min).toBe(10)
    expect(spark.max).toBe(20)
    expect(spark.points.map((p) => p.x)).toEqual([0, 50, 100])
    // Inverted: the coldest reading sits at the bottom of the box, not the top.
    expect(spark.points.map((p) => p.y)).toEqual([100, 50, 0])
  })

  /**
   * A device sitting at a constant temperature has zero range. Without the clamp this divides
   * by zero, and the line becomes NaN vertices that draw as nothing at all.
   */
  it('draws a flat series as a flat line rather than dividing by zero', () => {
    const spark = buildSpark([point(21), point(21), point(21)])

    expect(spark.points.every((p) => Number.isFinite(p.y))).toBe(true)
    expect(spark.points.every((p) => p.y === 100)).toBe(true)
  })

  it('skips buckets without a temperature', () => {
    const spark = buildSpark([point(10), point(null), point(20)])

    expect(spark.points).toHaveLength(2)
    expect(spark.min).toBe(10)
    expect(spark.max).toBe(20)
  })

  it('produces no line from fewer than two readings', () => {
    expect(buildSpark([point(10)]).points).toEqual([])
    expect(buildSpark([]).points).toEqual([])
  })
})
