using NetPulse;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NetPulse.Samples.Basic
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class TakeoverCube : NetworkBehaviour
    {
        [SerializeField, Min(0f)] private float _speed = 4f;
        [SerializeField] private Color _freeColor = Color.gray;

        private Renderer _renderer;

        public override void OnNetworkSpawned() => Refresh();

        public override void OnOwnerChanged(Player previous, Player current) => Refresh();

        private void Awake() => _renderer = GetComponentInChildren<Renderer>();

        private void Update()
        {
            if (!IsSpawned || !Pulse.IsInRoom)
            {
                return;
            }

            if (!IsMine)
            {
                if (ReadGrab())
                {
                    NetworkObject.RequestOwnership();
                }

                return;
            }

            transform.position += ReadMove() * (_speed * Time.deltaTime);
        }

        private void OnGUI()
        {
            if (!IsSpawned || !Pulse.IsInRoom)
            {
                return;
            }

            var owner = Owner != null ? Owner.NickName : "nobody";
            var hint = IsMine ? "arrow keys to move" : "press E to take";
            GUI.Label(new Rect(Screen.width - 260f, 10f, 250f, 24f), $"Shared cube: {owner} ({hint})");
        }

        private void Refresh()
        {
            if (_renderer == null)
            {
                return;
            }

            var owner = Owner;
            _renderer.material.color = owner == null ? _freeColor : Color.HSVToRGB((owner.ActorId * 0.173f) % 1f, 0.7f, 0.95f);
        }

        private static bool ReadGrab()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.E);
#else
            return false;
#endif
        }

        private static Vector3 ReadMove()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return Vector3.zero;
            }

            var x = (keyboard.rightArrowKey.isPressed ? 1f : 0f) - (keyboard.leftArrowKey.isPressed ? 1f : 0f);
            var z = (keyboard.upArrowKey.isPressed ? 1f : 0f) - (keyboard.downArrowKey.isPressed ? 1f : 0f);
            return new Vector3(x, 0f, z).normalized;
#elif ENABLE_LEGACY_INPUT_MANAGER
            var x = (Input.GetKey(KeyCode.RightArrow) ? 1f : 0f) - (Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f);
            var z = (Input.GetKey(KeyCode.UpArrow) ? 1f : 0f) - (Input.GetKey(KeyCode.DownArrow) ? 1f : 0f);
            return new Vector3(x, 0f, z).normalized;
#else
            return Vector3.zero;
#endif
        }
    }
}
