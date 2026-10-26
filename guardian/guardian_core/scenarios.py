import time
from .store import number
from . import VERSION


def simulate(inputs, latest=None):
    flow = number(inputs.get("flow_m3_hour"), "flow m3/hour", 0, 100000)
    hours = number(inputs.get("hours_day"), "hours/day", 0, 24)
    reduction = number(inputs.get("reduction_percent"), "reduction %", 0, 100) / 100
    days = number(inputs.get("days", 7), "days", .01, 365)
    delay = number(inputs.get("start_day", 0), "intervention start day", 0, days)
    recharge = number(inputs.get("recharge_m3_day", 0), "assumed recharge m3/day", 0, 1e8)
    baseline = flow * hours
    scenario = baseline * (1-reduction)
    avoided = (baseline-scenario) * (days-delay)
    result = {"model_version": VERSION, "source": "hypothetical", "baseline_m3_day": baseline,
              "scenario_m3_day": scenario, "avoided_m3": avoided, "days": days, "start_day": delay,
              "baseline_total_m3": baseline*days, "scenario_total_m3": baseline*delay+scenario*(days-delay),
              "level_model_available": False, "baseline_depth_cm": None, "scenario_depth_cm": None,
              "scenario_lower_cm": None, "scenario_upper_cm": None,
              "message": "User-entered flow/runtime assumptions. Avoided abstraction is not proven recharge or recovery."}
    area, sy = inputs.get("area_m2"), inputs.get("specific_yield")
    if area is not None or sy is not None:
        area = number(area, "effective area m2", .01, 1e12)
        sy = number(sy, "specific yield", .0001, 1)
        if latest is None: raise ValueError("A reference well reading is required for hypothetical level response")
        base_change = (baseline - recharge) * days / (area * sy) * 100
        intervention_change = (result["scenario_total_m3"] - recharge * days) / (area * sy) * 100
        # A visible sensitivity envelope from +/-20% effective area assumptions.
        ends = [latest["depth"] + intervention_change / .8, latest["depth"] + intervention_change / 1.2]
        result.update(level_model_available=True, baseline_depth_cm=latest["depth"]+base_change,
                      scenario_depth_cm=latest["depth"]+intervention_change,
                      scenario_lower_cm=min(ends), scenario_upper_cm=max(ends),
                      message="Hypothetical unconfined water balance; uniform effective area and specific yield. Band varies area +/-20%; not a calibrated confidence interval.")
    return result


def run_scenario(store, request_id, well_id, inputs, now=None):
    now = time.time() if now is None else now
    row = store.db.execute("SELECT well_id,inputs,payload FROM scenario_runs WHERE request_id=?", (request_id,)).fetchone()
    if row:
        import json
        if row["well_id"] != well_id or json.loads(row["inputs"]) != inputs: raise ValueError("request ID already used for different inputs")
        return json.loads(row["payload"])
    result = simulate(inputs, store.latest(well_id))
    result.update(request_id=request_id, well_id=well_id, computed_at=now, inputs=inputs)
    store.save_scenario(request_id, well_id, inputs, result, now)
    return result
