using NetPulse;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NetPulse.Samples.Basic
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class SamplePlayer : NetworkBehaviour
    {
        [SerializeField, Min(0f)] private float _speed = 5f;

        [Synced] private Color _color = Color.white;

        private Renderer _renderer;
        private string _message = string.Empty;
        private float _messageUntil;

        public override void OnNetworkSpawned()
        {
            if (NetworkObject.InstantiationData is Color color)
            {
                _color = color;
            }

            Apply();
        }

        private void Awake() => _renderer = GetComponentInChildren<Renderer>();

        private void Update()
        {
            if (!IsSpawned)
            {
                return;
            }

            if (_renderer != null && _renderer.material.color != _color)
            {
                Apply();
            }

            if (!IsMine)
            {
                return;
            }

            transform.position += ReadMove() * (_speed * Time.deltaTime);
            if (ReadHello())
            {
                CallRpc(nameof(Hello), RpcTarget.All, $"Hello from {Owner?.NickName}");
            }
        }

        private void OnGUI()
        {
            if (Time.time > _messageUntil || Camera.main == null)
            {
                return;
            }

            var screen = Camera.main.WorldToScreenPoint(transform.position + Vector3.up * 1.5f);
            if (screen.z <= 0f)
            {
                return;
            }

            GUI.Label(new Rect(screen.x - 80f, Screen.height - screen.y, 160f, 24f), _message);
        }

        [NetRpc]
        private void Hello(string text, NetRpcInfo info)
        {
            _message = text;
            _messageUntil = Time.time + 2f;
        }

        private void Apply()
        {
            if (_renderer != null)
            {
                _renderer.material.color = _color;
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
    }
}
