# ADR-0006: One shared .NET state core; per-ecosystem view-state idioms

- **Status:** Accepted
- **Date:** 2026-08-20

## Context

Two of the planned clients are .NET — WinUI 3 and Blazor — with Qt/QML, Electron and later
Flutter alongside. Because comparing view-state approaches across these stacks is one of the
repository's purposes, the pattern each client uses is a design decision rather than an
implementation detail. MVVM was the initial candidate for all of them.

That candidate deserves scrutiny, because the industry position in 2026 is genuinely split:

**Where MVVM remains the standard.** XAML-based .NET UI — WPF, WinUI 3, MAUI, Uno, Avalonia
— is built around it. The binding engine assumes a ViewModel, `CommunityToolkit.Mvvm` is
the de facto library, and the enterprise, industrial and medical desktop shops that build
fleet consoles run on this stack. Android's official guidance (ViewModel + StateFlow) is
also MVVM-descended, though it has drifted toward unidirectional flow.

**Where it has clearly lost ground.** The web converged on **signals and unidirectional data
flow**: React hooks and stores, Vue refs, Svelte 5 runes, Solid, and Angular signals —
Angular having moved away from being the two-way-binding exemplar. Blazor's own idiom is
component state plus `StateHasChanged`, not `INotifyPropertyChanged`. SwiftUI uses
`@Observable`. Flutter's mainstream options (BLoC, Riverpod) lean unidirectional. Qt calls
its equivalent Model/View.

What survived in every one of those ecosystems is the underlying principle, not the
pattern name: **observable state lives outside the view, the view is a projection of that
state, and user intent flows one way back.** What specifically fell out of favour is
two-way binding, everywhere except XAML.

Imposing MVVM uniformly on all five clients would therefore be applying a pattern by
reflex, and would produce a Blazor client written against the grain of its own framework.

## Decision

Share the state, not the binding style.

**`clients/Fleet.Client.Core`** — a .NET 10 library with no UI framework dependency:

- `FleetConnection` — REST + WebSocket transport, reconnect with jitter, resilience
  pipelines, snapshot/delta reconciliation.
- `FleetStore` — observable fleet state: device collection, filter, sort, selection, derived
  aggregates. Exposes **both** a change-notification stream and immutable snapshots, so
  either binding style composes on top of it.
- Command dispatch with `cmdId` correlation and optimistic/settled state.
- DI registration extensions.

**Each client then uses its own ecosystem's idiom:**

| Client | Idiom |
|---|---|
| WinUI 3 | MVVM — `CommunityToolkit.Mvvm` ViewModels over `FleetStore`, bound with `x:Bind` |
| Blazor | Store subscription + `InvokeAsync(StateHasChanged)` |
| Qt/QML | Model/View — `QAbstractTableModel` + `Q_PROPERTY` |
| Electron | Signals/observable store |
| Flutter | `ChangeNotifier` or Riverpod |

## Rationale

- **The valuable reuse is not the binding layer.** Transport, reconnect, snapshot/delta
  reconciliation and filter/sort/aggregate semantics are where duplication causes real
  divergence between the two .NET clients. Those are shared regardless of binding style.
  The `INotifyPropertyChanged` layer was always the thin part.
- **WinUI 3 gets a genuine, idiomatic MVVM implementation** rather than a diluted one. MVVM
  is effectively mandatory in XAML, so this is the client where the pattern is applied at
  full strength.
- **Blazor gets written the way Blazor is written.** A Blazor client built around
  `INotifyPropertyChanged` fights its own framework, and would produce a misleading data
  point in any comparison of the two.
- **The comparison is only meaningful if each client is idiomatic.** Five implementations
  forced into one pattern would measure how well that pattern ports, not how the frameworks
  actually behave in the hands of someone using them normally.
- A dual-surface store (notifications *and* snapshots) is a small amount of extra design for
  a large amount of flexibility, and keeps a future MAUI/Uno client cheap.

## Consequences

**Positive**

- Two .NET clients share the expensive logic without either being distorted.
- The core stays honestly UI-agnostic, because it must serve two different consumption
  styles — any leak of view concerns shows up immediately.
- ViewModels and store are unit-testable with no UI host.
- Clear migration path to MAUI, Uno or Avalonia.

**Negative**

- Two consumption surfaces on `FleetStore` is more API than a single-style core would need.
- "The same ViewModels drive every client" would be a simpler rule to state, so the reason
  it was rejected has to be written down — which is what this ADR is for.
- Care is needed around thread affinity: the core must never assume a dispatcher; each host
  marshals to its own.

## Notes

At 1000 devices streaming at 4 Hz, per-property change notification is a performance hazard
in every host. `FleetStore` therefore raises **batched** collection and aggregate
notifications aligned to the delta cadence, never one notification per changed field. This
constraint applies equally to the MVVM and the signals consumers.

## Supersedes

An earlier draft of this ADR proposed reusing one set of `INotifyPropertyChanged`
ViewModels across both WinUI 3 and Blazor. It was revised before implementation began, on
the grounds set out in the Context section above.
