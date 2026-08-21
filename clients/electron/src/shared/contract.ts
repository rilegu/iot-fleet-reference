// Wire types for the API's REST and WebSocket surface.
//
// The TypeScript counterpart of Fleet.Client.Core/Contract.cs, hand-written for the same
// reason: once the OpenAPI document drives codegen these become generated, and no client
// will be able to drift from the server by editing a model.
//
// Field names stay snake_case, exactly as they arrive. Mapping them to camelCase would add
// a translation layer whose only job is to be kept in sync with the contract, and a silent
// mismatch there is far harder to see than an unfamiliar property name.

export interface Metrics {
  temp_c: number
  humidity_pct: number
  voltage_v: number
  rssi_dbm: number
  uptime_s: number
}

export interface DeviceState {
  device_id: string
  site: string
  boot_id: string
  online: boolean
  offline_reason?: string | null
  fw_version?: string | null
  model?: string | null
  seq: number
  gaps: number
  metrics?: Metrics | null
  last_event?: string | null
  last_event_severity?: string | null
  last_seen: string

  /**
   * True when this device changed in the most recent delta frame. Purely a view concern —
   * the grid flashes the row — and never present on the wire. The .NET record marks the
   * equivalent field `[JsonIgnore]`; here it is simply never serialised back.
   */
  justChanged?: boolean
}

export interface FleetAggregates {
  total: number
  online: number
  offline: number
  alerting: number
  gaps: number
  applied: number
  stale_dropped: number
  sites: number
}

/**
 * Frames arriving on the WebSocket. A snapshot carries the whole fleet once; every frame
 * after it carries only what changed.
 */
export interface ServerFrame {
  type: 'snapshot' | 'delta'
  frame: number
  cadence_ms: number
  devices?: DeviceState[]
  changed?: DeviceState[]
  aggregates?: FleetAggregates
}

export interface TelemetryPoint {
  bucket: string
  samples: number
  temp_c_avg?: number | null
  temp_c_max?: number | null
  humidity_pct_avg?: number | null
  voltage_v_avg?: number | null
  voltage_v_min?: number | null
  rssi_dbm_avg?: number | null
}

export interface DeviceEvent {
  received_at: string
  device_id: string
  site: string
  kind: string
  severity: string
  detail?: string | null
  metric?: string | null
  value?: number | null
}

/** Transport state, reported by the main process so the UI can say so rather than showing stale data silently. */
export interface ConnectionStatus {
  connected: boolean
  error: string | null
  apiUrl: string
  /** Cadence the server said it is sending at. Null until the first frame arrives. */
  cadenceMs: number | null
}

/**
 * The surface the preload bridge exposes on `window.fleet`.
 *
 * This interface is the whole contract between the two processes. Keeping it narrow and
 * explicitly typed is the point of context isolation: the renderer gets these functions and
 * nothing else — no `require`, no filesystem, no arbitrary network.
 */
export interface FleetBridge {
  /** Subscribes to server frames. Returns an unsubscribe function. */
  onFrame(handler: (frame: ServerFrame) => void): () => void
  /**
   * Tells the main process this renderer is listening, which is what starts the session.
   *
   * The snapshot is sent once, immediately on connect, and a frame sent to a renderer that
   * has not subscribed yet is simply dropped. Starting the socket when the window is created
   * therefore loses it every time on a fast API — leaving a client running on deltas alone,
   * which looks fine, because deltas refill the grid within a second. What never comes back
   * is any device that does not change: it stays missing until the next reconnect.
   */
  ready(): Promise<void>
  /** Subscribes to transport state changes. Returns an unsubscribe function. */
  onStatus(handler: (status: ConnectionStatus) => void): () => void
  /** Current transport state, for the initial render before any event arrives. */
  status(): Promise<ConnectionStatus>
  history(deviceId: string, minutes: number): Promise<TelemetryPoint[]>
  events(deviceId: string | null, limit: number): Promise<DeviceEvent[]>
}

export const IPC = {
  ready: 'fleet:ready',
  frame: 'fleet:frame',
  status: 'fleet:status',
  statusQuery: 'fleet:status-query',
  history: 'fleet:history',
  events: 'fleet:events',
} as const
