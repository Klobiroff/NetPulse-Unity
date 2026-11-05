namespace NetPulse
{
    public sealed class ServerClock
    {
        private const int SampleCount = 8;

        private readonly double[] _offsets = new double[SampleCount];
        private readonly double[] _rtts = new double[SampleCount];
        private int _count;
        private int _next;
        private double _offsetMs;

        public bool HasSample => _count > 0;

        public double RoundTripMs { get; private set; }

        public void Reset()
        {
            _count = 0;
            _next = 0;
            _offsetMs = 0d;
            RoundTripMs = 0d;
        }

        public void SetInitial(long serverMs, double localMs)
        {
            _offsetMs = serverMs - localMs;
            _count = 0;
            _next = 0;
        }

        public void AddSample(long clientSendMs, long serverMs, double localNowMs)
        {
            var rtt = localNowMs - clientSendMs;
            if (rtt < 0d)
            {
                return;
            }

            _offsets[_next] = serverMs + (rtt * 0.5d) - localNowMs;
            _rtts[_next] = rtt;
            _next = (_next + 1) % SampleCount;
            if (_count < SampleCount)
            {
                _count++;
            }

            var best = 0;
            for (var i = 1; i < _count; i++)
            {
                if (_rtts[i] < _rtts[best])
                {
                    best = i;
                }
            }

            _offsetMs = _offsets[best];
            RoundTripMs = _rtts[_next == 0 ? _count - 1 : _next - 1];
        }

        public double ServerSeconds(double localSeconds) => ((localSeconds * 1000d) + _offsetMs) / 1000d;
    }
}
