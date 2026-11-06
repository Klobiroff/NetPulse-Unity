using System;
using System.Collections.Generic;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    internal sealed class CallbackRegistry
    {
        private readonly List<object> _targets = new();

        public CallbackRegistry(NetPulseEvents events)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            events.OnConnected += () => Each<INetPulseConnectionCallbacks>(static t => t.OnConnected());
            events.OnDisconnected += cause => Each<INetPulseConnectionCallbacks>(t => t.OnDisconnected(cause));
            events.OnJoinedLobby += () => Each<ILobbyCallbacks>(static t => t.OnJoinedLobby());
            events.OnLeftLobby += () => Each<ILobbyCallbacks>(static t => t.OnLeftLobby());
            events.OnRoomListUpdate += rooms => Each<ILobbyCallbacks>(t => t.OnRoomListUpdate(rooms));
            events.OnCreatedRoom += () => Each<IMatchmakingCallbacks>(static t => t.OnCreatedRoom());
            events.OnCreateRoomFailed += (code, message) => Each<IMatchmakingCallbacks>(t => t.OnCreateRoomFailed(code, message));
            events.OnJoinedRoom += () => Each<IMatchmakingCallbacks>(static t => t.OnJoinedRoom());
            events.OnJoinRoomFailed += (code, message) => Each<IMatchmakingCallbacks>(t => t.OnJoinRoomFailed(code, message));
            events.OnJoinRandomFailed += (code, message) => Each<IMatchmakingCallbacks>(t => t.OnJoinRandomFailed(code, message));
            events.OnLeftRoom += () => Each<IMatchmakingCallbacks>(static t => t.OnLeftRoom());
            events.OnPlayerEnteredRoom += player => Each<IRoomCallbacks>(t => t.OnPlayerEnteredRoom(player));
            events.OnPlayerLeftRoom += player => Each<IRoomCallbacks>(t => t.OnPlayerLeftRoom(player));
            events.OnRoomPropertiesChanged += changed => Each<IRoomCallbacks>(t => t.OnRoomPropertiesChanged(changed));
            events.OnPlayerPropertiesChanged += (player, changed) => Each<IRoomCallbacks>(t => t.OnPlayerPropertiesChanged(player, changed));
            events.OnMasterClientSwitched += master => Each<IRoomCallbacks>(t => t.OnMasterClientSwitched(master));
            events.OnOwnershipRequest += (networkObject, requester) => Each<IOwnershipCallbacks>(t => t.OnOwnershipRequest(networkObject, requester));
            events.OnOwnershipChanged += (networkObject, previous, current) => Each<IOwnershipCallbacks>(t => t.OnOwnershipChanged(networkObject, previous, current));
            events.OnOwnershipFailed += (networkObject, code, message) => Each<IOwnershipCallbacks>(t => t.OnOwnershipFailed(networkObject, code, message));
            events.OnNetworkSceneLoaded += sceneName => Each<ISceneCallbacks>(t => t.OnNetworkSceneLoaded(sceneName));
            events.OnEvent += (code, sender, data) => Each<IOnEventCallback>(t => t.OnEvent(code, sender, data));
        }

        public void Add(object target)
        {
            if (target != null && !_targets.Contains(target))
            {
                _targets.Add(target);
            }
        }

        public void Remove(object target) => _targets.Remove(target);

        private void Each<T>(Action<T> action) where T : class
        {
            if (_targets.Count == 0)
            {
                return;
            }

            var snapshot = _targets.ToArray();
            foreach (var target in snapshot)
            {
                if (target is not T typed || (target is UnityEngine.Object unityObject && unityObject == null))
                {
                    continue;
                }

                try
                {
                    action(typed);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }
    }
}
