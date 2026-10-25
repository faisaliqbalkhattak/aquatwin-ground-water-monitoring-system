using System;

namespace AquaTwin
{
    /// <summary>Transient transport failures retry; rejected credentials require an explicit correction.</summary>
    public sealed class MqttRecoveryPolicy
    {
        public double RetryAt { get; private set; } = double.PositiveInfinity;
        public double LastTrafficAt { get; private set; }
        public int ConsecutiveFailures { get; private set; }
        public void Starting(double now) { RetryAt = double.PositiveInfinity; LastTrafficAt = now; }
        public void Connected(double now) { ConsecutiveFailures = 0; Starting(now); }
        public void Received(double now) { LastTrafficAt = now; }
        public void Failed(string reason, double now)
        {
            ConsecutiveFailures++;
            bool permanent = reason.IndexOf("authentication rejected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("Subscription rejected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("Credentials are missing", StringComparison.OrdinalIgnoreCase) >= 0;
            RetryAt = permanent ? double.PositiveInfinity : now + Math.Min(30, Math.Pow(2, Math.Min(5, ConsecutiveFailures)));
        }
        public bool ShouldRetry(double now) => now >= RetryAt;
    }
}
