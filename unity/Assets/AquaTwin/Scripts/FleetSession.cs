namespace AquaTwin
{
    /// <summary>Broker connectivity stays independent from the active visualization data source.</summary>
    public sealed class FleetSession
    {
        public WellFleet LiveFleet { get; } = new();
        public WellFleet DemoFleet { get; } = new();
        public WellFleet ActiveFleet => IsDemo ? DemoFleet : LiveFleet;
        public bool IsDemo { get; private set; }
        public int MessagesThisConnection { get; private set; }
        public int RejectedMessages { get; private set; }
        public void SetDeliveryCounts(int messagesThisConnection, int rejected)
        { MessagesThisConnection = messagesThisConnection; RejectedMessages = rejected; }
        public string Connection { get; private set; } = "CONNECTING";
        public string ConnectionDetail { get; private set; } = "Opening secure HiveMQ connection…";
        public void SetConnection(string state, string detail)
        {
            Connection = state;
            ConnectionDetail = detail ?? (state == "CONNECTED" ? "Subscribed to well telemetry" : "HiveMQ connection unavailable");
        }
        public void StartDemo() { DemoFleet.Clear(); IsDemo = true; }
        public void ReturnToLive() { IsDemo = false; }
        public bool DemoAvailable => !IsDemo;
        public int FreshLiveCount(float now, float timeout)
        {
            int count = 0;
            foreach (var well in LiveFleet.Wells) if (well.IsFresh(now, timeout)) count++;
            return count;
        }
        public string LiveDataStatus(float now, float timeout)
        {
            int fresh = FreshLiveCount(now, timeout);
            if (fresh == 0)
            {
                bool allCached = LiveFleet.Wells.Count > 0;
                foreach (var well in LiveFleet.Wells) if (!well.IsCached) { allCached = false; break; }
                if (allCached) return "LAST KNOWN READINGS · WAITING FOR LIVE DATA";
                if (Connection == "CONNECTED" && MessagesThisConnection == 0 && LiveFleet.Wells.Count == 0) return "BROKER CONNECTED · NO SENSOR MESSAGES";
                if (Connection == "CONNECTED" && RejectedMessages > 0 && LiveFleet.Wells.Count == 0) return "MESSAGES RECEIVED · TELEMETRY REJECTED";
                if (Connection == "CONNECTED" && MessagesThisConnection > 0)
                    foreach (var well in LiveFleet.Wells)
                        if (well.HasOldSourceTime(now, timeout)) return "RECEIVING · SENSOR TIMESTAMP STALE";
                return LiveFleet.Wells.Count == 0 ? "NO SENSOR DATA RECEIVED" : "NO FRESH SENSOR DATA";
            }
            return (Connection == "CONNECTED" ? "SENSORS REPORTING · " : "RECENT READINGS · ") + fresh;
        }
    }
}
