"""Explainable, unit-explicit trend baselines; no causal or safety diagnoses."""
from collections import defaultdict
import json
import math
from statistics import median
import time
from . import VERSION

DAY = 86400


def aggregate(rows):
    bins = defaultdict(list)
    for row in rows:
        bins[int(row["source_ts"] // 60)].append(row)
    return [{"t": median(r["source_ts"] for r in group),
             "depth": median(r["depth"] for r in group),
             "tds": median(r["tds"] for r in group), "n": len(group)}
            for _, group in sorted(bins.items())]


def fit(points, field):
    # Bound quadratic Theil-Sen work for large fleets without changing time span.
    pts = points if len(points) <= 128 else [points[round(i * (len(points) - 1) / 127)] for i in range(128)]
    t0 = pts[0]["t"]
    xy = [((p["t"] - t0) / DAY, p[field]) for p in pts]
    slopes = [(b[1] - a[1]) / (b[0] - a[0]) for i, a in enumerate(xy) for b in xy[i + 1:] if b[0] > a[0]]
    slope = median(slopes) if slopes else 0
    intercept = median(y - slope * x for x, y in xy)
    residual = [p[field] - (intercept + slope * (p["t"] - t0) / DAY) for p in points]
    center = median(residual)
    sigma = 1.4826 * median(abs(r - center) for r in residual)
    return slope, intercept, t0, sigma


def anomaly(points):
    if len(points) < 8: return 0.0
    base, last = points[-31:-1], points[-1]
    result = 0.0
    # Scores on model residuals avoid flagging a smooth persistent trend as a spike.
    for field, floor in (("depth", 1.0), ("tds", 25.0)):
        slope, intercept, t0, sigma = fit(base, field)
        expected = intercept + slope * (last["t"] - t0) / DAY
        result = max(result, abs(last[field] - expected) / max(floor, sigma))
    return result


def crossing(now_value, slope, threshold, horizon):
    if threshold is None: return None
    if now_value >= threshold: return 0.0
    if slope <= 0: return None
    days = (threshold - now_value) / slope
    return round(days, 3) if 0 <= days <= horizon else None


def backtest(points):
    if len(points) < 15: return None
    split = max(8, int(len(points) * .8))
    train, test = points[:split], points[split:]
    slope, intercept, t0, sigma = fit(train, "depth")
    errors, covered = [], 0
    span = max(1 / DAY, (train[-1]["t"] - train[0]["t"]) / DAY)
    for p in test:
        predicted = intercept + slope * (p["t"] - t0) / DAY
        error = abs(p["depth"] - predicted)
        half = max(1, 1.96 * sigma) * math.sqrt(1 + max(0, p["t"] - train[-1]["t"]) / DAY / span)
        errors.append(error)
        covered += error <= half
    return {"mae_cm": sum(errors) / len(errors), "median_absolute_error_cm": median(errors),
            "interval_coverage": covered / len(errors), "held_out_samples": len(errors),
            "note": "chronological holdout; heuristic residual band, not calibrated confidence"}


def analyze(store, well_id, now=None):
    now = time.time() if now is None else now
    cfg, meta = store.config, store.metadata(well_id)
    latest = store.latest(well_id)
    points = store.minute_history(well_id, now - cfg.get("window_days", 14) * DAY, now)
    span = (points[-1]["t"] - points[0]["t"]) if len(points) > 1 else 0
    out = {"schema_version": 1, "well_id": well_id, "source": store.source(well_id),
           "computed_at": now, "valid_until": now + max(30, cfg.get("publish_interval_seconds", 15) * 3),
           "model_version": VERSION, "model": "Theil-Sen / minute medians", "quality": "INSUFFICIENT_EVIDENCE",
           "risk": "UNKNOWN", "window_hours": span / 3600, "sample_count": sum(p["n"] for p in points),
           "aggregate_count": len(points), "source_start": points[0]["t"] if points else 0,
           "source_end": points[-1]["t"] if points else 0,
           "depth_cm": latest["depth"] if latest else None, "column_cm": latest["column_cm"] if latest else None,
           "tds_ppm": latest["tds"] if latest else None, "drawdown_cm_day": None,
           "tds_change_ppm_day": None, "depth_change_cm": None, "anomaly_score": 0,
           "max_horizon_days": 0, "critical_depth_cm": meta.get("critical_depth_cm"),
           "time_to_critical_days": None, "time_to_tds_review_days": None,
           "forecasts": [], "history": [], "alerts": [], "neighbors": [], "recovery_cm": None,
           "flags": [], "message": "Collecting representative history; predictions are withheld.",
           "reference_elevation_m": meta.get("reference_elevation_m"), "vertical_datum": meta.get("vertical_datum"),
           "aquifer_id": meta.get("aquifer_id"), "tds_calibrated": bool(meta.get("tds_calibrated", False))}
    if not latest:
        out["message"] = "No observations have been stored for this well."
        return out
    if any(x in json.loads(latest["flags"]) for x in ("conflicting_same_timestamp", "outside_configured_sensor_range", "zero_reported_well_depth", "source_clock_future")):
        out["quality"] = "VERIFY_OBSERVATION"
        out["message"] = "Latest observation failed an input check; forecast withheld pending a valid observation."
        return out
    last_usable = store.db.execute("SELECT MAX(source_ts) FROM telemetry WHERE well_id=? AND source=? AND eligible=1 AND source_ts<=?", (well_id, out["source"], now)).fetchone()[0]
    if not points or last_usable is None or now - last_usable > cfg.get("stale_after_seconds", 60):
        out["quality"] = "STALE"
        out["message"] = "No fresh usable observations. Retained/stale packets cannot refresh a forecast."
        return out
    # Current operational limits need one valid fresh observation, not a long forecast history.
    if points[-1]["tds"] > cfg.get("tds_review_ppm", 600):
        out["alerts"].append("TDS exceeds the configured operational review threshold.")
    if meta.get("critical_depth_cm") is not None and points[-1]["depth"] >= meta["critical_depth_cm"]:
        out["alerts"].append("Depth exceeds the configured operating limit.")
    if out["alerts"]: out["risk"]="REVIEW"
    out["anomaly_score"] = round(anomaly(points), 3)
    if out["anomaly_score"] > 6:
        out["quality"] = "VERIFY_OBSERVATION"
        out["risk"] = "REVIEW"
        out["alerts"] = ["Latest reading departs from its local baseline; verify before interpreting a regional event."]
        out["message"] = "Suspect isolated change. Forecast withheld pending corroboration."
        return out
    if len(points) < cfg.get("min_samples", 12) or span < cfg.get("min_span_hours", 6) * 3600:
        return out
    gaps = [b["t"]-a["t"] for a,b in zip(points, points[1:])]
    if gaps and max(gaps) > max(6*3600, median(gaps)*5):
        out["message"] = "History contains a major observation gap; forecast withheld."
        return out
    ds, di, t0, sigma = fit(points, "depth")
    ts, ti, _, _ = fit(points, "tds")
    latest_depth, latest_tds = points[-1]["depth"], points[-1]["tds"]
    max_horizon = min(cfg.get("max_horizon_days", 30), span / DAY * 2)
    out.update(quality="ILLUSTRATIVE" if out["source"] == "simulated" else "EXPLORATORY",
               drawdown_cm_day=round(ds, 5), tds_change_ppm_day=round(ts, 5),
               depth_change_cm=round(points[-1]["depth"] - points[0]["depth"], 4),
               max_horizon_days=round(max_horizon, 3), risk="NORMAL")
    if not meta.get("tds_calibrated", False): out["flags"].append("TDS estimate is uncalibrated; review indicator only")
    if meta.get("critical_depth_cm") is None: out["flags"].append("Configure critical operating depth to estimate its crossing time")
    if meta.get("critical_depth_cm") is not None and meta["critical_depth_cm"] > meta.get("valid_depth_max_cm", float("inf")):
        out["flags"].append("Configured critical depth exceeds sensor range")
    if ds > cfg.get("drawdown_review_cm_day", 2): out["alerts"].append(f"Depth is increasing by {ds:.2f} cm/day over {span / 3600:.1f} hours.")
    if ts > cfg.get("tds_rise_review_ppm_day", 10): out["alerts"].append(f"Dissolved-solids estimate is rising by {ts:.2f} ppm/day.")
    if out["alerts"]: out["risk"] = "REVIEW"
    out["time_to_critical_days"] = crossing(latest_depth, ds, meta.get("critical_depth_cm"), max_horizon)
    out["time_to_tds_review_days"] = crossing(latest_tds, ts, cfg.get("tds_review_ppm", 600), max_horizon)
    for day in sorted({0.0, min(1., max_horizon), min(7., max_horizon), min(30., max_horizon), max_horizon}):
        half = max(1., 1.96 * sigma) * math.sqrt(1 + day / max(span / DAY, .001))
        depth = latest_depth + ds * day
        out["forecasts"].append({"day": day, "depth_cm": depth, "lower_cm": depth - half,
                                 "upper_cm": depth + half, "tds_ppm": max(0, latest_tds + ts * day)})
    out["backtest"] = backtest(points)
    stride = max(1, math.ceil(len(points)/64))
    out["history"] = [{"day": (p["t"]-points[-1]["t"])/DAY, "depth_cm": p["depth"], "tds_ppm": p["tds"]} for p in points[::stride]]
    pause = meta.get("pump_pause_at")
    if isinstance(pause, (int, float)):
        before = [p["depth"] for p in points if pause - 3600 <= p["t"] < pause]
        after = [p["depth"] for p in points if pause <= p["t"] <= pause + 3600]
        if before and after: out["recovery_cm"] = median(before) - median(after)
    out["message"] = ("Review the observed trend and verify calibration/pumping records." if out["alerts"] else "No persistent deterioration trigger in this analysis window.")
    out["message"] += " Trend extrapolation is not a validated aquifer model."
    return out


def correlate(store, results, now=None):
    """Coincidence only: require an explicitly configured shared aquifer and nearby positions."""
    now = time.time() if now is None else now
    for a in results:
        if a["quality"] not in ("EXPLORATORY", "ILLUSTRATIVE") or not a["aquifer_id"]: continue
        pa = store.latest(a["well_id"])
        for b in results:
            if a is b or b["aquifer_id"] != a["aquifer_id"] or b["source"] != a["source"]: continue
            if b["quality"] not in ("EXPLORATORY", "ILLUSTRATIVE"): continue
            pb = store.latest(b["well_id"])
            km = math.hypot((pa["lat"] - pb["lat"]) * 111.2, (pa["lng"] - pb["lng"]) * 111.2 * math.cos(math.radians(pa["lat"])))
            if km > 5: continue
            values = []
            for well in (a, b):
                groups = defaultdict(list)
                for r in store.minute_history(well["well_id"], now - store.config.get("window_days", 14) * DAY, now):
                    groups[int(r["t"] // 3600)].append(r["depth"])
                values.append({k: median(v) for k, v in groups.items()})
            aligned = sorted(values[0].keys() & values[1].keys())
            if len(aligned) < 8: continue
            x = [values[0][k] for k in aligned]; y = [values[1][k] for k in aligned]
            mx, my = sum(x) / len(x), sum(y) / len(y)
            den = math.sqrt(sum((v-mx)**2 for v in x)*sum((v-my)**2 for v in y))
            coefficient = sum((xx-mx)*(yy-my) for xx, yy in zip(x, y)) / den if den else 0
            if coefficient > .7 and a["drawdown_cm_day"] > 0 and b["drawdown_cm_day"] > 0:
                a["neighbors"].append(f"{b['well_id']}: r={coefficient:.2f}, {len(aligned)} aligned hours; coincident trend, not proof of cause")
    return results
