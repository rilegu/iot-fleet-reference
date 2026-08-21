import type { ConnectionStatus, FleetAggregates } from '@shared/contract'
import type { Filters } from '@/fleet/project'

export function Header({ status, cadenceMs, frame }: { status: ConnectionStatus | null; cadenceMs: number; frame: number }) {
  const summary = !status
    ? 'starting'
    : status.connected
      ? `live · ${cadenceMs} ms cadence · frame ${frame}`
      : status.error
        ? `reconnecting — ${status.error}`
        : 'connecting'

  return (
    <header className="flex items-baseline gap-3.5">
      <h1 className="text-lg font-semibold">Fleet</h1>
      <span className="text-xs text-muted">{summary}</span>
    </header>
  )
}

export function Aggregates({ aggregates }: { aggregates: FleetAggregates }) {
  return (
    <div className="flex gap-2">
      <Stat value={aggregates.total} label="DEVICES" />
      <Stat value={aggregates.online} label="ONLINE" />
      <Stat value={aggregates.offline} label="OFFLINE" />
      <Stat value={aggregates.alerting} label="ALERTING" />
      <Stat value={aggregates.gaps} label="GAPS" />
      <Stat value={aggregates.stale_dropped} label="STALE" />
    </div>
  )
}

function Stat({ value, label }: { value: number; label: string }) {
  return (
    <div className="min-w-[86px] rounded-md border border-line bg-panel px-2.5 py-1.5">
      <p className="text-xl font-semibold">{value.toLocaleString()}</p>
      <p className="text-[10px] text-muted">{label}</p>
    </div>
  )
}

interface FilterProps {
  filters: Filters
  sites: string[]
  shown: number
  total: number
  onChange: (changes: Partial<Filters>) => void
  onClear: () => void
}

export function FilterBar({ filters, sites, shown, total, onChange, onClear }: FilterProps) {
  return (
    <div className="flex items-center gap-3">
      <input
        type="search"
        value={filters.search}
        onChange={(e) => onChange({ search: e.target.value })}
        placeholder="device, site or firmware"
        className="w-65 rounded border border-line bg-panel px-2 py-1 text-xs outline-none focus:border-accent"
      />

      <select
        value={filters.site ?? ''}
        onChange={(e) => onChange({ site: e.target.value || null })}
        className="rounded border border-line bg-panel px-2 py-1 text-xs outline-none focus:border-accent"
      >
        <option value="">all sites</option>
        {sites.map((site) => (
          <option key={site} value={site}>
            {site}
          </option>
        ))}
      </select>

      <Toggle checked={filters.onlineOnly} onChange={(v) => onChange({ onlineOnly: v })} label="online only" />
      <Toggle checked={filters.alertingOnly} onChange={(v) => onChange({ alertingOnly: v })} label="alerting only" />

      <button type="button" onClick={onClear} className="cursor-pointer text-xs text-muted hover:text-fg">
        clear
      </button>

      <span className="text-xs text-muted">
        {shown === total ? `${total} devices` : `${shown} of ${total}`}
      </span>
    </div>
  )
}

function Toggle({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <label className="flex cursor-pointer items-center gap-1.5 text-xs">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        className="accent-accent"
      />
      {label}
    </label>
  )
}
