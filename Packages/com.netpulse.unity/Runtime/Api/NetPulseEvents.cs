using System;
using System.Collections.Generic;
using NetPulse.Common;

namespace NetPulse
{
    public sealed class NetPulseEvents
    {
        public event Action<NetState> OnStateChanged;

        public event Action OnConnected;

        public event Action<string> OnDisconnected;

        public event Action OnJoinedLobby;

        public event Action OnLeftLobby;

        public event Action<IReadOnlyList<RoomInfo>> OnRoomListUpdate;

        public event Action OnCreatedRoom;

        public event Action<short, string> OnCreateRoomFailed;

        public event Action OnJoinedRoom;

        public event Action<short, string> OnJoinRoomFailed;

        public event Action<short, string> OnJoinRandomFailed;

        public event Action OnLeftRoom;

        public event Action<Player> OnPlayerEnteredRoom;

        public event Action<Player> OnPlayerLeftRoom;

        public event Action<PropertyTable> OnRoomPropertiesChanged;

        public event Action<Player, PropertyTable> OnPlayerPropertiesChanged;

        public event Action<Player> OnMasterClientSwitched;

        public event Action<byte, int, object> OnEvent;

        internal event Action<byte, int, object, int> OnRoomEvent;

        public event Action<NetworkObject, Player, Player> OnOwnershipChanged;

        public event Action<NetworkObject, Player> OnOwnershipRequest;

        public event Action<NetworkObject, short, string> OnOwnershipFailed;

        internal event Action<int, int, int> OnOwnershipChangedRaw;

        internal event Action<int, int> OnOwnershipRequestedRaw;

        internal event Action<int, short, string> OnOwnershipFailedRaw;

        internal void RaiseStateChanged(NetState state) => OnStateChanged?.Invoke(state);

        internal void RaiseConnected() => OnConnected?.Invoke();

        internal void RaiseDisconnected(string cause) => OnDisconnected?.Invoke(cause);

        internal void RaiseJoinedLobby() => OnJoinedLobby?.Invoke();

        internal void RaiseLeftLobby() => OnLeftLobby?.Invoke();

        internal void RaiseRoomListUpdate(IReadOnlyList<RoomInfo> rooms) => OnRoomListUpdate?.Invoke(rooms);

        internal void RaiseCreatedRoom() => OnCreatedRoom?.Invoke();

        internal void RaiseCreateRoomFailed(short code, string message) => OnCreateRoomFailed?.Invoke(code, message);

        internal void RaiseJoinedRoom() => OnJoinedRoom?.Invoke();

        internal void RaiseJoinRoomFailed(short code, string message) => OnJoinRoomFailed?.Invoke(code, message);

        internal void RaiseJoinRandomFailed(short code, string message) => OnJoinRandomFailed?.Invoke(code, message);

        internal void RaiseLeftRoom() => OnLeftRoom?.Invoke();

        internal void RaisePlayerEntered(Player player) => OnPlayerEnteredRoom?.Invoke(player);

        internal void RaisePlayerLeft(Player player) => OnPlayerLeftRoom?.Invoke(player);

        internal void RaiseRoomProperties(PropertyTable changed) => OnRoomPropertiesChanged?.Invoke(changed);

        internal void RaisePlayerProperties(Player player, PropertyTable changed) => OnPlayerPropertiesChanged?.Invoke(player, changed);

        internal void RaiseMasterSwitched(Player master) => OnMasterClientSwitched?.Invoke(master);

        internal void RaiseEvent(byte code, int sender, object data) => OnEvent?.Invoke(code, sender, data);

        internal void RaiseRoomEvent(byte code, int sender, object data, int viewId) => OnRoomEvent?.Invoke(code, sender, data, viewId);

        internal void RaiseOwnershipChanged(int viewId, int newOwner, int sender) => OnOwnershipChangedRaw?.Invoke(viewId, newOwner, sender);

        internal void RaiseOwnershipRequested(int viewId, int requester) => OnOwnershipRequestedRaw?.Invoke(viewId, requester);

        internal void RaiseOwnershipFailed(int viewId, short code, string message) => OnOwnershipFailedRaw?.Invoke(viewId, code, message);

        internal void PublishOwnershipChanged(NetworkObject networkObject, Player previous, Player current) => OnOwnershipChanged?.Invoke(networkObject, previous, current);

        internal void PublishOwnershipRequest(NetworkObject networkObject, Player requester) => OnOwnershipRequest?.Invoke(networkObject, requester);

        internal void PublishOwnershipFailed(NetworkObject networkObject, short code, string message) => OnOwnershipFailed?.Invoke(networkObject, code, message);
    }
}
