import json
import math
from pathlib import Path
import shutil
import uuid
from contextlib import contextmanager

@contextmanager
def test_directory():
    root=Path.cwd().resolve()
    path=root/(".test-data-"+uuid.uuid4().hex)
    path.mkdir()
    try:yield path
    finally:
        assert path.resolve().parent==root and path.name.startswith(".test-data-")
        shutil.rmtree(path)
import unittest

from guardian_core.analytics import analyze, correlate
from guardian_core.scenarios import simulate, run_scenario
from guardian_core.service import handle_request
from guardian_core.store import Store

NOW = 1793016000


def packet(well="WELL_A", timestamp=NOW, depth=280, tds=450, **extra):
    return json.dumps({"well_id": well, "location": {"lat": 24.45, "lng": 54.37},
        "depth_to_water_cm": depth, "water_column_height_cm": 500-depth,
        "tds_ppm": tds, "status": "ONLINE", "timestamp": timestamp, **extra})


def ingest(store, well="WELL_A", timestamp=NOW, depth=280, tds=450, **extra):
    return store.ingest(packet(well,timestamp,depth,tds,**extra),f"aquatwin/well/{well}/telemetry",received_at=timestamp)


def series(store, well="WELL_A", ds=0, ts=0, hours=720):
    for h in range(hours+1):
        ingest(store,well,NOW-(hours-h)*3600,280-ds*(hours-h)/24,450-ts*(hours-h)/24)


class GuardianTests(unittest.TestCase):
    def setUp(self):
        self.store=Store(":memory:",{"window_days":45,"max_horizon_days":60,
            "wells":{"WELL_A":{"critical_depth_cm":360}}})
    def tearDown(self): self.store.close()

    def test_stable_no_alert(self):
        series(self.store)
        result=analyze(self.store,"WELL_A",NOW)
        self.assertEqual(result["risk"],"NORMAL");self.assertEqual(result["drawdown_cm_day"],0)
        self.assertEqual(result["alerts"],[]);self.assertIsNone(result["time_to_critical_days"])

    def test_report_linear_example(self):
        series(self.store,ds=2,ts=4)
        result=analyze(self.store,"WELL_A",NOW)
        self.assertAlmostEqual(result["drawdown_cm_day"],2)
        self.assertAlmostEqual(result["tds_change_ppm_day"],4)
        self.assertAlmostEqual(result["time_to_critical_days"],40)
        self.assertAlmostEqual(result["time_to_tds_review_days"],37.5)
        self.assertAlmostEqual(result["backtest"]["mae_cm"],0,places=7)

    def test_persistent_deterioration(self):
        series(self.store,ds=4.2,ts=12,hours=48)
        result=analyze(self.store,"WELL_A",NOW)
        self.assertEqual(result["risk"],"REVIEW");self.assertGreaterEqual(len(result["alerts"]),2)
        self.assertIn("not a validated",result["message"])

    def test_spike_is_verification_not_contamination(self):
        series(self.store,hours=48)
        ingest(self.store,timestamp=NOW+60,tds=2200)
        result=analyze(self.store,"WELL_A",NOW+60)
        self.assertEqual(result["quality"],"VERIFY_OBSERVATION");self.assertEqual(result["forecasts"],[])

    def test_short_history_withheld(self):
        series(self.store,hours=3)
        result=analyze(self.store,"WELL_A",NOW)
        self.assertEqual(result["quality"],"INSUFFICIENT_EVIDENCE");self.assertEqual(result["forecasts"],[])

    def test_reachable_current_operating_limit_needs_no_forecast_history(self):
        ingest(self.store,depth=370)
        result=analyze(self.store,"WELL_A",NOW)
        self.assertEqual(result["risk"],"REVIEW")
        self.assertTrue(result["alerts"])
        self.assertEqual(result["forecasts"],[])

    def test_retained_and_stale_never_refresh_forecast(self):
        series(self.store,hours=12)
        result=self.store.ingest(packet(timestamp=NOW+120),"aquatwin/well/WELL_A/telemetry",retained=True,received_at=NOW+120)
        self.assertFalse(result["eligible"])
        result=analyze(self.store,"WELL_A",NOW+120)
        self.assertEqual(result["quality"],"STALE");self.assertEqual(result["forecasts"],[])

    def test_stale_at_receipt_excluded(self):
        result=self.store.ingest(packet(timestamp=NOW-3600),"aquatwin/well/WELL_A/telemetry",received_at=NOW)
        self.assertFalse(result["eligible"])

    def test_future_clock_is_not_fresh_evidence(self):
        result=self.store.ingest(packet(timestamp=NOW+30),"aquatwin/well/WELL_A/telemetry",received_at=NOW)
        self.assertFalse(result["eligible"])
        self.assertEqual(analyze(self.store,"WELL_A",NOW)["quality"],"VERIFY_OBSERVATION")

    def test_dedup_and_timestamp_conflict(self):
        self.assertTrue(ingest(self.store)["inserted"])
        self.assertFalse(ingest(self.store)["inserted"])
        conflict=ingest(self.store,depth=281)
        self.assertFalse(conflict["eligible"])
        self.assertIn("conflicting_same_timestamp",conflict["flags"])
        self.assertEqual(self.store.minute_history("WELL_A",NOW-1,NOW+1)[0]["depth"],280)
        self.assertEqual(analyze(self.store,"WELL_A",NOW)["quality"],"VERIFY_OBSERVATION")

    def test_invalid_inputs(self):
        for payload,topic in [(packet(tds=float("nan")),"aquatwin/well/WELL_A/telemetry"),
                              (packet(),"aquatwin/well/OTHER/telemetry"),
                              (packet(depth=-1),"aquatwin/well/WELL_A/telemetry")]:
            with self.assertRaises(ValueError):self.store.ingest(payload,topic,received_at=NOW)
        for malformed in ("[]", "null", "42"):
            with self.assertRaises(ValueError):self.store.ingest(malformed,"aquatwin/well/WELL_A/telemetry",received_at=NOW)

    def test_optional_critical_depth_null(self):
        self.store.config["wells"]["WELL_A"]["critical_depth_cm"]=None
        series(self.store,hours=48)
        result=analyze(self.store,"WELL_A",NOW)
        self.assertIsNone(result["time_to_critical_days"])

    def test_history_survives_restart(self):
        with test_directory() as folder:
            path=Path(folder)/"history.sqlite3"
            first=Store(path);series(first,hours=12);first.close()
            second=Store(path)
            self.assertEqual(len(second.history("WELL_A",0,NOW)),13)
            self.assertEqual(analyze(second,"WELL_A",NOW)["aggregate_count"],13)
            second.close()

    def test_dynamic_well_count_and_provenance(self):
        for i in range(37): ingest(self.store,f"SIM_{i}")
        self.assertEqual(len(self.store.wells()),37)
        self.assertEqual(self.store.source("SIM_3"),"simulated")
        self.assertEqual(self.store.source("not_seen"),"unverified")

    def test_no_cross_aquifer_claim_without_metadata(self):
        series(self.store,ds=3,hours=48);series(self.store,"WELL_B",ds=3,hours=48)
        results=correlate(self.store,[analyze(self.store,w,NOW) for w in self.store.wells()],NOW)
        self.assertTrue(all(not r["neighbors"] for r in results))
        self.store.config["wells"]={"WELL_A":{"aquifer_id":"known"},"WELL_B":{"aquifer_id":"known"}}
        results=correlate(self.store,[analyze(self.store,w,NOW) for w in self.store.wells()],NOW)
        self.assertTrue(all(r["neighbors"] for r in results))

    def test_scenario_volume_conservation(self):
        inputs={"flow_m3_hour":2,"hours_day":6,"reduction_percent":30,"days":7}
        result=simulate(inputs)
        self.assertAlmostEqual(result["baseline_m3_day"],12)
        self.assertAlmostEqual(result["scenario_m3_day"],8.4)
        self.assertAlmostEqual(result["avoided_m3"],25.2)
        self.assertAlmostEqual(result["baseline_total_m3"]-result["scenario_total_m3"],result["avoided_m3"])
        self.assertFalse(result["level_model_available"])
        inputs["reduction_percent"]=0
        self.assertEqual(simulate(inputs)["avoided_m3"],0)

    def test_scenario_delay_and_response_require_assumptions(self):
        x={"flow_m3_hour":2,"hours_day":6,"reduction_percent":30,"days":7,"start_day":2,"area_m2":1000,"specific_yield":.1}
        result=simulate(x,{"depth":280})
        self.assertAlmostEqual(result["avoided_m3"],18)
        self.assertTrue(result["level_model_available"])
        self.assertLess(result["scenario_depth_cm"],result["baseline_depth_cm"])
        self.assertIn("Hypothetical",result["message"])
        x["specific_yield"]=0
        with self.assertRaises(ValueError):simulate(x,{"depth":280})

    def test_save_and_compare_idempotent_scenarios(self):
        x={"flow_m3_hour":2,"hours_day":6,"reduction_percent":30,"days":7}
        a=run_scenario(self.store,"one","WELL_A",x,NOW)
        b=run_scenario(self.store,"one","WELL_A",x,NOW+10)
        self.assertEqual(a,b);self.assertEqual(len(self.store.scenarios("WELL_A")),1)
        self.assertEqual(self.store.wells(),[]) # never fabricates raw telemetry
        x["days"]=10
        with self.assertRaises(ValueError):run_scenario(self.store,"one","WELL_A",x,NOW)

    def test_request_routing_and_expiry(self):
        request={"request_id":"r1","well_id":"WELL_A","sent_at":NOW,"action":"scenario",
                 "inputs":{"flow_m3_hour":2,"hours_day":6,"reduction_percent":30,"days":7}}
        reply,result=handle_request(self.store,"aquatwin/guardian/requests/unity_test",json.dumps(request),NOW)
        self.assertEqual(reply,"aquatwin/guardian/replies/unity_test");self.assertTrue(result["ok"])
        with self.assertRaises(ValueError):handle_request(self.store,"aquatwin/guardian/requests/../../other",json.dumps(request),NOW)
        with self.assertRaises(ValueError):handle_request(self.store,"aquatwin/guardian/requests/unity_test",json.dumps(request),NOW+301)
        with self.assertRaises(ValueError):handle_request(self.store,"aquatwin/guardian/requests/unity_test","[]",NOW)
        request["inputs"]=[]
        with self.assertRaises(ValueError):handle_request(self.store,"aquatwin/guardian/requests/unity_test",json.dumps(request),NOW)


if __name__=="__main__":unittest.main()
