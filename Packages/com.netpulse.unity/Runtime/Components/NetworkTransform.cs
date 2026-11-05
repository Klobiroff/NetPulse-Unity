using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    [AddComponentMenu("NetPulse/Network Transform")]
    [DisallowMultipleComponent]
    public sealed class NetworkTransform : NetworkBehaviour
    {
        [Header("Sync")]
        [SerializeField] private bool _syncPosition = true;
        [SerializeField] private bool _syncRotation = true;
        [SerializeField] private bool _syncScale = false;
        [SerializeField] private bool _useLocalSpace = true;
        [SerializeField] private bool _reliable = false;

        [Header("Send thresholds")]
        [SerializeField, Min(0f)] private float _positionThreshold = 0.001f;
        [SerializeField, Min(0f)] private float _rotationThreshold = 0.1f;

        [Header("Receive")]
        [SerializeField, Min(0f)] private float _interpolationDelay = 0f;
        [SerializeField] private bool _extrapolate = false;
        [SerializeField, Min(0f)] private float _maxExtrapolationTime = 0.25f;
        [SerializeField, Min(0f)] private float _teleportDistance = 0f;

        private readonly SnapshotBuffer _buffer = new();
        private Transform _transform;
        private Vector3 _sentPosition;
        private Quaternion _sentRotation = Quaternion.identity;
        private Vector3 _sentScale = Vector3.one;
        private Vector3 _previousPosition;
        private double _previousTime;
        private bool _hasSent;

        protected override DeliveryMode SerializeDelivery => _reliable ? DeliveryMode.ReliableOrdered : DeliveryMode.UnreliableSequenced;

        public override void OnNetworkSpawned() => _buffer.Clear();

        public override void OnOwnerChanged(Player previous, Player current)
        {
            _buffer.Clear();
            _hasSent = false;
        }

        public override void OnSerialize(NetStream stream)
        {
            if (stream.IsWriting)
            {
                WriteCurrent(stream);
                return;
            }

            var position = _transform.position;
            var rotation = _transform.rotation;
            var scale = _transform.localScale;
            if (_syncPosition)
            {
                stream.Serialize(ref position);
            }

            if (_syncRotation)
            {
                stream.Serialize(ref rotation);
            }

            if (_syncScale)
            {
                stream.Serialize(ref scale);
            }

            var velocity = Vector3.zero;
            if (_previousTime > 0d && stream.Timestamp > _previousTime)
            {
                velocity = (position - _previousPosition) / (float)(stream.Timestamp - _previousTime);
            }

            _previousPosition = position;
            _previousTime = stream.Timestamp;
            _buffer.Add(new TransformSnapshot(stream.Timestamp, position, rotation, scale, velocity));
        }

        private void Awake() => _transform = transform;

        private void Update()
        {
            if (!IsSpawned || IsMine || _buffer.Count == 0)
            {
                return;
            }

            if (!_buffer.TrySample(Pulse.ServerTime - ResolveDelay(), _extrapolate, _maxExtrapolationTime, out var snapshot))
            {
                return;
            }

            Apply(snapshot);
        }

        private float ResolveDelay()
        {
            if (_interpolationDelay > 0f)
            {
                return _interpolationDelay;
            }

            return 2f / Mathf.Max(1, Mathf.Min(NetPulseSettings.Instance.SendRate, NetPulseSettings.Instance.SerializationRate));
        }

        private void WriteCurrent(NetStream stream)
        {
            var position = _useLocalSpace ? _transform.localPosition : _transform.position;
            var rotation = _useLocalSpace ? _transform.localRotation : _transform.rotation;
            var scale = _transform.localScale;
            if (!_hasSent
                || (position - _sentPosition).sqrMagnitude > _positionThreshold * _positionThreshold
                || Quaternion.Angle(rotation, _sentRotation) > _rotationThreshold
                || (scale - _sentScale).sqrMagnitude > _positionThreshold * _positionThreshold)
            {
                _sentPosition = position;
                _sentRotation = rotation;
                _sentScale = scale;
                _hasSent = true;
            }

            if (_syncPosition)
            {
                stream.Serialize(ref _sentPosition);
            }

            if (_syncRotation)
            {
                stream.Serialize(ref _sentRotation);
            }

            if (_syncScale)
            {
                stream.Serialize(ref _sentScale);
            }
        }

        private void Apply(TransformSnapshot snapshot)
        {
            if (_syncPosition)
            {
                var current = _useLocalSpace ? _transform.localPosition : _transform.position;
                var target = snapshot.Position;
                if (_teleportDistance > 0f && (current - target).magnitude > _teleportDistance)
                {
                    _buffer.Clear();
                }

                if (_useLocalSpace)
                {
                    _transform.localPosition = target;
                }
                else
                {
                    _transform.position = target;
                }
            }

            if (_syncRotation)
            {
                if (_useLocalSpace)
                {
                    _transform.localRotation = snapshot.Rotation;
                }
                else
                {
                    _transform.rotation = snapshot.Rotation;
                }
            }

            if (_syncScale)
            {
                _transform.localScale = snapshot.Scale;
            }
        }
    }
}
