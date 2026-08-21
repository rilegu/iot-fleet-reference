import { useRef } from 'react'
import { useVirtualizer } from '@tanstack/react-virtual'
import type { DeviceState } from '@shared/contract'
import type { Filters, SortColumn } from '@/fleet/project'
import { isAlerting } from '@/fleet/project'

/** Row height in pixels. Fixed, so the virtualizer can size its scroll range without measuring. */
const ROW_HEIGHT = 26

const COLUMNS: { key: SortColumn | null; label: string; width: string; numeric?: boolean }[] = [
  { key: 'device', label: 'DEVICE', width: '130px' },
  { key: 'site', label: 'SITE', width: '80px' },
  { key: 'status', label: 'STATUS', width: '90px' },
  { key: null, label: 'FIRMWARE', width: '80px' },
  { key: 'temp', label: '°C', width: '70px', numeric: true },
  { key: null, label: 'RH%', width: '70px', numeric: true },
  { key: 'voltage', label: 'VOLTS', width: '70px', numeric: true },
  { key: 'rssi', label: 'RSSI', width: '70px', numeric: true },
  { key: 'seq', label: 'SEQ', width: '80px', numeric: true },
  { key: null, label: 'LAST EVENT', width: 'minmax(0, 1fr)' },
]

const TEMPLATE = COLUMNS.map((c) => c.width).join(' ')

interface Props {
  devices: DeviceState[]
  filters: Filters
  selectedId: string | null
  onSort: (column: SortColumn) => void
  onSelect: (deviceId: string) => void
}

/**
 * The fleet grid.
 *
 * Virtualized: only the rows in view are mounted, about forty instead of a thousand. In a
 * browser engine that is not an optimisation but a requirement — a thousand rows of ten cells
 * is ten thousand DOM nodes, and React would reconcile all of them four times a second.
 *
 * The header is a sibling of the scroll container rather than a sticky row inside it, so it
 * cannot be scrolled away and does not participate in virtualization at all.
 */
export function DeviceGrid({ devices, filters, selectedId, onSort, onSelect }: Props) {
  const scrollRef = useRef<HTMLDivElement>(null)

  const virtualizer = useVirtualizer({
    count: devices.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    // A few rows either side of the viewport, so a fast scroll does not show empty space
    // before React has rendered into it.
    overscan: 8,
  })

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div
        className="grid gap-x-2 border-b border-line px-1.5 py-1 text-[10px] text-muted"
        style={{ gridTemplateColumns: TEMPLATE }}
      >
        {COLUMNS.map((column) => (
          <button
            key={column.label}
            type="button"
            disabled={column.key === null}
            onClick={() => column.key && onSort(column.key)}
            className={`truncate text-left ${column.numeric ? 'text-right' : ''} ${
              column.key ? 'cursor-pointer hover:text-fg' : 'cursor-default'
            }`}
          >
            {column.label}
            {filters.sort === column.key ? (filters.descending ? ' ▾' : ' ▴') : ''}
          </button>
        ))}
      </div>

      <div ref={scrollRef} className="min-h-0 flex-1 overflow-y-auto">
        {/* The spacer gives the scrollbar the full height of the unrendered list. */}
        <div className="relative w-full" style={{ height: virtualizer.getTotalSize() }}>
          {virtualizer.getVirtualItems().map((item) => {
            const device = devices[item.index]
            if (!device) return null
            return (
              <DeviceRow
                key={device.device_id}
                device={device}
                selected={device.device_id === selectedId}
                top={item.start}
                onSelect={onSelect}
              />
            )
          })}
        </div>
      </div>
    </div>
  )
}

interface RowProps {
  device: DeviceState
  selected: boolean
  top: number
  onSelect: (deviceId: string) => void
}

function DeviceRow({ device, selected, top, onSelect }: RowProps) {
  const alerting = isAlerting(device)

  return (
    <div
      data-device-id={device.device_id}
      onClick={() => onSelect(device.device_id)}
      className={`absolute inset-x-0 grid cursor-pointer items-center gap-x-2 px-1.5 text-xs ${
        selected ? 'bg-hit' : device.justChanged ? 'bg-white/4' : 'hover:bg-white/3'
      }`}
      style={{ height: ROW_HEIGHT, transform: `translateY(${top}px)`, gridTemplateColumns: TEMPLATE }}
    >
      <span className="truncate font-mono">{device.device_id}</span>
      <span className="truncate">{device.site}</span>
      <span className={`truncate ${device.online ? 'text-ok' : 'text-bad'}`}>
        {device.online ? 'online' : (device.offline_reason ?? 'offline')}
      </span>
      <span className="truncate text-muted">{device.fw_version}</span>
      <span className="text-right font-mono">{device.metrics?.temp_c.toFixed(1)}</span>
      <span className="text-right font-mono">{device.metrics?.humidity_pct.toFixed(1)}</span>
      <span className="text-right font-mono">{device.metrics?.voltage_v.toFixed(2)}</span>
      <span className="text-right font-mono">{device.metrics?.rssi_dbm}</span>
      <span className="text-right font-mono text-muted">{device.seq}</span>
      <span className={`truncate ${alerting ? 'text-warn' : 'text-muted'}`}>{device.last_event}</span>
    </div>
  )
}
