# Electron client

The same dashboard as the .NET clients, built the way the web builds one: TypeScript, React,
Vite and Tailwind, running in Electron.

```
src/
  shared/      wire types and the IPC contract, imported by all three builds
  main/        Node process: owns the socket, the REST reads, and the window
    transport.ts   WebSocket, jittered reconnect, REST queries
  preload/     the only bridge between the two, exposed on window.fleet
  renderer/    Chromium page: React, Tailwind, the store and the grid
    src/fleet/     store.ts, project.ts, spark.ts, useFleet.ts
    src/components/
test/          vitest: the store, the projection, the sparkline, session ordering
```

## What is shared with the other clients, and what is not

Nothing, in code — and that is the point. Qt is C++, Electron is TypeScript, Flutter is Dart;
none of them can consume `Fleet.Client.Core`, and compiling it to WebAssembly to avoid
rewriting two hundred lines would make this a wrapper rather than an implementation, which
would say nothing about the framework. The duplication between runtime families is the
deliverable, not a defect. See [ADR-0006](../../docs/adr/0006-shared-client-state-core.md).

What is shared is the *contract* and the *behaviour*. `test/store.test.ts` mirrors
`Fleet.Client.Core.Tests` case for case, and `test/project.test.ts` pins the same
filter, sort and sparkline semantics the .NET clients implement, so a divergence fails a
build rather than showing up as one dashboard quietly disagreeing with another.

One deliberate difference: this client breaks sort ties by device id. The store's array comes
from `Map` iteration order, which changes whenever a snapshot arrives, so sorting by a column
with many equal values would otherwise shuffle rows between frames. The .NET clients rely on
LINQ's stable sort over an equally unstable input, and get an arbitrary but usually unchanging
order instead.

## Why the network lives in the main process

The renderer is a Chromium page, so everything it fetches is subject to the same-origin
policy. The API sets no CORS headers, and has never needed to: WPF and WinUI are not browsers,
and Blazor Server calls the API from the server side. This is the first client for which the
browser security model applies at all.

Adding a CORS policy to the API would have been the obvious fix, and was rejected: adding a UI
framework must not require a backend change. Disabling web security in the renderer was
rejected outright. So the main process does the networking, in Node, where the policy does not
apply, and passes results over a typed IPC bridge.

That is also the arrangement a security review asks for. The renderer runs sandboxed with
`contextIsolation`, no Node integration, and a CSP that allows it no outbound connections at
all; `window.fleet` in [`src/shared/contract.ts`](src/shared/contract.ts) is the complete list
of what it can do. The cost is that every frame is structured-cloned across a process
boundary, which at four coalesced frames a second is not measurable.

A consequence worth stating: a pure browser build of this dashboard, with no Electron around
it, *would* need a CORS policy on the API.

## The startup race

The renderer tells the main process when to connect, rather than the main process connecting
as soon as the window exists. That is not ceremony — the first version did the obvious thing
and was wrong.

The API sends its snapshot within a millisecond or two of accepting the socket, long before a
React tree has mounted, and a frame sent to a renderer that has not subscribed yet is dropped
silently. Nothing looked broken: deltas refill the grid within a second, so the fleet appeared
and the counters moved. What was missing was every device that had not changed since connect —
against the live fleet, 100 devices where the server had 112 — and the cadence, which only the
snapshot carries. `test/session.test.ts` pins the ordering that fixes it.

## What makes a thousand devices render

The server coalesces deltas, so a frame carries only what changed and the client is woken four
times a second rather than a thousand. That work happens in the API and benefits every client.

On top of that, the grid is virtualized with `@tanstack/react-virtual`: about forty rows are
mounted instead of a thousand. In a browser engine this is not an optimisation but a
requirement — a thousand rows of ten cells is ten thousand DOM nodes, and React would
reconcile all of them on every frame.

Two React-specific details carry the rest:

- **Fleet state is not React state.** It arrives from another process on a fixed cadence and
  exists whether or not anything is rendering, so it lives in a plain observable store read
  through `useSyncExternalStore`. That hook compares snapshots by identity, which is why the
  store caches its array and rebuilds it only when a frame is applied — a `snapshot()` that
  allocated per call would re-render forever. The .NET store allocates freely on the same
  method; same data, different rule about who may allocate when.
- **View state is React state.** Filters, sort and selection are `useState`, and the visible
  list is a `useMemo` over the store array and the filters. Two windows on the same fleet
  should not share a search box, which is the same split the XAML and Blazor clients make.

There are no ViewModels here, and adding them would be machinery React never consults —
exactly as their absence in Blazor is idiomatic and their presence in XAML is mandatory.

## Running it

The API must be up:

```bash
docker compose -f deploy/compose.yaml --profile full up -d
```

Then, from `clients/electron`:

```bash
npm install
npm run dev        # vite dev server, hot reload in the renderer
npm run build      # production bundles into out/
npm run preview    # run the built app
npm test           # vitest
npm run lint       # oxlint
npm run typecheck  # both tsconfigs: Node side and web side
```

`FLEET_API_URL` (default `http://localhost:8080`) and `FLEET_MAX_RATE_HZ` (default `4`)
configure the session.

### If it starts as Node instead of Electron

Some editor-integrated terminals export `ELECTRON_RUN_AS_NODE=1`, and child processes inherit
it. Electron then starts as a plain Node process: `app` is `undefined`, `require('electron')`
returns a path string, and `electron --version` prints a Node version. The app detects this
and says so rather than failing on an unrelated line. Unset the variable and launch again.

## Toolchain notes

`vite` is pinned to 7 because `electron-vite` 5 does not accept 8 as a peer, which also fixes
`@vitejs/plugin-react` to 5.

Linting is `oxlint` rather than ESLint. `typescript-eslint` still declares a peer range ending
below TypeScript 7, and downgrading the compiler to satisfy a linter is the wrong way round;
oxlint does not bind to the TypeScript compiler API, so the two are independent. Three rules
are turned off in `.oxlintrc.json`, each for a stated reason: `react-in-jsx-scope` (the
automatic JSX transform is in use), `incompatible-library` (it flags `useVirtualizer` for React
Compiler, which this build does not use), and `promise/always-return` (it fires on a `then`
used for its side effect).

It earned its place immediately: it caught `setState` being called synchronously inside the
detail panel's effect, which cost a second render pass on every row click. The loading flag is
derived during render now.

`main` and `preload` build as CommonJS, not ESM. Electron's own module exposes no named ESM
exports, and a sandboxed preload script cannot be an ES module at all — the sandbox has no
module loader in it. Only the renderer is ESM, which is what Vite wants anyway.
