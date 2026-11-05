using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NetPulse.Common;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace NetPulse.Tests
{
    public sealed class NetworkObjectOwnershipTests
    {
        private const int FixedPrefabId = 1;
        private const int TakeoverPrefabId = 2;
        private const int RequestPrefabId = 3;

        private readonly List<GameObject> _created = new();
        private FakeTransport _transport;
        private NetPulseSettings _settings;
        private NetworkPrefabRegistry _registry;
        private GameObject _fixedPrefab;
        private GameObject _takeoverPrefab;
        private GameObject _requestPrefab;

        [SetUp]
        public void SetUp()
        {
            _transport = new FakeTransport();
            Pulse.UseTransport(_transport);
            _fixedPrefab = CreatePrefab("Fixed", OwnershipMode.Fixed);
            _takeoverPrefab = CreatePrefab("Takeover", OwnershipMode.Takeover);
            _requestPrefab = CreatePrefab("Request", OwnershipMode.Request);
            _registry = ScriptableObject.CreateInstance<NetworkPrefabRegistry>();
            _registry.SetEntries(
                new List<NetworkPrefabEntry>
                {
                    new(FixedPrefabId, "guid-fixed", _fixedPrefab),
                    new(TakeoverPrefabId, "guid-takeover", _takeoverPrefab),
                    new(RequestPrefabId, "guid-request", _requestPrefab)
                },
                4);
            _settings = ScriptableObject.CreateInstance<NetPulseSettings>();
            _settings.PrefabRegistry = _registry;
            NetPulseSettings.SetInstance(_settings);
        }

        [TearDown]
        public void TearDown()
        {
            _transport.SimulateDisconnected("test");
            Pulse.UseTransport(null);
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
        public void Instantiate_SendsOwnershipModeAndViewId()
        {
            JoinRoom(1, 1);
            var prefabs = new[] { (_fixedPrefab, ObjectOwnershipMode.Fixed), (_takeoverPrefab, ObjectOwnershipMode.Takeover), (_requestPrefab, ObjectOwnershipMode.Request) };

            foreach (var (prefab, expected) in prefabs)
            {
                var spawned = Track(Pulse.Instantiate(prefab, Vector3.zero, Quaternion.identity));
                var raise = TakeRaise(EventCode.ObjectInstantiate);

                Assert.AreEqual(expected, raise.OwnershipMode);
                Assert.AreEqual(spawned.GetComponent<NetworkObject>().ViewId, raise.ViewId);
            }
        }

        [Test]
        public void RequestOwnership_FixedSendsNothing()
        {
            JoinRoom(2, 1);
            var no = SpawnRemote(FixedPrefabId, 1001, 1);
            _transport.DrainRequests();

            no.RequestOwnership();

            Assert.AreEqual(0, Take(OpCode.RequestOwnership).Count);
            Assert.AreEqual(0, Take(OpCode.TransferOwnership).Count);
            Assert.IsFalse(no.IsMine);
        }

        [Test]
        public void RequestOwnership_TakeoverAndRequestSendRequestOwnership()
        {
            JoinRoom(2, 1);
            var takeover = SpawnRemote(TakeoverPrefabId, 1001, 1);
            var request = SpawnRemote(RequestPrefabId, 1002, 1);
            _transport.DrainRequests();

            takeover.RequestOwnership();
            request.RequestOwnership();
            var sent = Take(OpCode.RequestOwnership);

            Assert.AreEqual(2, sent.Count);
            Assert.AreEqual(1001, RequestOwnershipRequest.FromParameters(sent[0].Parameters).ViewId);
            Assert.AreEqual(1002, RequestOwnershipRequest.FromParameters(sent[1].Parameters).ViewId);
        }

        [Test]
        public void RequestOwnership_ChangesNothingUntilServerConfirms()
        {
            JoinRoom(2, 1);
            var no = SpawnRemote(TakeoverPrefabId, 1001, 1);
            var changes = new List<(Player Previous, Player Current)>();
            no.OwnershipChanged += (_, previous, current) => changes.Add((previous, current));

            no.RequestOwnership();
            var sent = Take(OpCode.RequestOwnership);
            _transport.Respond(sent[0], ReturnCode.Ok);

            Assert.IsFalse(no.IsMine);
            Assert.AreEqual(1, no.OwnerActorId);
            Assert.AreEqual(0, changes.Count);

            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = 1001, NewOwnerActorId = 2, SenderActorId = 2 }.ToParameters());

            Assert.IsTrue(no.IsMine);
            Assert.AreEqual(2, no.OwnerActorId);
            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual(1, changes[0].Previous.ActorId);
            Assert.AreEqual(2, changes[0].Current.ActorId);
        }

        [Test]
        public void TransferOwnership_ChangesNothingUntilServerConfirms()
        {
            JoinRoom(1, 1);
            var spawned = Track(Pulse.Instantiate(_requestPrefab, Vector3.zero, Quaternion.identity));
            var no = spawned.GetComponent<NetworkObject>();
            _transport.DrainRequests();

            no.TransferOwnership(Pulse.CurrentRoom.GetPlayer(2));
            var sent = Take(OpCode.TransferOwnership);

            Assert.AreEqual(1, sent.Count);
            Assert.AreEqual(2, TransferOwnershipRequest.FromParameters(sent[0].Parameters).NewOwnerActorId);
            Assert.IsTrue(no.IsMine);

            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = no.ViewId, NewOwnerActorId = 2, SenderActorId = 1 }.ToParameters());

            Assert.IsFalse(no.IsMine);
            Assert.AreEqual(2, no.Owner.ActorId);
        }

        [Test]
        public void TransferOwnership_FixedIsRefusedLocally()
        {
            JoinRoom(1, 1);
            var no = Track(Pulse.Instantiate(_fixedPrefab, Vector3.zero, Quaternion.identity)).GetComponent<NetworkObject>();
            _transport.DrainRequests();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Fixed ownership"));

            no.TransferOwnership(Pulse.CurrentRoom.GetPlayer(2));

            Assert.AreEqual(0, Take(OpCode.TransferOwnership).Count);
        }

        [Test]
        public void OwnershipRequestedEvent_RaisesCallbackAndApproveTransfersOnlyAfterConfirmation()
        {
            JoinRoom(1, 1);
            var no = Track(Pulse.Instantiate(_requestPrefab, Vector3.zero, Quaternion.identity)).GetComponent<NetworkObject>();
            var probe = no.GetComponent<OwnershipProbe>();
            var callbacks = new List<(NetworkObject Object, Player Requester)>();
            Pulse.Events.OnOwnershipRequest += (networkObject, requester) => callbacks.Add((networkObject, requester));
            _transport.DrainRequests();

            _transport.SendEvent(EventCode.OwnershipRequested, new OwnershipRequestedData { ViewId = no.ViewId, RequesterActorId = 2 }.ToParameters());

            Assert.AreEqual(1, callbacks.Count);
            Assert.AreSame(no, callbacks[0].Object);
            Assert.AreEqual(2, callbacks[0].Requester.ActorId);
            Assert.AreEqual(1, probe.Requests.Count);
            Assert.AreEqual(0, Take(OpCode.TransferOwnership).Count);

            no.ApproveOwnership(callbacks[0].Requester);
            var sent = Take(OpCode.TransferOwnership);

            Assert.AreEqual(1, sent.Count);
            Assert.AreEqual(no.ViewId, TransferOwnershipRequest.FromParameters(sent[0].Parameters).ViewId);
            Assert.AreEqual(2, TransferOwnershipRequest.FromParameters(sent[0].Parameters).NewOwnerActorId);
            Assert.IsTrue(no.IsMine);

            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = no.ViewId, NewOwnerActorId = 2, SenderActorId = 1 }.ToParameters());

            Assert.IsFalse(no.IsMine);
            Assert.AreEqual(1, probe.OwnerChanges);
        }

        [Test]
        public void OwnershipRequestedEvent_IgnoredWhenNotOwner()
        {
            JoinRoom(2, 1);
            var no = SpawnRemote(RequestPrefabId, 1001, 1);
            var callbacks = 0;
            Pulse.Events.OnOwnershipRequest += (_, _) => callbacks++;

            _transport.SendEvent(EventCode.OwnershipRequested, new OwnershipRequestedData { ViewId = 1001, RequesterActorId = 1 }.ToParameters());

            Assert.AreEqual(0, callbacks);
            Assert.AreEqual(0, no.GetComponent<OwnershipProbe>().Requests.Count);
        }

        [Test]
        public void Forbidden_LeavesOwnershipUnchangedAndRaisesFailure()
        {
            JoinRoom(2, 1);
            var no = SpawnRemote(TakeoverPrefabId, 1001, 1);
            var failures = new List<(NetworkObject Object, short Code)>();
            Pulse.Events.OnOwnershipFailed += (networkObject, code, _) => failures.Add((networkObject, code));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("forbidden"));

            no.RequestOwnership();
            _transport.Respond(Take(OpCode.RequestOwnership)[0], ReturnCode.Forbidden, null, "denied");

            Assert.IsFalse(no.IsMine);
            Assert.AreEqual(1, no.OwnerActorId);
            Assert.AreEqual(1, failures.Count);
            Assert.AreSame(no, failures[0].Object);
            Assert.AreEqual(ReturnCode.Forbidden, failures[0].Code);
        }

        [Test]
        public void StateSync_ForOwnedObjectIsNotApplied()
        {
            JoinRoom(2, 1);
            var no = SpawnRemote(TakeoverPrefabId, 1001, 1);
            no.RequestOwnership();
            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = 1001, NewOwnerActorId = 2, SenderActorId = 2 }.ToParameters());
            var writer = new NetWriter(64);
            writer.WriteInt(1001);
            writer.WriteDouble(1d);

            _transport.SendEvent(
                EventCode.StateSync,
                new RoomEventData { SenderActorId = 1, Data = writer.ToArray(), ViewId = 1001 }.ToParameters());

            Assert.IsTrue(no.IsMine);
        }

        [UnityTest]
        public IEnumerator PlayerLeft_DestroysOnlyObjectsTheLeaverOwns()
        {
            JoinRoom(2, 1);
            var kept = SpawnRemote(TakeoverPrefabId, 3001, 3);
            var lost = SpawnRemote(TakeoverPrefabId, 3002, 3);
            _transport.SendEvent(EventCode.PlayerJoined, new PlayerInfo { ActorId = 3, NickName = "Three", UserId = "u3" }.ToParameters());
            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = 3001, NewOwnerActorId = 2, SenderActorId = 3 }.ToParameters());

            _transport.SendEvent(EventCode.PlayerLeft, new PlayerLeftData { ActorId = 3, IsInactive = false, MasterClientId = 1 }.ToParameters());
            yield return null;

            Assert.IsTrue(kept != null);
            Assert.IsTrue(kept.IsMine);
            Assert.IsTrue(lost == null);
        }

        [UnityTest]
        public IEnumerator PlayerLeft_SceneObjectsOfLeaverReturnToMaster()
        {
            JoinRoom(2, 1);
            var scene = CreateSceneObject(7, OwnershipMode.Takeover);
            _transport.SendEvent(EventCode.PlayerJoined, new PlayerInfo { ActorId = 3, NickName = "Three", UserId = "u3" }.ToParameters());
            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = 7, NewOwnerActorId = 3, SenderActorId = 3 }.ToParameters());
            Assert.AreEqual(3, scene.OwnerActorId);

            _transport.SendEvent(EventCode.PlayerLeft, new PlayerLeftData { ActorId = 3, IsInactive = false, MasterClientId = 1 }.ToParameters());
            yield return null;

            Assert.IsTrue(scene != null);
            Assert.AreEqual(0, scene.OwnerActorId);
            Assert.AreEqual(1, scene.EffectiveOwnerActorId);
            Assert.IsFalse(scene.IsMine);
        }

        [Test]
        public void SceneObjects_MasterRegistersThemWithTheirModes()
        {
            JoinRoom(1, 1, false);
            var takeover = CreateSceneObject(7, OwnershipMode.Takeover);
            var request = CreateSceneObject(8, OwnershipMode.Request);
            var fixedObject = CreateSceneObject(9, OwnershipMode.Fixed);
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, JoinResponse(1, 1).ToParameters());
            _transport.DrainRequests();

            Pulse.Objects.Tick(0f);
            var sent = Take(OpCode.RegisterSceneObjects);

            Assert.AreEqual(1, sent.Count);
            var parsed = RegisterSceneObjectsRequest.FromParameters(sent[0].Parameters);
            var modes = new Dictionary<int, byte>();
            for (var i = 0; i < parsed.ViewIds.Length; i++)
            {
                modes[parsed.ViewIds[i]] = parsed.Modes[i];
            }

            Assert.AreEqual(3, modes.Count);
            Assert.AreEqual((byte)OwnershipMode.Takeover, modes[7]);
            Assert.AreEqual((byte)OwnershipMode.Request, modes[8]);
            Assert.AreEqual((byte)OwnershipMode.Fixed, modes[9]);
            Assert.IsTrue(takeover.IsMine && request.IsMine && fixedObject.IsMine);
        }

        [Test]
        public void SceneObjects_NonMasterRegistersOnlyAfterBecomingMaster()
        {
            JoinRoom(2, 1);
            CreateSceneObject(7, OwnershipMode.Takeover);
            _transport.DrainRequests();

            Pulse.Objects.Tick(0f);
            Assert.AreEqual(0, Take(OpCode.RegisterSceneObjects).Count);

            _transport.SendEvent(EventCode.MasterClientChanged, new ParameterDictionary { [ParamKey.MasterClientId] = 2 });
            Pulse.Objects.Tick(0f);

            Assert.AreEqual(1, Take(OpCode.RegisterSceneObjects).Count);
        }

        private static JoinRoomResponse JoinResponse(int localActor, int master) => new()
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
            ServerTimeTicks = 1000
        };

        private GameObject CreatePrefab(string name, OwnershipMode mode)
        {
            var go = new GameObject(name);
            _created.Add(go);
            go.AddComponent<OwnershipProbe>();
            go.AddComponent<NetworkObject>().OwnershipMode = mode;
            return go;
        }

        private NetworkObject CreateSceneObject(int sceneId, OwnershipMode mode)
        {
            var go = new GameObject("Scene" + sceneId);
            _created.Add(go);
            go.SetActive(false);
            var no = go.AddComponent<NetworkObject>();
            no.OwnershipMode = mode;
            typeof(NetworkObject).GetField("_sceneId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(no, sceneId);
            go.SetActive(true);
            return no;
        }

        private NetworkObject SpawnRemote(int prefabId, int viewId, int creator)
        {
            var writer = new NetWriter(128);
            writer.WriteInt(viewId);
            writer.WriteInt(prefabId);
            writer.WriteInt(creator);
            UnityTypeSerializers.WriteVector3(writer, Vector3.zero);
            UnityTypeSerializers.WriteQuaternion(writer, Quaternion.identity);
            writer.WriteByte(0);
            writer.WriteObject(null);
            _transport.SendEvent(
                EventCode.ObjectInstantiate,
                new RoomEventData { SenderActorId = creator, Data = writer.ToArray(), ViewId = viewId }.ToParameters());
            var no = Pulse.FindObject(viewId);
            Assert.IsTrue(no != null, "remote object was not spawned");
            _created.Add(no.gameObject);
            return no;
        }

        private GameObject Track(GameObject go)
        {
            Assert.IsTrue(go != null);
            _created.Add(go);
            return go;
        }

        private void JoinRoom(int localActor, int master, bool respond = true)
        {
            Pulse.Client.Connect(new ConnectionOptions { UserId = "u", NickName = "Me" });
            _transport.SimulateConnected();
            var auth = new AuthenticateResponse { UserId = "u", NickName = "Me", ServerTimeTicks = 1000 };
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, auth.ToParameters());
            Pulse.Client.CreateRoom("room");
            if (respond)
            {
                _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, JoinResponse(localActor, master).ToParameters());
                _transport.DrainRequests();
            }
        }

        private List<OperationRequest> Take(byte opCode)
        {
            var result = new List<OperationRequest>();
            foreach (var request in _transport.DrainRequests())
            {
                if (request.OpCode == opCode)
                {
                    result.Add(request);
                }
            }

            return result;
        }

        private RaiseEventRequest TakeRaise(byte eventCode)
        {
            foreach (var request in Take(OpCode.RaiseEvent))
            {
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
