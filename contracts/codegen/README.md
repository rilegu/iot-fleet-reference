# Code generation

The contract is the source of truth. How each language stays faithful to it differs, and
the difference is deliberate.

## Generate, where the ecosystem handles it well

TypeScript, Dart and C++ generators map a snake_case wire format onto idiomatic names
without help, so the Electron, Flutter and Qt clients will generate their models. Nothing
hand-written means nothing to drift.

`csharp.nswag` runs and produces valid types:

```bash
dotnet tool restore
dotnet nswag run contracts/codegen/csharp.nswag
```

## Validate, for C#

The .NET clients keep hand-written models, checked against the contract by a conformance
test rather than produced from it.

The reason is ergonomic, and visible in the generator's own output. The wire format is
snake_case — correct for a contract several languages consume — and NSwag carries that
straight into C#:

```csharp
public string Device_id { get; init; }
public DeviceStateOffline_reason? Offline_reason { get; init; }
```

Adopting those would make every line of client code worse than the hand-written records
they replace, in exchange for a guarantee that can be obtained another way.

That other way is `tools/contract_test.py`, which drives the running API and validates every
response against the schemas in `openapi.yaml`. It is a stronger check than generation in
one respect: generated models prove a client and a contract agree, whereas this proves the
*server* and the contract agree — and the server is what clients actually talk to. A field
renamed in the API without a contract change fails CI, which generation alone would not
catch.

```bash
python tools/contract_test.py --base-url http://localhost:8080
```

## The tradeoff, stated plainly

This deviates from [ADR-0001](../../docs/adr/0001-contract-first-ui-boundary.md), which says
client models are generated and never hand-written. Two hand-written .NET model sets remain
— `services/api/Fleet/Model.cs` and `clients/dotnet/Fleet.Client.Core/Contract.cs` — and
they are kept honest by conformance testing instead.

If the .NET clients ever outnumber the effort of a better generator, or a generator gains
proper snake_case-to-PascalCase mapping, this should be revisited. The generator
configuration is kept working precisely so that revisiting it is cheap.
