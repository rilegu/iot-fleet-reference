import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from 'react'
import type { ConnectionStatus, DeviceEvent, DeviceState, TelemetryPoint } from '@shared/contract'
import { FleetStore } from './store'
import { DEFAULT_FILTERS, projectDevices, sitesOf, type Filters, type SortColumn } from './project'
import { buildSpark, type Spark } from './spark'

// One store per application, created outside React. Fleet state is not React state: it
// arrives from another process on a fixed cadence and exists whether or not anything is
// rendering. `useSyncExternalStore` is the supported way to read exactly that.
const store = new FleetStore()

// Subscribing at module scope rather than in an effect, and only then telling the main
// process to connect. Doing it in an effect would leave a window in which the socket is open
// and the snapshot has already been sent past a renderer that is not listening yet; this
// ordering makes that window impossible rather than merely unlikely.
window.fleet.onFrame((frame) => store.applyFrame(frame))
void window.fleet.ready()

export function useFleetDevices(): DeviceState[] {
  return useSyncExternalStore(store.subscribe, store.snapshot)
}

export function useFleetAggregates() {
  // The store's aggregates object is replaced wholesale per frame, so identity comparison is
  // exactly right here.
  return useSyncExternalStore(
    store.subscribe,
    useCallback(() => store.aggregates, []),
  )
}

export function useFleetFrameInfo() {
  return useSyncExternalStore(
    store.subscribe,
    // A fresh object per read would loop, so this returns a primitive the caller unpacks.
    useCallback(() => `${store.lastFrame}:${store.framesApplied}:${store.cadenceMs}`, []),
  )
}

/** Transport state, which lives in the main process and arrives over the bridge. */
export function useConnectionStatus(): ConnectionStatus | null {
  const [status, setStatus] = useState<ConnectionStatus | null>(null)

  useEffect(() => {
    let live = true
    void window.fleet.status().then((initial) => {
      if (live) setStatus(initial)
    })
    const unsubscribe = window.fleet.onStatus(setStatus)
    return () => {
      live = false
      unsubscribe()
    }
  }, [])

  return status
}

/**
 * Filter, sort and selection state.
 *
 * Kept in React rather than in the store, which is the deliberate counterpart of the same
 * split in the other clients: fleet state is shared and arrives from outside, view state is
 * per-view and belongs to the view. Two windows open on the same fleet should not share a
 * search box.
 *
 * The projection is memoised on the device array and the filters. Filtering and sorting a
 * thousand devices is a few hundred microseconds — negligible once per frame, wasteful if
 * React re-runs it on every unrelated render.
 */
export function useFleetView() {
  const devices = useFleetDevices()
  const [filters, setFilters] = useState<Filters>(DEFAULT_FILTERS)
  const [selectedId, setSelectedId] = useState<string | null>(null)

  const visible = useMemo(() => projectDevices(devices, filters), [devices, filters])
  const sites = useMemo(() => sitesOf(devices), [devices])

  // Resolved from the store every frame rather than held as an object, so the detail panel
  // keeps updating live while it is open.
  const selected = selectedId === null ? null : (devices.find((d) => d.device_id === selectedId) ?? null)

  const sortBy = useCallback((column: SortColumn) => {
    // Clicking the active column reverses it; clicking another switches to it ascending.
    setFilters((current) =>
      current.sort === column
        ? { ...current, descending: !current.descending }
        : { ...current, sort: column, descending: false },
    )
  }, [])

  const patch = useCallback((changes: Partial<Filters>) => {
    setFilters((current) => ({ ...current, ...changes }))
  }, [])

  const clearFilters = useCallback(() => setFilters(DEFAULT_FILTERS), [])

  return {
    devices,
    visible,
    sites,
    filters,
    setFilters: patch,
    sortBy,
    clearFilters,
    selected,
    select: setSelectedId,
  }
}

export interface DeviceDetail {
  loading: boolean
  spark: Spark
  events: DeviceEvent[]
}

const EMPTY_SPARK: Spark = { points: [], min: 0, max: 0 }

/** What was loaded, and which device it belongs to. The pairing is what makes `loading` derivable. */
interface LoadedDetail {
  deviceId: string | null
  spark: Spark
  events: DeviceEvent[]
}

const NOTHING_LOADED: LoadedDetail = { deviceId: null, spark: EMPTY_SPARK, events: [] }

/**
 * History and events for the selected device.
 *
 * These need a request, because the realtime channel carries current state only: pushing
 * every device's history to every client would undo the saving the delta protocol exists to
 * make. They fire once per selection, not once per frame.
 *
 * The `cancelled` flag is what stops a slow earlier response landing after a faster later one
 * and leaving another device's history on screen — the same hazard the XAML clients handle
 * with a CancellationToken.
 */
export function useDeviceDetail(deviceId: string | null): DeviceDetail {
  const [loaded, setLoaded] = useState<LoadedDetail>(NOTHING_LOADED)

  useEffect(() => {
    if (deviceId === null) return

    let cancelled = false

    // Independent reads, so issued together rather than in sequence.
    void Promise.all([window.fleet.history(deviceId, 60), window.fleet.events(deviceId, 20)])
      .then(([history, events]: [TelemetryPoint[], DeviceEvent[]]) => {
        if (!cancelled) setLoaded({ deviceId, spark: buildSpark(history), events })
      })
      .catch(() => {
        // A failed history read must not take the panel down: the live fields still work, and
        // the chart simply shows nothing. Recording the device id anyway stops it retrying in
        // a loop and leaves the panel showing an empty chart rather than a stuck spinner.
        if (!cancelled) setLoaded({ deviceId, spark: EMPTY_SPARK, events: [] })
      })

    return () => {
      cancelled = true
    }
  }, [deviceId])

  // Derived during render, not set from the effect. The panel is loading exactly when what it
  // holds does not belong to the device being shown, so a separate loading flag would be a
  // second copy of that fact — and setting it inside the effect would cost an extra render
  // pass on every selection, which at a click per row is the whole panel rendering twice.
  const current = loaded.deviceId === deviceId
  return {
    loading: deviceId !== null && !current,
    spark: current ? loaded.spark : EMPTY_SPARK,
    events: current ? loaded.events : [],
  }
}
