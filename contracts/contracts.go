// Package contracts embeds the device-facing message schemas and compiles them once at
// startup.
//
// The schema files are the single source of truth. Both the simulator and the ingest
// service validate against these exact bytes rather than against a copy, so a contract
// change cannot land in one component and be missed in the other.
package contracts

import (
	"embed"
	"fmt"
	"io/fs"
	"path"
	"strings"
	"sync"

	"github.com/santhosh-tekuri/jsonschema/v6"
)

//go:embed schemas/*.json
var schemaFS embed.FS

// baseURI must match the $id prefix in the schema files, so that relative $ref values
// such as "envelope.json" resolve without network access.
const baseURI = "https://rilegu.github.io/iot-fleet-reference/schemas/"

// Message kinds, matching the `schema` field and the file names.
const (
	KindTelemetry = "telemetry"
	KindStatus    = "status"
	KindEvent     = "event"
)

// Schema identifiers as they appear in the `schema` envelope field.
const (
	SchemaTelemetry = "telemetry/1"
	SchemaStatus    = "status/1"
	SchemaEvent     = "event/1"
)

var (
	once       sync.Once
	compiled   map[string]*jsonschema.Schema
	compileErr error
)

// FS exposes the raw schema files, for callers that need to serve or publish them.
func FS() fs.FS { return schemaFS }

// Validator compiles the embedded schemas on first use and validates messages against
// them. The zero value is not usable; call NewValidator.
type Validator struct {
	schemas map[string]*jsonschema.Schema
}

// NewValidator returns a validator over the embedded schemas. Compilation happens once
// per process; the result is safe for concurrent use.
func NewValidator() (*Validator, error) {
	once.Do(func() {
		compiled, compileErr = compileAll()
	})
	if compileErr != nil {
		return nil, compileErr
	}
	return &Validator{schemas: compiled}, nil
}

func compileAll() (map[string]*jsonschema.Schema, error) {
	c := jsonschema.NewCompiler()

	// Formats are annotations by default in JSON Schema, so "format": "date-time" would
	// be decoration unless assertion is switched on. A declared constraint that is never
	// checked is worse than no constraint: it reads as enforced.
	c.AssertFormat()

	entries, err := fs.Glob(schemaFS, "schemas/*.json")
	if err != nil {
		return nil, fmt.Errorf("listing embedded schemas: %w", err)
	}
	if len(entries) == 0 {
		return nil, fmt.Errorf("no embedded schemas found")
	}

	// Register every file first, so cross-file $ref resolves regardless of order.
	for _, name := range entries {
		f, err := schemaFS.Open(name)
		if err != nil {
			return nil, fmt.Errorf("opening %s: %w", name, err)
		}
		doc, err := jsonschema.UnmarshalJSON(f)
		f.Close()
		if err != nil {
			return nil, fmt.Errorf("parsing %s: %w", name, err)
		}
		if err := c.AddResource(baseURI+path.Base(name), doc); err != nil {
			return nil, fmt.Errorf("adding %s: %w", name, err)
		}
	}

	out := make(map[string]*jsonschema.Schema, len(entries))
	for _, name := range entries {
		base := path.Base(name)
		kind := strings.TrimSuffix(base, ".json")
		if kind == "envelope" {
			continue // referenced by the others, never validated against directly
		}
		sch, err := c.Compile(baseURI + base)
		if err != nil {
			return nil, fmt.Errorf("compiling %s: %w", base, err)
		}
		out[kind] = sch
	}
	return out, nil
}

// Validate checks a decoded message against the schema for kind. The value must be the
// result of jsonschema.UnmarshalJSON or an equivalent any-typed decode, not a struct.
func (v *Validator) Validate(kind string, doc any) error {
	sch, ok := v.schemas[kind]
	if !ok {
		return fmt.Errorf("no schema for kind %q", kind)
	}
	return sch.Validate(doc)
}

// ValidateBytes decodes and validates a raw payload.
func (v *Validator) ValidateBytes(kind string, payload []byte) error {
	doc, err := jsonschema.UnmarshalJSON(strings.NewReader(string(payload)))
	if err != nil {
		return fmt.Errorf("payload is not valid JSON: %w", err)
	}
	return v.Validate(kind, doc)
}

// Kinds returns the validatable message kinds, sorted by the caller's needs.
func (v *Validator) Kinds() []string {
	out := make([]string, 0, len(v.schemas))
	for k := range v.schemas {
		out = append(out, k)
	}
	return out
}
