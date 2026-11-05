using UnityEngine;
using UnityEngine.Scripting;

namespace NetPulse
{
    [Preserve]
    [DefaultExecutionOrder(-1000)]
    internal sealed class NetPulseRunner : MonoBehaviour
    {
        private void Update()
        {
            var client = Pulse.ClientOrNull;
            if (client == null)
            {
                return;
            }

            client.Tick();
            Pulse.ObjectsOrNull?.Tick(Time.unscaledDeltaTime);
        }

        private void OnApplicationQuit() => Pulse.ClientOrNull?.Disconnect();
    }
}
