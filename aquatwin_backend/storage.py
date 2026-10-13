"""SQLite time-series storage for validated AquaTwin telemetry."""

from __future__ import annotations

from datetime import datetime
from pathlib import Path
import sqlite3

from cloud_monitor.telemetry import Telemetry


class TelemetryStore:
    def __init__(self, database_path: str | Path) -> None:
        self.database_path = str(database_path)
        if self.database_path != ":memory:":
            Path(self.database_path).parent.mkdir(parents=True, exist_ok=True)
        self.initialize()

    def _connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(self.database_path, timeout=10)
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA journal_mode=WAL")
        connection.execute("PRAGMA foreign_keys=ON")
        return connection

    def initialize(self) -> None:
        with self._connect() as connection:
            connection.executescript(
                """
                CREATE TABLE IF NOT EXISTS telemetry (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    well_id TEXT NOT NULL,
                    latitude REAL NOT NULL,
                    longitude REAL NOT NULL,
                    depth_to_water_cm REAL NOT NULL,
                    water_column_height_cm REAL NOT NULL,
                    tds_ppm REAL NOT NULL,
                    status TEXT NOT NULL,
                    source_timestamp INTEGER NOT NULL,
                    topic TEXT NOT NULL,
                    received_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_telemetry_well_id_id
                ON telemetry(well_id, id DESC);
                """
            )

    def insert(self, telemetry: Telemetry) -> int:
        with self._connect() as connection:
            cursor = connection.execute(
                """
                INSERT INTO telemetry (
                    well_id, latitude, longitude, depth_to_water_cm,
                    water_column_height_cm, tds_ppm, status,
                    source_timestamp, topic, received_at
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    telemetry.well_id,
                    telemetry.latitude,
                    telemetry.longitude,
                    telemetry.depth_to_water_cm,
                    telemetry.water_column_height_cm,
                    telemetry.tds_ppm,
                    telemetry.status,
                    telemetry.source_timestamp,
                    telemetry.topic,
                    telemetry.received_at.isoformat(),
                ),
            )
            return int(cursor.lastrowid)

    def latest_wells(self) -> list[dict[str, object]]:
        with self._connect() as connection:
            rows = connection.execute(
                """
                SELECT * FROM (
                    SELECT t.*,
                        ROW_NUMBER() OVER (
                            PARTITION BY well_id
                            ORDER BY source_timestamp DESC, id DESC
                        ) AS newest
                    FROM telemetry AS t
                )
                WHERE newest = 1
                ORDER BY well_id
                """
            ).fetchall()
        return [self._row_to_payload(row) for row in rows]

    def history(self, well_id: str, limit: int = 100) -> list[dict[str, object]]:
        with self._connect() as connection:
            rows = connection.execute(
                """
                SELECT * FROM telemetry
                WHERE well_id = ?
                ORDER BY id DESC
                LIMIT ?
                """,
                (well_id, max(1, min(limit, 1000))),
            ).fetchall()
        return [self._row_to_payload(row) for row in reversed(rows)]

    @staticmethod
    def _row_to_payload(row: sqlite3.Row) -> dict[str, object]:
        return {
            "well_id": row["well_id"],
            "location": {"lat": row["latitude"], "lng": row["longitude"]},
            "depth_to_water_cm": row["depth_to_water_cm"],
            "water_column_height_cm": row["water_column_height_cm"],
            "tds_ppm": row["tds_ppm"],
            "status": row["status"],
            "timestamp": row["source_timestamp"],
            "received_at": row["received_at"],
        }
