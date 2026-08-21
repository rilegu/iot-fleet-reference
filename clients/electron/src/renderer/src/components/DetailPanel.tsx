import type { DeviceState } from '@shared/contract'
import { useDeviceDetail } from '@/fleet/useFleet'

interface Props {
  device: DeviceState
  onClose: () => void
}

/**
 * Detail for one device, docked beside the grid rather than opened as a second window, so the
 * fleet stays visible and keeps updating while one device is being read.
 *
 * Live fields come straight from the device the grid already holds, so they keep moving with
 * the socket while the panel is open. History and events are read once per selection.
 */
export function DetailPanel({ device, onClose }: Props) {
  const detail = useDeviceDetail(device.device_id)

  return (
    <aside className="flex w-90 flex-col overflow-y-auto border-l border-line bg-panel p-3.5">
      <div className="flex items-start justify-between">
        <span className="font-mono text-sm font-semibold">{device.device_id}</span>
        <button type="button" onClick={onClose} className="cursor-pointer px-1.5 text-muted hover:text-fg">
          ✕
        </button>
      </div>

      <dl className="mt-3 grid grid-cols-[90px_minmax(0,1fr)] gap-y-0.5 text-xs">
        <Field label="Site">{device.site}</Field>
        <Field label="Status">
          <span className={device.online ? 'text-ok' : 'text-bad'}>
            {device.online ? 'online' : (device.offline_reason ?? 'offline')}
          </span>
        </Field>
        <Field label="Model">{device.model}</Field>
        <Field label="Firmware">{device.fw_version}</Field>
        {/* The ordering key. A change means the device rebooted and its sequence restarted,
            which is otherwise invisible. */}
        <Field label="Boot">
          <span className="font-mono text-[11px]">{device.boot_id}</span>
        </Field>
        <Field label="Sequence">
          <span className="font-mono">{device.seq}</span>
        </Field>
        <Field label="Gaps">
          <span className="font-mono">{device.gaps}</span>
        </Field>
      </dl>

      <div className="mt-3.5 grid grid-cols-2 gap-1.5">
        <Reading value={device.metrics?.temp_c.toFixed(1)} unit="DEG C" />
        <Reading value={device.metrics?.humidity_pct.toFixed(1)} unit="RH%" />
        <Reading value={device.metrics?.voltage_v.toFixed(2)} unit="VOLTS" />
        <Reading value={device.metrics?.rssi_dbm} unit="dBm" />
      </div>

      <h2 className="mt-4 mb-1 text-[10px] text-muted">TEMPERATURE, LAST HOUR</h2>
      <div className="h-[70px] rounded border border-line">
        {detail.spark.points.length >= 2 ? (
          // The vertices are already normalised to a 0-100 box, so the viewBox does the
          // scaling and no resize handling is needed. preserveAspectRatio is off because the
          // box is a coordinate space, not a shape to be kept square.
          <svg viewBox="0 0 100 100" preserveAspectRatio="none" className="h-full w-full">
            <polyline
              points={detail.spark.points.map((p) => `${p.x},${p.y}`).join(' ')}
              fill="none"
              stroke="var(--color-accent)"
              strokeWidth="1"
              vectorEffect="non-scaling-stroke"
            />
          </svg>
        ) : (
          <p className="flex h-full items-center justify-center text-[10px] text-muted">
            {detail.loading ? 'loading' : 'no history'}
          </p>
        )}
      </div>
      {detail.spark.points.length >= 2 && (
        <p className="mt-0.5 text-[10px] text-muted">
          {detail.spark.min.toFixed(1)}° to {detail.spark.max.toFixed(1)}°
        </p>
      )}

      <h2 className="mt-4 mb-1 text-[10px] text-muted">RECENT EVENTS</h2>
      <ul className="space-y-1.5">
        {detail.events.map((event, index) => (
          // Events have no id, and two can share a timestamp, so the index participates in
          // the key. The list is replaced wholesale per selection and never reordered, which
          // is the case where an index key is safe.
          <li key={`${event.received_at}-${index}`}>
            <div className="flex items-baseline gap-2">
              <span className="text-[11px] font-semibold">{event.kind}</span>
              <span
                className={`text-[10px] ${
                  event.severity === 'critical'
                    ? 'text-bad'
                    : event.severity === 'warning'
                      ? 'text-warn'
                      : 'text-muted'
                }`}
              >
                {event.severity}
              </span>
              <span className="text-[10px] text-muted">
                {new Date(event.received_at).toLocaleTimeString()}
              </span>
            </div>
            {event.detail && <p className="text-[10px] text-muted">{event.detail}</p>}
          </li>
        ))}
        {detail.events.length === 0 && !detail.loading && (
          <li className="text-[10px] text-muted">none recorded</li>
        )}
      </ul>
    </aside>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt className="text-[11px] text-muted">{label}</dt>
      <dd className="truncate">{children}</dd>
    </>
  )
}

function Reading({ value, unit }: { value: string | number | undefined; unit: string }) {
  return (
    <div className="rounded border border-line px-2 py-1">
      <p className="text-[17px] font-semibold">{value ?? '—'}</p>
      <p className="text-[10px] text-muted">{unit}</p>
    </div>
  )
}
