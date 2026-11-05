using System.Collections.Generic;
using UnityEngine;

namespace NetPulse
{
    internal readonly struct TransformSnapshot
    {
        public TransformSnapshot(double time, Vector3 position, Quaternion rotation, Vector3 scale, Vector3 velocity)
        {
            Time = time;
            Position = position;
            Rotation = rotation;
            Scale = scale;
            Velocity = velocity;
        }

        public double Time { get; }

        public Vector3 Position { get; }

        public Quaternion Rotation { get; }

        public Vector3 Scale { get; }

        public Vector3 Velocity { get; }
    }

    internal sealed class SnapshotBuffer
    {
        private const int Capacity = 32;

        private readonly List<TransformSnapshot> _snapshots = new(Capacity);

        public int Count => _snapshots.Count;

        public void Clear() => _snapshots.Clear();

        public void Add(TransformSnapshot snapshot)
        {
            if (_snapshots.Count > 0 && snapshot.Time <= _snapshots[_snapshots.Count - 1].Time)
            {
                return;
            }

            if (_snapshots.Count == Capacity)
            {
                _snapshots.RemoveAt(0);
            }

            _snapshots.Add(snapshot);
        }

        public bool TrySample(double renderTime, bool extrapolate, double maxExtrapolation, out TransformSnapshot result)
        {
            result = default;
            if (_snapshots.Count == 0)
            {
                return false;
            }

            var first = _snapshots[0];
            if (renderTime <= first.Time)
            {
                result = first;
                return true;
            }

            for (var i = 0; i < _snapshots.Count - 1; i++)
            {
                var from = _snapshots[i];
                var to = _snapshots[i + 1];
                if (renderTime > to.Time)
                {
                    continue;
                }

                var t = (float)((renderTime - from.Time) / (to.Time - from.Time));
                result = new TransformSnapshot(
                    renderTime,
                    Vector3.LerpUnclamped(from.Position, to.Position, t),
                    Quaternion.SlerpUnclamped(from.Rotation, to.Rotation, t),
                    Vector3.LerpUnclamped(from.Scale, to.Scale, t),
                    Vector3.LerpUnclamped(from.Velocity, to.Velocity, t));
                if (i > 1)
                {
                    _snapshots.RemoveRange(0, i - 1);
                }

                return true;
            }

            var last = _snapshots[_snapshots.Count - 1];
            if (!extrapolate)
            {
                result = last;
                return true;
            }

            var elapsed = (float)System.Math.Min(renderTime - last.Time, maxExtrapolation);
            var velocity = last.Velocity;
            if (velocity == Vector3.zero && _snapshots.Count > 1)
            {
                var previous = _snapshots[_snapshots.Count - 2];
                velocity = (last.Position - previous.Position) / (float)(last.Time - previous.Time);
            }

            result = new TransformSnapshot(renderTime, last.Position + (velocity * elapsed), last.Rotation, last.Scale, last.Velocity);
            return true;
        }
    }
}
