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

## The 0.3 API

Everyday code needs no strings, byte codes or type registration. A Roslyn source generator (`Runtime/Analyzers/NetPulse.Generators.dll`,
active for `NetPulse.Runtime` and every assembly that references it) writes the serialization and dispatch code at compile time,
so nothing is found by reflection at runtime. Classes that declare `[NetRpc]`, `SyncVar<T>`, `[NetSerializable]` or `INetMessage` must be `partial`.
`sealed partial` works too: there the generated `Rpc` property is private.

### RPCs

```csharp
public partial class Hero : NetworkBehaviour
{
    [NetRpc(RpcTarget.Others)]
    private void Hit(int damage, Vector3 at, NetRpcInfo info) => Debug.Log($"{info.Sender.NickName} hit for {damage}");

    [NetRpc(RpcTarget.MasterClient, Delivery = DeliveryMode.Unreliable, OwnerOnly = true)]
    private void Report(string text) { }

    private void Update()
    {
        Rpc.Hit(10, transform.position);                    // target from the attribute
        Rpc.To(RpcTarget.All).Hit(10, transform.position);  // explicit target
        Rpc.To(somePlayer).Report("hi");                    // one player
    }
}
```

Supported parameter types: `bool, byte, sbyte, short, ushort, int, uint, long, ulong, float, double, char, string, Guid`, enums, Unity vectors/`Quaternion`/colors/`Rect`,
`Player`, `NetworkObject`, `[NetSerializable]`/`INetMessage` types and `T[]` / `List<T>` of those. A trailing `NetRpcInfo` parameter receives the sender.
RPC overloads are not allowed. `OwnerOnly` RPCs called by a non-owner log a warning and are not sent.

### SyncVar

```csharp
public partial class Hero : NetworkBehaviour
{
    private readonly SyncVar<int> _health = new(100);                               // only the owner writes
    private readonly SyncVar<string> _title = new("", SyncPermission.Master);       // only the master writes

    public override void OnNetworkSpawned() => _health.Changed += (previous, current) => Debug.Log($"{previous} -> {current}");
}
```

`Owner` variables travel as a delta inside the object's state sync; `Master` variables use their own event. Late joiners receive full values.
A write without permission is ignored and logged once per field. `SyncVar<T>` converts implicitly to `T`.

### Typed messages and serializable types

```csharp
public partial struct ChatMessage : INetMessage { public string Text; public int Channel; }

using var subscription = Pulse.On<ChatMessage>((message, sender) => Debug.Log($"{sender.NickName}: {message.Text}"));
Pulse.Send(new ChatMessage { Text = "hi" });                                // RpcTarget.Others by default
Pulse.Send(new ChatMessage { Text = "all" }, RpcTarget.All);
Pulse.Send(new ChatMessage { Text = "psst" }, somePlayer);
Pulse.Send(new ChatMessage { Text = "once" }, RpcTarget.OthersBuffered, new MessageOptions { Delivery = DeliveryMode.ReliableOrdered });

[NetSerializable] public partial struct Stats { public int Hp; public bool Alive; }   // usable in RPCs, messages and properties
```

Public fields and public get/set auto-properties are serialized in declaration order. A `[NetSerializable]` class writes a leading null flag and needs a parameterless constructor.
Null and empty collections look the same on the wire.

### Typed room and player keys

```csharp
static readonly RoomKey<int> Score = new("score");
static readonly PlayerKey<string> Mood = new("mood");

room.Set(Score, 5);
var score = room.Get(Score);
await room.SetAsync(Score, 6, expected: 5);                                   // compare-and-set; fails with PropertyCasFailed
using var watch = room.OnChanged(Score, value => Debug.Log(value));
player.Set(Mood, "happy");  player.OnChanged(Mood, (p, value) => { });
```

### Network tick

`Pulse.TickRate` (from the server), `Pulse.Tick` (`floor(ServerTime * TickRate)`) and `Pulse.TickDelta`. `NetworkBehaviour.OnNetworkTick(long tick)` runs at that rate with
a catch-up loop of at most 5 ticks per frame. `NetPulseSettings.SerializationRate = 0` follows the tick rate, and state is sent on tick boundaries.

### Lifecycle and order guarantees

1. `Awake` / `OnEnable` run first. The object is registered and its `SyncVar`s are collected in `NetworkObject.Awake`.
2. `OnNetworkSpawned` is called exactly once. For objects owned by another player it waits until the initial state (`SyncVar`, `[Synced]`, `OnSerialize`) has been applied, or one second at most, so the first thing you see is real data.
   It always runs before the first `OnNetworkTick` of that object.
3. Ownership change: for each behaviour `OnOwnerChanged(previous, current)`, then `OnOwnershipGained()` or `OnOwnershipLost()`, then `NetworkObject.OwnershipChanged` and `Events.OnOwnershipChanged`.
4. Master switch: `OnMasterChanged(previous, current)` on every spawned behaviour (scene objects without an explicit owner also get Gained/Lost), then `Events.OnMasterClientSwitched`.
5. `OnNetworkDespawned` runs before `OnDestroy`, only if `OnNetworkSpawned` ran.

### Async helpers

```csharp
bool ok = await networkObject.RequestOwnershipAsync(timeoutSeconds: 5);   // false on Forbidden, timeout or Fixed mode
bool moved = await networkObject.TransferOwnershipAsync(player);
await Pulse.WaitForPlayersAsync(2);
await Pulse.WaitUntilAsync(() => ready);                                  // checked once per frame
var op = Pulse.WaitForPlayersAsync(4); op.Cancel();                       // awaiting a cancelled operation throws OperationCanceledException
```

### Network scenes

```csharp
Pulse.LoadScene("Arena");           // master only (others get a warning)
Pulse.Events.OnNetworkSceneLoaded += scene => Debug.Log(scene);
```

The master sets the room property `_scene` (`"{loadId}:{nameOrIndex}"`). With `NetPulseSettings.AutoSyncScene` (default on) every client, including late joiners, loads it asynchronously.
Room events that arrive during the load are queued and replayed in order afterwards. Non-persistent network objects are destroyed locally; tick `Persist Across Scenes` on a root `NetworkObject` to keep it.
Scene objects register after the load and the master re-registers them. `OnNetworkSceneLoaded` is not raised for a late joiner whose active scene already matches.
Add the scenes to Build Settings. With `AutoSyncScene` off, `LoadScene` only sets the property and nobody loads automatically.

### Traffic profiler

`Pulse.Stats` counts bytes and messages in and out per category (`NetworkObject` per object, `Rpc` as `Type.Method`, `Message` per type, `StateSync`, `SyncVar` for Master variables, `System`),
with bytes/s over a 1 second window and session totals. Categories overlap on purpose (an RPC counts under `Rpc` and under its `NetworkObject`).
`NetPulseSettings.CollectStats` defaults to on in the editor and development builds; when off, the counters cost nothing. See the Traffic tab of **NetPulse -> Open Hub** (sort, filter, Reset).

### Analyzers

| Id | Severity | Rule |
|---|---|---|
| NP0001 | Error | Class with `[NetRpc]`, `SyncVar<T>` or `[NetSerializable]` is not `partial` |
| NP0002 | Error | `[NetRpc]` outside a `NetworkBehaviour` |
| NP0003 | Error | Unsupported RPC parameter type |
| NP0004 | Error | RPC returns a value or has `ref`/`out`/`in` parameters |
| NP0005 | Error | RPC overload |
| NP0006 | Error | Unsupported field type in `[NetSerializable]` / `INetMessage` |
| NP0007 | Error | `SyncVar<T>` with unsupported `T` |
| NP0008 | Warning | `SyncVar<T>` field is not `readonly` or is outside a `NetworkBehaviour` |
| NP0009 | Error | Id collision (RPCs in a type, messages / extended types in an assembly) |
| NP0010 | Error | `[Synced]` on an unsupported field type |
| NP0011 | Warning | `CallRpc("Name", ...)` while a `[NetRpc]` method with that name exists; use `Rpc.Name(...)` |

## Low-level API

Still supported and unchanged: reflection RPCs `CallRpc(nameof(Hit), RpcTarget.All, 10)` on `[NetRpc]` methods, `[Synced]` fields (primitives, strings, enums, Unity vectors/colors),
`OnSerialize(NetStream)`, string property keys (`room.SetProperty("map", "forest")`, `SetProperties(delta, expected)`), and byte-coded events:
`Pulse.RaiseEvent(code 0-199, data, new RaiseEventOptions { Receivers, TargetActors, Caching, InterestGroup, Delivery })`.

```csharp
public partial class Health : NetworkBehaviour
{
    [Synced] private int _hp = 100;

    public void Damage(int amount) => CallRpc(nameof(TakeDamage), RpcTarget.All, amount);

    [NetRpc]
    private void TakeDamage(int amount, NetRpcInfo info) => _hp -= amount;
}
```

The analyzer suggests `Rpc.TakeDamage(amount)` for that `CallRpc` (NP0011, a warning).

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
public partial class Health : NetworkBehaviour
{
    private readonly SyncVar<int> _hp = new(100);

    public void Damage(int amount) => Rpc.TakeDamage(amount);

    [NetRpc]
    private void TakeDamage(int amount, NetRpcInfo info) => _hp.Value -= amount;
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
* **NetPulse -> Open Hub** - live connection status, room, players, ping in Play Mode, plus the Traffic tab (profiler table with sorting, filter and Reset).
* **NetworkObject inspector** - ownership mode, scene object id, runtime view id and owner.

## Tests

Tests live in `Tests/Runtime` (serialization, `NetPulseClient` on a fake transport, messages, typed keys, tick, async helpers, network scenes, stats, SyncVar, generated RPCs, hooks, ownership) and `Tests/Editor`.
`NetworkObjectOwnershipTests` create GameObjects and use `[UnityTest]`, so run them from the Test Runner in Play Mode.
Enable them by adding the package to `testables` in `Packages/manifest.json` (the package is embedded, so it is already testable).

## Known limitations

* `SendRate` caps the state-sync rate together with `SerializationRate` (the effective rate is the lower of the two; `SerializationRate = 0` means the server tick rate).
* Buffered RPCs of destroyed objects stay in the room cache until their sender leaves.
* Ownership is validated by the server; a client built for an older server (before ownership enforcement) must be updated, otherwise its objects stay `Fixed` and cannot change owner.
