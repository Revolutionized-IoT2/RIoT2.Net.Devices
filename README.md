# RIoT2.Net.Devices

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


## Default Net Node plugins

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
