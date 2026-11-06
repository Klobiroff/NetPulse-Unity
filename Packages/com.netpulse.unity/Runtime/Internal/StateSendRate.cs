using UnityEngine;

namespace NetPulse
{
    internal static class StateSendRate
    {
        private const float DelayInSendIntervals = 2f;

        public static long TicksPerSend(NetPulseSettings settings, int tickRate) => TicksPerSend(settings.SendRate, settings.SerializationRate, tickRate);

        public static long TicksPerSend(int sendRate, int serializationRate, int tickRate)
        {
            var safeTickRate = Mathf.Max(1, tickRate);
            var serialization = serializationRate > 0 ? serializationRate : safeTickRate;
            var rate = Mathf.Max(1, Mathf.Min(sendRate, serialization));
            return Mathf.Max(1, Mathf.RoundToInt(safeTickRate / (float)rate));
        }

        public static float InterpolationDelay(int sendRate, int serializationRate, int tickRate) =>
            DelayInSendIntervals * TicksPerSend(sendRate, serializationRate, tickRate) / Mathf.Max(1, tickRate);

        public static float DefaultInterpolationDelay()
        {
            var settings = NetPulseSettings.Instance;
            return InterpolationDelay(settings.SendRate, settings.SerializationRate, Pulse.TickRate);
        }
    }
}
