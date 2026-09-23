# RIoT2.Net.Devices

## Shared package release prerequisite

These plugins, including Netatmo, require `RIoT2.Core` **0.1.41**. Publish that
package to the configured trusted feed before releasing the plugins. Local
validation uses the final package in `C:\Src\RIoT2\.localfeed` plus cached
dependencies; no external publication is performed by the regression tests.

## Regression tests

```powershell
dotnet test .\Tests\RIoT2.Net.Devices.Tests.csproj
```

The Netatmo authentication tests use synthetic tokens in an isolated test-output
directory and never contact Netatmo. On restart, a valid `Data/netatmoAuth.json`
takes precedence over configured tokens so refreshed credentials are preserved.
Keep that data directory persistent across node restarts.

The same suite replays Hue event JSON and EasyPLC connection streams entirely
in memory; it does not contact a Hue bridge or PLC.

### Driver lifecycle and partial updates

- Hue event updates are merged with the last known state for each light. Missing
  fields do not reset on/off or brightness. A color received before brightness is
  cached until brightness becomes known rather than inventing a brightness value.
  Reconfiguration clears the per-light cache.
- EasyPLC shutdown and connection failures dispose and clear the old connection.
  Restart/reconfiguration establishes a fresh connection using the current
  endpoint. Initialization reads complete eight-byte responses, including when
  TCP splits them into smaller reads; rejected/incomplete handshakes fail startup.

## Quick note on creating custom net core plugin

- Create new Class library project

- Add reference to RIoT2.Core

- Create Plugin.cs which implements IDevicePlugin -interface
 - The plugin must have a following contructor: public Plugin(IServiceProvider services)
 - The plugin provides a list of devices to Net Node
- Create custom Devices
 - At minimum, a Device must implement IDevice interface
 - Implement abstract class DeviceBase for easier implementation
	- Add configuration logic to: public override void ConfigureDevice()
	- Add device start logic to: public override async void StartDevice()
	- Add device stop logic to: public override void StopDevice()
	- If device is IRefresableReportDevice, add refresh logic to: public override void Refresh(ReportTemplate report)
	- Throw error from overridden functions. This will change devices state to error. Error message is accessible from devices StateMessage
 - Implement IDeviceWithConfiguration for device that can provide a configuration template 
 - Implement ICommandDevice -interface if device is capable of executing commands (switch, etc.)
 - Implement IRefreshableReportDevice if device data is refreshed periodically 
	- The refresh logic is implemented by function (from DeviceBase): public override void Refresh(ReportTemplate report) 
 - Implement IMatterDevice if the device should be exposed to a Matter ecosystem (Google Home, etc.) through the RIoT Control Bridge
	- Return one MatterEndpointTemplate per Matter endpoint from: public IEnumerable&lt;MatterEndpointTemplate&gt; GetMatterEndpoints(DeviceConfiguration configuration)
	- Build the bindings against the configuration instance passed in, since template ids are generated per call
	- Give each endpoint an Id that is stable across restarts, derived from the underlying device (see Catalog/Hue.cs for a worked example)


## Default Net Node plugins

### AP Systems
Get inverter/meter data from an AP Systems ECU

```
TODO configuration exable
```

> [!NOTE]
> Requires AP Systems app ID, app secret, SID and ECU ID

### Azure Relay
Receive webhooks or other messages from the internet into privete web through Azure Relay service

```
TODO configuration exable
```

> [!NOTE]
> Requires settings up Azure!

### Easy PLC
Control Easy PLC from Eaton / Moeller

```
TODO configuration exable
```

### Electricity Price
Receive current electricity price from entsoe.eu

```
TODO configuration exable
```

> [!NOTE]
> Requires registering to entsoe.eu

### Eufy Security
Connects to the eufy-security-ws websocket service to receive events from Eufy security devices (motion, person, pet, sound, stranger, vehicle detected)

```
TODO configuration exable
```

> [!NOTE]
> Requires running the eufy-security-ws service, see https://bropat.github.io/eufy-security-ws/

### FTP
Trigger events from received files (e.g. Web cam sending images via FTP)

```
TODO configuration exable
```

### Philips HUE
Control HUE lamps and other devices connected to bridge

```
TODO configuration exable
```

### Messaging
Send / Receive firebase messages or email

```
TODO configuration exable
```

> [!NOTE]
> Requires email address and setting up firabase.
> Todo details on setting up firebase and storing auth key

### MQTT
Send / Receive mqtt messages

```
TODO configuration exable
```

### Netatmo Security
Receive events from Netatmo security

```
TODO configuration exable
```

> [!NOTE]
> Requires activating API in netatmo.
> TODO details 

### Netatmo Weather
Access Netatmo weather information

```
TODO configuration exable
```

> [!NOTE]
> Requires activating API in netatmo.
> TODO details

### Timer
Create timed triggers to the system

```
TODO configuration exable
```

### Virtual Device
Generic memory based device which is used to trigger other events

```
TODO configuration exable
```

### Water Consumption
Get your water consumption data from wrm-systems

```
TODO configuration exable
```

> [!NOTE]
> Requires API -key from wrm-systems

### Web Device
Call generic service on web or trigger actions based on received webhooks

```
TODO configuration exable
```

## TODO
- Instructions and an example for creating a pluging and a device 
