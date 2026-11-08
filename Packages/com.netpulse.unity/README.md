# NetPulse for Unity

A Photon-like networking client for Unity 2021.3 LTS through 6000.x. Plain C# 9, no `Awaitable`, everything runs on the main thread, IL2CPP safe. It talks to the NetPulse server over reliable UDP (LiteNetLib 1.3.5, vendored in `Runtime/ThirdParty`).

The static entry point is called `Pulse`, not `NetPulse`: the `NetPulse` identifier is already taken by the `NetPulse.Common` namespace.

Everyday code needs no strings, byte codes or type registration. A Roslyn source generator writes the serialization and dispatch code at compile time, so classes that use `[NetRpc]`, `SyncVar<T>`, `[NetSerializable]` or `INetMessage` are declared `partial`.

## Install

Package Manager, `Add package from git URL`:

```text
https://github.com/Klobiroff/NetPulse-Unity.git?path=Packages/com.netpulse.unity
```

The repository is private for now, so git needs access to it. You can also copy this folder into your project's `Packages` folder.

Then open `Project Settings > NetPulse` (or `NetPulse > Create Settings Asset`) and set the host, the port (UDP 5055 by default), the `AppId` and the `AppVersion`.

The *Basic* sample can be imported from the Package Manager and built with `NetPulse > Samples > Build Basic Sample Scene`.

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

`await` works on every `NetOperation`; failures throw `NetPulseException` with a `ReturnCode`. Coroutines (`yield return Pulse.Connect();`) and `.Completed` callbacks work as well.

A generated RPC and a synchronized value on a network object:

```csharp
public sealed partial class Hero : NetworkBehaviour
{
    private readonly SyncVar<int> _health = new(100);

    public void Hit(int damage) => Rpc.TakeDamage(damage);

    [NetRpc(RpcTarget.All)]
    private void TakeDamage(int damage, NetRpcInfo info) => _health.Value -= damage;
}
```

## Documentation

The full documentation lives in the server repository, in [`docs/manual/en/`](https://github.com/Klobiroff/NetPulse-Server/tree/main/docs/manual/en) (English) and `docs/manual/ru/` (Russian). Start with these pages:

- `concepts.md`: rooms, players, the master client, network objects, ownership, ticks
- `unity/installation.md`, `unity/connecting.md`, `unity/matchmaking.md`
- `unity/rpc.md`, `unity/state-sync.md`, `unity/messages.md`, `unity/network-objects.md`, `unity/ownership.md`
- `unity/analyzers.md` (NP0001 to NP0011), `unity/il2cpp.md`, `unity/settings-and-editor.md`
- `migrating-from-pun.md`, `troubleshooting.md`

See `CHANGELOG.md` for the version history. The package requires a 0.4 server. See the security section of the server manual for transport encryption.
