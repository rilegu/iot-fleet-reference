import { describe, expect, it, vi } from 'vitest'
import type { DeviceState, FleetAggregates } from '../src/shared/contract'
import { FleetStore } from '../src/renderer/src/fleet/store'

/**
 * These mirror `Fleet.Client.Core.Tests` case for case.
 *
 * That is the entire point of writing them: the reconciler exists twice, in two languages,
 * because a .NET library cannot run in a Chromium renderer. Duplicated logic diverges unless
 * something checks, so the same scenarios are pinned on both sides and a disagreement fails a
 * build rather than surfacing as one dashboard showing different numbers from another.
 */

function device(id: string, overrides: Partial<DeviceState> = {}): DeviceState {
  return {
    device_id: id,
    site: 'site-00',
    boot_id: 'aaaaaaaaaaaaaaaa',
    online: true,
    seq: 1,
    gaps: 0,
    last_seen: '2026-08-21T12:00:00Z',
    metrics: { temp_c: 20, humidity_pct: 40, voltage_v: 12, rssi_dbm: -60, uptime_s: 100 },
    ...overrides,
  }
}

const aggregates = (patch: Partial<FleetAggregates> = {}): FleetAggregates => ({
  total: 0,
  online: 0,
  offline: 0,
  alerting: 0,
  gaps: 0,
  applied: 0,
  stale_dropped: 0,
  sites: 0,
  ...patch,
})

describe('FleetStore', () => {
  it('replaces everything on a snapshot', () => {
    const store = new FleetStore()
    store.applySnapshot([device('dev-1'), device('dev-2')], undefined, 250, 1)
    store.applySnapshot([device('dev-3')], undefined, 250, 2)

    expect(store.snapshot().map((d) => d.device_id)).toEqual(['dev-3'])
    expect(store.get('dev-1')).toBeUndefined()
  })

  it('updates only the devices a delta carries', () => {
    const store = new FleetStore()
    store.applySnapshot([device('dev-1', { seq: 1 }), device('dev-2', { seq: 1 })], undefined, 250, 1)
    store.applyDelta([device('dev-1', { seq: 9 })], undefined, 2)

    expect(store.get('dev-1')?.seq).toBe(9)
    expect(store.get('dev-2')?.seq).toBe(1)
  })

  it('adds devices not seen before', () => {
    const store = new FleetStore()
    store.applySnapshot([device('dev-1')], undefined, 250, 1)
    store.applyDelta([device('dev-2')], undefined, 2)

    expect(store.snapshot()).toHaveLength(2)
    expect(store.get('dev-2')).toBeDefined()
  })

  /**
   * The highlight marks devices from the most recent frame only. If it accumulated, every row
   * would end up flagged within seconds and the cue would stop meaning anything.
   */
  it('keeps the change highlight for exactly one frame', () => {
    const store = new FleetStore()
    store.applySnapshot([device('dev-1'), device('dev-2')], undefined, 250, 1)

    store.applyDelta([device('dev-1', { seq: 2 })], undefined, 2)
    expect(store.get('dev-1')?.justChanged).toBe(true)
    expect(store.get('dev-2')?.justChanged).toBeFalsy()

    store.applyDelta([device('dev-2', { seq: 2 })], undefined, 3)
    expect(store.get('dev-1')?.justChanged).toBe(false)
    expect(store.get('dev-2')?.justChanged).toBe(true)
  })

  it('changes version on every applied frame', () => {
    const store = new FleetStore()
    const first = store.version

    store.applySnapshot([device('dev-1')], undefined, 250, 1)
    const second = store.version
    store.applyDelta([], undefined, 2)

    expect(second).not.toBe(first)
    expect(store.version).not.toBe(second)
  })

  /**
   * One notification per frame, not one per device. Notifying per device would hand the UI
   * back the fan-out the delta protocol exists to remove.
   */
  it('notifies once per frame regardless of device count', () => {
    const store = new FleetStore()
    const listener = vi.fn()
    store.subscribe(listener)

    store.applySnapshot(Array.from({ length: 500 }, (_, i) => device(`dev-${i}`)), undefined, 250, 1)
    expect(listener).toHaveBeenCalledTimes(1)

    store.applyDelta(Array.from({ length: 200 }, (_, i) => device(`dev-${i}`, { seq: 2 })), undefined, 2)
    expect(listener).toHaveBeenCalledTimes(2)
  })

  it('unsubscribes cleanly', () => {
    const store = new FleetStore()
    const listener = vi.fn()
    const unsubscribe = store.subscribe(listener)

    store.applyDelta([], undefined, 1)
    unsubscribe()
    store.applyDelta([], undefined, 2)

    expect(listener).toHaveBeenCalledTimes(1)
  })

  it('tracks the server frame number and how many frames it applied', () => {
    const store = new FleetStore()
    store.applySnapshot([], undefined, 250, 40)
    store.applyDelta([], undefined, 41)
    store.applyDelta([], undefined, 44) // the server skipped ahead; the client did not

    expect(store.lastFrame).toBe(44)
    expect(store.framesApplied).toBe(3)
  })

  it('keeps aggregates through a frame that omits them', () => {
    const store = new FleetStore()
    store.applySnapshot([], aggregates({ total: 1000, online: 999 }), 250, 1)
    store.applyDelta([], undefined, 2)

    expect(store.aggregates.total).toBe(1000)
    expect(store.aggregates.online).toBe(999)
  })

  /**
   * The snapshot's identity must change when and only when a frame is applied. React's
   * `useSyncExternalStore` compares it by reference: a fresh array per call would re-render
   * forever, and a reused array after a frame would never re-render at all.
   */
  it('returns a stable snapshot between frames and a new one after each', () => {
    const store = new FleetStore()
    store.applySnapshot([device('dev-1')], undefined, 250, 1)

    const first = store.snapshot()
    expect(store.snapshot()).toBe(first)

    store.applyDelta([device('dev-1', { seq: 2 })], undefined, 2)
    expect(store.snapshot()).not.toBe(first)
  })

  it('routes frames by type', () => {
    const store = new FleetStore()
    store.applyFrame({ type: 'snapshot', frame: 1, cadence_ms: 250, devices: [device('dev-1')] })
    expect(store.snapshot()).toHaveLength(1)
    expect(store.cadenceMs).toBe(250)

    store.applyFrame({ type: 'delta', frame: 2, cadence_ms: 250, changed: [device('dev-2')] })
    expect(store.snapshot()).toHaveLength(2)
  })

  it('does not count a local clear as an applied frame', () => {
    const store = new FleetStore()
    store.applySnapshot([device('dev-1')], undefined, 250, 1)
    store.clear()

    expect(store.snapshot()).toHaveLength(0)
    expect(store.framesApplied).toBe(1)
  })
})
