import type { DeviceState, FleetAggregates, ServerFrame } from '@shared/contract'

const EMPTY_AGGREGATES: FleetAggregates = {
  total: 0,
  online: 0,
  offline: 0,
  alerting: 0,
  gaps: 0,
  applied: 0,
  stale_dropped: 0,
  sites: 0,
}

/**
 * Client-side fleet state, rebuilt from the snapshot/delta stream.
 *
 * This is the TypeScript implementation of the same reconciler that `Fleet.Client.Core`
 * implements in C#. The duplication is deliberate — a .NET library cannot be consumed from a
 * Chromium renderer, and compiling one to WebAssembly to avoid rewriting two hundred lines
 * would make this client a wrapper rather than an implementation, which would say nothing
 * about the framework. What is *not* left to chance is that the two agree: `test/store.test.ts`
 * mirrors `Fleet.Client.Core.Tests` case for case, so a divergence fails a build rather than
 * showing up as one dashboard quietly disagreeing with the others.
 *
 * It is a plain observable object rather than a React store on purpose. Fleet state arrives
 * four times a second from outside React entirely, and `useSyncExternalStore` is built for
 * exactly that shape.
 */
export class FleetStore {
  private devices = new Map<string, DeviceState>()
  private lastChanged: string[] = []
  private aggregatesValue: FleetAggregates = EMPTY_AGGREGATES
  private listeners = new Set<() => void>()

  /** Cached array form of `devices`, rebuilt only when a frame is applied. See `snapshot`. */
  private cached: DeviceState[] = []

  private versionValue = 0
  private cadenceMsValue = 0
  private lastFrameValue = 0
  private framesAppliedValue = 0

  /** Increments on every applied frame. A cheap way for a view to tell whether anything moved. */
  get version(): number {
    return this.versionValue
  }

  get aggregates(): FleetAggregates {
    return this.aggregatesValue
  }

  /** Cadence the server told us it is sending at, useful for display and for measurement. */
  get cadenceMs(): number {
    return this.cadenceMsValue
  }

  get lastFrame(): number {
    return this.lastFrameValue
  }

  /**
   * Frames applied since connecting. Compared against `lastFrame` this reveals dropped
   * frames, which would otherwise be invisible.
   */
  get framesApplied(): number {
    return this.framesAppliedValue
  }

  get(deviceId: string): DeviceState | undefined {
    return this.devices.get(deviceId)
  }

  /**
   * Subscribes to frame application. Returns an unsubscribe function, which is the shape
   * `useSyncExternalStore` wants.
   *
   * Fired once per frame, never once per device: the server already coalesces changes into a
   * frame, and re-firing per device would hand the UI back the very fan-out the delta
   * protocol exists to remove.
   */
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }

  /**
   * The current fleet as an array.
   *
   * The .NET store materialises a fresh copy on every call. Here the copy is cached and
   * rebuilt only when a frame is applied, because React demands it: `useSyncExternalStore`
   * compares snapshots by identity and re-renders whenever it sees a new one, so a method
   * that allocated per call would report a change on every render and loop forever. Same
   * data, same cost per frame, different rule about who may allocate when.
   */
  snapshot = (): DeviceState[] => this.cached

  /**
   * Replaces the entire fleet. Sent once when a connection is established, and again after a
   * reconnect, since anything could have changed while the socket was down.
   */
  applySnapshot(devices: DeviceState[], aggregates: FleetAggregates | undefined, cadenceMs: number, frame: number): void {
    this.devices = new Map(devices.map((d) => [d.device_id, d]))
    this.lastChanged = []

    if (aggregates) this.aggregatesValue = aggregates
    this.cadenceMsValue = cadenceMs
    this.lastFrameValue = frame
    this.commit(true)
  }

  /**
   * Applies a delta frame: only devices that changed since the previous frame.
   *
   * No ordering rule is applied here. The server's projection already enforces
   * `(boot_id, seq)` ordering and sends whole current records, so a client applying frames in
   * the order they arrive on a single ordered stream cannot go backwards. Re-deriving that
   * logic per client language would duplicate it four times over, and any divergence would be
   * a bug that manifests in exactly one dashboard.
   */
  applyDelta(changed: DeviceState[], aggregates: FleetAggregates | undefined, frame: number): void {
    // Clear the previous frame's highlight before marking this one, so a flash lasts exactly
    // one frame rather than accumulating until every row is lit and the cue means nothing.
    for (const id of this.lastChanged) {
      const previous = this.devices.get(id)
      if (previous?.justChanged) this.devices.set(id, { ...previous, justChanged: false })
    }
    this.lastChanged = []

    for (const device of changed) {
      this.devices.set(device.device_id, { ...device, justChanged: true })
      this.lastChanged.push(device.device_id)
    }

    if (aggregates) this.aggregatesValue = aggregates
    this.lastFrameValue = frame
    this.commit(true)
  }

  /** Routes a frame to the right apply. The only place frame `type` is interpreted. */
  applyFrame(frame: ServerFrame): void {
    if (frame.type === 'snapshot') {
      this.applySnapshot(frame.devices ?? [], frame.aggregates, frame.cadence_ms, frame.frame)
    } else {
      this.applyDelta(frame.changed ?? [], frame.aggregates, frame.frame)
    }
  }

  clear(): void {
    this.devices = new Map()
    this.lastChanged = []
    this.aggregatesValue = EMPTY_AGGREGATES
    // Not a frame: clearing is a local reset, and counting it would corrupt the
    // frames-applied figure that dropped-frame detection reads.
    this.commit(false)
  }

  private commit(appliedFrame: boolean): void {
    this.versionValue += 1
    if (appliedFrame) this.framesAppliedValue += 1
    this.cached = [...this.devices.values()]
    for (const listener of this.listeners) listener()
  }
}
