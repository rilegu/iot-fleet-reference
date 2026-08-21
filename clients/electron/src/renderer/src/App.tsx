import { DeviceGrid } from './components/DeviceGrid'
import { DetailPanel } from './components/DetailPanel'
import { Aggregates, FilterBar, Header } from './components/Toolbar'
import { useConnectionStatus, useFleetAggregates, useFleetFrameInfo, useFleetView } from './fleet/useFleet'

/**
 * The whole dashboard.
 *
 * Structurally the same application as the Blazor and XAML clients: fleet state arrives from
 * outside, view state is local, and the tree below is a projection of the two. What differs
 * is only how each framework expresses that — here, an external store read through
 * `useSyncExternalStore` and plain component composition, with no ViewModels, because React
 * re-renders from state rather than consulting change notification on objects.
 */
export default function App() {
  const { visible, devices, sites, filters, setFilters, sortBy, clearFilters, selected, select } = useFleetView()
  const aggregates = useFleetAggregates()
  const status = useConnectionStatus()

  const [lastFrame, , cadenceMs] = useFleetFrameInfo().split(':').map(Number)

  return (
    <div className="flex h-full">
      <main className="flex min-w-0 flex-1 flex-col gap-2.5 p-3">
        <Header status={status} cadenceMs={cadenceMs ?? 0} frame={lastFrame ?? 0} />
        <Aggregates aggregates={aggregates} />
        <FilterBar
          filters={filters}
          sites={sites}
          shown={visible.length}
          total={devices.length}
          onChange={setFilters}
          onClear={clearFilters}
        />
        <DeviceGrid
          devices={visible}
          filters={filters}
          selectedId={selected?.device_id ?? null}
          onSort={sortBy}
          onSelect={select}
        />
      </main>

      {selected && <DetailPanel device={selected} onClose={() => select(null)} />}
    </div>
  )
}
