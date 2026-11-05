# NetPulse for Unity

A Photon-like networking client for Unity 2021.3 LTS - 6000.x. Plain C# 9, no `Awaitable`, everything runs on the main thread,
IL2CPP safe. Talks to the NetPulse server (reliable UDP over LiteNetLib 1.3.5, vendored in `Runtime/ThirdParty`).

> The static entry point is called **`Pulse`** (not `NetPulse`): the `NetPulse` identifier is already taken by the
> `NetPulse.Common` namespace, and C# cannot have a type and a namespace with the same name visible from user code.

## Install

Add the package folder to your project (`Packages/com.netpulse.unity`) or use *Add package from disk* with `package.json`.
Open **Project Settings -> NetPulse** (or *NetPulse -> Create Settings Asset*) to create `Assets/Resources/NetPulseSettings.asset`.
Set host, port (default UDP 5055), AppId and AppVersion.
The *Basic* sample can be imported from the Package Manager and built with *NetPulse -> Samples -> Build Basic Sample Scene*.

## Quick start

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public class Demo : MonoBehaviour
{
    [SerializeField] private GameObject _playerPrefab;

    private async void Start()
    {
        Pulse.NickName = "Alice";
        await Pulse.Connect();
        var room = await Pulse.JoinOrCreateRoom("arena", new RoomOptions { MaxPlayers = 4 });
        Pulse.Instantiate(_playerPrefab, Vector3.zero, Quaternion.identity);
    }
}
```

`await` works on every `NetOperation`; failures throw `NetPulseException` (with `ReturnCode`). Alternatives:

```csharp
Pulse.JoinRoom("arena").Completed += op => Debug.Log(op.IsSuccess ? "ok" : op.Error.ToString());
yield return Pulse.Connect();            // inside a coroutine
```

## Callbacks

Either C# events:

```csharp
Pulse.Events.OnJoinedRoom += () => Debug.Log("joined");
Pulse.Events.OnPlayerEnteredRoom += player => Debug.Log(player.NickName);
```

or derive from `NetPulseCallbacks` (registers in `OnEnable`, unregisters in `OnDisable`) and override only what you need,
or implement `INetPulseConnectionCallbacks`, `ILobbyCallbacks`, `IMatchmakingCallbacks`, `IRoomCallbacks`, `IOnEventCallback`
and call `Pulse.AddCallbackTarget(this)` / `RemoveCallbackTarget(this)`.

## API overview

| Area | API |
|---|---|
| Connection | `Pulse.Connect()`, `Connect(settings)`, `Disconnect()`, `State`, `StateChanged`, `IsConnected`, `ServerTime`, `RoundTripTimeMs`, `UseTransport(INetTransport)` |
| Lobby | `JoinLobby()`, `LeaveLobby()`, `GetRoomList()`, `LobbyRooms`, `Events.OnRoomListUpdate` |
| Rooms | `CreateRoom`, `JoinRoom`, `JoinOrCreateRoom`, `JoinRandomRoom`, `LeaveRoom(becomeInactive)`, `CurrentRoom`, `LocalPlayer`, `IsMasterClient` |
| Properties | `room.SetProperty(key, value)`, `player.SetProperty(...)`, `SetProperties(delta, expected)` (compare-and-set), `null` removes a key |
| Events | `Pulse.RaiseEvent(code 0-199, data, new RaiseEventOptions { Receivers, TargetActors, Caching, InterestGroup, Delivery })` |
| Objects | `Pulse.Instantiate(prefab, pos, rot, data)`, `Pulse.Destroy`, `NetworkObject`, `NetworkBehaviour`, `NetworkPrefabRegistry` |
| RPC | `[NetRpc] void Hit(int dmg)` + `CallRpc(nameof(Hit), RpcTarget.All, 10)`; add `NetRpcInfo` as the last parameter to get the sender |
| State sync | `[Synced]` fields (primitives, strings, enums, Unity vectors/colors) and/or `OnSerialize(NetStream)`; only changed data is sent |
| Ownership | `OwnershipMode.Fixed / Takeover / Request`, `IsMine`, `Owner`, `RequestOwnership()`, `TransferOwnership(player)`, `ApproveOwnership(player)`, `OwnershipChanged`, `Events.OnOwnershipRequest`, `Events.OnOwnershipFailed` |
| Authentication | `Pulse.AuthToken = "..."` (or `ConnectionOptions.AuthToken`) before `Connect()` |
| Components | `NetworkTransform` (interpolation on server time), `NetworkRigidbody`, `NetworkAnimator` |
| Offline | `Pulse.OfflineMode = true` before `Connect()` - the same API runs against a local room |

### Networked objects

1. Put a `NetworkObject` (and your `NetworkBehaviour`s) on the root of a prefab. Prefabs with a `NetworkObject` are added to the
   `NetworkPrefabRegistry` automatically (also before every build; manual: *NetPulse -> Rebuild Prefab Registry*).
2. `Pulse.Instantiate(prefabReference, ...)` spawns it on every client (cached for late joiners). The overload taking a name is kept for compatibility.
3. Scene objects: tick *Scene Object* on the `NetworkObject`; they belong to the master client and are synchronised by view id.

```csharp
public class Health : NetworkBehaviour
{
    [Synced] private int _hp = 100;

    public void Damage(int amount) => CallRpc(nameof(TakeDamage), RpcTarget.All, amount);

    [NetRpc]
    private void TakeDamage(int amount, NetRpcInfo info) => _hp -= amount;
}
```

### Ownership

The server keeps a registry of network objects and enforces ownership, so the client never changes an owner by itself:
`IsMine` and `Owner` change only when the server's `OwnershipChanged` event arrives, including for requests you sent yourself.

| Mode | Who can become the owner |
|---|---|
| `Fixed` | nobody; the creator keeps the object (`RequestOwnership`/`TransferOwnership` are refused) |
| `Takeover` | anyone: `RequestOwnership()` takes it immediately; the owner or the master can also assign it with `TransferOwnership(player)` |
| `Request` | `RequestOwnership()` asks the owner (callback below); only the owner or the master can assign with `TransferOwnership(player)` |

```csharp
public sealed class Crate : NetworkBehaviour
{
    public override void OnOwnershipRequest(Player requester)
    {
        if (requester.GetProperty("team", 0) == Owner.GetProperty("team", 0))
        {
            NetworkObject.ApproveOwnership(requester);
        }
    }

    public override void OnOwnerChanged(Player previous, Player current) => Debug.Log($"Now owned by {current?.NickName}");
}
```

* The mode is the `OwnershipMode` field on the `NetworkObject` and is sent with the spawn, so the server knows it.
* A request is delivered to the owner as `OnOwnershipRequest` (a virtual on `NetworkBehaviour`, plus `Pulse.Events.OnOwnershipRequest` and `IOwnershipCallbacks`).
  Denying means doing nothing: the request expires on the server (5 s by default) and nobody is told.
* Rejections (`Forbidden`, `ObjectNotFound`) are logged as warnings and raised as `Pulse.Events.OnOwnershipFailed(networkObject, returnCode, message)`; the local state stays as it was.
  State, destroy or spawn messages that the server rejects (for example a `StateSync` from a non-owner) produce at most one warning per second.
* Only the owner sends state; `NetworkObject.Destroy` works for the owner or the master client.
* Scene objects belong to the master client (`OwnerActorId == 0` means "the master"). The master registers their modes with the server after joining
  and after becoming master, so a `Takeover` scene object can be grabbed by anyone. Scene objects of a player who leaves return to the master.
* When a player leaves, only the objects they own at that moment are destroyed; objects they handed over survive.

The *Basic* sample contains a shared **Takeover Cube**: press `E` to take it, move it with the arrow keys.

### Authentication

By default any client is accepted. If the server is configured with an auth plugin, pass the token before connecting:

```csharp
Pulse.AuthToken = myBackendIssuedToken;
await Pulse.Connect();
```

The server replies with the authoritative `UserId` and `NickName`; the client adopts them (`Pulse.UserId`, `Pulse.Client.NickName`). A rejected token
fails the `Connect()` operation with `AuthenticationFailed` and the server drops the connection.

### IL2CPP / stripping

You do not need `[Preserve]` or your own `link.xml` for `[NetRpc]` methods, `[Synced]` fields, `NetworkBehaviour` subclasses or
`INetSerializable` types. Two layers keep them alive under IL2CPP managed stripping (any level):

1. `[NetRpc]` and `[Synced]` derive from `UnityEngine.Scripting.PreserveAttribute`, which Unity documents as subclassable and honoured by the linker.
2. At build time `NetPulseLinkerProcessor` (an `IUnityLinkerProcessor`) finds every `[NetRpc]` method, `[Synced]` field, non-abstract
   `NetworkBehaviour` and `INetSerializable` type in your player assemblies via `TypeCache` and writes `Library/NetPulse/link.xml`
   with exactly those members (plus `OnSerialize` overrides). The NetPulse assemblies themselves are preserved there too, because Unity
   does not read `link.xml` files that live inside packages.

Inspect the result with **NetPulse -> Tools -> Preview IL2CPP link.xml**. If a method is still missing at runtime, the error names the
type and method and points back to this mechanism.

## Coming from PUN

| PUN 2 | NetPulse |
|---|---|
| `PhotonNetwork.ConnectUsingSettings()` | `Pulse.Connect()` |
| `PhotonNetwork.NickName` | `Pulse.NickName` |
| `PhotonNetwork.JoinOrCreateRoom(name, options, lobby)` | `await Pulse.JoinOrCreateRoom(name, options)` |
| `PhotonNetwork.CreateRoom / JoinRoom / JoinRandomRoom` | `Pulse.CreateRoom / JoinRoom / JoinRandomRoom` (return `NetOperation<Room>`) |
| `PhotonNetwork.LeaveRoom()` / `Disconnect()` | `Pulse.LeaveRoom()` / `Pulse.Disconnect()` |
| `PhotonNetwork.CurrentRoom`, `LocalPlayer`, `IsMasterClient` | `Pulse.CurrentRoom`, `LocalPlayer`, `IsMasterClient` |
| `PhotonNetwork.Instantiate("PrefabName", ...)` (Resources) | `Pulse.Instantiate(prefabReference, ...)` (registry, no Resources) |
| `PhotonNetwork.Destroy(go)` | `Pulse.Destroy(go)` |
| `PhotonNetwork.Time` | `Pulse.ServerTime` |
| `PhotonNetwork.OfflineMode` | `Pulse.OfflineMode` |
| `PhotonNetwork.RaiseEvent(code, data, options, sendOptions)` | `Pulse.RaiseEvent(code, data, new RaiseEventOptions { ... })` |
| `MonoBehaviourPunCallbacks` | `NetPulseCallbacks` |
| `IConnectionCallbacks / IMatchmakingCallbacks / IInRoomCallbacks / ILobbyCallbacks` | `INetPulseConnectionCallbacks / IMatchmakingCallbacks / IRoomCallbacks / ILobbyCallbacks` |
| `OnEvent(EventData)` | `IOnEventCallback.OnEvent(code, senderActorId, data)` |
| `PhotonView` | `NetworkObject` |
| `MonoBehaviourPun` / `photonView.IsMine` | `NetworkBehaviour` / `IsMine` |
| `[PunRPC]` + `photonView.RPC("Name", RpcTarget.All, args)` | `[NetRpc]` + `CallRpc(nameof(Name), RpcTarget.All, args)` |
| `RpcTarget.AllBuffered / OthersBuffered / MasterClient` | same names |
| `IPunObservable.OnPhotonSerializeView(stream, info)` | `NetworkBehaviour.OnSerialize(NetStream stream)` |
| `stream.SendNext / ReceiveNext` | `stream.SendNext / ReceiveNext<T>` or symmetric `stream.Serialize(ref value)` |
| `PhotonTransformView` / `PhotonRigidbodyView` / `PhotonAnimatorView` | `NetworkTransform` / `NetworkRigidbody` / `NetworkAnimator` |
| `OwnershipOption.Fixed / Takeover / Request`, `photonView.RequestOwnership()` | `OwnershipMode`, `networkObject.RequestOwnership()` (server-enforced, applied on `OwnershipChanged`) |
| `OnOwnershipRequest(PhotonView, Player)` + `TransferOwnership(requester)` | `OnOwnershipRequest(Player)` + `networkObject.ApproveOwnership(requester)` |
| `AuthenticationValues` / `PhotonNetwork.AuthValues` | `Pulse.AuthToken` (server auth plugin validates it) |
| `Hashtable` custom properties | `PropertyTable`; `player.SetProperty(key, value)` |
| Coroutine polling / callbacks only | `await`, `yield return`, `.Completed` on every operation |

## Server and protocol

The client speaks the NetPulse protocol defined in `NetPulse.Common` (source is synced into `Runtime/Common`, do not edit it).
Unity types are registered at start-up on type codes 100-105 (`Vector2`, `Vector3`, `Quaternion`, `Color`, `Vector2Int`, `Vector3Int`),
so they can be used in events, RPC arguments, room/player properties and `OnSerialize`.

`Pulse.UseTransport(INetTransport)` replaces the transport (for example a WebSocket transport for WebGL) before connecting.

## Editor tools

* **Project Settings -> NetPulse** - connection and rate settings, prefab registry.
* **NetPulse -> Open Hub** - live connection status, room, players, ping and traffic in Play Mode.
* **NetworkObject inspector** - ownership mode, scene object id, runtime view id and owner.

## Tests

Tests live in `Tests/Runtime` (serialization, `NetPulseClient` on a fake transport, `NetOperation`, ownership) and `Tests/Editor`.
`NetworkObjectOwnershipTests` create GameObjects and use `[UnityTest]`, so run them from the Test Runner in Play Mode.
Enable them by adding the package to `testables` in `Packages/manifest.json` (the package is embedded, so it is already testable).

## Known limitations

* `SendRate` caps the state-sync tick together with `SerializationRate` (the effective rate is the lower of the two).
* Buffered RPCs of destroyed objects stay in the room cache until their sender leaves.
* Ownership is validated by the server; a client built for an older server (before ownership enforcement) must be updated, otherwise its objects stay `Fixed` and cannot change owner.
