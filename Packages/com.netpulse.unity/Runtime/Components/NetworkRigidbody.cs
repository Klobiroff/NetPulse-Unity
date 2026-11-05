using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    [AddComponentMenu("NetPulse/Network Rigidbody")]
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public sealed class NetworkRigidbody : NetworkBehaviour
    {
        [SerializeField] private bool _syncVelocity = true;
        [SerializeField, Min(0f)] private float _interpolationDelay = 0f;
        [SerializeField, Min(0f)] private float _teleportDistance = 5f;

        private readonly SnapshotBuffer _buffer = new();
        private Rigidbody _body;
        private bool _originalKinematic;

        protected override DeliveryMode SerializeDelivery => DeliveryMode.UnreliableSequenced;

        public override void OnNetworkSpawned() => ApplyOwnership();

        public override void OnOwnerChanged(Player previous, Player current)
        {
            _buffer.Clear();
            ApplyOwnership();
        }

        public override void OnSerialize(NetStream stream)
        {
            var position = _body.position;
            var rotation = _body.rotation;
            var velocity = GetVelocity();
            var angular = _body.angularVelocity;
            stream.Serialize(ref position);
            stream.Serialize(ref rotation);
            if (_syncVelocity)
            {
                stream.Serialize(ref velocity);
                stream.Serialize(ref angular);
            }

            if (stream.IsWriting)
            {
                return;
            }

            _buffer.Add(new TransformSnapshot(stream.Timestamp, position, rotation, Vector3.one, velocity));
        }

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _originalKinematic = _body.isKinematic;
        }

        private void FixedUpdate()
        {
            if (!IsSpawned || IsMine || _buffer.Count == 0)
            {
                return;
            }

            var delay = _interpolationDelay > 0f ? _interpolationDelay : 2f / Mathf.Max(1, NetPulseSettings.Instance.SerializationRate);
            if (!_buffer.TrySample(Pulse.ServerTime - delay, true, 0.25d, out var snapshot))
            {
                return;
            }

            if (_teleportDistance > 0f && (_body.position - snapshot.Position).magnitude > _teleportDistance)
            {
                _body.position = snapshot.Position;
                _body.rotation = snapshot.Rotation;
                _buffer.Clear();
                return;
            }

            _body.MovePosition(snapshot.Position);
            _body.MoveRotation(snapshot.Rotation);
        }

        private Vector3 GetVelocity()
        {
#if UNITY_6000_0_OR_NEWER
            return _body.linearVelocity;
#else
            return _body.velocity;
#endif
        }

        private void SetVelocity(Vector3 value)
        {
#if UNITY_6000_0_OR_NEWER
            _body.linearVelocity = value;
#else
            _body.velocity = value;
#endif
        }

        private void ApplyOwnership()
        {
            if (IsMine)
            {
                _body.isKinematic = _originalKinematic;
                return;
            }

            if (!_body.isKinematic)
            {
                SetVelocity(Vector3.zero);
                _body.angularVelocity = Vector3.zero;
            }

            _body.isKinematic = true;
        }
    }
}
