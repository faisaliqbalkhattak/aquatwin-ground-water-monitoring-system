using System;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

namespace AquaTwin.Editor
{
    public static class FleetDemoValidation
    {
#if UNITY_EDITOR
        [MenuItem("AquaTwin/Validate Demo Playback", priority = 31)]
        private static void RunInEditor()
        {
            var asset = Resources.Load<TextAsset>("AquaTwinDemo");
            if (asset == null) throw new Exception("Demo JSON resource is missing.");
            Run(asset.text);
        }
#endif
        public static void Run(string json)
        {
            int checks = 0;
            void Check(bool pass, string message) { if (!pass) throw new Exception("Demo validation: " + message); checks++; }
            var data = DemoTelemetryDataset.Load(json); // Validates every JSON keyframe against the live data contract.
            Check(data.WellCount == 10 && data.DurationSeconds == 90 && data.SampleIntervalSeconds == 2, "dataset manifest");
            var fractional = new WellFleet();
            data.Emit(fractional, .25f, .25f, 1792218000.125, 10);
            Check(fractional.Wells.Count == 9 && Math.Abs(fractional.Find("DEMO_001").Longitude - 54.37) < .000001,
                "JSON longitude remains a number on Mono and modern .NET");
            Check(fractional.Find("DEMO_001").SourceTimestamp == 1792218000.125,
                "fractional Unix timestamp round-trips independently of closing JSON braces");
            var session = new FleetSession();
            Check(!session.IsDemo && session.Connection == "CONNECTING", "startup attempts broker, not demo");
            Check(session.DemoAvailable, "demo remains available during connection attempts");
            session.SetConnection("CONNECTED", "Subscribed");
            Check(session.DemoAvailable && session.LiveDataStatus(0, 15) == "BROKER CONNECTED · NO SENSOR MESSAGES", "broker connection alone cannot indicate live sensor data or hide demo");
            const string staleReading = "{\"device\":\"stale01\",\"distance_cm\":10,\"tds_ppm\":200}";
            session.LiveFleet.ApplyJson(staleReading, null, false, 0, 1000, 20, false, out _);
            Check(session.DemoAvailable && session.FreshLiveCount(20, 15) == 0 && session.LiveDataStatus(20, 15) == "NO FRESH SENSOR DATA", "all-stale live fleet cannot hide demo or indicate fresh readings");
            session.LiveFleet.Clear();
            session.SetConnection("OFFLINE", "No credentials");
            Check(!session.IsDemo && session.DemoAvailable, "connection failure offers demo without silently starting synthetic readings");
            session.StartDemo();
            Check(session.IsDemo && session.Connection == "OFFLINE", "demo does not pretend the broker is connected");
            Check(!session.DemoAvailable && session.LiveDataStatus(0, 15) == "NO SENSOR DATA RECEIVED", "demo view and real sensor availability remain independent");
            data.Emit(session.DemoFleet, 0, 0, 1000000, 10);
            Check(session.ActiveFleet.Wells.Count == 9 && session.LiveFleet.Wells.Count == 0, "initial discovery excludes the late well and live data");
            Check(float.IsNaN(session.DemoFleet.Find("DEMO_005").Ph), "missing optional pH remains unknown");
            Check(session.DemoFleet.Find("DEMO_006").Fill < .2f, "low-water warning scenario");
            Check(!session.DemoFleet.Find("DEMO_009").ReportedOnline, "explicit offline scenario");
            float initialDepth = session.DemoFleet.Find("DEMO_002").DepthCm;
            for (int time = 2; time <= 90; time += 2)
            {
                data.Emit(session.DemoFleet, time, time, 1000000 + time, 10);
                if (time == 12) Check(session.ActiveFleet.Wells.Count == 10, "late well discovered at 12 seconds");
                if (time == 34) Check(!session.DemoFleet.Find("DEMO_008").IsFresh(time, 15), "silent sensor independently becomes stale");
                if (time == 36)
                {
                    Check(session.DemoFleet.Find("DEMO_007").Tds > 2000, "unclamped TDS spike");
                    Check(session.DemoFleet.Find("DEMO_009").IsFresh(time, 15), "offline well reconnects");
                    Check(session.DemoFleet.Find("DEMO_002").DepthCm > initialDepth, "animated drawdown uses successive JSON samples");
                }
                if (time == 60) Check(session.DemoFleet.Find("DEMO_008").IsFresh(time, 15), "silent well resumes reporting");
            }
            Check(session.DemoFleet.Find("DEMO_001").History.Count > 30, "history builds throughout playback");
            Check(session.DemoFleet.Find("DEMO_006").Fill > .2f && session.DemoFleet.Find("DEMO_007").Tds < 600, "warning recovery scenarios");
            const string live = "{\"device\":\"real01\",\"distance_cm\":10,\"tds_ppm\":200}";
            Check(session.LiveFleet.ApplyJson(live, null, false, 90, 1000090, 20, false, out _), "background live readings remain accepted during demo");
            Check(session.ActiveFleet.Find("real01") == null, "background live data cannot contaminate demo");
            session.SetConnection("CONNECTED", "Subscribed");
            Check(session.IsDemo && session.Connection == "CONNECTED", "broker reconnection cannot silently replace demo");
            session.ReturnToLive();
            Check(session.ActiveFleet.Find("real01") != null && session.ActiveFleet.Find("DEMO_001") == null, "return-to-live restores isolated real readings");
            session.StartDemo();
            Check(session.DemoFleet.Wells.Count == 0 && session.LiveFleet.Wells.Count == 1, "restart clears demo history only");
            data.Emit(session.DemoFleet, 0, 100, 1000100, 1);
            Check(session.ActiveFleet.Wells.Count == 1, "runtime demo count can be one");
            session.StartDemo(); data.Emit(session.DemoFleet, 0, 100, 1000100, 0);
            Check(session.ActiveFleet.Wells.Count == 0, "runtime demo count can be zero");
            bool invalidRejected = false;
            try { DemoTelemetryDataset.Load(json.Replace("\"sample_interval_seconds\": 2", "\"sample_interval_seconds\": 0")); }
            catch (FormatException) { invalidRejected = true; }
            Check(invalidRejected, "invalid dataset rejected before playback");
#if UNITY_EDITOR
            Debug.Log("AQUATWIN_DEMO_CHECKS_PASSED: " + checks);
#else
            Console.WriteLine("AQUATWIN_DEMO_CHECKS_PASSED: " + checks);
#endif
        }
    }
}
