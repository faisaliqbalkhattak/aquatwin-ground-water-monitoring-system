import hashlib
import json
import math
from statistics import median
from pathlib import Path
import re
import sqlite3
import time

ID = re.compile(r"^[^/+#\x00-\x1f]{1,128}$")
SOURCES = {"measured", "simulated", "unverified", "historical"}


def number(value, name, low=0, high=1e12):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not low <= value <= high:
        raise ValueError(f"{name} must be finite and in [{low}, {high}]")
    return float(value)


class Store:
    """Raw packets and usable observations are deliberately different records."""
    def __init__(self, path, config=None):
        self.config = config or {}
        if str(path) != ":memory:":
            Path(path).parent.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(str(path))
        self.db.row_factory = sqlite3.Row
        self.db.executescript("""
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS telemetry(
          id INTEGER PRIMARY KEY, well_id TEXT NOT NULL, source TEXT NOT NULL,
          source_ts REAL NOT NULL, received_ts REAL NOT NULL, topic TEXT,
          depth REAL, column_cm REAL, tds REAL, lat REAL, lng REAL,
          retained INTEGER, eligible INTEGER, flags TEXT, raw TEXT, hash TEXT,
          UNIQUE(well_id,source,source_ts,hash));
        CREATE INDEX IF NOT EXISTS telemetry_window ON telemetry(well_id,source,eligible,source_ts);
        CREATE TABLE IF NOT EXISTS well_metadata(well_id TEXT PRIMARY KEY, source TEXT, metadata TEXT);
        CREATE TABLE IF NOT EXISTS aggregates(well_id TEXT,source TEXT,minute INTEGER,depth REAL,tds REAL,n INTEGER,
          PRIMARY KEY(well_id,source,minute));
        CREATE TABLE IF NOT EXISTS insight_runs(id INTEGER PRIMARY KEY,well_id TEXT,computed_at REAL,model TEXT,payload TEXT);
        CREATE TABLE IF NOT EXISTS scenario_runs(request_id TEXT PRIMARY KEY,well_id TEXT,created_at REAL,inputs TEXT,payload TEXT);
        """)

    def close(self):
        self.db.close()

    def metadata(self, well_id):
        return dict(self.config.get("wells", {}).get(well_id, {}))

    def ingest(self, payload, topic, retained=False, received_at=None, replay=False):
        now = time.time() if received_at is None else received_at
        if len(payload.encode("utf-8")) > 16384:
            raise ValueError("telemetry exceeds 16 KiB")
        data = json.loads(payload)
        if not isinstance(data, dict): raise ValueError("telemetry must be a JSON object")
        well_id = data.get("well_id")
        if not isinstance(well_id, str) or not ID.fullmatch(well_id):
            raise ValueError("invalid well_id")
        if topic != f"aquatwin/well/{well_id}/telemetry":
            raise ValueError("topic and payload well_id disagree")
        loc = data.get("location", {})
        lat = number(loc.get("lat"), "latitude", -90, 90)
        lng = number(loc.get("lng"), "longitude", -180, 180)
        depth = number(data.get("depth_to_water_cm"), "depth", 0, 1e6)
        column = number(data.get("water_column_height_cm"), "column", 0, 1e6)
        tds = number(data.get("tds_ppm"), "TDS", 0, 1e6)
        ts = number(data.get("timestamp"), "Unix seconds", 1, now + 300)
        if data.get("status") not in ("ONLINE", "OFFLINE"):
            raise ValueError("status must be ONLINE or OFFLINE")
        meta = self.metadata(well_id)
        default_source = "simulated" if any(x in well_id.upper() for x in ("DEMO", "SIM", "WOKWI")) else "unverified"
        source = meta.get("source", data.get("source", default_source))
        if source not in SOURCES:
            raise ValueError("unknown provenance")
        if replay and source != "simulated":
            raise ValueError("replay import is allowed only for explicitly simulated data")
        flags = []
        if retained: flags.append("retained_snapshot")
        if ts > now + 5: flags.append("source_clock_future")
        if now - ts > self.config.get("stale_after_seconds", 60): flags.append("stale_at_receipt")
        if data["status"] != "ONLINE": flags.append("reported_offline")
        if depth > meta.get("valid_depth_max_cm", 1e6): flags.append("outside_configured_sensor_range")
        if depth + column <= 0: flags.append("zero_reported_well_depth")
        canonical = json.dumps(data, sort_keys=True, separators=(",", ":"), allow_nan=False)
        digest = hashlib.sha256(canonical.encode()).hexdigest()
        conflict = self.db.execute("SELECT 1 FROM telemetry WHERE well_id=? AND source=? AND source_ts=? AND hash!=? AND eligible=1",
                                   (well_id, source, ts, digest)).fetchone()
        if conflict: flags.append("conflicting_same_timestamp")
        eligible = not any(f != "stale_at_receipt" or not replay for f in flags)
        with self.db:
            changed = self.db.execute("""INSERT OR IGNORE INTO telemetry
                (well_id,source,source_ts,received_ts,topic,depth,column_cm,tds,lat,lng,retained,eligible,flags,raw,hash)
                VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                (well_id, source, ts, now, topic, depth, column, tds, lat, lng, retained, eligible, json.dumps(flags), payload, digest)).rowcount
            self.db.execute("INSERT OR REPLACE INTO well_metadata VALUES(?,?,?)", (well_id, source, json.dumps(meta)))
            if changed and eligible:
                minute = int(ts // 60)
                bucket = self.db.execute("SELECT depth,tds FROM telemetry WHERE well_id=? AND source=? AND eligible=1 AND source_ts>=? AND source_ts<?",
                                         (well_id, source, minute*60, minute*60+60)).fetchall()
                self.db.execute("INSERT OR REPLACE INTO aggregates VALUES(?,?,?,?,?,?)",
                    (well_id, source, minute, median(r[0] for r in bucket), median(r[1] for r in bucket), len(bucket)))
        return {"well_id": well_id, "inserted": bool(changed), "eligible": eligible, "flags": flags}

    def wells(self):
        return [r[0] for r in self.db.execute("SELECT well_id FROM well_metadata ORDER BY well_id")]

    def source(self, well_id):
        row = self.db.execute("SELECT source FROM well_metadata WHERE well_id=?", (well_id,)).fetchone()
        return row[0] if row else "unverified"

    def latest(self, well_id):
        row = self.db.execute("SELECT * FROM telemetry WHERE well_id=? AND source=? ORDER BY source_ts DESC,id DESC LIMIT 1",
                              (well_id, self.source(well_id))).fetchone()
        return dict(row) if row else None

    def history(self, well_id, start, end):
        return [dict(r) for r in self.db.execute("""SELECT * FROM telemetry
            WHERE well_id=? AND source=? AND eligible=1 AND source_ts BETWEEN ? AND ? ORDER BY source_ts""",
            (well_id, self.source(well_id), start, end))]

    def minute_history(self, well_id, start, end):
        return [{"t": r["minute"]*60, "depth": r["depth"], "tds": r["tds"], "n": r["n"]}
                for r in self.db.execute("SELECT * FROM aggregates WHERE well_id=? AND source=? AND minute BETWEEN ? AND ? ORDER BY minute",
                    (well_id, self.source(well_id), int(start//60), int(end//60)))]

    def save_insight(self, insight):
        with self.db:
            self.db.execute("INSERT INTO insight_runs(well_id,computed_at,model,payload) VALUES(?,?,?,?)",
                            (insight["well_id"], insight["computed_at"], insight["model_version"], json.dumps(insight, allow_nan=False)))

    def save_scenario(self, request_id, well_id, inputs, result, now):
        with self.db:
            self.db.execute("INSERT OR IGNORE INTO scenario_runs VALUES(?,?,?,?,?)",
                            (request_id, well_id, now, json.dumps(inputs), json.dumps(result, allow_nan=False)))

    def scenarios(self, well_id, limit=10):
        return [json.loads(r[0]) for r in self.db.execute("SELECT payload FROM scenario_runs WHERE well_id=? ORDER BY created_at DESC LIMIT ?", (well_id, limit))]
