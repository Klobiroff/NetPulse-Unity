using System.Collections.Generic;
using NetPulse.Common;
using NUnit.Framework;

namespace NetPulse.Tests
{
    public sealed class NetPulseClientTests
    {
        private FakeTransport _transport;
        private NetPulseClient _client;
        private double _now;

        [SetUp]
        public void SetUp()
        {
            UnityTypeSerializers.Register();
            _transport = new FakeTransport();
            _now = 100d;
            _client = new NetPulseClient(_transport, () => _now);
        }

        [Test]
        public void Connect_AuthenticatesAndCompletes()
        {
            var op = ConnectAndAuthenticate();

            Assert.IsTrue(op.IsSuccess);
            Assert.AreEqual(NetState.Connected, _client.State);
            Assert.AreEqual("user-1", _client.UserId);
        }

        [Test]
        public void Connect_UsesOptionsForTransportAndAuthRequest()
        {
            var options = new ConnectionOptions { Host = "10.0.0.5", Port = 6000, AppId = "app", AppVersion = "2.0", UserId = "u", NickName = "Nick" };
            _client.Connect(options);
            _transport.SimulateConnected();
            var request = _transport.NextRequest();
            var auth = AuthenticateRequest.FromParameters(request.Parameters);

            Assert.AreEqual("10.0.0.5", _transport.Host);
            Assert.AreEqual(6000, _transport.Port);
            Assert.AreEqual(OpCode.Authenticate, request.OpCode);
            Assert.AreEqual("app", auth.AppId);
            Assert.AreEqual("2.0", auth.AppVersion);
            Assert.AreEqual("Nick", auth.NickName);
            Assert.AreEqual(NetState.Authenticating, _client.State);
        }

        [Test]
        public void Connect_AuthFailure_FailsOperationAndDisconnects()
        {
            var op = _client.Connect(new ConnectionOptions { UserId = "u" });
            _transport.SimulateConnected();
            var disconnects = new List<string>();
            _client.Events.OnDisconnected += disconnects.Add;

            _transport.Respond(_transport.NextRequest(), ReturnCode.InvalidAppId, null, "bad app");

            Assert.IsTrue(op.HasError);
            Assert.AreEqual(ReturnCode.InvalidAppId, op.Error.ReturnCode);
            Assert.AreEqual(NetState.Disconnected, _client.State);
            Assert.AreEqual(1, disconnects.Count);
        }

        [Test]
        public void Connect_WhileConnected_Fails()
        {
            ConnectAndAuthenticate();
            var op = _client.Connect(new ConnectionOptions());
            Assert.IsTrue(op.HasError);
            Assert.AreEqual(NetPulseReturnCode.InvalidState, op.Error.ReturnCode);
        }

        [Test]
        public void FullFlow_Connect_Lobby_Create_Events_Properties_Leave()
        {
            ConnectAndAuthenticate();
            var log = new List<string>();
            _client.Events.OnJoinedLobby += () => log.Add("lobby");
            _client.Events.OnRoomListUpdate += rooms => log.Add("rooms:" + rooms.Count);
            _client.Events.OnCreatedRoom += () => log.Add("created");
            _client.Events.OnJoinedRoom += () => log.Add("joined");
            _client.Events.OnPlayerEnteredRoom += p => log.Add("enter:" + p.ActorId);
            _client.Events.OnPlayerLeftRoom += p => log.Add("exit:" + p.ActorId);
            _client.Events.OnMasterClientSwitched += p => log.Add("master:" + p.ActorId);
            _client.Events.OnRoomPropertiesChanged += c => log.Add("roomprops:" + c.Count);
            _client.Events.OnPlayerPropertiesChanged += (p, c) => log.Add("playerprops:" + p.ActorId);
            _client.Events.OnLeftRoom += () => log.Add("left");

            var lobby = _client.JoinLobby();
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok);
            Assert.IsTrue(lobby.IsSuccess);
            Assert.AreEqual(NetState.InLobby, _client.State);

            var list = new RoomListData { IsFullList = true, Rooms = new[] { new RoomInfo { Name = "arena", PlayerCount = 1, MaxPlayers = 4, IsOpen = true } } };
            _transport.SendEvent(EventCode.RoomList, list.ToParameters());
            Assert.AreEqual(1, _client.LobbyRooms.Count);
            Assert.AreEqual("arena", _client.LobbyRooms[0].Name);

            var create = _client.CreateRoom("arena", new RoomOptions { MaxPlayers = 4 });
            var request = _transport.NextRequest();
            Assert.AreEqual(OpCode.CreateRoom, request.OpCode);
            Assert.AreEqual("arena", CreateRoomRequest.FromParameters(request.Parameters).RoomName);
            _transport.Respond(request, ReturnCode.Ok, JoinResponse(1, 1, new PlayerInfo { ActorId = 1, NickName = "Me", UserId = "user-1" }).ToParameters());

            Assert.IsTrue(create.IsSuccess);
            Assert.AreEqual(NetState.InRoom, _client.State);
            Assert.AreEqual("arena", create.Result.Name);
            Assert.AreEqual(1, create.Result.LocalPlayer.ActorId);
            Assert.IsTrue(create.Result.LocalPlayer.IsMasterClient);

            _transport.SendEvent(EventCode.PlayerJoined, new PlayerInfo { ActorId = 2, NickName = "Other", UserId = "u2" }.ToParameters());
            Assert.AreEqual(2, _client.CurrentRoom.PlayerCount);

            var changed = new PropertiesChangedData { ActorId = 0, Properties = new PropertyTable { ["map"] = "forest" }, SenderActorId = 2 };
            _transport.SendEvent(EventCode.RoomPropertiesChanged, changed.ToParameters());
            Assert.AreEqual("forest", _client.CurrentRoom.GetProperty<string>("map"));

            var player = new PropertiesChangedData { ActorId = 2, Properties = new PropertyTable { ["score"] = 5 }, SenderActorId = 2 };
            _transport.SendEvent(EventCode.PlayerPropertiesChanged, player.ToParameters());
            Assert.AreEqual(5, _client.CurrentRoom.GetPlayer(2).GetProperty<int>("score"));

            _client.CurrentRoom.SetProperty("round", 3);
            var setRequest = _transport.NextRequest();
            Assert.AreEqual(OpCode.SetRoomProperties, setRequest.OpCode);
            Assert.AreEqual(3, _client.CurrentRoom.GetProperty<int>("round"));
            _transport.Respond(setRequest, ReturnCode.Ok);

            _transport.SendEvent(EventCode.PlayerLeft, new PlayerLeftData { ActorId = 1, MasterClientId = 2 }.ToParameters());
            _transport.SendEvent(EventCode.MasterClientChanged, new ParameterDictionary { [ParamKey.MasterClientId] = 2 });
            Assert.AreEqual(2, _client.CurrentRoom.MasterClientId);

            var leave = _client.LeaveRoom();
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok);
            Assert.IsTrue(leave.IsSuccess);
            Assert.AreEqual(NetState.Connected, _client.State);
            Assert.IsNull(_client.CurrentRoom);

            var expected = new[]
            {
                "lobby",
                "rooms:1",
                "created",
                "joined",
                "enter:2",
                "roomprops:1",
                "playerprops:2",
                "roomprops:1",
                "exit:1",
                "master:2",
                "left"
            };
            CollectionAssert.AreEqual(expected, log);
        }

        [Test]
        public void JoinRoom_Failure_ReportsErrorAndKeepsState()
        {
            ConnectAndAuthenticate();
            var failedCode = (short)0;
            _client.Events.OnJoinRoomFailed += (code, message) => failedCode = code;

            var op = _client.JoinRoom("missing");
            _transport.Respond(_transport.NextRequest(), ReturnCode.RoomNotFound, null, "no such room");

            Assert.IsTrue(op.HasError);
            Assert.AreEqual(ReturnCode.RoomNotFound, op.Error.ReturnCode);
            Assert.AreEqual(ReturnCode.RoomNotFound, failedCode);
            Assert.AreEqual(NetState.Connected, _client.State);
        }

        [Test]
        public void JoinOrCreate_FirstPlayer_RaisesCreated()
        {
            ConnectAndAuthenticate();
            var created = false;
            _client.Events.OnCreatedRoom += () => created = true;

            var op = _client.JoinOrCreateRoom("lobby-1");
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, JoinResponse(1, 1, new PlayerInfo { ActorId = 1 }).ToParameters());

            Assert.IsTrue(op.IsSuccess);
            Assert.IsTrue(created);
        }

        [Test]
        public void RaiseEvent_SendsRequestAndReceivesCustomEvents()
        {
            JoinTestRoom();
            _client.RaiseEvent(5, "payload", new RaiseEventOptions { Receivers = ReceiverGroup.All });
            var request = _transport.NextRequest();
            var raise = RaiseEventRequest.FromParameters(request.Parameters);
            Assert.AreEqual(OpCode.RaiseEvent, request.OpCode);
            Assert.AreEqual(5, raise.EventCode);
            Assert.AreEqual(ReceiverGroup.All, raise.Receivers);
            Assert.AreEqual("payload", raise.Data);

            var received = new List<string>();
            _client.Events.OnEvent += (code, sender, data) => received.Add(code + ":" + sender + ":" + data);
            _transport.SendEvent(5, new RoomEventData { SenderActorId = 2, Data = "hello" }.ToParameters());

            var expected = new[] { "5:2:hello" };
            CollectionAssert.AreEqual(expected, received);
        }

        [Test]
        public void RaiseEvent_SystemCodeIsRejected()
        {
            JoinTestRoom();
            Assert.Throws<System.ArgumentOutOfRangeException>(() => _client.RaiseEvent(240, null));
        }

        [Test]
        public void SetPlayerProperties_WithExpected_AppliesOnlyAfterServerOk()
        {
            JoinTestRoom();
            var local = _client.CurrentRoom.LocalPlayer;
            var op = _client.SetPlayerProperties(local, new PropertyTable { ["ready"] = true }, new PropertyTable());

            Assert.IsFalse(local.GetProperty<bool>("ready"));
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok);
            Assert.IsTrue(op.IsSuccess);
            Assert.IsTrue(local.GetProperty<bool>("ready"));
        }

        [Test]
        public void Request_Timeout_FailsOperation()
        {
            ConnectAndAuthenticate();
            var op = _client.JoinLobby();
            _now += 60d;
            _client.Tick();

            Assert.IsTrue(op.HasError);
            Assert.AreEqual(NetPulseReturnCode.Timeout, op.Error.ReturnCode);
        }

        [Test]
        public void TransportDisconnect_FailsPendingAndRaisesDisconnected()
        {
            JoinTestRoom();
            var cause = (string)null;
            var left = false;
            _client.Events.OnDisconnected += c => cause = c;
            _client.Events.OnLeftRoom += () => left = true;
            var pending = _client.LeaveRoom();

            _transport.SimulateDisconnected("Timeout");

            Assert.IsTrue(pending.HasError);
            Assert.AreEqual("Timeout", cause);
            Assert.IsTrue(left);
            Assert.AreEqual(NetState.Disconnected, _client.State);
        }

        [Test]
        public void Offline_CreateRoomAndLoopbackEvents()
        {
            _client.OfflineMode = true;
            var connect = _client.Connect(new ConnectionOptions { NickName = "Solo" });
            Assert.IsTrue(connect.IsSuccess);

            var room = _client.CreateRoom("local");
            Assert.IsTrue(room.IsSuccess);
            Assert.IsTrue(room.Result.LocalPlayer.IsMasterClient);

            var received = new List<byte>();
            _client.Events.OnEvent += (code, sender, data) => received.Add(code);
            _client.RaiseEvent(7, 1, new RaiseEventOptions { Receivers = ReceiverGroup.All });
            _client.RaiseEvent(8, 1, new RaiseEventOptions { Receivers = ReceiverGroup.Others });
            var expectedCodes = new byte[] { 7 };
            CollectionAssert.AreEqual(expectedCodes, received);

            room.Result.SetProperty("k", 1);
            Assert.AreEqual(1, room.Result.GetProperty<int>("k"));
            Assert.IsTrue(_client.LeaveRoom().IsSuccess);
            Assert.AreEqual(0, _transport.PendingCount);
        }

        [Test]
        public void ServerClock_UsesAuthTimeAndPongSamples()
        {
            ConnectAndAuthenticate(serverTimeTicks: 5000);
            Assert.AreEqual(5d + 0d, _client.ServerTime, 0.01d);
            _now += 1d;
            Assert.AreEqual(6d, _client.ServerTime, 0.01d);
        }

        private NetOperation ConnectAndAuthenticate(long serverTimeTicks = 1000)
        {
            var op = _client.Connect(new ConnectionOptions { UserId = "user-1", NickName = "Me" });
            _transport.SimulateConnected();
            var response = new AuthenticateResponse { UserId = "user-1", NickName = "Me", ServerTimeTicks = serverTimeTicks };
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, response.ToParameters());
            return op;
        }

        private void JoinTestRoom()
        {
            ConnectAndAuthenticate();
            _client.CreateRoom("room");
            var response = JoinResponse(1, 1, new PlayerInfo { ActorId = 1, NickName = "Me", UserId = "user-1" }, new PlayerInfo { ActorId = 2, NickName = "Other", UserId = "u2" });
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, response.ToParameters());
        }

        private static JoinRoomResponse JoinResponse(int localActor, int master, params PlayerInfo[] players) => new()
        {
            RoomName = "arena",
            LocalActorId = localActor,
            MasterClientId = master,
            Players = players,
            RoomProperties = new PropertyTable(),
            MaxPlayers = 4,
            ServerTimeTicks = 1000
        };
    }
}
