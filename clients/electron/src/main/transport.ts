import type { ConnectionStatus, DeviceEvent, ServerFrame, TelemetryPoint } from '@shared/contract'

export interface TransportOptions {
  /** Base address of the API, for example http://localhost:8080. */
  apiUrl: string
  /**
   * Frames per second to request. The server treats this as a ceiling it will honour by
   * slowing down, never as a request to speed up.
   */
  maxRateHz: number
}

/**
 * One client session: a WebSocket for live state, REST for what the socket does not carry.
 *
 * This runs in the **main** process, not the renderer, and that is the one genuinely
 * structural difference between this client and the .NET ones.
 *
 * The renderer is a Chromium page, so every request it makes is subject to the same-origin
 * policy. The API sets no CORS headers — it has never needed any, because WPF and WinUI are
 * not browsers and Blazor Server calls it from the server side — so a `fetch` from the
 * renderer to a different origin is refused before it is sent. Three ways out of that:
 *
 *   1. Add a CORS policy to the API. Rejected: adding a UI framework must not require a
 *      backend change, which is the claim this whole repository is built to support.
 *   2. Disable web security in the renderer. Rejected outright — it turns off the origin
 *      model for every page the app will ever load.
 *   3. Do the network here, in Node, where the same-origin policy does not apply, and hand
 *      results to the renderer over a narrow typed channel.
 *
 * The third is also the arrangement a security review would ask for anyway: the renderer
 * stays sandboxed with no direct network access at all. The cost is that every frame crosses
 * a process boundary and is structured-cloned. At a coalesced four frames a second that is
 * not measurable, which is worth stating plainly rather than leaving as an assumption.
 *
 * Node 22 and later expose `WebSocket` and `fetch` as globals, so this needs no transport
 * dependency.
 */
export class FleetTransport {
  private socket: WebSocket | null = null
  private stopped = false
  private attempt = 0
  private reconnectTimer: NodeJS.Timeout | null = null
  private status: ConnectionStatus

  constructor(
    private readonly options: TransportOptions,
    private readonly onFrame: (frame: ServerFrame) => void,
    private readonly onStatus: (status: ConnectionStatus) => void,
  ) {
    this.status = { connected: false, error: null, apiUrl: options.apiUrl, cadenceMs: null }
  }

  currentStatus(): ConnectionStatus {
    return this.status
  }

  start(): void {
    this.stopped = false
    this.open()
  }

  stop(): void {
    this.stopped = true
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer)
    this.reconnectTimer = null
    this.socket?.close()
    this.socket = null
  }

  private setStatus(patch: Partial<ConnectionStatus>): void {
    this.status = { ...this.status, ...patch }
    this.onStatus(this.status)
  }

  private open(): void {
    if (this.stopped) return

    // Derive the WebSocket scheme from the configured base address rather than doing string
    // surgery on the whole URL: replacing 'http' anywhere in the string would also rewrite a
    // host that happens to contain it.
    const base = new URL(this.options.apiUrl)
    const wsUrl = new URL(base.toString())
    wsUrl.protocol = base.protocol === 'https:' ? 'wss:' : 'ws:'
    wsUrl.pathname = '/ws/fleet'

    const socket = new WebSocket(wsUrl.toString())
    this.socket = socket

    socket.addEventListener('open', () => {
      // Asking for a cadence is optional — a client that says nothing gets server defaults —
      // but stating it makes this client's expectations explicit, and it is the knob the
      // framework comparison varies.
      socket.send(JSON.stringify({ type: 'subscribe', max_rate_hz: this.options.maxRateHz }))
      this.attempt = 0
      this.setStatus({ connected: true, error: null })
    })

    socket.addEventListener('message', (event) => {
      // A malformed frame must not kill the session. One unparseable message is a bug worth
      // reporting; a transport that tears down the connection over it turns a glitch into an
      // outage.
      try {
        const frame = JSON.parse(String(event.data)) as ServerFrame
        if (frame.type === 'snapshot' || frame.type === 'delta') {
          if (frame.cadence_ms) this.setStatus({ cadenceMs: frame.cadence_ms })
          this.onFrame(frame)
        }
      } catch (err) {
        console.error('unreadable frame', err)
      }
    })

    socket.addEventListener('error', () => {
      // The error event carries nothing useful in the WHATWG API; 'close' follows and does
      // the reconnecting.
      this.setStatus({ error: 'connection failed' })
    })

    socket.addEventListener('close', () => {
      this.socket = null
      this.setStatus({ connected: false })
      this.scheduleReconnect()
    })
  }

  /**
   * Capped, jittered backoff, matching the .NET client's schedule.
   *
   * A dashboard that dies when the API restarts is useless during exactly the events an
   * operator cares about, so a dropped socket is a normal condition rather than an error.
   * The jitter matters at fleet scale: without it every client reconnects on the same tick
   * after an outage and the API takes a thundering herd at the worst possible moment.
   */
  private scheduleReconnect(): void {
    if (this.stopped || this.reconnectTimer) return

    this.attempt = Math.min(this.attempt + 1, 6)
    const backoff = Math.min(500 * 2 ** this.attempt, 15_000) + Math.floor(Math.random() * 400)

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null
      this.open()
    }, backoff)
  }

  // -------------------------------------------------------------------------------------
  // REST queries. Live state comes over the socket; these are for what the socket does not
  // carry — history and the event feed, read on demand rather than pushed.
  // -------------------------------------------------------------------------------------

  async history(deviceId: string, minutes: number): Promise<TelemetryPoint[]> {
    return this.get<TelemetryPoint[]>(
      `/api/devices/${encodeURIComponent(deviceId)}/history?minutes=${minutes}`,
    )
  }

  async events(deviceId: string | null, limit: number): Promise<DeviceEvent[]> {
    const url =
      deviceId === null
        ? `/api/events?limit=${limit}`
        : `/api/events?device=${encodeURIComponent(deviceId)}&limit=${limit}`
    return this.get<DeviceEvent[]>(url)
  }

  private async get<T>(path: string): Promise<T> {
    const response = await fetch(new URL(path, this.options.apiUrl))
    if (!response.ok) throw new Error(`${path} returned ${response.status}`)
    return (await response.json()) as T
  }
}
