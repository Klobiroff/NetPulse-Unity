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
        [SerializeField] private bool _persistAcrossScenes;

        private NetworkBehaviour[] _behaviours = Array.Empty<NetworkBehaviour>();
        private int _viewId;
        private bool _registered;
        private bool _spawnNotified;
        private bool _awaitingInitialState;
        private double _initialStateDeadline;
        private string _statName;

        public event Action<NetworkObject, Player, Player> OwnershipChanged;

        public OwnershipMode OwnershipMode
        {
            get => _ownershipMode;
            set => _ownershipMode = value;
        }

        public bool PersistAcrossScenes
        {
            get => _persistAcrossScenes;
            set => _persistAcrossScenes = value;
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

        internal bool IsSpawnNotified => _spawnNotified;

        internal bool IsAwaitingInitialState => _awaitingInitialState;

        internal double InitialStateDeadline => _initialStateDeadline;

        internal string StatName => _statName ??= $"{_viewId} {name}";

        public void RequestOwnership() => Pulse.Objects.RequestOwnership(this);

        public NetOperation<bool> RequestOwnershipAsync(float timeoutSeconds = 5f) => Pulse.Objects.RequestOwnershipAsync(this, timeoutSeconds);

        public void TransferOwnership(Player newOwner) => Pulse.Objects.TransferOwnership(this, newOwner ?? throw new ArgumentNullException(nameof(newOwner)));

        public NetOperation<bool> TransferOwnershipAsync(Player newOwner, float timeoutSeconds = 5f) =>
            Pulse.Objects.TransferOwnershipAsync(this, newOwner ?? throw new ArgumentNullException(nameof(newOwner)), timeoutSeconds);

        public void ApproveOwnership(Player requester) => Pulse.Objects.ApproveOwnership(this, requester ?? throw new ArgumentNullException(nameof(requester)));

        public void Destroy() => Pulse.Destroy(this);

        internal static void SetPendingSpawn(SpawnContext context) => PendingSpawn = context;

        internal void Spawn(double initialStateDeadline)
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            if (_persistAcrossScenes && !IsSceneObject && transform.parent == null)
            {
                DontDestroyOnLoad(gameObject);
            }

            if (!IsMine && HasInitialState())
            {
                _awaitingInitialState = true;
                _initialStateDeadline = initialStateDeadline;
                return;
            }

            NotifySpawned();
        }

        internal void CompleteInitialState()
        {
            if (!_awaitingInitialState)
            {
                return;
            }

            _awaitingInitialState = false;
            NotifySpawned();
        }

        internal void Despawn()
        {
            if (!_registered)
            {
                return;
            }

            _registered = false;
            _awaitingInitialState = false;
            if (!_spawnNotified)
            {
                return;
            }

            _spawnNotified = false;
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
            var wasMine = IsMine;
            OwnerActorId = newOwnerActorId;
            var current = room?.GetPlayer(newOwnerActorId);
            var isMine = IsMine;
            foreach (var behaviour in _behaviours)
            {
                if (behaviour == null)
                {
                    continue;
                }

                behaviour.OnOwnerChanged(previous, current);
                if (!wasMine && isMine)
                {
                    behaviour.OnOwnershipGained();
                }
                else if (wasMine && !isMine)
                {
                    behaviour.OnOwnershipLost();
                }

                behaviour.ForceFullSync();
            }

            OwnershipChanged?.Invoke(this, previous, current);
            Pulse.ClientOrNull?.Events.PublishOwnershipChanged(this, previous, current);
        }

        internal void NotifyMasterChanged(Player previous, Player current)
        {
            var wasMine = previous != null && IsSceneObject && OwnerActorId == 0 && previous.IsLocal;
            var isMine = current != null && IsSceneObject && OwnerActorId == 0 && current.IsLocal;
            foreach (var behaviour in _behaviours)
            {
                if (behaviour == null)
                {
                    continue;
                }

                behaviour.OnMasterChanged(previous, current);
                if (!wasMine && isMine)
                {
                    behaviour.OnOwnershipGained();
                }
                else if (wasMine && !isMine)
                {
                    behaviour.OnOwnershipLost();
                }
            }
        }

        internal void ForceFullSync()
        {
            foreach (var behaviour in _behaviours)
            {
                behaviour.ForceFullSync();
            }
        }

        private bool HasInitialState()
        {
            foreach (var behaviour in _behaviours)
            {
                if (behaviour != null && behaviour.HasInitialState)
                {
                    return true;
                }
            }

            return false;
        }

        private void NotifySpawned()
        {
            if (_spawnNotified)
            {
                return;
            }

            _spawnNotified = true;
            foreach (var behaviour in _behaviours)
            {
                if (behaviour != null)
                {
                    behaviour.OnNetworkSpawned();
                }
            }
        }

        private void Awake()
        {
            _behaviours = GetComponentsInChildren<NetworkBehaviour>(true);
            for (var i = 0; i < _behaviours.Length; i++)
            {
                _behaviours[i].BehaviourIndex = i;
                _behaviours[i].CollectSyncVars();
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
