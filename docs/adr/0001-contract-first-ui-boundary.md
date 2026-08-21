# ADR-0001: Contract-first REST + WebSocket as the only UI boundary

- **Status:** Accepted
- **Date:** 2026-08-20

## Context

The project deliberately implements the same dashboard in Blazor, WinUI 3, Qt/QML and
Electron, with Flutter planned. The stated requirement is that adding a UI framework must
not require backend changes.

That rules out any boundary technology with uneven support across those ecosystems:

- **SignalR** is excellent for Blazor and usable from TypeScript, but there is no
  first-class C++/Qt client and the Dart story is third-party. It would make .NET clients
  cheap and every other client expensive.
- **gRPC** has good C#, Dart, TypeScript and C++ support (including Qt gRPC), but needs
  grpc-web or a proxy for browser-hosted Blazor, and pushes protobuf toolchain setup into
  every client.
- **Direct MQTT from each UI** removes the server-side projection entirely, forcing every
  client to reimplement fleet state, aggregation and backpressure. It also drags the
  GPL-licensed Qt MQTT add-on into an Apache-2.0 repository.

## Decision

The only supported UI boundary is **HTTP/REST for queries and commands, plus a WebSocket
channel for realtime state**, both carrying JSON described by machine-readable contracts:

- `contracts/openapi.yaml` for the REST surface.
- `contracts/asyncapi.yaml` for the WebSocket channel and the device-facing MQTT topics.
- `contracts/schemas/` JSON Schema for every payload.

Client models are generated from these contracts per language. CI verifies that checked-in
generated code matches the contract and that breaking changes carry a version bump.

## Consequences

**Positive**

- Every current and plausible future UI framework has a mature HTTP and WebSocket client in
  its standard library or first-party ecosystem. Nothing is privileged.
- The server owns fleet state, aggregation and backpressure once, instead of five times.
- Qt talks WebSocket (LGPL) instead of MQTT (GPL/commercial), keeping the repo Apache-2.0.
- The contract becomes a reviewable artifact and the natural place to discuss compatibility.

**Negative**

- JSON over WebSocket is less efficient than a binary protocol. At the target rates
  (coalesced deltas at 4 Hz, not raw 1000 msg/s) this is not the bottleneck; if it ever
  becomes one, per-frame compression or a MessagePack encoding can be negotiated without
  changing the contract shape.
- Codegen adds a build step and a CI check.

## Alternatives kept open

A gRPC or Server-Sent Events facade may be added *beside* REST + WebSocket later. Because
the fleet state projection sits behind the transport rather than inside it, that is additive
and does not invalidate this decision.
