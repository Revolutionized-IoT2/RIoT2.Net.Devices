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
| `POST` | `/api/netatmo/webhook/{secret}` | Netatmo's webhook pushes for Netatmo Security; the secret is the credential |
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
| Eufy Security | `email`\*, `password`\* (secret) of the eufy account, `countryCode` (default `US`; selects the cloud region), `ffmpegPath` (default `ffmpeg`) | Connects to the eufy cloud and the stations itself (since 3.0.0; no eufy-security-ws container). Reports `eufy\|status` (account state; a pending captcha with its image or a pending verification code), per station `<stationSn>\|guardMode` (eufy value: 0 away, 1 home, 2 schedule, 3-5 custom, 6 off, 47 geofence, 63 disarmed) and `<stationSn>\|alarm` (`phase` triggered, delayed or stopped), and per camera `<deviceSn>` (`SecurityReport` with the push thumbnail: motion, person or a known person's name, stranger, pet or dog, sound, crying, vehicle, doorbell press, package delivered, taken or left). Commands `eufy\|verify` (answers the captcha or verification code), `<stationSn>\|guardMode` and `<deviceSn>\|snapshot` (a still from the live stream, needs ffmpeg). See below. |
| Eufy Security (eufy-sdk) | as Eufy Security | The same reports and commands as Eufy Security, through eufy-sdk itself (`@mega-yfue/eufy-sdk`) in a Node.js child process instead of the C# port. Needs Node.js 24.5 or newer on the PATH, which the Node images include. See below. |
| FTP | `ftpUsers` (secret; `user:password` pairs separated by `\|`), `ftpPort` (default `21`) | File uploads become in-memory photo `SecurityReport` values keyed by FTP username. |
| Hue | `bridgeIpAddress`\*, `apiKey`\* (secret) | Dynamic light command/report templates when running; event-stream updates merge partial state and include Matter endpoint metadata. |
| MELCloud | `cloud` (`classic`, the default: the MELCloud app; or `home`: the MELCloud Home app), `email`\*, `password`\* (secret) | Mitsubishi Electric air-to-air heat pumps. Per unit (address `<unitId>\|<key>`), reports `roomTemperature` and `setTemperature` (°C), `power`, `mode` (`heat`, `dry`, `cool`, `fan`, `auto`), `thermostatMode` (Matter SystemMode: 0 off, 1 auto, 3 cool, 4 heat, 7 fan, 8 dry), `fanSpeed` (`auto`, `1`-`5`), `vaneVertical`, `vaneHorizontal`, `offline`, and `energyConsumed` (classic meter, kWh) or `energyToday` (Home, kWh). Commands `setTemperature`, `power`, `mode`, `thermostatMode`, `fanSpeed`, `vaneVertical`, `vaneHorizontal`, plus `refresh`. Each unit is a Matter thermostat. Polls hourly by default, at most once a minute. |
| Miele | `clientId`\*, `clientSecret`\* (secret) of an app registered at developer.miele.com; `email`, `password` (secret) and `country` (default `fi-FI`) of the Miele account, or `authorizationCode` (secret) with `redirectUri` (default `http://localhost/miele`); `language` (default `en`) | Miele appliances through the Miele 3rd Party API, with live updates from its event stream. Per appliance (address `<fabNumber>\|<key>`), reports `status` (`off`, `on`, `programmed`, `waitingToStart`, `running`, `pause`, `programEnded`, `failure`, `programInterrupted`, `idle`, `rinseHold`, `service`, `notConnected`, ...), `statusCode`, `program`, `programId`, `programPhase`, `remainingTime`, `elapsedTime`, `startDelay` (min), `finishTime` (ISO local time), `door`, `failure`, `info`, `remoteStart`, `connected`, `programEvent` (`started`, `finished`, `interrupted`, `failed`) and `lastRun` (program, start, end, duration, kWh, litres). Ovens add `temperature` and `targetTemperature` (°C), and `coreTemperature` and `coreTargetTemperature` with a food probe; washing machines add `targetTemperature`, `spinningSpeed` (rpm) and `twinDos1Level`/`twinDos2Level` (%); washers, dryers and dishwashers add `energyLastRun` (kWh), `waterLastRun` (l), `energyToday`, `waterToday`, `energyForecast` and `waterForecast` (%). Commands `start`, `stop`, `pause`, `startDelay` (min), `startAt` (ISO time), `power`, `program` (a name or id, or `{"program", "temperature", "duration"}`), `targetTemperature`, plus `refresh`. Oven temperatures are Matter temperature sensors. Polls hourly as a safety net, at most once a minute. |
| Messaging | `firebaseProjectName`, `smtp_Server`, `smtp_User`, `smtp_Password` (secret), `smtp_Port` (default `587`); mail needs the server, user and password | Commands `fb` (Firebase topic message) and `mail` (SMTP email). Firebase service account JSON is read from `Data/service_account.json`. |
| MQTT | `clientId`, `serverUrl`\* (broker host, port 1883), `userName`, `password` (secret), `subscribeTopics` (separated by `;`) | Publishes command payloads to command addresses and reports subscribed topic payloads as text. |
| myUplink | `clientId`\*, `clientSecret`\* (secret) of an app registered at dev.myuplink.com, `language` (default `en-GB`), `extraParameters` (comma-separated parameter ids), `offsetParameter` (default `47011`), `baseTemperature` (default `20`), `degreesPerStep` (default `1`) | NIBE heat pumps (written for the F750) through the official myUplink API. Per heat pump (address `<deviceId>\|<key>`), reports the curated parameters it has: `outdoorTemperature`, `supplyTemperature`, `returnTemperature`, `hotWaterTop`, `hotWaterCharging`, `exhaustAirTemperature`, `extractAirTemperature`, `calculatedSupplyTemperature` (°C), `degreeMinutes`, `compressorFrequency`, `compressorStarts`, `compressorHours`, `hotWaterCompressorHours`, `additionalHeatPower`, `fanSpeed`, `heatingOffset`, `hotWaterComfortMode`, `hotWaterBoost`, `ventilationSpeed` (enums as their text), `energyHeating` and `energyHotWater` (produced heat, kWh); plus `setTemperature` (the virtual setpoint, °C), `thermostatMode`, `smartHomeMode`, `connected`, `alarm`, `activeAlarms`, `alarmText`, `premiumManage`, `energyToday` (kWh) and `p<id>` for each extra parameter. Commands `setTemperature`, `heatingOffset`, `hotWaterComfortMode`, `hotWaterBoost`, `ventilationSpeed`, `smartHomeMode` (`Default`, `Normal`, `Away`, `Vacation`, `Home`), `setParameter` (`{"parameterId": "47011", "value": 1}`), `p<id>` for writable extra parameters, plus `refresh`. Each heat pump is a Matter thermostat. Polls hourly by default, at most once a minute. |
| Netatmo Security | `clientId`\*, `clientSecret`\* (secret), `homeId`, `webhookUrl`, `webhookSecret` (secret), `webhookTakeover`, `downloadSnapshots` | Cameras, doorbell, door tags, siren, smoke and CO alarms: `<id>\|event` reports and their state; presence; monitoring, floodlight and presence commands. Events by webhook and polling. See below. |
| Netatmo Weather | `clientId`\*, `clientSecret`\* (secret), `stationId`, `includeHomeCoach` | What each module measures (with wind and rain), battery, reachability and signal; Home Coaches. See below. |
| Timer | No device parameters; uses report-level schedules | Emits the report address whenever the scheduler refreshes that report. |
| Themo | `email`\*, `password`\* (secret) of the Themo app | Themo smart thermostats through the Themo Public API. Per thermostat (address `<thermostatId>\|<key>`), reports `temperature` (the active sensor), `roomTemperature`, `floorTemperature` (with a floor sensor), `outsideTemperature`, `setTemperature` (°C), `mode` (`off`, `manual`, `sls`), `thermostatMode` (Matter SystemMode: 0 off, 1 auto = SLS, 4 heat = manual), `heating`, `connected`, `lights`, `lock` (`off`, `on`, `hard`, `boost`), `frostProtection`, `frostTemperature`, `schedule`, `energyHour` (kWh of each finished hour, time-stamped to the hour), `energyHourly` (the last 48 hours as a `confirmed` series), `energyToday` (kWh) and `heatingShareToday` (%). Commands `setTemperature`, `mode`, `thermostatMode`, `schedule` (a schedule's name), `lights`, `lock`, `frostProtection`, `frostTemperature`, `boost` (manual mode timer, minutes), plus `refresh`. Each thermostat is a Matter thermostat. Polls hourly by default, at most once a minute. |
| Virtual | No device parameters | Stores command values by address and reports each one, with its value and type, on the report template with the same address. |
| Water Consumption | `securityToken`\* (secret), `endpoint` (default the WRM Systems API) | Scheduled latest water meter reading at address `watermeter`, with `unit` and `precision` report parameters. |
| Weather Forecast | `provider` (`openMeteo`, the default, or `fmi`), `latitude` and `longitude` (degrees), `fmiPlace` (with `fmi`, instead of the coordinates), `forecastHours` (default `48`, at most `240`), `endpoint` (empty for the public API) | Hourly forecast series (`kind` `forecast`) from the current hour on, refreshed every hour and sent when they change: `temperature` °C, `precipitation` mm, `windSpeed` m/s, `cloudCover` %, `solarRadiation` W/m², `humidity` %. FMI takes solar radiation from HARMONIE. Since 2.1.0. |
| Web Device | No device parameters | Commands perform HTTP GET/POST through the webhook service; `POST /api/webhook/{address}` publishes matching reports. |

Netatmo setup (the app, connecting, the webhook through a Cloudflare Tunnel, scopes): the hub's
[Netatmo guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/netatmo.md). Devices with the same
`clientId` share its tokens, kept in `Data/netatmoAuth-<clientId>.json`.

Eufy Security is a C# port of part of [eufy-sdk](https://github.com/mega-yfue/eufy-sdk) (Apache-2.0, see
[NOTICE](NOTICE)): the cloud login and device list, push events over Firebase Cloud Messaging and guard mode and
snapshots over P2P to the station. The protocols are reverse-engineered and unofficial. On the first login the eufy
cloud may ask for a captcha or send a verification code: `eufy|status` then shows `CaptchaRequired` (with the image's
URL) or `TwoFactorRequired`, and the `eufy|verify` command answers it. The session is kept in
`Data/eufy-session-<account>.json` and the push registration in `Data/eufy-push-<account>.json` (the account part
is a hash of the e-mail), so later starts don't ask again; keep the data folder private. Rapid or failed logins make
the cloud ask for a captcha or hold logins off. Stations of models T9000 and T8N00 (and devices on them) and
T8223/T8224 doorbells use WebRTC in eufy-sdk, so their guard mode and snapshot commands are refused; their events
still arrive. The station's guard mode is re-read from the cloud after a mode change push and on a scheduled refresh.

Eufy Security (eufy-sdk) runs eufy-sdk 0.5.0 itself, the same eufy-sdk version the C# port follows, so the two can be
compared. The plugin embeds a bundle of eufy-sdk and a small bridge (built from `EufySdkBridge/`), writes it to
`Data/eufy-sdk/` and runs it with `node`; the device talks to it over stdin and stdout, and starts it again when it
stops. Its parameters, templates, status, captcha and verification flow are those of Eufy Security. Its session and
push registration are separate files (`Data/eufy-sdk-session-<account>.json`, `Data/eufy-sdk-push-<account>.json`), and it
reports its own install id to the cloud, so both devices can be logged in to one account at the same time. The WebRTC
stations (T9000, T8N00, T8223/T8224) need eufy-sdk's native `node-datachannel` module, which isn't included, so
their guard mode and snapshots fail as with Eufy Security.

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

Themo uses the Themo Public API (connect.themo.io) with the Themo app's e-mail and password; the
login's token is kept in memory only. The device asks for API version 2.1, the last one with hourly
device data. Only manual mode follows a target temperature, so `setTemperature` first switches a
thermostat in `sls` (Themo's schedule and price optimization) or `off` mode to `manual`; send `mode`
`sls` or a `schedule` to hand control back to Themo. A Matter setpoint change does the same. Commands
are reported at once; the thermostat applies them with a delay. `energyHour` reports every finished
hour once, at the hour's own time; the last reported hour is kept in
`Data/themo-<hash of the thermostat id>.json`, so after a restart or an outage the device reports the
hours it missed, at most 48. `energyToday` sums the finished hours since local midnight, and
`heatingShareToday` divides it by the thermostat's rated power over those hours. Themo doesn't
document the rated power's unit; the device reads a value up to 1 as MW, up to 100 as kW and a larger
one as W. Without the hourly data, the state and commands still work and the energy reports wait.
The `boost` command needs thermostat software 4.2 or later, and the `boost` lock 4.4.0. The Matter
thermostat has no Auto mode, so a thermostat in `sls` shows its setpoints but not its mode there.

Miele uses the Miele 3rd Party API with your own app: register one at
[developer.miele.com](https://www.miele.com/f/com/en/register_api.aspx) and enter its client id and
secret. The device signs in with the account's e-mail, password and country through Miele's login
form, as several open-source gateways do; if that fails or isn't configured, open the login address
the device's error shows (`https://api.mcs3.miele.com/thirdparty/login?...`, with the app's redirect
URI as `redirectUri`), log in, and paste the code (or the whole address you are sent to) into
`authorizationCode`. Each code is used once. The tokens are kept in
`Data/miele-auth-<hash of the client id>.json` and refreshed before they expire, so a login lasts
months. Miele pushes changes on an event stream that the device keeps open, reconnecting after 5 s
up to 5 minutes; the hourly poll reads everything again. Remote start, a delayed start
(`startDelay`, or `startAt` with a time, e.g. a cheap price slot's start) and choosing a program need
remote start (MobileStart) switched on at the appliance, and a washing machine needs its program
chosen on the machine; a command Miele doesn't allow now fails with the reason. Washing machines
usually don't let their program be chosen remotely, so they get no `program` command. A run starts
when the appliance starts running and ends when the program ends, fails or is interrupted;
`programEvent`, `lastRun`, `energyLastRun` and `waterLastRun` are sent then, time-stamped at the
run's end. The energy and water come from Miele's eco feedback, which only newer appliances have.
Day totals and an open run are kept in `Data/miele-<hash of the fabrication number>.json`, so a
restart in the middle of a run still reports it. Core has no appliance device types for Matter, so
only oven temperatures are bridged, as temperature sensors.

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
