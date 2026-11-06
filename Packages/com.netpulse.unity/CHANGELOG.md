# Changelog

## [0.3.0] - 2026-11-06
Lighter API: no strings, byte codes or manual type registration in everyday code. The old low-level API (`CallRpc(string, ...)`, `RaiseEvent`, `[Synced]`, string property keys) keeps working.
- Roslyn source generator and analyzers (`Runtime/Analyzers/NetPulse.Generators.dll`, ids NP0001-NP0011): `Rpc.Hit(10)`, `Rpc.To(RpcTarget.Others).Hit(10)`, `[NetSerializable]`, `INetMessage`, `SyncVar<T>`. Classes that declare them must be `partial` (`sealed partial` is supported).
- `[NetRpc(RpcTarget, Delivery = ..., OwnerOnly = true)]`; generated RPCs travel as event 242 with kind 1 (`[kind][viewId][behaviourIndex][rpcId][args]`).
- `SyncVar<T>` with `Changed(previous, current)`, `Owner`/`Master` permission, delta inside StateSync (Owner) or event 245 (Master), full snapshot for late joiners, a one-time warning for writes without permission.
- Typed messages: `Pulse.Send<T>(in T, RpcTarget | Player, MessageOptions)` and `Pulse.On<T>(handler)` on event 244.
- Typed keys: `RoomKey<T>` / `PlayerKey<T>` with `Get`, `Set`, `SetAsync` (compare-and-set) and `OnChanged`.
- Network tick: `Pulse.Tick`, `TickRate`, `TickDelta`, `NetworkBehaviour.OnNetworkTick(long)` (catch-up loop, at most 5 ticks per frame). `SerializationRate = 0` (new default) follows the server tick rate; state is sent on tick boundaries.
- Lifecycle hooks `OnOwnershipGained`, `OnOwnershipLost`, `OnMasterChanged`; `OnNetworkSpawned` for remote objects waits for the initial state (or one second).
- Async: `NetworkObject.RequestOwnershipAsync`, `TransferOwnershipAsync`, `Pulse.WaitForPlayersAsync`, `WaitUntilAsync`, `NetOperation.Cancel()`.
- Network scenes: `Pulse.LoadScene(name | index)` (master only), `NetPulseSettings.AutoSyncScene`, room property `_scene`, late joiners, room events queued during the load and replayed in order, `Events.OnNetworkSceneLoaded` / `ISceneCallbacks`, `NetworkObject.PersistAcrossScenes`.
- Traffic profiler: `Pulse.Stats` (categories, bytes/s over a 1 s window, session totals), `NetPulseSettings.CollectStats` (default: editor and development builds), NetPulse Hub "Traffic" tab.
- link.xml generation also keeps `[NetSerializable]` / `INetMessage` types and generated dispatch methods.
- `NetworkAnimator` uses a generated RPC (the class is now `partial`).
- Basic sample rewritten for the new API and gets a second scene (built by *NetPulse -> Samples -> Build Basic Sample Scene*).
- Requires a 0.3 server (Common: `Extended` type code, events 244/245, `_scene`, `ObjectFlags`, `TickRate`).


## [0.2.0] - 2026-11-05
- Server-enforced ownership: `OwnershipMode` is sent with the spawn; `IsMine`/`Owner` change only on the server's `OwnershipChanged` (also for your own requests).
- `RequestOwnership()` uses the new `RequestOwnership` operation for `Request` and `Takeover` objects; nothing is sent for `Fixed`. The old ownership-over-RPC path is removed.
- Owners get `OnOwnershipRequest` (`NetworkBehaviour` virtual, `Events.OnOwnershipRequest`, `IOwnershipCallbacks`) and answer with `ApproveOwnership(player)`; ignoring it lets the request expire.
- `Events.OnOwnershipFailed` for `Forbidden`/`ObjectNotFound`; `ErrorInfo` warnings from the server are rate limited on the client.
- The master registers scene objects and their modes with the server after joining and after a master switch; scene objects of a leaving player return to the master.
- On player leave only objects the player currently owns are destroyed.
- Authentication: `ConnectionOptions.AuthToken` / `Pulse.AuthToken` is sent to the server; `UserId` and `NickName` come from the server's response.
- Basic sample: shared Takeover Cube; new ownership tests.
- Includes 0.1.1: IL2CPP build-time link.xml generation (IUnityLinkerProcessor + TypeCache), PreserveAttribute-based `[NetRpc]` and `[Synced]`, clearer errors for missing RPC methods, menu NetPulse > Tools > Preview IL2CPP link.xml. Removed the ineffective in-package link.xml.

## [0.1.0] - 2026-11-05
- Initial release: connection, lobby, rooms, player/room properties, custom events, NetOperation (callback / coroutine / await),
  networked objects (NetworkObject, NetworkBehaviour, [NetRpc], [Synced], OnSerialize), ownership, scene objects,
  NetworkTransform / NetworkRigidbody / NetworkAnimator, offline mode, editor tooling (settings, Hub, prefab registry), tests and Basic sample.
