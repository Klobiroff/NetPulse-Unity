using System.Collections.Generic;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    public interface INetPulseConnectionCallbacks
    {
        void OnConnected();

        void OnDisconnected(string cause);
    }

    public interface ILobbyCallbacks
    {
        void OnJoinedLobby();

        void OnLeftLobby();

        void OnRoomListUpdate(IReadOnlyList<RoomInfo> rooms);
    }

    public interface IMatchmakingCallbacks
    {
        void OnCreatedRoom();

        void OnCreateRoomFailed(short returnCode, string message);

        void OnJoinedRoom();

        void OnJoinRoomFailed(short returnCode, string message);

        void OnJoinRandomFailed(short returnCode, string message);

        void OnLeftRoom();
    }

    public interface IRoomCallbacks
    {
        void OnPlayerEnteredRoom(Player player);

        void OnPlayerLeftRoom(Player player);

        void OnRoomPropertiesChanged(PropertyTable changed);

        void OnPlayerPropertiesChanged(Player player, PropertyTable changed);

        void OnMasterClientSwitched(Player newMaster);
    }

    public interface IOwnershipCallbacks
    {
        void OnOwnershipRequest(NetworkObject networkObject, Player requester);

        void OnOwnershipChanged(NetworkObject networkObject, Player previous, Player current);

        void OnOwnershipFailed(NetworkObject networkObject, short returnCode, string message);
    }

    public interface IOnEventCallback
    {
        void OnEvent(byte eventCode, int senderActorId, object data);
    }

    public abstract class NetPulseCallbacks : MonoBehaviour,
        INetPulseConnectionCallbacks, ILobbyCallbacks, IMatchmakingCallbacks, IRoomCallbacks, IOwnershipCallbacks, IOnEventCallback
    {
        protected virtual void OnEnable() => Pulse.AddCallbackTarget(this);

        protected virtual void OnDisable() => Pulse.RemoveCallbackTarget(this);

        public virtual void OnConnected()
        {
        }

        public virtual void OnDisconnected(string cause)
        {
        }

        public virtual void OnJoinedLobby()
        {
        }

        public virtual void OnLeftLobby()
        {
        }

        public virtual void OnRoomListUpdate(IReadOnlyList<RoomInfo> rooms)
        {
        }

        public virtual void OnCreatedRoom()
        {
        }

        public virtual void OnCreateRoomFailed(short returnCode, string message)
        {
        }

        public virtual void OnJoinedRoom()
        {
        }

        public virtual void OnJoinRoomFailed(short returnCode, string message)
        {
        }

        public virtual void OnJoinRandomFailed(short returnCode, string message)
        {
        }

        public virtual void OnLeftRoom()
        {
        }

        public virtual void OnPlayerEnteredRoom(Player player)
        {
        }

        public virtual void OnPlayerLeftRoom(Player player)
        {
        }

        public virtual void OnRoomPropertiesChanged(PropertyTable changed)
        {
        }

        public virtual void OnPlayerPropertiesChanged(Player player, PropertyTable changed)
        {
        }

        public virtual void OnMasterClientSwitched(Player newMaster)
        {
        }

        public virtual void OnOwnershipRequest(NetworkObject networkObject, Player requester)
        {
        }

        public virtual void OnOwnershipChanged(NetworkObject networkObject, Player previous, Player current)
        {
        }

        public virtual void OnOwnershipFailed(NetworkObject networkObject, short returnCode, string message)
        {
        }

        public virtual void OnEvent(byte eventCode, int senderActorId, object data)
        {
        }
    }
}
