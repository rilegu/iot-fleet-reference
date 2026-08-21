# .NET clients

Three dashboards over two shared libraries, split along the line that actually matters:
what can be shared between .NET UI frameworks, and what cannot.

```
Fleet.Client.Core/    no UI framework dependency at all
  Contract.cs         wire types for the API's REST and WebSocket surface
  FleetConnection.cs  WebSocket transport, reconnect with jittered backoff, REST queries
  FleetStore.cs       snapshot/delta reconciliation and change notification

Fleet.Client.Xaml/          MVVM ViewModels, no XAML framework dependency either
  FleetViewModel.cs         filtering, sorting, selection, frame application
  DeviceViewModel.cs        one device, updated in place so bindings observe it
  DeviceDetailViewModel.cs  history and events for the selected device
  IUiDispatcher.cs          the one thing each XAML host must supply

blazor/                     subscribes to the store directly, no ViewModels
wpf/                        runs the ViewModels; supplies a Dispatcher
winui/                      runs the same ViewModels; supplies a DispatcherQueue
```

## What is actually shared, and what is not

The XAML hosts contribute about seventy lines of C# each — construct the store, connection
and ViewModel, wrap their dispatcher, bind — plus their own XAML dialect. Everything else
comes from `Fleet.Client.Xaml`, referenced unchanged by both. Neither host subclasses or
adapts a ViewModel; if either did, the reuse would be nominal rather than real.

The only genuinely framework-specific requirement is marshalling onto the UI thread. WPF has
`Dispatcher`, WinUI has `DispatcherQueue`, and neither reference assembly is available to
the other, so the ViewModels take `IUiDispatcher` and each host implements it in three
lines. That interface is also what makes the ViewModels testable without a UI thread at
all — `Fleet.Client.Xaml.Tests` runs them with an inline dispatcher.

Two decisions carry most of the performance:

- **Devices are updated in place, never replaced.** Replacing them would raise a collection
  change per device per frame, and every bound row would be torn down and rebuilt. Updating
  in place costs one notification per field that actually moved.
- **The bound collection is rebuilt only when order could have changed.** Sorting by device
  id is stable across frames; sorting by temperature is not. Rebuilding unconditionally
  resets scroll position and selection several times a second.

`Fleet.Client.Core` is consumed unchanged by every .NET client. The WinUI and WPF clients
wrap it in `CommunityToolkit.Mvvm` ViewModels, because XAML's binding engine is built around
change notification. Blazor subscribes to it directly and calls `StateHasChanged`, because
that is how Blazor renders — a ViewModel there would be machinery the framework never
consults. Both are idiomatic; neither is a compromise. See
[ADR-0006](../../docs/adr/0006-shared-client-state-core.md).

## The detail panel, and where sharing stops

Selecting a row opens a panel beside the grid rather than a second window, so the fleet stays
visible and keeps updating while one device is being read.

Its live fields bind straight to the `DeviceViewModel` the grid already holds — no request is
needed for those, and they keep moving with the socket while the panel is open. History and
events do need a request, because the realtime channel carries current state only: pushing
every device's history to every client would undo the saving the delta protocol exists to
make. Those two reads fire once per selection, are issued together, and cancel any load still
running, so clicking quickly down the list cannot leave an earlier device's history on screen.

The panel is also where the shared layer's boundary is easiest to see. `DeviceDetailViewModel`
turns a temperature series into sparkline vertices normalised to a 0-100 box and stops there,
because WPF and WinUI draw with different, mutually unavailable geometry types. Each host
scales those vertices onto its own canvas in about a dozen lines. The arithmetic — bounds,
the divide-by-zero clamp on a flat series, the inverted Y axis — is written and tested once.

Selection itself splits the same way for a different reason. WPF binds `ListView.SelectedItem`
and gets highlighting for free. WinUI's `ItemsRepeater` carries no selection model at all, so
the row keeps its own `IsSelected` — it has to live on the row for the highlight to survive a
recycled container scrolling back into view — and a tap handler assigns the ViewModel's
`Selected`.

That handler finds the row by `GetElementIndex` rather than by reading the element's
`DataContext`. A repeater whose template is compiled with `x:Bind` hands the item straight to
the generated bindings and never sets `DataContext` on the realised element, so the obvious
version of that handler reads null and drops every tap without erroring. Rows still render,
which makes it look like a layout problem rather than a binding one.

WinUI also has no `BooleanToVisibilityConverter`, and this client's XAML root is a `Window`
rather than a `Page`. A `Window` is not a `FrameworkElement`, so it has no resource scope for
`x:Bind` to resolve a converter against and the generated binder does not compile. The
conversion is a static function called from the binding instead.

## Running it

The dashboard needs the API, which needs the rest of the stack:

```bash
docker compose -f deploy/compose.yaml --profile full up -d --build   # everything
```

Then open <http://localhost:8090>.

The two XAML clients are Windows desktop applications and are not containerised. They need
the API running and nothing else:

```bash
dotnet run --project clients/dotnet/wpf
dotnet run --project clients/dotnet/winui
```

Both read `FLEET_API_URL` and default to `http://localhost:8080`, so pointing one at another
machine's API is an environment variable rather than a rebuild.

To work on the Blazor dashboard itself, run the infrastructure in containers and the
dashboard on your machine:

```bash
docker compose -f deploy/compose.yaml --profile full up -d
docker compose -f deploy/compose.yaml stop fleet-dashboard
dotnet run --project clients/dotnet/blazor        # http://localhost:5300
```

`Api:BaseUrl` and `Api:MaxRateHz` in `appsettings.json` point it at the API and set the
delta cadence it asks for.

## What makes a thousand devices render

Every client needs the same two things, and neither is optional.

**The server coalesces deltas.** A frame carries only the devices that changed since the
last one, so the client is woken a few times a second rather than a thousand. That work
happens in the API, not here, which is why it benefits every client including the ones not
written in .NET.

**Only visible rows are realised** — about forty instead of a thousand. Each framework
spells this differently:

| Client | Mechanism |
|---|---|
| Blazor | `<Virtualize>`, plus `@key` on each row so a re-sort moves elements rather than rebuilding them |
| WinUI 3 | `ItemsRepeater` inside a `ScrollViewer` |
| WPF | `ListView` with `VirtualizationMode=Recycling`, so scrolling reuses row containers rather than allocating new ones |

One Blazor-specific trap: `ItemSize` in `Dashboard.razor` must match the row `height` in
`app.css`. Virtualize uses it to size its scroll spacers, and a mismatch shows up as rows
drifting or blanking while scrolling rather than as an error.

## Tests

```bash
dotnet test clients/dotnet/Fleet.Client.Core.Tests
dotnet test clients/dotnet/Fleet.Client.Xaml.Tests
```

The core tests pin the snapshot/delta semantics that the Qt, Electron and Flutter clients will have
to reproduce independently, so a divergence shows up as a failing test rather than as one
client quietly disagreeing with the others. The XAML tests run the ViewModels with an inline
dispatcher, so filtering, in-place updates, selection and the sparkline projection are all
covered without a UI thread.
