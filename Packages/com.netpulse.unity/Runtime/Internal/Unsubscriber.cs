using System;

namespace NetPulse
{
    internal sealed class Unsubscriber : IDisposable
    {
        public static readonly IDisposable None = new Unsubscriber(null);

        private Action _action;

        public Unsubscriber(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            var action = _action;
            _action = null;
            action?.Invoke();
        }
    }
}
