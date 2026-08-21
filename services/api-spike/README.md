# api-spike

**Throwaway.** This is an exploratory consumer: it subscribes to the broker, keeps an
in-memory fleet projection, and renders it as a plain table so the message contract in
`contracts/README.md` can be validated against something that actually parses it.

It is deleted once ingest moves to a Go service behind a durable event log and the API
becomes a real service with a snapshot/delta protocol. Nothing should be built on top
of it and no code should be carried forward from it.

What it deliberately does not do: persistence, checkpointing, replay, authentication,
schema validation, row virtualization, or delta coalescing. Each of those belongs to a
component that does not exist yet.

```bash
dotnet run --project services/api-spike     # http://localhost:5183
```

Broker connection is configured under `Mqtt` in `appsettings.json`.
