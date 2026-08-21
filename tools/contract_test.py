#!/usr/bin/env python3
"""Check that the running API matches the contract.

Generated client models prove a client agrees with the contract. This proves the *server*
does, which is the direction that actually matters: clients talk to the server, not to the
document. A field renamed in the API without a corresponding contract change fails here.

Every response is validated against the schema the contract declares for it, and a handful
of behaviours the schema cannot express are asserted separately — that filters filter, that
an unknown device is a 404, that readiness and liveness are distinct.

    python tools/contract_test.py --base-url http://localhost:8080
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from typing import Any

try:
    import yaml
except ImportError:
    sys.exit("pyyaml is required: pip install pyyaml jsonschema")

try:
    from jsonschema import Draft202012Validator
except ImportError:
    sys.exit("jsonschema is required: pip install pyyaml jsonschema")


CONTRACT = "contracts/openapi.yaml"


def load_contract(path: str) -> dict[str, Any]:
    with open(path, encoding="utf-8") as handle:
        return yaml.safe_load(handle)


def to_json_schema(spec: dict[str, Any], schema: dict[str, Any]) -> dict[str, Any]:
    """Rewrite an OpenAPI schema into something a JSON Schema validator accepts.

    OpenAPI 3.0 is close to, but not, JSON Schema. Two differences matter here: `$ref`
    points into `#/components/schemas`, which is resolved by handing the validator the
    whole document as its root; and `nullable: true` is OpenAPI's spelling of a union with
    null, which has to be rewritten or every optional numeric field fails.
    """
    return {
        **schema,
        "components": spec["components"],
    }


def normalise_nullable(node: Any) -> Any:
    """Turn OpenAPI's `nullable: true` into a JSON Schema type union, recursively."""
    if isinstance(node, list):
        return [normalise_nullable(item) for item in node]
    if not isinstance(node, dict):
        return node

    out = {key: normalise_nullable(value) for key, value in node.items()}
    if out.pop("nullable", False) and "type" in out:
        declared = out["type"]
        out["type"] = [declared, "null"] if isinstance(declared, str) else declared + ["null"]
    return out


class Checker:
    def __init__(self, base_url: str, spec: dict[str, Any]) -> None:
        self.base = base_url.rstrip("/")
        self.spec = normalise_nullable(spec)
        self.failures: list[str] = []
        self.checks = 0

    # ---------------------------------------------------------------- plumbing

    def get(self, path: str) -> tuple[int, Any, str]:
        url = f"{self.base}{path}"
        try:
            with urllib.request.urlopen(url, timeout=20) as response:
                body = response.read().decode("utf-8")
                status = response.status
        except urllib.error.HTTPError as err:
            body = err.read().decode("utf-8", errors="replace")
            status = err.code
        except Exception as err:  # noqa: BLE001 - reported, not raised
            return 0, None, str(err)

        try:
            return status, json.loads(body), body
        except json.JSONDecodeError:
            return status, None, body

    def check(self, description: str, condition: bool, detail: str = "") -> None:
        self.checks += 1
        if condition:
            print(f"  ok    {description}")
        else:
            print(f"  FAIL  {description}{(' — ' + detail) if detail else ''}")
            self.failures.append(description)

    def validate(self, description: str, payload: Any, schema_name: str, as_array: bool = False) -> None:
        """Validate a payload against a named component schema."""
        schema: dict[str, Any] = {"$ref": f"#/components/schemas/{schema_name}"}
        if as_array:
            schema = {"type": "array", "items": schema}

        validator = Draft202012Validator(to_json_schema(self.spec, schema))
        errors = sorted(validator.iter_errors(payload), key=lambda e: list(e.path))
        if errors:
            first = errors[0]
            location = "/".join(str(p) for p in first.path) or "(root)"
            self.check(description, False, f"{location}: {first.message}")
        else:
            self.check(description, True)

    # ------------------------------------------------------------------ checks

    def run(self) -> bool:
        print("contract conformance")

        status, fleet, _ = self.get("/api/fleet?limit=5")
        self.check("GET /api/fleet responds 200", status == 200, f"got {status}")
        if fleet is not None:
            self.validate("  matches FleetResponse", fleet, "FleetResponse")
            self.check(
                "  limit is honoured",
                len(fleet.get("devices", [])) <= 5,
                f"returned {len(fleet.get('devices', []))}",
            )

        status, aggregates, _ = self.get("/api/fleet/aggregates")
        self.check("GET /api/fleet/aggregates responds 200", status == 200, f"got {status}")
        if aggregates is not None:
            self.validate("  matches FleetAggregates", aggregates, "FleetAggregates")

        # A filter that does not filter is worse than one that errors, because the caller
        # cannot tell. The schema cannot express this, so it is asserted directly.
        status, filtered, _ = self.get("/api/fleet?online=true")
        if filtered is not None:
            devices = filtered.get("devices", [])
            self.check(
                "online=true returns only online devices",
                all(d["online"] for d in devices),
                f"{sum(1 for d in devices if not d['online'])} offline devices returned",
            )

        device_id = None
        if fleet and fleet.get("devices"):
            device_id = fleet["devices"][0]["device_id"]

        if device_id:
            status, device, _ = self.get(f"/api/devices/{device_id}")
            self.check(f"GET /api/devices/{device_id} responds 200", status == 200, f"got {status}")
            if device is not None:
                self.validate("  matches DeviceState", device, "DeviceState")

            status, history, _ = self.get(f"/api/devices/{device_id}/history?minutes=30")
            self.check("GET device history responds 200", status == 200, f"got {status}")
            if history is not None:
                self.validate("  matches TelemetryPoint[]", history, "TelemetryPoint", as_array=True)
        else:
            print("  skip  device endpoints — the fleet is empty")

        status, _, _ = self.get("/api/devices/definitely-not-a-real-device")
        self.check("unknown device is 404", status == 404, f"got {status}")

        status, fleet_history, _ = self.get("/api/history/fleet?minutes=30")
        self.check("GET /api/history/fleet responds 200", status == 200, f"got {status}")
        if fleet_history is not None:
            self.validate("  matches FleetPoint[]", fleet_history, "FleetPoint", as_array=True)

        status, events, _ = self.get("/api/events?limit=5")
        self.check("GET /api/events responds 200", status == 200, f"got {status}")
        if events is not None:
            self.validate("  matches DeviceEvent[]", events, "DeviceEvent", as_array=True)
            self.check("  limit is honoured", len(events) <= 5, f"returned {len(events)}")

        status, stats, _ = self.get("/stats")
        self.check("GET /stats responds 200", status == 200, f"got {status}")
        if stats is not None:
            self.validate("  matches ProjectionStats", stats, "ProjectionStats")

        status, _, body = self.get("/healthz")
        self.check("GET /healthz responds 200", status == 200, f"got {status}")

        # Readiness must be able to disagree with liveness, or it is not readiness. Both
        # 200 and 503 are contractual; anything else is not.
        status, _, _ = self.get("/readyz")
        self.check("GET /readyz responds 200 or 503", status in (200, 503), f"got {status}")

        print()
        if self.failures:
            print(f"{len(self.failures)} of {self.checks} checks failed:")
            for failure in self.failures:
                print(f"  - {failure}")
            return False

        print(f"all {self.checks} checks passed")
        return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--contract", default=CONTRACT)
    args = parser.parse_args()

    spec = load_contract(args.contract)
    return 0 if Checker(args.base_url, spec).run() else 1


if __name__ == "__main__":
    raise SystemExit(main())
