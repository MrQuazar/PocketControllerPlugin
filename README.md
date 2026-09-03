# Pocket Controller

Unity-side receiver for **PocketControllerHost**'s UDP sensor relay. Turns a
paired Android phone into a live source of gyro, accelerometer, GPS, touch,
camera, and NFC data inside your Unity game — no phone-specific code required,
just read `PocketPlayerSensors` from any script.

```
Phone (PocketController app) → PocketControllerHost (.NET, UDP/TCP relay) → this package
```

The host handles pairing, transport, and turning phone buttons/sticks into a
virtual Xbox controller (via ViGEm) that Unity's Input system sees on its own.
This package only covers the **sensor/camera/NFC side** — the part that
doesn't come through as generic gamepad input.

## Setup

### Requirements

- Unity 2021.2+ (uses C# 9 target-typed `new()`)
- **PocketControllerHost** (the companion .NET relay app) running on the
  same PC, paired with at least one phone running the PocketController
  Android app
- Host and Unity must agree on the UDP port it relays sensor packets to
  (default `5556`) and, if you use photo capture, the TCP photo port
  (`5557`, currently fixed)

### Get the host app and phone app

The PocketControllerHost source and the Android app source aren't public —
they're distributed as prebuilt binaries:

- **PocketControllerHost (.exe)**: [Download from Google Drive]([https://drive.google.com/PASTE-YOUR-LINK-HERE](https://drive.google.com/drive/folders/1Vwsp8s5641FX_vexARmg8mmBWZZ-A1t3?usp=sharing))
- **PocketController Android app (.apk)**: [Download from Google Drive]([https://drive.google.com/PASTE-YOUR-LINK-HERE](https://drive.google.com/drive/folders/1Vwsp8s5641FX_vexARmg8mmBWZZ-A1t3?usp=sharing))

### Install

**Via git URL** (Package Manager → `+` → Add package from git URL):

```
https://github.com/MrQuazar/PocketControllerPlugin.git
```

**Via local disk** (Package Manager → `+` → Add package from disk): point it
at this folder's `package.json`.

### Add it to your scene

1. Add a `PocketControllerSensors` component to any GameObject — a single
   empty "PocketController" object works fine. It calls `DontDestroyOnLoad`
   on itself, so one instance in your first scene covers the whole game.
2. Set `listenPort` on the component to match the port `PocketControllerHost`
   relays to (default `5556` — leave it alone unless you changed the host
   config too).
3. Start the host, pair a phone, and press Play. `PocketControllerSensors.Instance`
   is now live.

That's the entire setup — everything below is how to read data out of it.

## Basic usage

### Getting a player

Most single-player games just want "whichever phone is connected":

```csharp
var player = PocketControllerSensors.Instance.GetFirstPlayer();

if (player != null && !player.IsStale())
{
    Vector3 tilt = player.Gravity;
}
```

`IsStale(maxAgeSeconds = 5f)` returns true once no packet has arrived from
that phone for a while — always check it (or handle a null player) before
acting on stale sensor data, e.g. after a phone disconnects mid-game.

For multiplayer, iterate `Players` (keyed by `SessionId`) or subscribe to
`OnPlayerJoined`:

```csharp
PocketControllerSensors.Instance.OnPlayerJoined += player =>
    Debug.Log($"Player {player.SessionId} connected");

foreach (var p in PocketControllerSensors.Instance.Players.Values) { /* ... */ }
```

### Checking a sensor is actually available before reading it

A phone only reports sensors it physically has, and the player must have the
relevant toggle enabled in the app (e.g. Touchscreen mode). Always gate a
read with `HasSensor`:

```csharp
if (player.HasSensor(PocketSensorId.Gravity))
{
    Vector3 tilt = player.Gravity;
}
```

Reading a sensor property without checking is safe (it returns a zeroed
default), but `HasSensor` is how you tell "phone hasn't sent this yet" apart
from "phone is reporting exactly zero".

### Polling vs. events

Poll from `Update()` for continuous values you act on every frame (tilt,
rotation). Subscribe to `OnSensorUpdated` for values you only care about the
moment they change (a step, a proximity cover/uncover):

```csharp
PocketControllerSensors.Instance.OnSensorUpdated += (player, sensorId) =>
{
    if (sensorId == PocketSensorId.StepDetector)
        Debug.Log("Step!");
};
```

## Sensor reference

Every sensor lives on `PocketPlayerSensors` as a typed property, backed by
`HasSensor(PocketSensorId.X)` to check availability. Units below are Android's
native units, passed through unmodified.

| Sensor | `PocketSensorId` | Property | Type | Notes |
|---|---|---|---|---|
| Gyroscope | `Gyroscope` | `.Gyroscope` | `Vector3` | rad/s, angular velocity |
| Accelerometer | `Accelerometer` | `.Accelerometer` | `Vector3` | m/s², raw (gravity + motion) |
| Gravity | `Gravity` | `.Gravity` | `Vector3` | m/s², gravity only — stable under shake, good for tilt steering |
| Linear Acceleration | `LinearAcceleration` | `.LinearAcceleration` | `Vector3` | m/s², gravity-compensated motion |
| Rotation Vector | `RotationVector` | `.Rotation` | `Quaternion` | absolute device orientation |
| Game Rotation Vector | `GameRotationVector` | `.GameRotation` | `Quaternion` | like Rotation but not compass-referenced (no magnetometer drift/jumps) |
| Geomagnetic Rotation Vector | `GeomagneticRotationVector` | `.GeomagneticRotation` | `Quaternion` | rotation vector computed from mag+accel only (no gyro) |
| Magnetometer | `Magnetometer` | `.Magnetometer` | `Vector3` | µT, raw magnetic field — use for a compass heading |
| Light | `Light` | `.Light` | `float` | lux |
| Proximity | `Proximity` | `.Proximity` | `float` | cm to nearest object; near-zero usually means "covered" |
| GPS | `Gps` | `.GpsLatLon` | `Vector2` | (lat, lon) |
| Pressure | `Pressure` | `.Pressure` | `float` | hPa, barometric |
| Humidity | `Humidity` | `.Humidity` | `float` | % relative humidity |
| Ambient Temperature | `AmbientTemperature` | `.AmbientTemperature` | `float` | °C |
| Step Counter | `StepCounter` | `.StepCounter` | `float` | cumulative steps since the phone last rebooted |
| Step Detector | `StepDetector` | `.StepDetectorFired` | `bool` | true once ANY step has ever fired — for the exact moment of a step, use `OnSensorUpdated` instead of polling this |
| Heart Rate | `HeartRate` | `.HeartRate` | `float` | bpm (needs a heart rate sensor — most phones don't have one) |
| Touchscreen | `Touchscreen` | `.TouchPoints` | `IReadOnlyList<PocketTouchPoint>` | see Advanced below |
| Gyroscope Uncalibrated | `GyroscopeUncalibrated` | `.GyroscopeUncalibrated` | `PocketUncalibratedReading` | see Advanced below |
| Accelerometer Uncalibrated | `AccelerometerUncalibrated` | `.AccelerometerUncalibrated` | `PocketUncalibratedReading` | see Advanced below |
| Magnetometer Uncalibrated | `MagnetometerUncalibrated` | `.MagnetometerUncalibrated` | `PocketUncalibratedReading` | see Advanced below |

### Common basic patterns

**Tilt steering** (Gravity is preferred over raw Accelerometer — it doesn't
jitter when the player shakes the phone):

```csharp
void Update()
{
    if (player == null || player.IsStale() || !player.HasSensor(PocketSensorId.Gravity)) return;
    float steer = Mathf.Clamp(player.Gravity.y / 5f, -1f, 1f);
}
```

**Light-driven effects** (e.g. a day/night mechanic tied to real ambient light):

```csharp
if (player.HasSensor(PocketSensorId.Light))
    float brightness01 = Mathf.InverseLerp(1f, 1000f, player.Light);
```

**Compass heading from the magnetometer**:

```csharp
Vector3 mag = player.Magnetometer;
float headingDeg = Mathf.Atan2(mag.x, mag.y) * Mathf.Rad2Deg;
if (headingDeg < 0f) headingDeg += 360f;
```

**Cover-to-trigger, via Proximity** (e.g. "close your eyes" by covering the sensor):

```csharp
bool covered = player.HasSensor(PocketSensorId.Proximity) && player.Proximity <= 3f;
```

## Device capabilities

Separate from continuous sensors, `PocketDeviceCapability` reports on/off
features that need their own request/response flow rather than a stream of
floats:

| Capability | Meaning |
|---|---|
| `Camera` | Player has the camera toggle enabled — required before `RequestPhoto` will get a response |
| `Microphone` | Player has the microphone toggle enabled (flag only in this package version — audio isn't streamed yet) |
| `Nfc` | Phone supports NFC tag reads — `OnNfcTagDetected` will fire when the player taps a tag |
| `UsbConnection` | This session is on a USB/TCP link rather than WiFi/UDP |

```csharp
if (player.HasCapability(PocketDeviceCapability.Camera)) { /* show a capture button */ }
```

`OnCapabilitiesUpdated` fires whenever a player's capability set changes
(e.g. they flip a toggle mid-session).

## Advanced usage

### Multi-touch pad

Enabling the Touchscreen toggle on the phone covers its own controller UI
with a full-screen touch pad and reports every finger down on it (up to 10).
What multiple fingers mean is entirely up to your game — counting them,
tracking a swipe, whatever fits:

```csharp
if (player.HasSensor(PocketSensorId.Touchscreen))
{
    foreach (var touch in player.TouchPoints)
    {
        Vector2 pos01 = touch.Position; // 0..1, origin top-left, Y down (raw Android convention)
        float pressure = touch.Pressure;
    }
}
```

Positions are normalized to the phone's screen, top-left origin, Y increasing
downward — flip Y yourself (`1f - y`) if you want it to line up with Unity's
`Input.mousePosition` convention instead.

### Uncalibrated IMU readings

Android's uncalibrated variants expose both the raw sensor reading and the
device's own estimated bias — most games only need `.Raw`, but the bias is
there if you want to do your own drift correction:

```csharp
var g = player.GyroscopeUncalibrated;
Vector3 rawGyro = g.Raw;
Vector3 estimatedDriftBias = g.EstimatedBias;
```

### Camera capture

Photo capture rides a dedicated TCP channel, not the UDP sensor stream — it's
a JPEG, not a float packet — so expect it to take noticeably longer than a
sensor read (typically well under a second on a local network, but always
async). `RequestPhoto` fires its callback with the decoded `Texture2D`, or
`null` if the phone declined (camera off, no permission) or the JPEG failed
to decode:

```csharp
if (player.HasCapability(PocketDeviceCapability.Camera))
{
    PocketControllerSensors.Instance.RequestPhoto(player, texture =>
    {
        if (texture == null) { /* capture failed or was declined */ return; }
        myImage.texture = texture;
    }, cameraFacing: 0); // 0 = back camera, 1 = front camera
}
```

There's also a fire-and-forget event if something else in your game wants to
react to *any* photo coming in, regardless of who requested it:

```csharp
PocketControllerSensors.Instance.OnPhotoReceived += (player, texture) => { /* ... */ };
```

Give the player feedback the instant you call `RequestPhoto` (a shutter SFX,
a UI flash) rather than waiting for the callback — the round trip can take
long enough that players won't otherwise know their tap registered.

### NFC tags

Fires whenever the player taps an NFC tag to their phone. The tag ID is a raw
byte array (its length varies by tag type) — hex-encode it if you need a
stable string key for matching tags:

```csharp
PocketControllerSensors.Instance.OnNfcTagDetected += (player, tagId) =>
{
    string hex = System.BitConverter.ToString(tagId).Replace("-", "");
    Debug.Log($"Player {player.SessionId} tapped tag {hex}");
};
```

This is unsolicited (no request needed) and requires
`player.HasCapability(PocketDeviceCapability.Nfc)` to ever fire.

### Connection quality / WiFi vs. USB

`LastPacketRealtime` and `IsStale` aren't just for "is the phone still
connected" — they're also useful for surfacing connection quality to the
player, or logging it for analytics:

```csharp
float secondsSinceLastPacket = Time.realtimeSinceStartup - player.LastPacketRealtime;
bool onUsb = player.HasCapability(PocketDeviceCapability.UsbConnection);
```

USB (TCP) sessions are generally more stable than WiFi (UDP) on a congested
network — `UsbConnection` lets you branch behavior (e.g. tighter timing
tolerances) or just log which transport a player used.

### Sensor-first, input-fallback pattern

Every minigame in this project's sample content follows the same priority
order when a mechanic *can* use a sensor but shouldn't require one: try the
phone sensor first, then fall back to gamepad, then keyboard — checking each
**independently**, not as an if/else chain. A merely-connected-but-idle
gamepad should never block keyboard input:

```csharp
if (player != null && player.HasSensor(PocketSensorId.Magnetometer))
{
    // use the sensor
}
else
{
    bool padPressed = Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame;
    bool keyPressed = Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
    if (padPressed || keyPressed) { /* fallback input */ }
}
```

This keeps every mechanic playable with zero phones connected, which matters
a lot for testing in the Editor.

## API summary

- `PocketControllerSensors.Instance` — the singleton; `Players`,
  `GetFirstPlayer()`, `RequestPhoto(player, callback, cameraFacing)`
- Events: `OnPlayerJoined`, `OnSensorUpdated`, `OnCapabilitiesUpdated`,
  `OnNfcTagDetected`, `OnPhotoReceived`
- `PocketPlayerSensors` — `SessionId`, `IsStale()`, `HasSensor()`,
  `HasCapability()`, plus one property per sensor listed above
- `PocketSensorId`, `PocketDeviceCapability` — enums, see tables above
- `PocketTouchPoint` — `Position` (`Vector2`), `Pressure` (`float`)
- `PocketUncalibratedReading` — `Raw` (`Vector3`), `EstimatedBias` (`Vector3`)

## Troubleshooting

- **No player ever connects**: check `listenPort` matches the host's relay
  port, and that no firewall is blocking local UDP traffic on it.
- **Player connects but sensors never update**: the phone hasn't enabled that
  sensor's toggle in the app, or `HasSensor` is being checked against the
  wrong `PocketSensorId`.
- **`IsStale()` is always true**: the host may have stopped relaying (check
  it's still running) or the phone lost its network connection.

## Contact

Questions, bugs, or feature requests: [aartemsingh.uk@gmail.com](mailto:aartemsingh.uk@gmail.com),
or open an issue on the [GitHub repo](https://github.com/MrQuazar/PocketControllerPlugin).

## License

MIT — see [LICENSE.md](LICENSE.md).
