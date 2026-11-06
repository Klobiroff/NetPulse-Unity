using NetPulse;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NetPulse.Samples.Basic
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed partial class SamplePlayer : NetworkBehaviour
    {
        [SerializeField, Min(0f)] private float _speed = 5f;

        private readonly SyncVar<Color> _color = new(Color.white);
        private readonly SyncVar<int> _health = new(100);

        private Renderer _renderer;
        private Vector3 _move;
        private string _message = string.Empty;
        private float _messageUntil;

        public int Health => _health.Value;

        public override void OnNetworkSpawned()
        {
            if (IsMine && NetworkObject.InstantiationData is Color color)
            {
                _color.Value = color;
            }

            _color.Changed += (_, current) => Apply(current);
            _health.Changed += (previous, current) => Debug.Log($"[Sample] {name} health {previous} -> {current}");
            Apply(_color.Value);
        }

        public override void OnNetworkTick(long tick)
        {
            if (IsMine)
            {
                transform.position += _move * (_speed * (float)Pulse.TickDelta);
            }
        }

        private void Awake() => _renderer = GetComponentInChildren<Renderer>();

        private void Update()
        {
            if (!IsSpawned || !IsMine)
            {
                return;
            }

            _move = ReadMove();
            if (ReadHello())
            {
                Rpc.Hello($"Hello from {Owner?.NickName}");
            }

            if (ReadHurt())
            {
                _health.Value = Mathf.Max(0, _health.Value - 10);
            }
        }

        private void OnGUI()
        {
            if (Camera.main == null)
            {
                return;
            }

            var screen = Camera.main.WorldToScreenPoint(transform.position + Vector3.up * 1.5f);
            if (screen.z <= 0f)
            {
                return;
            }

            var text = Time.time > _messageUntil ? $"HP {_health.Value}" : $"HP {_health.Value}: {_message}";
            GUI.Label(new Rect(screen.x - 80f, Screen.height - screen.y, 200f, 24f), text);
        }

        [NetRpc(RpcTarget.All)]
        private void Hello(string text, NetRpcInfo info)
        {
            _message = text;
            _messageUntil = Time.time + 2f;
        }

        private void Apply(Color color)
        {
            if (_renderer != null)
            {
                _renderer.material.color = color;
            }
        }

        private static Vector3 ReadMove()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return Vector3.zero;
            }

            var x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
            var z = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
            return new Vector3(x, 0f, z).normalized;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical")).normalized;
#else
            return Vector3.zero;
#endif
        }

        private static bool ReadHello()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Space);
#else
            return false;
#endif
        }

        private static bool ReadHurt()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.hKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.H);
#else
            return false;
#endif
        }
    }
}
