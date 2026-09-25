# RIoT2.Net.Devices

## Shared package release prerequisite

These plugins, including Netatmo, require `RIoT2.Core` **0.1.43**. Publish that
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
- EasyPLC now opts into `AsyncDeviceBase` and `IAsyncCommandDevice`. Connect, handshake, command,
  and refresh I/O is awaited and cancellable, with a five-second transaction deadline. Responses
  are read as complete length-delimited CRC-checked frames; transport failures reset the connection.
  Stop cancels and awaits in-flight I/O. Non-zero marker-write expansions fail explicitly rather than
  silently targeting expansion zero. Deploy the Core 0.1.43 node before deploying this plugin.

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
	- For synchronous devices, add start logic to: public override void StartDevice()
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

For new asynchronous drivers, prefer `AsyncDeviceBase` and override `StartDeviceAsync`,
`StopDeviceAsync`, and `RefreshAsync`, forwarding the cancellation token through all I/O. Implement
`IAsyncCommandDevice.ExecuteCommandAsync` for commands. Keep synchronous compatibility entry points
when implementing the legacy command interface; the updated node selects the async contract.


## Default Net Node plugins

`Plugin.Initialize` registers: `Web`, `Timer`, `Virtual`, `Mqtt`, `WaterConsumption`, `Messaging`, `FTP`, `ElectricityPrice`, `EasyPLC`, `NetatmoWeather`, `NetatmoSecurity`, `Hue`, `AzureRelay`, `ApSystems`, and `EufySecurity`. Keep credentials in orchestrator-managed configuration or mounted files; do not bake them into images or package manifests.

| Device | Configuration parameters from current code | Reports / commands |
| --- | --- | --- |
| AP Systems | `appId`, `appSecret`, `sid`, `ecuId` | Hourly energy summaries: `today`, `month`, `year`, `lifetime` (`unit`, `precision`). |
| Azure Relay | No template is emitted; manually configure `relayNamespace`, `connectionName`, `keyName`, `key`. | Starts/stops relay listener; message handling is still TODO in code. |
| EasyPLC | `ipAddress`, `port` | Async/cancellable marker read/write driver; marker command addresses are `M-{netId}-{expansion}-{marker}` and `refresh`. |
| Electricity Price | `securityToken`, `domain`, `endpoint`, `vat`; report parameter `precision` | Scheduled current price report in `c/kWh`; cached XML stored at `Data/priceData.xml`. |
| Eufy Security | `serviceIp`, `port` for the external eufy-security-ws service | Dynamic camera/security reports and guard-mode command templates after the service is connected. |
| FTP | No custom template is emitted; manually configure `ftpUsers` (`user:password|...`) and `ftpPort`. | File uploads become in-memory photo `SecurityReport` values keyed by FTP username. |
| Hue | `bridgeIpAddress`, `apiKey` | Dynamic light command/report templates when running; event stream updates merge partial light state and include Matter endpoint metadata. |
| Messaging | `firebaseProjectName`, `smtp_Server`, `smtp_User`, `smtp_Password`, `smtp_Port`; Firebase service account read from `Data/<project>.json` | Commands `fb` (Firebase topic message) and `mail` (SMTP email). |
| MQTT | No custom template is emitted; manually configure `clientId`, `serverUrl`, `userName`, `password`, `subscribeTopics`. | Publishes command payloads and reports subscribed topic payloads as text. |
| Netatmo Security | `token`, `refresh_token`, `clientId`, `clientSecret` | Dynamic security camera reports plus `set-person-home` / `set-person-away` commands; rotated tokens are persisted in `Data/netatmoAuth.json`. |
| Netatmo Weather | `token`, `refresh_token`, `clientId`, `clientSecret`, `stationId` | Weather station and module measurements; rotated tokens are persisted in `Data/netatmoAuth.json`. |
| Timer | Uses report-level schedules, not device-level parameters | Emits the report address whenever the scheduler refreshes that report. |
| Virtual | No device parameters | Stores command values by address and republishes matching report values. |
| Water Consumption | `securityToken`, `endpoint`; report parameters `unit`, `precision` | Scheduled latest water meter reading; unchanged readings are suppressed. |
| Web Device | No device parameters | Commands perform HTTP GET/POST to command addresses; `POST /api/webhook/{address}` publishes matching reports. |

Plugin controllers currently add `POST /api/webhook/{address}` (body forwarded to the Web device) and download endpoints from `DownloadController`. The webhook endpoint is not authenticated in this plugin; put the node behind a trusted network, reverse proxy, or API gateway if exposed. Webhook request bodies are capped at 64 KiB. Download filenames are normalized and rejected if they contain traversal, absolute paths, encoded slashes, or encoded backslashes.

## TODO
- Instructions and an example for creating a pluging and a device 
