using System.Collections.Generic;
using NetPulse.Common;
using NUnit.Framework;
using UnityEngine;

namespace NetPulse.Tests
{
    public sealed partial class FeatureProbe : NetworkBehaviour
    {
        public readonly SyncVar<int> Health = new(100);
        public readonly SyncVar<string> Title = new(string.Empty, SyncPermission.Master);

        public List<string> Log { get; } = new();

        public List<int> Pings { get; } = new();

        public List<int> PingSenders { get; } = new();

        public List<string> Everyone { get; } = new();

        public List<long> Ticks { get; } = new();

        [NetRpc(RpcTarget.Others)]
        public void Ping(int value, NetRpcInfo info)
        {
            Pings.Add(value);
            PingSenders.Add(info.SenderActorId);
        }

        [NetRpc]
        public void Shout(string text) => Everyone.Add(text);

        public void CallPing(int value) => Rpc.Ping(value);

        public void CallShout(string text) => Rpc.Shout(text);

        public override void OnNetworkSpawned() => Log.Add("spawned");

        public override void OnOwnerChanged(Player previous, Player current) => Log.Add("ownerChanged");

        public override void OnOwnershipGained() => Log.Add("gained");

        public override void OnOwnershipLost() => Log.Add("lost");

        public override void OnMasterChanged(Player previous, Player current) => Log.Add("master" + (current != null ? current.ActorId : 0));

        public override void OnNetworkTick(long tick) => Ticks.Add(tick);
    }

    public sealed class NetworkFeatureTests
    {
        private const int PrefabId = 1;

        private readonly List<GameObject> _created = new();
        private FakeTransport _transport;
        private NetPulseSettings _settings;
        private NetworkPrefabRegistry _registry;
        private GameObject _prefab;
        private double _now;

        [SetUp]
        public void SetUp()
        {
            _now = 100d;
            _transport = new FakeTransport();
            Pulse.UseTransport(_transport);
            Pulse.UseClock(() => _now);
            _prefab = CreatePrefab(false);
            _registry = ScriptableObject.CreateInstance<NetworkPrefabRegistry>();
            _registry.SetEntries(new List<NetworkPrefabEntry> { new(PrefabId, "guid-probe", _prefab) }, 2);
            _settings = ScriptableObject.CreateInstance<NetPulseSettings>();
            _settings.PrefabRegistry = _registry;
            NetPulseSettings.SetInstance(_settings);
        }

        [TearDown]
        public void TearDown()
        {
            _transport.SimulateDisconnected("test");
            Pulse.UseTransport(null);
            Pulse.UseClock(null);
            NetPulseSettings.SetInstance(null);
            foreach (var go in _created)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            _created.Clear();
            Object.DestroyImmediate(_registry);
            Object.DestroyImmediate(_settings);
        }

        [Test]
        public void GeneratedRpc_OthersSendsGeneratedKindAndDoesNotRunLocally()
        {
            JoinRoom(1, 1);
            var probe = SpawnLocal();

            probe.CallPing(5);
            var raise = TakeRaise(EventCode.Rpc);
            var reader = new NetReader((byte[])raise.Data);

            Assert.AreEqual(1, reader.ReadByte());
            Assert.AreEqual(probe.ViewId, reader.ReadInt());
            Assert.AreEqual(0, reader.ReadByte());
            Assert.AreEqual(NetworkTypeInfo.ComputeRpcId("Ping"), reader.ReadUShort());
            Assert.AreEqual(5, reader.ReadInt());
            Assert.AreEqual(0, probe.Pings.Count);
        }

        [Test]
        public void GeneratedRpc_AllRunsLocallyThroughReaderAndSends()
        {
            JoinRoom(1, 1);
            var probe = SpawnLocal();

            probe.CallShout("hello");

            CollectionAssert.AreEqual(new[] { "hello" }, probe.Everyone);
            TakeRaise(EventCode.Rpc);
        }

        [Test]
        public void GeneratedRpc_ReceivedEventInvokesMethodWithSenderInfo()
        {
            JoinRoom(1, 1);
            var probe = SpawnRemote(2001, 2);
            var writer = new NetWriter(32);
            writer.WriteByte(1);
            writer.WriteInt(2001);
            writer.WriteByte(0);
            writer.WriteUShort(NetworkTypeInfo.ComputeRpcId("Ping"));
            writer.WriteInt(9);

            _transport.SendEvent(EventCode.Rpc, new RoomEventData { SenderActorId = 2, Data = writer.ToArray(), ViewId = 2001 }.ToParameters());

            CollectionAssert.AreEqual(new[] { 9 }, probe.Pings);
            CollectionAssert.AreEqual(new[] { 2 }, probe.PingSenders);
        }

        [Test]
        public void SyncVar_OwnerWritesPropagateAsDeltaAndRaiseChanged()
        {
            JoinRoom(1, 1);
            var owner = SpawnLocal();
            var remote = SpawnRemote(2001, 2);
            var changes = new List<(int Previous, int Current)>();
            remote.Health.Changed += (previous, current) => changes.Add((previous, current));
            Assert.IsFalse(remote.Log.Contains("spawned"));

            owner.Health.Value = 42;
            _transport.SendEvent(EventCode.StateSync, new RoomEventData { SenderActorId = 2, Data = StatePacket(owner, 2001), ViewId = 2001 }.ToParameters());

            Assert.AreEqual(42, remote.Health.Value);
            CollectionAssert.AreEqual(new[] { (100, 42) }, changes);
            CollectionAssert.AreEqual(new[] { "spawned" }, remote.Log);

            var writer = new NetWriter(16);
            Assert.IsFalse(owner.WriteState(writer, false));
            owner.Health.Value = 43;
            Assert.IsTrue(owner.WriteState(writer, false));
        }

        [Test]
        public void SyncVar_NonOwnerWriteIsIgnored()
        {
            JoinRoom(1, 1);
            var remote = SpawnRemote(2001, 2);

            remote.Health.Value = 7;
            remote.Health.Value = 8;

            Assert.AreEqual(100, remote.Health.Value);
        }

        [Test]
        public void SyncVar_MasterVariableIsSentOnlyByMasterAndAppliedFromMaster()
        {
            JoinRoom(1, 1);
            var local = SpawnLocal();
            local.Title.Value = "boss";
            Pulse.Objects.Tick(0f);
            var raise = TakeRaise(EventCode.SyncVarSet);
            var reader = new NetReader((byte[])raise.Data);

            Assert.AreEqual(local.ViewId, reader.ReadInt());
            Assert.AreEqual(1, reader.ReadUShort());
            Assert.AreEqual("boss", reader.ReadString());

            var remote = SpawnRemote(2001, 2);
            var writer = new NetWriter(32);
            writer.WriteInt(2001);
            writer.WriteUShort(1);
            writer.WriteString("from-master");
            _transport.SendEvent(EventCode.SyncVarSet, new RoomEventData { SenderActorId = 2, Data = writer.ToArray(), ViewId = 2001 }.ToParameters());
            Assert.AreEqual(string.Empty, remote.Title.Value);
            _transport.SendEvent(EventCode.SyncVarSet, new RoomEventData { SenderActorId = 1, Data = writer.ToArray(), ViewId = 2001 }.ToParameters());
            Assert.AreEqual("from-master", remote.Title.Value);
        }

        [Test]
        public void SyncVar_MasterVariableWriteByNonMasterIsIgnored()
        {
            JoinRoom(2, 1);
            var local = SpawnLocal();

            local.Title.Value = "nope";

            Assert.AreEqual(string.Empty, local.Title.Value);
        }

        [Test]
        public void Hooks_OwnershipGainedAndLostFollowOwnerChanged()
        {
            JoinRoom(1, 1);
            var remote = SpawnRemote(2001, 2);
            remote.Log.Clear();

            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = 2001, NewOwnerActorId = 1, SenderActorId = 1 }.ToParameters());
            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = 2001, NewOwnerActorId = 2, SenderActorId = 1 }.ToParameters());

            CollectionAssert.AreEqual(new[] { "ownerChanged", "gained", "ownerChanged", "lost" }, remote.Log);
        }

        [Test]
        public void Hooks_MasterChangedReachesSpawnedBehaviours()
        {
            JoinRoom(1, 1);
            var local = SpawnLocal();
            local.Log.Clear();

            _transport.SendEvent(EventCode.MasterClientChanged, new ParameterDictionary { [ParamKey.MasterClientId] = 2 });

            CollectionAssert.AreEqual(new[] { "master2" }, local.Log);
        }

        [Test]
        public void Hooks_NetworkTickRunsWithCatchUpCap()
        {
            JoinRoom(1, 1);
            var probe = SpawnLocal();
            Pulse.Objects.Tick(0f);

            _now += 0.1d;
            Pulse.Objects.Tick(0f);
            Assert.AreEqual(3, probe.Ticks.Count);
            Assert.AreEqual(probe.Ticks[0] + 1, probe.Ticks[1]);

            probe.Ticks.Clear();
            _now += 0.5d;
            Pulse.Objects.Tick(0f);
            Assert.AreEqual(5, probe.Ticks.Count);
        }

        [Test]
        public void Async_RequestOwnershipCompletesTrueOnOwnershipChanged()
        {
            JoinRoom(1, 1);
            var remote = SpawnRemote(2001, 2);

            var operation = remote.NetworkObject.RequestOwnershipAsync();
            Assert.IsFalse(operation.IsDone);
            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = 2001, NewOwnerActorId = 1, SenderActorId = 1 }.ToParameters());

            Assert.IsTrue(operation.IsSuccess);
            Assert.IsTrue(operation.Result);
        }

        [Test]
        public void Async_RequestOwnershipFalseOnForbiddenTimeoutAndFixed()
        {
            JoinRoom(1, 1);
            var remote = SpawnRemote(2001, 2);
            var forbidden = remote.NetworkObject.RequestOwnershipAsync();
            _transport.Respond(_transport.NextRequest(), ReturnCode.Forbidden);
            Assert.IsTrue(forbidden.IsDone);
            Assert.IsFalse(forbidden.Result);

            var timeout = remote.NetworkObject.RequestOwnershipAsync(2f);
            _now += 3d;
            Pulse.Client.Tick();
            Assert.IsTrue(timeout.IsDone);
            Assert.IsFalse(timeout.Result);

            remote.NetworkObject.OwnershipMode = OwnershipMode.Fixed;
            Assert.IsFalse(remote.NetworkObject.RequestOwnershipAsync().Result);
        }

        [Test]
        public void Async_TransferOwnershipCompletesWhenTargetBecomesOwner()
        {
            JoinRoom(1, 1);
            var local = SpawnLocal();

            var operation = local.NetworkObject.TransferOwnershipAsync(Pulse.CurrentRoom.GetPlayer(2));
            Assert.IsFalse(operation.IsDone);
            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = local.ViewId, NewOwnerActorId = 2, SenderActorId = 1 }.ToParameters());

            Assert.IsTrue(operation.Result);
        }

        [Test]
        public void Instantiate_SendsPersistFlag()
        {
            JoinRoom(1, 1);
            var persistent = CreatePrefab(true);
            _registry.SetEntries(new List<NetworkPrefabEntry> { new(PrefabId, "guid-probe", _prefab), new(2, "guid-persist", persistent) }, 3);

            var go = Pulse.Instantiate(persistent, Vector3.zero, Quaternion.identity);
            _created.Add(go);
            var raise = TakeRaise(EventCode.ObjectInstantiate);

            Assert.AreEqual(ObjectFlag.PersistAcrossScenes, raise.ObjectFlags);
        }

        [Test]
        public void Scene_BeginSceneChangeDestroysOnlyNonPersistentObjects()
        {
            JoinRoom(1, 1);
            var persistent = CreatePrefab(true);
            _registry.SetEntries(new List<NetworkPrefabEntry> { new(PrefabId, "guid-probe", _prefab), new(2, "guid-persist", persistent) }, 3);
            var keep = Pulse.Instantiate(persistent, Vector3.zero, Quaternion.identity);
            var drop = SpawnLocal();
            _created.Add(keep);

            Pulse.Objects.BeginSceneChange();

            Assert.IsNull(Pulse.FindObject(drop.ViewId));
            Assert.IsNotNull(Pulse.FindObject(keep.GetComponent<NetworkObject>().ViewId));
        }

        private GameObject CreatePrefab(bool persist)
        {
            var go = new GameObject("Probe");
            _created.Add(go);
            go.AddComponent<FeatureProbe>();
            var no = go.AddComponent<NetworkObject>();
            no.OwnershipMode = OwnershipMode.Takeover;
            no.PersistAcrossScenes = persist;
            return go;
        }

        private FeatureProbe SpawnLocal()
        {
            var go = Pulse.Instantiate(_prefab, Vector3.zero, Quaternion.identity);
            Assert.IsTrue(go != null);
            _created.Add(go);
            _transport.DrainRequests();
            return go.GetComponent<FeatureProbe>();
        }

        private FeatureProbe SpawnRemote(int viewId, int creator)
        {
            var writer = new NetWriter(128);
            writer.WriteInt(viewId);
            writer.WriteInt(PrefabId);
            writer.WriteInt(creator);
            UnityTypeSerializers.WriteVector3(writer, Vector3.zero);
            UnityTypeSerializers.WriteQuaternion(writer, Quaternion.identity);
            writer.WriteByte(0);
            writer.WriteObject(null);
            _transport.SendEvent(EventCode.ObjectInstantiate, new RoomEventData { SenderActorId = creator, Data = writer.ToArray(), ViewId = viewId }.ToParameters());
            var no = Pulse.FindObject(viewId);
            Assert.IsTrue(no != null, "remote object was not spawned");
            _created.Add(no.gameObject);
            return no.GetComponent<FeatureProbe>();
        }

        private static byte[] StatePacket(FeatureProbe source, int viewId)
        {
            var payload = new NetWriter(64);
            Assert.IsTrue(source.WriteState(payload, true));
            var packet = new NetWriter(96);
            packet.WriteInt(viewId);
            packet.WriteDouble(1d);
            packet.WriteByte(0);
            packet.WriteBytes(payload.AsSpan());
            return packet.ToArray();
        }

        private void JoinRoom(int localActor, int master)
        {
            Pulse.Client.Connect(new ConnectionOptions { UserId = "u", NickName = "Me" });
            _transport.SimulateConnected();
            var auth = new AuthenticateResponse { UserId = "u", NickName = "Me", ServerTimeTicks = 1000 };
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, auth.ToParameters());
            Pulse.Client.CreateRoom("room");
            var response = new JoinRoomResponse
            {
                RoomName = "arena",
                LocalActorId = localActor,
                MasterClientId = master,
                Players = new[]
                {
                    new PlayerInfo { ActorId = 1, NickName = "One", UserId = "u1" },
                    new PlayerInfo { ActorId = 2, NickName = "Two", UserId = "u2" }
                },
                RoomProperties = new PropertyTable(),
                MaxPlayers = 4,
                ServerTimeTicks = 1000,
                TickRate = 30
            };
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, response.ToParameters());
            _transport.DrainRequests();
        }

        private RaiseEventRequest TakeRaise(byte eventCode)
        {
            foreach (var request in _transport.DrainRequests())
            {
                if (request.OpCode != OpCode.RaiseEvent)
                {
                    continue;
                }

                var parsed = RaiseEventRequest.FromParameters(request.Parameters);
                if (parsed.EventCode == eventCode)
                {
                    return parsed;
                }
            }

            Assert.Fail($"No RaiseEvent with code {eventCode} was sent");
            return null;
        }
    }
}
