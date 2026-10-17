using System;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

namespace AquaTwin.Editor
{
    /// <summary>Offline data-contract regression checks. No broker, credentials or scene mutations.</summary>
    public static class FleetValidation
    {
#if UNITY_EDITOR
        [MenuItem("AquaTwin/Validate Fleet Telemetry", priority = 30)]
#endif
        public static void Run()
        {
            int checks = 0;
            void Check(bool pass, string message) { if (!pass) throw new Exception("Fleet validation: " + message); checks++; }
            var fleet = new WellFleet();
            const string legacy = "{\"device\":\"well01\",\"distance_cm\":12,\"tds_ppm\":342,\"ph\":7.2}";
            const string canonical = "{\"well_id\":\"WELL_A\",\"location\":{\"lat\":24.45,\"lng\":54.37},\"depth_to_water_cm\":142.5,\"water_column_height_cm\":357.5,\"tds_ppm\":320,\"status\":\"ONLINE\",\"timestamp\":1000}";
            Check(fleet.Wells.Count == 0, "empty registry");
            Check(fleet.ApplyJson(legacy, "digitaltwin/well01/sensors", false, 10, 1000, 20, false, out _), "legacy payload");
            Check(Math.Abs(fleet.Find("well01").Fill - .4f) < .001f, "20 cm legacy calibration");
            Check(fleet.ApplyJson(canonical, "aquatwin/well/WELL_A/telemetry", false, 10, 1000, 20, false, out _), "canonical payload");
            var a = fleet.Find("WELL_A");
            Check(float.IsNaN(a.Ph), "missing canonical pH is unknown, not zero");
            Check(Math.Abs(a.Fill - .715f) < .001f && a.HasLocation, "canonical column ratio and location");
            Check(a.IsFresh(11, 15) && !a.IsFresh(26, 15), "independent stale timeout");
            Check(fleet.ApplyJson(legacy.Replace("12,", "5,"), "digitaltwin/well01/sensors", false, 30, 1020, 20, false, out _), "update existing identity");
            Check(fleet.Wells.Count == 2 && Math.Abs(a.DepthCm - 142.5f) < .01f, "one well cannot overwrite another");
            Check(!a.IsFresh(30, 15) && fleet.Find("well01").IsFresh(30, 15), "one live sensor cannot mask a stale sensor");
            Check(!fleet.ApplyJson(legacy.Replace(",\"tds_ppm\":342", ""), null, false, 30, 1020, 20, false, out _), "missing TDS rejected");
            Check(!fleet.ApplyJson(legacy.Replace("12,", "-1,"), null, false, 30, 1020, 20, false, out _), "negative distance rejected");
            Check(!fleet.ApplyJson(legacy, "digitaltwin/well99/sensors", false, 30, 1020, 20, false, out _), "topic identity mismatch rejected");
            Check(!fleet.ApplyJson("{broken", null, false, 30, 1020, 20, false, out _), "malformed JSON rejected");
            Check(!fleet.ApplyJson(canonical.Replace("1000", "999"), null, false, 30, 1020, 20, false, out _), "out-of-order timestamp rejected");
            Check(!fleet.ApplyJson(canonical.Replace("1000", "999999"), null, false, 30, 1020, 20, false, out _), "future timestamp rejected");
            Check(!fleet.ApplyJson(canonical.Replace("\"timestamp\":1000", "\"timestamp\":null"), null, false, 30, 1020, 20, false, out _), "null timestamp rejected");
            var retained = new WellFleet();
            Check(retained.ApplyJson(legacy, null, true, 30, 1020, 20, false, out _), "retained legacy snapshot accepted");
            Check(!retained.Find("well01").IsFresh(30, 15) && retained.Find("well01").History.Count == 0, "unknown-age retained reading never appears fresh");
            Check(retained.ApplyJson(canonical, null, true, 30, 1020, 20, false, out _), "retained timestamped snapshot accepted");
            Check(!retained.Find("WELL_A").IsFresh(30, 15), "retained source age respected");
            Check(fleet.ApplyJson(canonical.Replace("1000", "1020").Replace("ONLINE", "OFFLINE"), null, false, 30, 1020, 20, false, out _), "explicit offline accepted");
            Check(!a.IsFresh(30, 15), "explicit offline status respected");
            for (int i = 0; i < 100; i++)
                Check(fleet.ApplyJson(legacy.Replace("well01", "sensor" + i), null, false, 30, 1020, 20, false, out _), "runtime discovery " + i);
            Check(fleet.Wells.Count == 102, "no fixed fleet size");
            for (int i = 0; i < 150; i++) fleet.ApplyJson(legacy, null, false, 30 + i, 1020 + i, 20, false, out _);
            Check(fleet.Find("well01").History.Count == 120, "history bounded per well");
            fleet.Clear(); Check(fleet.Wells.Count == 0, "reset clears identity index");
#if UNITY_EDITOR
            Debug.Log("AQUATWIN_FLEET_CHECKS_PASSED: " + checks);
#else
            Console.WriteLine("AQUATWIN_FLEET_CHECKS_PASSED: " + checks);
#endif
        }
    }
}
