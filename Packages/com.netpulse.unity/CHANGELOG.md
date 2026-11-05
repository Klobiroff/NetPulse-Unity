# Changelog

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
