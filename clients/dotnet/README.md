# .NET clients

Two projects, split along the line that actually matters: what can be shared between .NET
UI frameworks, and what cannot.

```
Fleet.Client.Core/    no UI framework dependency at all
  Contract.cs         wire types for the API's REST and WebSocket surface
  FleetConnection.cs  WebSocket transport, reconnect with jittered backoff, REST queries
  FleetStore.cs       snapshot/delta reconciliation and change notification

blazor/               the dashboard
  Components/Pages/Dashboard.razor   the grid
  Components/DeviceDetail.razor      side panel: live fields, sparkline, events
  FleetView.cs                       filtering, sorting, selection
  Program.cs                         dependency injection and configuration
  wwwroot/app.css                    layout and theme
```

`Fleet.Client.Core` is consumed unchanged by every .NET client. The WinUI and WPF clients
will wrap it in `CommunityToolkit.Mvvm` ViewModels, because XAML's binding engine is built
around change notification. Blazor subscribes to it directly and calls `StateHasChanged`,
because that is how Blazor renders — a ViewModel there would be machinery the framework
never consults. Both are idiomatic; neither is a compromise. See
[ADR-0006](../../docs/adr/0006-shared-client-state-core.md).

## Running it

The dashboard needs the API, which needs the rest of the stack:

```bash
docker compose -f deploy/compose.yaml --profile full up -d --build   # everything
```

Then open <http://localhost:8090>.

To work on the dashboard itself, run the infrastructure in containers and the dashboard on
your machine:

```bash
docker compose -f deploy/compose.yaml --profile full up -d
docker compose -f deploy/compose.yaml stop fleet-dashboard
dotnet run --project clients/dotnet/blazor        # http://localhost:5300
```

`Api:BaseUrl` and `Api:MaxRateHz` in `appsettings.json` point it at the API and set the
delta cadence it asks for.

## What makes a thousand devices render

Three things, and none of them is optional:

1. **The server coalesces deltas.** A frame carries only the devices that changed since the
   last one, so the client is woken a few times a second rather than a thousand. That work
   happens in the API, not here.
2. **`<Virtualize>` renders only visible rows** — about forty instead of a thousand. This
   matters more in Blazor Server than in a local UI framework, because every rendered row
   becomes DOM diff traffic over the circuit.
3. **`@key` on each row** so a re-sort moves elements rather than rebuilding them.

`ItemSize` in `Dashboard.razor` must match the row `height` in `app.css`. Virtualize uses it
to size its scroll spacers, and a mismatch shows up as rows drifting or blanking while
scrolling rather than as an error.

## Tests

```bash
dotnet test clients/dotnet/Fleet.Client.Core.Tests
```

These pin the snapshot/delta semantics that the Qt, Electron and Flutter clients will have
to reproduce independently, so a divergence shows up as a failing test rather than as one
client quietly disagreeing with the others.
