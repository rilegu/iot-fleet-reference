package main

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

// Store writes validated messages to TimescaleDB.
//
// Telemetry is batched: at a thousand devices reporting every second, one INSERT per
// message would spend most of its time in round trips. Status and events are written
// individually because they are low volume and must be durable before the broker is
// acknowledged.
type Store struct {
	pool *pgxpool.Pool
}

func NewStore(ctx context.Context, url string) (*Store, error) {
	cfg, err := pgxpool.ParseConfig(url)
	if err != nil {
		return nil, fmt.Errorf("parsing database url: %w", err)
	}
	// Batched writes come from a single worker, but status, events and dead letters are
	// written from the MQTT callback, so the pool needs room for both.
	cfg.MaxConns = 8
	cfg.MinConns = 2

	pool, err := pgxpool.NewWithConfig(ctx, cfg)
	if err != nil {
		return nil, fmt.Errorf("connecting to database: %w", err)
	}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		return nil, fmt.Errorf("pinging database: %w", err)
	}
	return &Store{pool: pool}, nil
}

func (s *Store) Close() { s.pool.Close() }

// WriteTelemetryBatch inserts a batch inside one transaction.
//
// The transaction is what makes a retry safe without a unique key: a partially applied
// batch cannot exist, so re-running it after a failure cannot double-insert rows that
// already committed.
func (s *Store) WriteTelemetryBatch(ctx context.Context, msgs []Message) error {
	if len(msgs) == 0 {
		return nil
	}

	rows := make([][]any, 0, len(msgs))
	for _, m := range msgs {
		var t Telemetry
		if err := json.Unmarshal(m.Payload, &t); err != nil {
			// Unreachable: the payload validated against the schema before reaching
			// here. Skip rather than fail the whole batch if it ever happens.
			continue
		}
		rows = append(rows, []any{
			m.ReceivedAt, t.DeviceID, t.Site, t.BootID, t.Seq, nullTime(t.TS),
			t.Metrics.TempC, t.Metrics.HumidityPct, t.Metrics.VoltageV,
			t.Metrics.RSSIdBm, t.Metrics.UptimeS,
		})
	}
	if len(rows) == 0 {
		return nil
	}

	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return fmt.Errorf("beginning telemetry transaction: %w", err)
	}
	defer tx.Rollback(ctx)

	_, err = tx.CopyFrom(ctx,
		pgx.Identifier{"telemetry"},
		[]string{"time", "device_id", "site", "boot_id", "seq", "device_ts",
			"temp_c", "humidity_pct", "voltage_v", "rssi_dbm", "uptime_s"},
		pgx.CopyFromRows(rows))
	if err != nil {
		return fmt.Errorf("copying telemetry: %w", err)
	}
	if err := tx.Commit(ctx); err != nil {
		return fmt.Errorf("committing telemetry: %w", err)
	}
	return nil
}

// WriteStatus records a presence transition and keeps the device registry current.
// ON CONFLICT DO NOTHING makes redelivery a no-op.
func (s *Store) WriteStatus(ctx context.Context, m Message) error {
	var st Status
	if err := json.Unmarshal(m.Payload, &st); err != nil {
		return fmt.Errorf("decoding status: %w", err)
	}

	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return fmt.Errorf("beginning status transaction: %w", err)
	}
	defer tx.Rollback(ctx)

	if _, err := tx.Exec(ctx, `
		INSERT INTO device_status
			(device_id, boot_id, seq, received_at, site, online, reason, fw_version, model, device_ts)
		VALUES ($1,$2,$3,$4,$5,$6,NULLIF($7,''),NULLIF($8,''),NULLIF($9,''),$10)
		ON CONFLICT DO NOTHING`,
		st.DeviceID, st.BootID, st.Seq, m.ReceivedAt, st.Site,
		st.Online, st.Reason, st.FwVersion, st.Model, nullTime(st.TS)); err != nil {
		return fmt.Errorf("inserting status: %w", err)
	}

	// The registry tracks identity, which telemetry does not carry. A Last Will is
	// published by the broker on the device's behalf and carries no fresh firmware
	// information, so it must not overwrite what the device itself reported.
	if _, err := tx.Exec(ctx, `
		INSERT INTO device (device_id, site, model, fw_version, first_seen, last_seen)
		VALUES ($1,$2,NULLIF($3,''),NULLIF($4,''),$5,$5)
		ON CONFLICT (device_id) DO UPDATE SET
			site       = EXCLUDED.site,
			model      = COALESCE(EXCLUDED.model, device.model),
			fw_version = COALESCE(EXCLUDED.fw_version, device.fw_version),
			last_seen  = GREATEST(device.last_seen, EXCLUDED.last_seen)`,
		st.DeviceID, st.Site, st.Model, st.FwVersion, m.ReceivedAt); err != nil {
		return fmt.Errorf("upserting device: %w", err)
	}

	return tx.Commit(ctx)
}

func (s *Store) WriteEvent(ctx context.Context, m Message) error {
	var ev Event
	if err := json.Unmarshal(m.Payload, &ev); err != nil {
		return fmt.Errorf("decoding event: %w", err)
	}
	_, err := s.pool.Exec(ctx, `
		INSERT INTO device_event
			(device_id, boot_id, seq, received_at, site, kind, severity, detail, metric, value, device_ts)
		VALUES ($1,$2,$3,$4,$5,$6,$7,NULLIF($8,''),NULLIF($9,''),$10,$11)
		ON CONFLICT (device_id, boot_id, seq) DO NOTHING`,
		ev.DeviceID, ev.BootID, ev.Seq, m.ReceivedAt, ev.Site,
		ev.Kind, ev.Severity, ev.Detail, ev.Metric, ev.Value, nullTime(ev.TS))
	if err != nil {
		return fmt.Errorf("inserting event: %w", err)
	}
	return nil
}

// WriteDeadLetter records a payload that failed validation. Callers sample rather than
// record every one: a misbehaving fleet could otherwise make this the largest table.
func (s *Store) WriteDeadLetter(ctx context.Context, topic, reason string, payload []byte) error {
	const maxStored = 4096
	if len(payload) > maxStored {
		payload = payload[:maxStored]
	}
	_, err := s.pool.Exec(ctx,
		`INSERT INTO dead_letter (topic, reason, payload) VALUES ($1,$2,$3)`,
		topic, reason, payload)
	return err
}

func nullTime(t time.Time) any {
	if t.IsZero() {
		return nil
	}
	return t
}
