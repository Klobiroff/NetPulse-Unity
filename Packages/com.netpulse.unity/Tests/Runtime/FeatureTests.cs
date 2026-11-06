using System;
using System.Collections.Generic;
using NetPulse.Common;
using NUnit.Framework;
using UnityEngine;

namespace NetPulse.Tests
{
    public partial struct TestMessage : INetMessage
    {
        public int Number;
        public string Text;
        public Vector3 Where;
    }

    [NetSerializable]
    public partial struct TestStats
    {
        public int Hp;
        public bool Alive;
    }

    internal sealed class FakeSceneLoader : ISceneLoader
    {
        private readonly List<Action<string>> _pending = new();

        public List<string> Loads { get; } = new();

        public string Active { get; set; } = "Menu";

        public bool Fail { get; set; }

        public bool IsActive(string nameOrIndex) => Active == nameOrIndex;

        public void Load(string nameOrIndex, Action<string> onLoaded)
        {
            Loads.Add(nameOrIndex);
            _pending.Add(onLoaded);
        }

        public void Complete(string sceneName)
        {
            var copy = _pending.ToArray();
            _pending.Clear();
            Active = sceneName;
            foreach (var done in copy)
            {
                done(Fail ? null : sceneName);
            }
        }
    }

    internal sealed class FakeSceneHost : ISceneObjectHost
    {
        public int Begins { get; private set; }

        public int Flushes { get; private set; }

        public void BeginSceneChange() => Begins++;

        public void FlushSceneRegistrationIfMaster() => Flushes++;
    }

    public sealed class FeatureTests
    {
        private FakeTransport _transport;
        private NetPulseClient _client;
        private double _now;

        [SetUp]
        public void SetUp()
        {
            UnityTypeSerializers.Register();
            TestMessage.__NetPulseRegistration.Register();
            TestStats.__NetPulseRegistration.Register();
            _transport = new FakeTransport();
            _now = 100d;
            _client = new NetPulseClient(_transport, () => _now);
        }

        [Test]
        public void RpcIds_RuntimeMatchesGenerator()
        {
            Assert.AreEqual(47489, NetworkTypeInfo.ComputeRpcId("RpcSetTrigger"));
            Assert.AreEqual(13591, NetworkTypeInfo.ComputeRpcId("Hit"));
        }

        [Test]
        public void Message_SendOthersRaisesEvent244WithIdAndPayload()
        {
            JoinRoom(1, 1);
            _client.Messages.Send(new TestMessage { Number = 7, Text = "hi", Where = Vector3.up }, RpcTarget.Others, null, null);
            var raise = RaiseEventRequest.FromParameters(_transport.NextRequest().Parameters);

            Assert.AreEqual(EventCode.Message, raise.EventCode);
            Assert.AreEqual(ReceiverGroup.Others, raise.Receivers);
            var reader = new NetReader((byte[])raise.Data);
            Assert.IsTrue(NetMessageRegistry.TryGet<TestMessage>(out var codec));
            Assert.AreEqual(codec.Id, reader.ReadUShort());
            var message = codec.Read(reader);
            Assert.AreEqual(7, message.Number);
            Assert.AreEqual("hi", message.Text);
            Assert.AreEqual(Vector3.up, message.Where);
        }

        [Test]
        public void Message_ReceiveInvokesHandlerWithSender()
        {
            JoinRoom(1, 1);
            var received = new List<(TestMessage Message, Player Sender)>();
            _client.Messages.On<TestMessage>((message, sender) => received.Add((message, sender)));

            DeliverMessage(new TestMessage { Number = 3, Text = "yo" }, 2);

            Assert.AreEqual(1, received.Count);
            Assert.AreEqual(3, received[0].Message.Number);
            Assert.AreEqual(2, received[0].Sender.ActorId);
        }

        [Test]
        public void Message_AllInvokesLocalHandlerAndSends()
        {
            JoinRoom(1, 1);
            var received = new List<Player>();
            _client.Messages.On<TestMessage>((message, sender) => received.Add(sender));

            _client.Messages.Send(new TestMessage { Number = 1 }, RpcTarget.All, null, null);

            Assert.AreEqual(1, received.Count);
            Assert.IsTrue(received[0].IsLocal);
            Assert.AreEqual(1, _transport.DrainRequests().FindAll(r => r.OpCode == OpCode.RaiseEvent).Count);
        }

        [Test]
        public void Message_ToPlayerTargetsActor_LocalPlayerDoesNotSend()
        {
            JoinRoom(1, 1);
            _client.Messages.Send(new TestMessage(), RpcTarget.Others, _client.CurrentRoom.GetPlayer(2), null);
            var raise = RaiseEventRequest.FromParameters(_transport.NextRequest().Parameters);
            Assert.AreEqual(ReceiverGroup.Targets, raise.Receivers);
            CollectionAssert.AreEqual(new[] { 2 }, raise.TargetActors);

            var count = 0;
            _client.Messages.On<TestMessage>((message, sender) => count++);
            _client.Messages.Send(new TestMessage(), RpcTarget.Others, _client.CurrentRoom.LocalPlayer, null);
            Assert.AreEqual(1, count);
            Assert.AreEqual(0, _transport.DrainRequests().Count);
        }

        [Test]
        public void Message_MasterTargetFromMasterStaysLocal_OptionsApply()
        {
            JoinRoom(1, 1);
            var count = 0;
            _client.Messages.On<TestMessage>((message, sender) => count++);
            _client.Messages.Send(new TestMessage(), RpcTarget.MasterClient, null, null);
            Assert.AreEqual(1, count);
            Assert.AreEqual(0, _transport.DrainRequests().Count);

            _client.Messages.Send(new TestMessage(), RpcTarget.Others, null, new MessageOptions { Delivery = DeliveryMode.Unreliable, Caching = CachingOption.AddToRoomCache });
            var raise = RaiseEventRequest.FromParameters(_transport.NextRequest().Parameters);
            Assert.AreEqual(DeliveryMode.Unreliable, raise.Delivery);
            Assert.AreEqual(CachingOption.AddToRoomCache, raise.Caching);
        }

        [Test]
        public void Message_DisposedHandlerIsSilent()
        {
            JoinRoom(1, 1);
            var count = 0;
            var subscription = _client.Messages.On<TestMessage>((message, sender) => count++);
            DeliverMessage(new TestMessage(), 2);
            subscription.Dispose();
            DeliverMessage(new TestMessage(), 2);
            Assert.AreEqual(1, count);
        }

        [Test]
        public void Extended_NetSerializableWorksInObjectContext()
        {
            var writer = new NetWriter(32);
            writer.WriteObject(new TestStats { Hp = 5, Alive = true });
            var value = (TestStats)new NetReader(writer.ToArray()).ReadObject();
            Assert.AreEqual(5, value.Hp);
            Assert.IsTrue(value.Alive);
        }

        [Test]
        public void RoomKey_SetGetAndOnChangedForRemoteChange()
        {
            JoinRoom(1, 1);
            var key = new RoomKey<int>("score");
            var seen = new List<int>();
            _client.CurrentRoom.OnChanged(key, seen.Add);

            _client.CurrentRoom.Set(key, 4);
            Assert.AreEqual(4, _client.CurrentRoom.Get(key));
            _transport.SendEvent(EventCode.RoomPropertiesChanged, new PropertiesChangedData { ActorId = 0, Properties = new PropertyTable { ["score"] = 9 }, SenderActorId = 2 }.ToParameters());

            CollectionAssert.AreEqual(new[] { 4, 9 }, seen);
            Assert.AreEqual(9, _client.CurrentRoom.Get(key));
        }

        [Test]
        public void RoomKey_SetAsyncSendsExpectedAndAppliesOnlyOnSuccess()
        {
            JoinRoom(1, 1);
            var key = new RoomKey<int>("score");
            var op = _client.CurrentRoom.SetAsync(key, 5, 1);
            var request = _transport.NextRequest();
            var parsed = SetPropertiesRequest.FromParameters(request.Parameters);

            Assert.AreEqual(1, parsed.ExpectedProperties.Get<int>("score"));
            Assert.AreEqual(0, _client.CurrentRoom.Get(key));
            _transport.Respond(request, ReturnCode.PropertyCasFailed);
            Assert.IsTrue(op.HasError);
            Assert.AreEqual(0, _client.CurrentRoom.Get(key));

            var second = _client.CurrentRoom.SetAsync(key, 5, 0);
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok);
            Assert.IsTrue(second.IsSuccess);
            Assert.AreEqual(5, _client.CurrentRoom.Get(key));
        }

        [Test]
        public void PlayerKey_SetGetAndOnChanged()
        {
            JoinRoom(1, 1);
            var key = new PlayerKey<string>("mood");
            var other = _client.CurrentRoom.GetPlayer(2);
            var seen = new List<string>();
            other.OnChanged(key, (player, value) => seen.Add(player.ActorId + value));

            _transport.SendEvent(EventCode.PlayerPropertiesChanged, new PropertiesChangedData { ActorId = 2, Properties = new PropertyTable { ["mood"] = "ok" }, SenderActorId = 2 }.ToParameters());

            CollectionAssert.AreEqual(new[] { "2ok" }, seen);
            Assert.AreEqual("ok", other.Get(key));
            _client.CurrentRoom.LocalPlayer.Set(key, "me");
            Assert.AreEqual("me", _client.CurrentRoom.LocalPlayer.Get(key));
        }

        [Test]
        public void Tick_RateComesFromJoinResponse()
        {
            JoinRoom(1, 1, 20);
            Assert.AreEqual(20, _client.TickRate);
            Assert.AreEqual(0.05d, _client.TickDelta, 1e-9);
            Assert.AreEqual((long)Math.Floor(_client.ServerTime * 20d), _client.CurrentTick);
            var before = _client.CurrentTick;
            _now += 1d;
            Assert.AreEqual(before + 20, _client.CurrentTick);
        }

        [Test]
        public void Tick_DefaultsWhenServerSendsNone()
        {
            JoinRoom(1, 1, 0);
            Assert.AreEqual(30, _client.TickRate);
        }

        [Test]
        public void WaitForPlayers_CompletesWhenEnoughPlayers()
        {
            JoinRoom(1, 1);
            var wait = _client.WaitForPlayers(3);
            _client.Tick();
            Assert.IsFalse(wait.IsDone);

            _transport.SendEvent(EventCode.PlayerJoined, new PlayerInfo { ActorId = 3, NickName = "Three", UserId = "u3" }.ToParameters());
            _client.Tick();

            Assert.IsTrue(wait.IsSuccess);
        }

        [Test]
        public void WaitForPlayers_FailsWhenRoomIsLeft_CancelStops()
        {
            JoinRoom(1, 1);
            var wait = _client.WaitForPlayers(5);
            var cancelled = _client.WaitForPlayers(5);
            cancelled.Cancel();
            _client.LeaveRoom();
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok);
            _client.Tick();

            Assert.IsTrue(wait.HasError);
            Assert.IsTrue(cancelled.IsCanceled);
            Assert.IsFalse(cancelled.IsSuccess);
            Assert.Throws<OperationCanceledException>(() => cancelled.GetAwaiter().GetResult());
        }

        [Test]
        public void WaitUntil_ChecksOncePerTick()
        {
            var flag = false;
            var wait = _client.WaitUntil(() => flag);
            _client.Tick();
            Assert.IsFalse(wait.IsDone);
            flag = true;
            Assert.IsFalse(wait.IsDone);
            _client.Tick();
            Assert.IsTrue(wait.IsSuccess);
            Assert.IsTrue(_client.WaitUntil(() => true).IsSuccess);
        }

        [Test]
        public void Scene_MasterLoadSetsPropertyAndStartsLoad()
        {
            JoinRoom(1, 1);
            var loader = new FakeSceneLoader();
            var host = new FakeSceneHost();
            var scenes = new NetworkSceneManager(_client, host, loader);
            var loaded = new List<string>();
            _client.Events.OnNetworkSceneLoaded += loaded.Add;

            var op = scenes.LoadScene("Arena");
            var request = _transport.NextRequest();
            var parsed = SetPropertiesRequest.FromParameters(request.Parameters);

            Assert.AreEqual("1:Arena", parsed.Properties.Get<string>(RoomPropertyKeys.Scene));
            CollectionAssert.AreEqual(new[] { "Arena" }, loader.Loads);
            Assert.AreEqual(1, host.Begins);
            Assert.IsTrue(scenes.IsLoading);
            Assert.IsFalse(op.IsDone);

            loader.Complete("Arena");

            Assert.IsTrue(op.IsSuccess);
            Assert.AreEqual(1, host.Flushes);
            CollectionAssert.AreEqual(new[] { "Arena" }, loaded);
            Assert.IsFalse(scenes.IsLoading);
        }

        [Test]
        public void Scene_NonMasterCannotLoad()
        {
            JoinRoom(2, 1);
            var loader = new FakeSceneLoader();
            var scenes = new NetworkSceneManager(_client, new FakeSceneHost(), loader);

            var op = scenes.LoadScene(3);

            Assert.AreEqual(ReturnCode.Forbidden, op.Error.ReturnCode);
            Assert.AreEqual(0, loader.Loads.Count);
            Assert.AreEqual(0, _transport.DrainRequests().Count);
            Assert.IsTrue(_client.SetRoomProperties(new PropertyTable { [RoomPropertyKeys.Scene] = "9:X" }).HasError);
        }

        [Test]
        public void Scene_RemoteChangeQueuesRoomEventsAndReplaysInOrder()
        {
            JoinRoom(2, 1);
            var loader = new FakeSceneLoader();
            var scenes = new NetworkSceneManager(_client, new FakeSceneHost(), loader);
            var order = new List<int>();
            _client.Messages.On<TestMessage>((message, sender) => order.Add(message.Number));
            var loaded = new List<string>();
            _client.Events.OnNetworkSceneLoaded += loaded.Add;

            _transport.SendEvent(EventCode.RoomPropertiesChanged, new PropertiesChangedData { ActorId = 0, Properties = new PropertyTable { [RoomPropertyKeys.Scene] = "4:Level" }, SenderActorId = 1 }.ToParameters());
            DeliverMessage(new TestMessage { Number = 1 }, 1);
            DeliverMessage(new TestMessage { Number = 2 }, 1);

            CollectionAssert.AreEqual(new[] { "Level" }, loader.Loads);
            Assert.AreEqual(0, order.Count);
            Assert.AreEqual(2, _client.QueuedRoomEventCount);

            loader.Complete("Level");
            DeliverMessage(new TestMessage { Number = 3 }, 1);

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order);
            CollectionAssert.AreEqual(new[] { "Level" }, loaded);
            Assert.IsNotNull(scenes);
        }

        [Test]
        public void Scene_LateJoinerLoadsUnlessAlreadyActive()
        {
            var loader = new FakeSceneLoader { Active = "Level" };
            var scenes = new NetworkSceneManager(_client, new FakeSceneHost(), loader);
            JoinRoom(2, 1, 30, "7:Level");
            Assert.AreEqual(0, loader.Loads.Count);

            _client.LeaveRoom();
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok);
            loader.Active = "Menu";
            JoinExisting(2, 1, 30, "7:Level");

            CollectionAssert.AreEqual(new[] { "Level" }, loader.Loads);
            Assert.IsTrue(scenes.IsLoading);
        }

        [Test]
        public void Scene_AutoSyncOffAndFailedLoad()
        {
            JoinRoom(2, 1);
            var loader = new FakeSceneLoader();
            var scenes = new NetworkSceneManager(_client, new FakeSceneHost(), loader);
            _client.AutoSyncScene = false;
            _transport.SendEvent(EventCode.RoomPropertiesChanged, new PropertiesChangedData { ActorId = 0, Properties = new PropertyTable { [RoomPropertyKeys.Scene] = "1:A" }, SenderActorId = 1 }.ToParameters());
            Assert.AreEqual(0, loader.Loads.Count);

            _client.AutoSyncScene = true;
            loader.Fail = true;
            _transport.SendEvent(EventCode.RoomPropertiesChanged, new PropertiesChangedData { ActorId = 0, Properties = new PropertyTable { [RoomPropertyKeys.Scene] = "2:B" }, SenderActorId = 1 }.ToParameters());
            Assert.IsTrue(scenes.IsLoading);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, "[NetPulse] The network scene could not be loaded. Is it added to Build Settings?");
            loader.Complete("B");
            Assert.IsFalse(scenes.IsLoading);
            Assert.IsFalse(_client.IsDispatchSuspended);
        }

        [Test]
        public void Stats_DisabledRecordsNothing_EnabledCountsByCategory()
        {
            JoinRoom(1, 1);
            _client.RaiseRoomEvent(EventCode.Rpc, new byte[] { 1, 2, 3 }, new RaiseEventOptions(), 0);
            Assert.AreEqual(0, _client.Stats.Entries.Count);
            Assert.AreEqual(0, _client.Stats.TotalBytesOut);

            _client.Stats.Enabled = true;
            _client.Stats.Tag(StatCategory.Rpc, "Hero.Hit");
            _client.Stats.Tag(StatCategory.NetworkObject, "1001 Hero");
            var before = _client.BytesSent;
            _client.RaiseRoomEvent(EventCode.Rpc, new byte[] { 1, 2, 3 }, new RaiseEventOptions(), 0);
            var size = _client.BytesSent - before;

            Assert.AreEqual(size, _client.Stats.TotalBytesOut);
            Assert.AreEqual(2, _client.Stats.Entries.Count);
            Assert.AreEqual(size, _client.Stats.Entries[0].BytesOut);
            Assert.AreEqual(1, _client.Stats.Entries[0].MessagesOut);

            _client.RaiseRoomEvent(EventCode.Rpc, new byte[] { 1 }, new RaiseEventOptions(), 0);
            Assert.AreEqual(StatCategory.System, _client.Stats.Entries[2].Category);
        }

        [Test]
        public void Stats_IncomingAndOneSecondWindowAndReset()
        {
            JoinRoom(1, 1);
            _client.Stats.Enabled = true;
            _client.Tick();
            DeliverMessage(new TestMessage { Number = 1 }, 2);
            DeliverMessage(new TestMessage { Number = 1 }, 2);

            var message = FindEntry(StatCategory.Message, "TestMessage");
            Assert.IsNotNull(message);
            Assert.AreEqual(2, message.MessagesIn);
            Assert.Greater(message.BytesIn, 0);
            Assert.AreEqual(0f, message.BytesInPerSecond);

            _now += 1d;
            _client.Tick();
            Assert.AreEqual(message.BytesIn, message.BytesInPerSecond, 0.5f);
            Assert.Greater(_client.Stats.BytesInPerSecond, 0f);

            _client.Stats.Reset();
            Assert.AreEqual(0, _client.Stats.Entries.Count);
            Assert.AreEqual(0, _client.Stats.TotalBytesIn);
        }

        private NetStatEntry FindEntry(StatCategory category, string name)
        {
            foreach (var entry in _client.Stats.Entries)
            {
                if (entry.Category == category && entry.Name == name)
                {
                    return entry;
                }
            }

            return null;
        }

        private void DeliverMessage(TestMessage message, int sender)
        {
            Assert.IsTrue(NetMessageRegistry.TryGet<TestMessage>(out var codec));
            var writer = new NetWriter(64);
            writer.WriteUShort(codec.Id);
            codec.Write(writer, message);
            var data = new RoomEventData { SenderActorId = sender, Data = writer.ToArray() };
            _transport.SendEvent(EventCode.Message, data.ToParameters());
        }

        private void JoinRoom(int localActor, int master, int tickRate = 30, string scene = null)
        {
            _client.Connect(new ConnectionOptions { UserId = "u", NickName = "Me" });
            _transport.SimulateConnected();
            var auth = new AuthenticateResponse { UserId = "u", NickName = "Me", ServerTimeTicks = 1000 };
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, auth.ToParameters());
            JoinExisting(localActor, master, tickRate, scene);
        }

        private void JoinExisting(int localActor, int master, int tickRate, string scene)
        {
            _client.CreateRoom("room");
            var properties = new PropertyTable();
            if (scene != null)
            {
                properties[RoomPropertyKeys.Scene] = scene;
            }

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
                RoomProperties = properties,
                MaxPlayers = 4,
                ServerTimeTicks = 1000,
                TickRate = tickRate
            };
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, response.ToParameters());
        }
    }
}
