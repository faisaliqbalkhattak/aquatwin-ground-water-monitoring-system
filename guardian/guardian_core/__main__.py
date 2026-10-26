import argparse
import csv
import json
import logging
from pathlib import Path
from .analytics import analyze, correlate
from .store import Store


def main():
    parser = argparse.ArgumentParser(description="AquaTwin Guardian intelligence worker")
    subs = parser.add_subparsers(dest="command", required=True)
    serve = subs.add_parser("run"); serve.add_argument("--config", default="config.local.json")
    replay = subs.add_parser("replay"); replay.add_argument("file"); replay.add_argument("--db", default="data/replay.sqlite3")
    report = subs.add_parser("analyze"); report.add_argument("--db", default="data/guardian.sqlite3"); report.add_argument("--config"); report.add_argument("--at", type=float)
    export = subs.add_parser("export"); export.add_argument("--db", default="data/guardian.sqlite3"); export.add_argument("--output", required=True)
    backup = subs.add_parser("backup"); backup.add_argument("--db", default="data/guardian.sqlite3"); backup.add_argument("--output", required=True)
    example = subs.add_parser("examples"); example.add_argument("--output", required=True)
    args = parser.parse_args()
    if args.command == "run":
        from .service import run
        logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
        run(args.config)
    elif args.command == "examples":
        from .examples import export_examples
        export_examples(args.output)
    else:
        cfg = json.loads(Path(args.config).read_text()) if getattr(args, "config", None) else {}
        store = Store(args.db, cfg)
        try:
            if args.command == "replay":
                count = 0
                for line in Path(args.file).read_text(encoding="utf-8-sig").splitlines():
                    if not line.strip(): continue
                    data = json.loads(line); data["source"] = "simulated"
                    count += store.ingest(json.dumps(data), f"aquatwin/well/{data['well_id']}/telemetry", received_at=data["timestamp"], replay=True)["inserted"]
                print(f"Imported {count} simulated observations. No MQTT messages were published.")
            elif args.command == "analyze":
                print(json.dumps(correlate(store, [analyze(store, w, args.at) for w in store.wells()], args.at), indent=2, allow_nan=False))
            elif args.command == "export":
                output = Path(args.output); output.parent.mkdir(parents=True, exist_ok=True)
                with output.open("w", newline="", encoding="utf-8") as file:
                    rows = store.db.execute("SELECT well_id,source,source_ts,received_ts,depth,column_cm,tds,lat,lng,retained,eligible,flags FROM telemetry ORDER BY well_id,source_ts")
                    writer = csv.writer(file); writer.writerow([d[0] for d in rows.description]); writer.writerows(rows)
            elif args.command == "backup":
                import sqlite3
                output = Path(args.output); output.parent.mkdir(parents=True, exist_ok=True)
                if output.exists() or output.resolve()==Path(args.db).resolve(): raise ValueError("Choose a new backup filename")
                target=sqlite3.connect(str(output))
                try: store.db.backup(target)
                finally: target.close()
        finally: store.close()


if __name__ == "__main__": main()
