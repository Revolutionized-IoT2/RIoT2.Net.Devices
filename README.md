# RIoT2.Net.Devices

Default device plugin catalog for the [RIoT2](https://github.com/Revolutionized-IoT2) platform.
The assembly is a .NET 10 class library with dynamic loading enabled so `RIoT2.Net.Node` can load it
from a plugin package at startup.

- Type: device plugin library
- Target framework: `net10.0`
- Core packages: `RIoT2.Core.Devices` (plus `RIoT2.Core.Http` and `RIoT2.Core.Mqtt` for the catalog devices that call web APIs or run an MQTT client), 2.0.0 (version in `Directory.Packages.props`). `RIoT2.Core.Devices.Build` packs the plugin zip
- Plugin entry point: `Plugin.cs`

How plugins fit into the platform: [configuration contract](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md).

## Contents

| Path | Contents |
|---|---|
| `Plugin.cs` | Derives from Core's `PluginBase`; registers plugin services and devices with the Node host |
| `Catalog/` | Device implementations |
| `Controllers/` | `WebhookController` and `DownloadController` |
| `Services/` | Supporting services for integrations and storage |
| `Models/` | DTOs used by devices and services |
| `Tests/` | Hardware-free regression tests and the plugin conformance test |

## Build and test

From the workspace root (`C:\Src\RIoT2`):

```powershell
dotnet build .\RIoT2.Net.Devices\RIoT2.Net.Devices.csproj
dotnet test .\RIoT2.Net.Devices\Tests\RIoT2.Net.Devices.Tests.csproj -c Release
```

The tests use synthetic tokens, in-memory Hue and EasyPLC streams, and isolated test-output files.
They do not contact Netatmo, Hue bridges, PLCs or cloud services. `PluginConformanceTests` runs
Core's shared conformance test: every device resolves, returns a template for its class, and
reads only the parameters its template declares; devices without `[RequiresHardware]` also start
and stop. The
[end-to-end tests](https://github.com/Revolutionized-IoT2/RIoT2.Tests/blob/main/README.md#end-to-end-tests) in RIoT2.Tests load this plugin into the Node and drive the Virtual
device through the Orchestrator.

To try an unreleased Core, pack it into `C:\Src\RIoT2\.localfeed` and restore with that folder as
an extra source. A local package is not a published release.

## Packaging and deployment

This library is not run directly. `RIoT2.Net.Node` loads it from a plugin zip extracted into its
`Plugins/` directory. `dotnet publish -c Release -p:Version=x.y.z` writes that zip,
`bin/plugin/RIoT2.Net.Devices_x.y.z.zip`, and its `.sha256` sidecar (Core's
`RIoT2.Core.Devices.Build`): the publish output without the assemblies the node already ships, plus
`PluginManifest.json`. Push a tag `x.y.z` to release: the release workflow runs the tests, publishes
the same way and attaches the zip and its `.sha256` to the GitHub release ([CI guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/ci.md)).

Install this plugin only after the Node image has been updated to `net10.0`. A `net10.0` plugin
cannot load into a `net9.0` node, while a `net9.0` plugin can load into the `net10.0` node.
Release this plugin with the Node image that hosts it, node first. Plugin assemblies share the
host's `RIoT2.Core*` assemblies, and a plugin built against a newer Core than the node's doesn't
load: 2.0.0 (Core 2.0.0) needs Node 2.0.0, which refuses the 1.x plugins; 1.7.0 (Core 1.23.0) needs Node 1.15.0 or newer; 1.6.0 (Core 1.17.0) Node 1.12.0; 1.4.0 and 1.5.0 (Core 1.16.0) Node 1.11.0;
1.3.x (Core 1.14.0) Node 1.10.0.

## Plugin HTTP endpoints

Loaded by the Node host:

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/webhook/{address}` | Forwards a request body up to 64 KiB to the Web device |
| `GET` | `/api/download/{filename}` | Returns an in-memory or configured stored image file |
| `GET` | `/api/download/list/files` | Lists in-memory documents |

These routes are anonymous. Keep the node behind a trusted network, reverse proxy or gateway when
exposing webhooks.

## Device catalog

The list below is verified against `Catalog/*.cs`. Required parameters are marked \*, secrets
are masked in the UI. Every device with parameters has typed options (since 2.0.0; Azure Relay,
EasyPLC, FTP, Messaging and MQTT since 1.4.0): it describes them in the template and validates them
at start, and a missing required or invalid value puts the device into the error state with the
reason.

| Device | Parameters | Reports and commands |
|---|---|---|
| AP Systems | `appId`\*, `appSecret`\* (secret), `sid`\*, `ecuId`\* | Hourly energy summaries: `today`, `month`, `year`, `lifetime` with `unit` and `precision` report parameters. |
| Azure Relay | `relayNamespace`\*, `connectionName`\*, `keyName`\*, `key`\* (secret) | Starts/stops an Azure Relay listener. Message handling is not implemented in code. |
| EasyPLC | `ipAddress`\*, `port` (default `10001`) | Async/cancellable PLC marker read/write driver. Marker command addresses are `M-{netId}-{expansion}-{marker}`; `refresh` reads markers. Non-zero write expansions are rejected. |
| Electricity Price | `securityToken`\* (secret), `domain` (default `10YFI-1--------U`), `endpoint` (default the ENTSO-E API), `vat` (percent) | Scheduled current price report at address `price`, with `unit=c/kWh` and `precision`. Since 2.1.0 also `prices`: today's and, once ENTSO-E publishes them (around 12:00 UTC), tomorrow's prices as a forecast series (`kind` `confirmed`), sent when they change. Prices are cached in `Data/priceData.json`. |
| Eufy Security | `serviceIp`\*, `port` (default `3000`) | Dynamic camera/security reports and a station `guardMode` command after the external eufy-security-ws service is connected. |
| FTP | `ftpUsers` (secret; `user:password` pairs separated by `\|`), `ftpPort` (default `21`) | File uploads become in-memory photo `SecurityReport` values keyed by FTP username. |
| Hue | `bridgeIpAddress`\*, `apiKey`\* (secret) | Dynamic light command/report templates when running; event-stream updates merge partial state and include Matter endpoint metadata. |
| MELCloud | `cloud` (`classic`, the default: the MELCloud app; or `home`: the MELCloud Home app), `email`\*, `password`\* (secret) | Mitsubishi Electric air-to-air heat pumps. Per unit (address `<unitId>\|<key>`), reports `roomTemperature` and `setTemperature` (°C), `power`, `mode` (`heat`, `dry`, `cool`, `fan`, `auto`), `thermostatMode` (Matter SystemMode: 0 off, 1 auto, 3 cool, 4 heat, 7 fan, 8 dry), `fanSpeed` (`auto`, `1`-`5`), `vaneVertical`, `vaneHorizontal`, `offline`, and `energyConsumed` (classic meter, kWh) or `energyToday` (Home, kWh). Commands `setTemperature`, `power`, `mode`, `thermostatMode`, `fanSpeed`, `vaneVertical`, `vaneHorizontal`, plus `refresh`. Each unit is a Matter thermostat. Polls hourly by default, at most once a minute. |
| Messaging | `firebaseProjectName`, `smtp_Server`, `smtp_User`, `smtp_Password` (secret), `smtp_Port` (default `587`); mail needs the server, user and password | Commands `fb` (Firebase topic message) and `mail` (SMTP email). Firebase service account JSON is read from `Data/service_account.json`. |
| MQTT | `clientId`, `serverUrl`\* (broker host, port 1883), `userName`, `password` (secret), `subscribeTopics` (separated by `;`) | Publishes command payloads to command addresses and reports subscribed topic payloads as text. |
| myUplink | `clientId`\*, `clientSecret`\* (secret) of an app registered at dev.myuplink.com, `language` (default `en-GB`), `extraParameters` (comma-separated parameter ids), `offsetParameter` (default `47011`), `baseTemperature` (default `20`), `degreesPerStep` (default `1`) | NIBE heat pumps (written for the F750) through the official myUplink API. Per heat pump (address `<deviceId>\|<key>`), reports the curated parameters it has: `outdoorTemperature`, `supplyTemperature`, `returnTemperature`, `hotWaterTop`, `hotWaterCharging`, `exhaustAirTemperature`, `extractAirTemperature`, `calculatedSupplyTemperature` (°C), `degreeMinutes`, `compressorFrequency`, `compressorStarts`, `compressorHours`, `hotWaterCompressorHours`, `additionalHeatPower`, `fanSpeed`, `heatingOffset`, `hotWaterComfortMode`, `hotWaterBoost`, `ventilationSpeed` (enums as their text), `energyHeating` and `energyHotWater` (produced heat, kWh); plus `setTemperature` (the virtual setpoint, °C), `thermostatMode`, `smartHomeMode`, `connected`, `alarm`, `activeAlarms`, `alarmText`, `premiumManage`, `energyToday` (kWh) and `p<id>` for each extra parameter. Commands `setTemperature`, `heatingOffset`, `hotWaterComfortMode`, `hotWaterBoost`, `ventilationSpeed`, `smartHomeMode` (`Default`, `Normal`, `Away`, `Vacation`, `Home`), `setParameter` (`{"parameterId": "47011", "value": 1}`), `p<id>` for writable extra parameters, plus `refresh`. Each heat pump is a Matter thermostat. Polls hourly by default, at most once a minute. |
| Netatmo Security | `token` (secret), `refresh_token`\* (secret), `clientId`\*, `clientSecret`\* (secret) | Dynamic security camera reports plus `refresh` and `set_home` commands; rotated tokens are persisted per client id (see below). |
| Netatmo Weather | as Netatmo Security, plus `stationId` | Weather station and module measurements; rotated tokens are persisted per client id (see below). |
| Timer | No device parameters; uses report-level schedules | Emits the report address whenever the scheduler refreshes that report. |
| Virtual | No device parameters | Stores command values by address and reports each one, with its value and type, on the report template with the same address. |
| Water Consumption | `securityToken`\* (secret), `endpoint` (default the WRM Systems API) | Scheduled latest water meter reading at address `watermeter`, with `unit` and `precision` report parameters. |
| Weather Forecast | `provider` (`openMeteo`, the default, or `fmi`), `latitude` and `longitude` (degrees), `fmiPlace` (with `fmi`, instead of the coordinates), `forecastHours` (default `48`, at most `240`), `endpoint` (empty for the public API) | Hourly forecast series (`kind` `forecast`) from the current hour on, refreshed every hour and sent when they change: `temperature` °C, `precipitation` mm, `windSpeed` m/s, `cloudCover` %, `solarRadiation` W/m², `humidity` %. FMI takes solar radiation from HARMONIE. Since 2.1.0. |
| Web Device | No device parameters | Commands perform HTTP GET/POST through the webhook service; `POST /api/webhook/{address}` publishes matching reports. |

Netatmo devices with the same `clientId` share one set of tokens; different client ids are
separate accounts. Netatmo rotates the refresh token on every refresh, so the latest tokens are
stored in `Data/netatmoAuth-<clientId>.json` and win over the configured ones. The file of
earlier versions, `Data/netatmoAuth.json`, stays with the account that uses it first. When
Netatmo refuses the stored refresh token, the file is deleted and the configured tokens are used.

MELCloud has no public API; the device uses the apps' APIs, like the pymelcloud library and the
MELCloud Home integration for Home Assistant, so a MELCloud change can break it. It keeps its login in
`Data/melcloud-<cloud>-<hash of the e-mail>.json` (classic: the context key; Home: the OAuth tokens,
whose refresh token changes on every refresh), because MELCloud limits logins. A wrong password puts
the device into the error state. While MELCloud is unavailable or limits requests (429), the device
keeps the last state and polls again at the next refresh. A command's change is reported at once;
the unit takes it from MELCloud within about a minute. Use one MELCloud Home device per account:
two would rotate each other's refresh token and log in again on every refresh. The Matter thermostat
has no Auto mode, so a unit in `auto` shows its setpoints but not its mode there. Air-to-water
(Ecodan) units are skipped.

myUplink uses the official API with the OAuth client credentials of your own app: register one at
[dev.myuplink.com](https://dev.myuplink.com) with the account that owns the heat pump and enter its
client id and secret. The device reads the heat pump's parameter list once at start, then polls only
the parameters it reports. The parameter ids are NIBE's and depend on the model and firmware; a
curated id the heat pump doesn't have is skipped. With debug logging, the device logs every parameter
it finds with its id, name and value; add the ones you want to `extraParameters`. Changing settings
through the API needs a myUplink Premium Manage subscription: without it myUplink refuses changes,
and the command fails with an error that says so (`premiumManage` reports whether the subscription is
active). Without a room sensor, the F750 has no target temperature, only the heating curve's offset.
`setTemperature` is a virtual setpoint that a Command Plan or the heat pump workflow templates can
drive: the offset becomes `round((target - baseTemperature) / degreesPerStep)`, and the reported
`setTemperature` is the offset turned back into °C. Tune `degreesPerStep` to how much one offset step
changes your room temperature. The Matter thermostat shows the exhaust air temperature (BT20), which
follows the room temperature with a delay, as its current temperature. `energyHeating` and
`energyHotWater` are heat meters (the heat the pump produced, including additional heat), not the
electricity it used; `energyToday` is their increase since the day's first poll, so it misses what was
produced between midnight and that poll. The day's starting point is kept in
`Data/myuplink-<hash of the device id>.json`, so a restart keeps it. While myUplink is unavailable or
limits requests (429), the device keeps the last state and polls again at the next refresh.

Devices and plugin controllers use the platform configuration and HTTP contracts:

- [Node configuration, templates and plugins](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md)
- [HTTP and gRPC APIs](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md)

## Writing a new device

1. Add a class under `Catalog/`.
2. Derive from `AsyncDeviceBase<TOptions>` (or `AsyncDeviceBase` without parameters), so the node
   can await and cancel its I/O; deriving from `DeviceBase` is deprecated since Core 2.0.0. The
   [plugin authoring guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/plugin-authoring.md)
   describes the members.
3. Implement `IDeviceWithConfiguration` and declare every parameter the device reads; the
   conformance test fails otherwise. Mark the class `[RequiresHardware("reason")]` when it needs a
   remote service or hardware.
4. Implement `ICommandDevice` / `IAsyncCommandDevice` for commands, and
   `IRefreshableReportDevice` for scheduled reports.
5. Register the device with `AddDevice<T>()` in `Plugin.Register()`.

Declare parameters in the options class with `[DeviceParameter("key")]` and keep the key once it is
stored. Keys should be camelCase: the platform serializes dictionary keys as camelCase (binding
matches them case-insensitively).

## Versions and releases

- Release notes are in [CHANGELOG.md](CHANGELOG.md).
- A version tag releases the plugin zip and its `.sha256` sidecar; CI tests every push.
- Release the plugin package with the compatible Node image.

## Contributing

- Instructions for AI coding agents: [AGENTS.md](AGENTS.md).
- Platform documentation: [.github/docs](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/README.md).

## License

See [LICENSE](LICENSE).
