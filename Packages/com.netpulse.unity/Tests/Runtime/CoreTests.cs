using NetPulse.Common;
using NUnit.Framework;
using UnityEngine;

namespace NetPulse.Tests
{
    public sealed class RequestTrackerTests
    {
        [Test]
        public void Add_ThenComplete_InvokesHandlerOnce()
        {
            var tracker = new RequestTracker();
            var code = (short)99;
            var id = tracker.Add(OpCode.JoinRoom, 10d, r => code = r.ReturnCode);
            Assert.AreNotEqual(0, id);
            Assert.AreEqual(1, tracker.PendingCount);

            var handled = tracker.TryComplete(new OperationResponse { RequestId = id, ReturnCode = ReturnCode.RoomFull });

            Assert.IsTrue(handled);
            Assert.AreEqual(ReturnCode.RoomFull, code);
            Assert.AreEqual(0, tracker.PendingCount);
            Assert.IsFalse(tracker.TryComplete(new OperationResponse { RequestId = id }));
        }

        [Test]
        public void Tick_ExpiresRequests()
        {
            var tracker = new RequestTracker();
            var code = (short)0;
            tracker.Add(OpCode.JoinRoom, 5d, r => code = r.ReturnCode);

            tracker.Tick(4d);
            Assert.AreEqual(1, tracker.PendingCount);
            tracker.Tick(5d);

            Assert.AreEqual(NetPulseReturnCode.Timeout, code);
            Assert.AreEqual(0, tracker.PendingCount);
        }

        [Test]
        public void FailAll_NotifiesEveryPendingRequest()
        {
            var tracker = new RequestTracker();
            var failures = 0;
            tracker.Add(1, 100d, _ => failures++);
            tracker.Add(2, 100d, _ => failures++);

            tracker.FailAll(NetPulseReturnCode.Disconnected, "gone");

            Assert.AreEqual(2, failures);
            Assert.AreEqual(0, tracker.PendingCount);
        }
    }

    public sealed class ServerClockTests
    {
        [Test]
        public void SetInitial_ProducesServerSeconds()
        {
            var clock = new ServerClock();
            clock.SetInitial(10000, 2000d);
            Assert.AreEqual(10d, clock.ServerSeconds(2d), 0.0001d);
            Assert.AreEqual(13d, clock.ServerSeconds(5d), 0.0001d);
        }

        [Test]
        public void AddSample_UsesHalfRoundTrip()
        {
            var clock = new ServerClock();
            clock.AddSample(1000, 50000, 1100d);
            Assert.AreEqual(100d, clock.RoundTripMs, 0.0001d);
            Assert.AreEqual(50.05d, clock.ServerSeconds(1.1d), 0.0001d);
        }
    }

    public sealed class SnapshotBufferTests
    {
        [Test]
        public void TrySample_InterpolatesBetweenSnapshots()
        {
            var buffer = new SnapshotBuffer();
            buffer.Add(Snapshot(1d, 0f));
            buffer.Add(Snapshot(2d, 10f));

            Assert.IsTrue(buffer.TrySample(1.5d, false, 0d, out var result));
            Assert.AreEqual(5f, result.Position.x, 0.001f);
        }

        [Test]
        public void TrySample_ClampsWithoutExtrapolation()
        {
            var buffer = new SnapshotBuffer();
            buffer.Add(Snapshot(1d, 0f));
            buffer.Add(Snapshot(2d, 10f));

            Assert.IsTrue(buffer.TrySample(5d, false, 0d, out var result));
            Assert.AreEqual(10f, result.Position.x, 0.001f);
        }

        [Test]
        public void TrySample_ExtrapolatesWithLimit()
        {
            var buffer = new SnapshotBuffer();
            buffer.Add(Snapshot(1d, 0f));
            buffer.Add(Snapshot(2d, 10f));

            Assert.IsTrue(buffer.TrySample(5d, true, 0.5d, out var result));
            Assert.AreEqual(15f, result.Position.x, 0.001f);
        }

        [Test]
        public void Add_IgnoresOutOfOrderSnapshots()
        {
            var buffer = new SnapshotBuffer();
            buffer.Add(Snapshot(2d, 10f));
            buffer.Add(Snapshot(1d, 0f));
            Assert.AreEqual(1, buffer.Count);
        }

        private static TransformSnapshot Snapshot(double time, float x) =>
            new(time, new Vector3(x, 0f, 0f), Quaternion.identity, Vector3.one, Vector3.zero);
    }

    public sealed partial class NetworkTypeInfoTests
    {
        [Test]
        public void Rpc_LookupByNameAndStableId()
        {
            var info = NetworkTypeInfo.Get(typeof(SampleBehaviour));

            Assert.IsTrue(info.TryGetRpc("Hit", out var byName));
            Assert.IsTrue(info.TryGetRpc(NetworkTypeInfo.StableHash("Hit"), out var byId));
            Assert.AreSame(byName, byId);
            Assert.AreEqual(1, byName.ArgumentCount);
            Assert.IsFalse(byName.AcceptsInfo);
            Assert.IsTrue(info.TryGetRpc("WithInfo", out var withInfo));
            Assert.IsTrue(withInfo.AcceptsInfo);
            Assert.AreEqual(1, withInfo.ArgumentCount);
            Assert.IsFalse(info.TryGetRpc("NotRpc", out _));
        }

        [Test]
        public void Info_IsCachedPerType()
        {
            Assert.AreSame(NetworkTypeInfo.Get(typeof(SampleBehaviour)), NetworkTypeInfo.Get(typeof(SampleBehaviour)));
        }

        [Test]
        public void SyncedFields_AreSortedAndFiltered()
        {
            var info = NetworkTypeInfo.Get(typeof(SampleBehaviour));
            Assert.AreEqual(2, info.SyncedFields.Length);
            Assert.AreEqual("Health", info.SyncedFields[0].Name);
            Assert.AreEqual("Name", info.SyncedFields[1].Name);
            Assert.IsTrue(info.HasState);
            Assert.IsFalse(info.OverridesSerialize);
        }

        [Test]
        public void SerializeOverride_IsDetected()
        {
            Assert.IsTrue(NetworkTypeInfo.Get(typeof(SerializingBehaviour)).OverridesSerialize);
        }

        [Test]
        public void Behaviour_WriteStateOnlyContainsChangedFields()
        {
            var go = new GameObject("test");
            try
            {
                var behaviour = go.AddComponent<SampleBehaviour>();
                behaviour.Health = 10;
                behaviour.Name = "a";
                var writer = new NetWriter();

                Assert.IsTrue(behaviour.WriteState(writer, false));
                Assert.IsFalse(behaviour.WriteState(new NetWriter(), false));

                behaviour.Health = 11;
                var delta = new NetWriter();
                Assert.IsTrue(behaviour.WriteState(delta, false));
                Assert.Less(delta.Length, writer.Length);

                var other = go.AddComponent<SampleBehaviour>();
                other.ReadState(new NetReader(writer.ToArray()), 0d);
                Assert.AreEqual(10, other.Health);
                Assert.AreEqual("a", other.Name);
                other.ReadState(new NetReader(delta.ToArray()), 0d);
                Assert.AreEqual(11, other.Health);
                Assert.AreEqual("a", other.Name);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        public sealed partial class SampleBehaviour : NetworkBehaviour
        {
            [Synced] public int Health;
            [Synced] public string Name;
#pragma warning disable NP0010
            [Synced] public object Unsupported;
#pragma warning restore NP0010
            public int NotSynced;

            [NetRpc]
            public void Hit(int damage)
            {
            }

            [NetRpc]
            public void WithInfo(int value, NetRpcInfo info)
            {
            }

            public void NotRpc()
            {
            }
        }

        public sealed class SerializingBehaviour : NetworkBehaviour
        {
            public override void OnSerialize(NetStream stream)
            {
            }
        }
    }
}
