using System;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

namespace AquaTwin.Editor
{
    public static class RecoveryValidation
    {
#if UNITY_EDITOR
        [MenuItem("AquaTwin/Validate Startup Recovery", priority = 34)]
#endif
        public static void Run()
        {
            int checks = 0;
            void Check(bool ok, string reason) { if (!ok) throw new Exception("Recovery validation: " + reason); checks++; }
            var recovery = new MqttRecoveryPolicy();
            recovery.Starting(0); Check(!recovery.ShouldRetry(100), "pending handshake cannot start duplicate connection");
            recovery.Failed("Timed out waiting for MQTT authentication (CONNACK).", 10);
            Check(!recovery.ShouldRetry(11) && recovery.ShouldRetry(12), "CONNACK timeout retries automatically");
            recovery.Failed("Timed out waiting for subscription acknowledgement (SUBACK).", 20);
            Check(!recovery.ShouldRetry(23) && recovery.ShouldRetry(24), "SUBACK timeout retries automatically");
            for (int i = 0; i < 10; i++) recovery.Failed("Network unavailable", 100);
            Check(recovery.RetryAt == 130, "network backoff is bounded");
            recovery.Connected(200);
            Check(recovery.ConsecutiveFailures == 0 && !recovery.ShouldRetry(1000), "successful reconnect resets failed retry loop");
            Check(!recovery.ShouldRetry(260) && !recovery.ShouldRetry(86400), "a healthy connection remains subscribed across long publisher silence");
            recovery.Received(259);
            Check(!recovery.ShouldRetry(86400), "new publication does not restart the connection");
            recovery.Failed("MQTT authentication rejected (CONNACK 4): Incorrect username or password", 300);
            Check(double.IsPositiveInfinity(recovery.RetryAt), "bad credentials do not trigger endless login attempts");
            recovery.Failed("Subscription rejected for private/topic (SUBACK 128).", 301);
            Check(double.IsPositiveInfinity(recovery.RetryAt), "permission rejection requires settings correction");
            recovery.Starting(302); recovery.Connected(303);
            Check(recovery.ConsecutiveFailures == 0, "corrected settings recover after explicit retry");

            const string topic = "aquatwin/well/WELL_WOKWI_01/telemetry";
            const string json = "{\"well_id\":\"WELL_WOKWI_01\",\"location\":{\"lat\":24.4539,\"lng\":54.3773},\"depth_to_water_cm\":143.14,\"water_column_height_cm\":356.86,\"tds_ppm\":320.3,\"status\":\"ONLINE\",\"timestamp\":1000}";
            var cache = new FleetSnapshotCache("broker|account"); cache.Remember(json, topic);
            cache.Remember(json, topic); Check(cache.Count == 1, "one last-known reading per runtime well identity");
            string stored = cache.Encode();
            var restart = new FleetSnapshotCache("broker|account"); var fleet = new WellFleet();
            Check(restart.Restore(stored, fleet, new[] { "aquatwin/well/+/telemetry" }, 0, 1001, 20) == 1, "restart restores prior well without waiting for publisher");
            var well = fleet.Find("WELL_WOKWI_01");
            Check(well.IsCached && !well.IsFresh(0, 15) && well.History.Count == 0, "even a recent cached reading never claims live reporting");
            Check(Math.Abs(well.DepthCm - 143.14f) < .001 && Math.Abs(well.Fill - .71372f) < .0001, "last-known sensor values are preserved");
            var session = new FleetSession(); cache.Restore(stored, session.LiveFleet, new[] { "#" }, 0, 1001, 20);
            session.SetConnection("CONNECTED", "Waiting");
            Check(session.LiveDataStatus(0, 15) == "LAST KNOWN READINGS · WAITING FOR LIVE DATA", "cached state is explicitly labelled");
            Check(fleet.ApplyJson(json.Replace("1000", "1002"), topic, false, 2, 1002, 20, false, out _, true), "late-starting sensor updates restored well");
            Check(!well.IsCached && well.IsFresh(2, 15) && fleet.Wells.Count == 1 && well.History.Count == 1, "new message automatically promotes cached well to live");
            Check(restart.Restore(stored, fleet, new[] { "#" }, 3, 1003, 20) == 0 && well.SourceTimestamp == 1002, "snapshot cannot overwrite a live sample");
            Check(new FleetSnapshotCache("other-broker|account").Restore(stored, new WellFleet(), new[] { "#" }, 0, 1001, 20) == 0, "broker/account snapshots stay isolated");
            Check(new FleetSnapshotCache("broker|account").Restore(stored, new WellFleet(), new[] { "other/topic" }, 0, 1001, 20) == 0, "restored readings respect current topic settings");
            string badEntry = "{\"topic\":\"" + topic + "\",\"json\":\"not JSON\"},";
            string mixed = stored.Replace("\"readings\":[", "\"readings\":[" + badEntry);
            Check(new FleetSnapshotCache("broker|account").Restore(mixed, new WellFleet(), new[] { "#" }, 0, 1001, 20) == 1, "one corrupt reading does not discard valid snapshots");
            var older = new WellFleet(); restart.Restore(stored, older, new[] { "#" }, 0, 10000, 20);
            Check(older.Find("WELL_WOKWI_01").Age(0) == 9000 && !older.Find("WELL_WOKWI_01").HasOldSourceTime(0, 15), "old cached data retains source age without pretending to be an arriving stream");
#if UNITY_EDITOR
            Debug.Log("AQUATWIN_RECOVERY_CHECKS_PASSED: " + checks);
#else
            Console.WriteLine("AQUATWIN_RECOVERY_CHECKS_PASSED: " + checks);
#endif
        }
    }
}
