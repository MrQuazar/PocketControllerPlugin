# Pocket Controller

Unity-side receiver for **PocketControllerHost**'s UDP sensor relay. Listens
for sensor packets sent from paired phones and exposes them as per-player
sensor state (`PocketPlayerSensors`) that you can read from any script.

## Installation

**Via git URL (Package Manager → Add package from git URL):**

```
https://github.com/<your-org>/<your-repo>.git?path=/com.unison.pocketcontroller
```

**Via local package (Package Manager → Add package from disk):**

Point it at this folder's `package.json`.

## Usage

1. Add a `PocketControllerSensors` component to a GameObject in your scene
   (it persists across scene loads via `DontDestroyOnLoad`, so one instance
   is enough for the whole game).
2. Set `listenPort` to match the port `PocketControllerHost` is relaying to
   (default `5556`).
3. Read sensor data:

```csharp
var sensors = PocketControllerSensors.Instance;
var player = sensors.GetFirstPlayer();

if (player != null && !player.IsStale())
{
    Vector3 tilt = player.Gravity;
    Quaternion rot = player.Rotation;
}
```

Or subscribe to events for multi-player setups:

```csharp
PocketControllerSensors.Instance.OnPlayerJoined += p =>
    Debug.Log($"Player {p.SessionId} connected");

PocketControllerSensors.Instance.OnSensorUpdated += (p, sensorId) => { /* ... */ };
```

## Requirements

- Unity 2021.2+ (uses C# 9 target-typed `new()`)

## License

See [LICENSE.md](LICENSE.md).
