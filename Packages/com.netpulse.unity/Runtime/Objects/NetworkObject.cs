using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetPulse
{
    internal sealed class SpawnContext
    {
        public SpawnContext(int viewId, int ownerActorId, int prefabId, object data)
        {
            ViewId = viewId;
            OwnerActorId = ownerActorId;
            PrefabId = prefabId;
            Data = data;
        }

        public int ViewId { get; }

        public int OwnerActorId { get; }

        public int PrefabId { get; }

        public object Data { get; }
    }

    [DisallowMultipleComponent]
    public sealed class NetworkObject : MonoBehaviour
    {
        private static SpawnContext PendingSpawn;

        [SerializeField] private OwnershipMode _ownershipMode = OwnershipMode.Fixed;
        [SerializeField] private int _sceneId = 0;
        [SerializeField] private byte _interestGroup = 0;

        private NetworkBehaviour[] _behaviours = Array.Empty<NetworkBehaviour>();
        private int _viewId;
        private bool _registered;

        public event Action<NetworkObject, Player, Player> OwnershipChanged;

        public OwnershipMode OwnershipMode
        {
            get => _ownershipMode;
            set => _ownershipMode = value;
        }

        public int SceneId => _sceneId;

        public bool IsSceneObject => _sceneId != 0;

        public byte InterestGroup => _interestGroup;

        public int ViewId => _viewId;

        public int PrefabId { get; private set; }

        public object InstantiationData { get; private set; }

        public bool IsSpawned => _registered;

        public int OwnerActorId { get; internal set; }

        public IReadOnlyList<NetworkBehaviour> Behaviours => _behaviours;

        public int EffectiveOwnerActorId
        {
            get
            {
                if (OwnerActorId != 0 || !IsSceneObject)
                {
                    return OwnerActorId;
                }

                var room = Pulse.CurrentRoom;
                return room != null ? room.MasterClientId : 0;
            }
        }

        public Player Owner => Pulse.CurrentRoom?.GetPlayer(EffectiveOwnerActorId);

        public bool IsMine => !Pulse.IsInRoom || EffectiveOwnerActorId == Pulse.LocalActorId;

        public void RequestOwnership() => Pulse.Objects.RequestOwnership(this);

        public void TransferOwnership(Player newOwner) => Pulse.Objects.TransferOwnership(this, newOwner ?? throw new ArgumentNullException(nameof(newOwner)));

        public void ApproveOwnership(Player requester) => Pulse.Objects.ApproveOwnership(this, requester ?? throw new ArgumentNullException(nameof(requester)));

        public void Destroy() => Pulse.Destroy(this);

        internal static void SetPendingSpawn(SpawnContext context) => PendingSpawn = context;

        internal void Spawn()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            foreach (var behaviour in _behaviours)
            {
                behaviour.OnNetworkSpawned();
            }
        }

        internal void Despawn()
        {
            if (!_registered)
            {
                return;
            }

            _registered = false;
            foreach (var behaviour in _behaviours)
            {
                if (behaviour != null)
                {
                    behaviour.OnNetworkDespawned();
                }
            }
        }

        internal void ChangeOwner(int newOwnerActorId)
        {
            var room = Pulse.CurrentRoom;
            var previous = Owner;
            OwnerActorId = newOwnerActorId;
            var current = room?.GetPlayer(newOwnerActorId);
            foreach (var behaviour in _behaviours)
            {
                behaviour.OnOwnerChanged(previous, current);
                behaviour.ForceFullSync();
            }

            OwnershipChanged?.Invoke(this, previous, current);
            Pulse.ClientOrNull?.Events.PublishOwnershipChanged(this, previous, current);
        }

        internal void ForceFullSync()
        {
            foreach (var behaviour in _behaviours)
            {
                behaviour.ForceFullSync();
            }
        }

        private void Awake()
        {
            _behaviours = GetComponentsInChildren<NetworkBehaviour>(true);
            for (var i = 0; i < _behaviours.Length; i++)
            {
                _behaviours[i].BehaviourIndex = i;
            }

            if (PendingSpawn != null)
            {
                _viewId = PendingSpawn.ViewId;
                OwnerActorId = PendingSpawn.OwnerActorId;
                PrefabId = PendingSpawn.PrefabId;
                InstantiationData = PendingSpawn.Data;
                PendingSpawn = null;
                return;
            }

            if (IsSceneObject)
            {
                _viewId = _sceneId;
            }
        }

        private void OnEnable()
        {
            if (IsSceneObject && !_registered)
            {
                Pulse.Objects.RegisterSceneObject(this);
            }
        }

        private void OnDestroy()
        {
            Pulse.ObjectsOrNull?.Unregister(this);
            Despawn();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => PendingSpawn = null;
    }
}
