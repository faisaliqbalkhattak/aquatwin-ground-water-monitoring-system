# Guardian integration contract (schema version 1)

## Raw telemetry

Topic: `aquatwin/well/{well_id}/telemetry`. Existing fields/units are preserved: `well_id`, `location.lat/lng` in decimal degrees, `depth_to_water_cm`, `water_column_height_cm`, `tds_ppm`, `status`, `timestamp` in UTC Unix seconds. Optional `source` can be `measured`, `simulated`, `unverified` or `historical`; configured well metadata takes precedence. Known SIM/WOKWI/DEMO identities default to simulated; everything else defaults to unverified.

Guardian requires canonical topic/payload identity agreement. Unity continues accepting its existing configured topic conventions and legacy packets. The worker's history/analysis currently consumes the canonical fleet schema; legacy Unity monitoring still works without Guardian analysis.

Structurally invalid packets are logged as rejected. Structurally valid raw events are persisted with source time, receipt time, provenance, retained/eligible flags, original JSON and a canonical payload hash. The unique key `(well_id,source,source_ts,hash)` deduplicates delivery. Different payloads at the same well/source/time are flagged and do not replace a usable aggregate. Retained, late-at-receipt, configured-out-of-range and reported-OFFLINE observations do not become new forecast evidence. Backfill is explicit and simulated-only.

## Insight messages

Retained topic: `aquatwin/insights/well/{well_id}`. Outputs never replace raw telemetry.

| Field | Meaning |
|---|---|
| schema_version, model_version, model | Versioned JSON and deterministic algorithm identity |
| well_id, source | Identity and measured/simulated/unverified/historical provenance |
| computed_at, valid_until | UTC Unix seconds; expired outputs cannot drive a live projection |
| quality | INSUFFICIENT_EVIDENCE, STALE, VERIFY_OBSERVATION, EXPLORATORY or ILLUSTRATIVE |
| window_hours, sample_count, aggregate_count, source_start/end | Exact evidence coverage/counts; aggregates are minute medians |
| depth_cm, column_cm, tds_ppm | Reference values, never aquifer storage percentage |
| drawdown_cm_day | Positive = depth increases / water surface falls |
| tds_change_ppm_day, depth_change_cm | Robust trend / observed window change |
| anomaly_score | Latest residual against the local robust baseline; an isolated large score withholds forecasts |
| risk, alerts, flags, neighbors, message | Operational review trigger and supporting facts; no causal/safety diagnosis |
| max_horizon_days | Minimum of configured cap and twice observed span |
| time_to_critical_days, time_to_tds_review_days | Null if not established within supported horizon; zero means already crossed |
| forecasts[] | `day`, `depth_cm`, `lower_cm`, `upper_cm`, `tds_ppm`; residual band is heuristic |
| history[] | Downsampled stored minute medians for the evidence chart, `day` relative to last point |
| backtest | Chronological holdout depth MAE, median error, heuristic-band coverage and sample count |
| reference_elevation_m, vertical_datum, aquifer_id | Optional configured metadata; missing/unequal datums disable a physical head-field interpretation |

Null crossing values must never be interpreted as zero days. The Unity decoder checks nullable presence independently from numeric deserialization. The bundled analytical demo uses a distinct, labelled long-duration synthetic fixture and never publishes fake raw packets.

## Scenario requests and replies

Publish a non-retained JSON request to `aquatwin/guardian/requests/{client_id}`:

```json
{
  "request_id": "unique_nonce",
  "well_id": "WELL_A",
  "sent_at": 1793016000,
  "action": "scenario",
  "inputs": {
    "flow_m3_hour": 2,
    "hours_day": 6,
    "reduction_percent": 30,
    "days": 7,
    "start_day": 0,
    "recharge_m3_day": 0
  }
}
```

Optional `area_m2` and `specific_yield` must be supplied together for a hypothetical uniform unconfined level response. They are assumptions, not silently inferred sensor channels. `start_day` delays the intervention within the specified period. Recharge does not change avoided extraction arithmetic; it affects only the optional water balance.

The service replies, non-retained, on `aquatwin/guardian/replies/{client_id}` with `{request_id,well_id,action,ok,result}` or `{request_id,ok:false,error}`. The result includes baseline/scenario m³ per day, total volumes, avoided m³, and optional modeled depths/sensitivity band. A request timestamp outside five minutes is rejected. Client IDs and nonces are constrained; callers cannot nominate arbitrary reply destinations. Retained commands are ignored. The worker deduplicates scenario requests and stores inputs/results. `action:"scenarios"` returns the last ten stored scenarios for that well.

Client rate is one request/second; a pending Unity request times out after 15 seconds. Local arithmetic remains usable while disconnected and is clearly identified separately from confirmed service persistence.

## Service status

`aquatwin/guardian/status` is retained. ONLINE includes processing timestamp and well count. An MQTT last-will marks unclean termination OFFLINE. Unity also expires stale service status rather than trusting an old ONLINE snapshot.

## Scientific boundaries

Forecasts are robust trend extrapolations, not calibrated groundwater-flow predictions. Residual bands and holdout coverage do not justify probability claims. Abstraction avoided is not proven aquifer recharge/recovery. TDS alone cannot certify water safety or a physical contamination mechanism. Cross-well coincidence requires explicitly shared aquifer metadata and proximity. No irrigation optimizer, AI investigator, advanced reliability score or sensor-placement adviser is included.
