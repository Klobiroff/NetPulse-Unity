using System.Collections.Generic;
using NetPulse.Common;
using NUnit.Framework;

namespace NetPulse.Tests
{
    public sealed class OwnershipClientTests
    {
        private FakeTransport _transport;
        private NetPulseClient _client;

        [SetUp]
        public void SetUp()
        {
            UnityTypeSerializers.Register();
            _transport = new FakeTransport();
            _client = new NetPulseClient(_transport, () => 100d);
        }

        [Test]
        public void Authenticate_SendsTokenAndAdoptsServerIdentity()
        {
            var op = _client.Connect(new ConnectionOptions { UserId = "client-user", NickName = "client-nick", AuthToken = "secret-token" });
            _transport.SimulateConnected();
            var request = _transport.NextRequest();
            var auth = AuthenticateRequest.FromParameters(request.Parameters);
            var response = new AuthenticateResponse { UserId = "server-user", NickName = "server-nick", ServerTimeTicks = 1000 };
            _transport.Respond(request, ReturnCode.Ok, response.ToParameters());

            Assert.AreEqual("secret-token", auth.AuthToken);
            Assert.IsTrue(op.IsSuccess);
            Assert.AreEqual("server-user", _client.UserId);
            Assert.AreEqual("server-nick", _client.NickName);
        }

        [Test]
        public void RaiseRoomEvent_CarriesOwnershipModeAndViewId()
        {
            JoinRoom(1, 1);
            var options = new RaiseEventOptions { OwnershipMode = ObjectOwnershipMode.Takeover };

            _client.RaiseRoomEvent(EventCode.ObjectInstantiate, new byte[] { 1 }, options, 1001);
            var request = _transport.NextRequest();
            var parsed = RaiseEventRequest.FromParameters(request.Parameters);

            Assert.AreEqual(OpCode.RaiseEvent, request.OpCode);
            Assert.AreEqual(EventCode.ObjectInstantiate, parsed.EventCode);
            Assert.AreEqual(1001, parsed.ViewId);
            Assert.AreEqual(ObjectOwnershipMode.Takeover, parsed.OwnershipMode);
        }

        [Test]
        public void RequestOwnership_SendsOperationAndChangesNothingLocally()
        {
            JoinRoom(2, 1);
            var changes = new List<(int View, int Owner, int Sender)>();
            _client.Events.OnOwnershipChangedRaw += (view, owner, sender) => changes.Add((view, owner, sender));

            _client.RequestOwnership(1005);
            var request = _transport.NextRequest();

            Assert.AreEqual(OpCode.RequestOwnership, request.OpCode);
            Assert.AreEqual(1005, RequestOwnershipRequest.FromParameters(request.Parameters).ViewId);
            Assert.AreEqual(0, changes.Count);
        }

        [Test]
        public void TransferOwnership_SendsOperationAndChangesNothingLocally()
        {
            JoinRoom(1, 1);
            var changes = 0;
            _client.Events.OnOwnershipChangedRaw += (_, _, _) => changes++;

            _client.TransferOwnership(1001, 2);
            var request = _transport.NextRequest();
            var parsed = TransferOwnershipRequest.FromParameters(request.Parameters);

            Assert.AreEqual(OpCode.TransferOwnership, request.OpCode);
            Assert.AreEqual(1001, parsed.ViewId);
            Assert.AreEqual(2, parsed.NewOwnerActorId);
            Assert.AreEqual(0, changes);
        }

        [Test]
        public void OwnershipChangedEvent_IsRaisedEvenWhenSenderIsLocalActor()
        {
            JoinRoom(2, 1);
            var changes = new List<(int View, int Owner, int Sender)>();
            _client.Events.OnOwnershipChangedRaw += (view, owner, sender) => changes.Add((view, owner, sender));

            _transport.SendEvent(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = 1005, NewOwnerActorId = 2, SenderActorId = 2 }.ToParameters());

            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual((1005, 2, 2), changes[0]);
        }

        [Test]
        public void OwnershipRequestedEvent_IsForwardedToOwnerCallbacks()
        {
            JoinRoom(1, 1);
            var requests = new List<(int View, int Requester)>();
            _client.Events.OnOwnershipRequestedRaw += (view, requester) => requests.Add((view, requester));

            _transport.SendEvent(EventCode.OwnershipRequested, new OwnershipRequestedData { ViewId = 1001, RequesterActorId = 2 }.ToParameters());

            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual((1001, 2), requests[0]);
        }

        [Test]
        public void ForbiddenResponse_RaisesOwnershipFailed()
        {
            JoinRoom(2, 1);
            var failures = new List<(int View, short Code, string Message)>();
            _client.Events.OnOwnershipFailedRaw += (view, code, message) => failures.Add((view, code, message));

            _client.TransferOwnership(1001, 2);
            _transport.Respond(_transport.NextRequest(), ReturnCode.Forbidden, null, "fixed ownership");

            Assert.AreEqual(1, failures.Count);
            Assert.AreEqual(1001, failures[0].View);
            Assert.AreEqual(ReturnCode.Forbidden, failures[0].Code);
        }

        [Test]
        public void ObjectNotFoundResponse_RaisesOwnershipFailed()
        {
            JoinRoom(2, 1);
            var failures = new List<short>();
            _client.Events.OnOwnershipFailedRaw += (_, code, _) => failures.Add(code);

            _client.RequestOwnership(1999);
            _transport.Respond(_transport.NextRequest(), ReturnCode.ObjectNotFound, null, "Unknown view id");

            CollectionAssert.AreEqual(new[] { ReturnCode.ObjectNotFound }, failures);
        }

        [Test]
        public void OkResponse_DoesNotRaiseOwnershipFailed()
        {
            JoinRoom(2, 1);
            var failures = 0;
            _client.Events.OnOwnershipFailedRaw += (_, _, _) => failures++;

            _client.RequestOwnership(1001);
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok);

            Assert.AreEqual(0, failures);
        }

        [Test]
        public void ErrorInfoEvent_DoesNotThrowAndRepeatsAreHandled()
        {
            JoinRoom(2, 1);
            var data = new ErrorInfoData { ErrorCode = ReturnCode.Forbidden, Message = "State of view 7 is owned by actor 1" };

            Assert.DoesNotThrow(() =>
            {
                for (var i = 0; i < 20; i++)
                {
                    _transport.SendEvent(EventCode.ErrorInfo, data.ToParameters());
                }
            });
            Assert.AreEqual(NetState.InRoom, _client.State);
        }

        [Test]
        public void RegisterSceneObjects_SendsViewIdsAndModes()
        {
            JoinRoom(1, 1);

            _client.RegisterSceneObjects(new[] { 1, 2 }, new byte[] { 1, 2 });
            var request = _transport.NextRequest();
            var parsed = RegisterSceneObjectsRequest.FromParameters(request.Parameters);

            Assert.AreEqual(OpCode.RegisterSceneObjects, request.OpCode);
            CollectionAssert.AreEqual(new[] { 1, 2 }, parsed.ViewIds);
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, parsed.Modes);
        }

        private void JoinRoom(int localActor, int master)
        {
            _client.Connect(new ConnectionOptions { UserId = "u", NickName = "Me" });
            _transport.SimulateConnected();
            var auth = new AuthenticateResponse { UserId = "u", NickName = "Me", ServerTimeTicks = 1000 };
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, auth.ToParameters());
            _client.CreateRoom("room");
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
                ServerTimeTicks = 1000
            };
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, response.ToParameters());
        }
    }
}
