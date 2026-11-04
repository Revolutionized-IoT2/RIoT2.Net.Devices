# Changelog

All notable changes to `RIoT2.Net.Devices`. A version is released by pushing a git tag; CI then
uploads a plugin zip as a GitHub release asset.

## [Unreleased]

- Added: the `MelCloud` device for Mitsubishi Electric air-to-air heat pumps through MELCloud: classic MELCloud
  (`cloud` `classic`, the default) or MELCloud Home (`home`), with the account's `email` and `password`. It reports
  each unit's room and target temperature, power, mode, fan speed, vanes, connection and energy, and its commands
  set them; `setTemperature` takes the bare number a Command Plan sends, so the heat pump workflow templates can
  drive it. Each unit is a Matter thermostat on the Control Bridge. The login is kept in
  `Data/melcloud-<cloud>-<account>.json`.
- Added: the `MyUplink` device for NIBE heat pumps (written for the F750) through the official myUplink API, with
  the `clientId` and `clientSecret` of an app registered at dev.myuplink.com. It reports a curated set of
  parameters (temperatures, degree minutes, compressor, ventilation, hot water and produced heat), parameters
  added with `extraParameters`, alarms, the smart home mode and `energyToday`. Its commands set the heating
  offset, hot water, ventilation, the smart home mode and any writable parameter (`setParameter`). Without a room
  sensor, `setTemperature` is a virtual setpoint that becomes a heating offset (`baseTemperature`,
  `degreesPerStep`), so the heat pump workflow templates can drive it. Each heat pump is a Matter thermostat.
  Changes need a myUplink Premium Manage subscription.

## [2.1.0] - 2026-11-04

Forecasts ([ADR 0019](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0019-forecasts-and-command-plans.md)).
Built on `RIoT2.Core.Devices` 2.3.0: **needs Node 2.1.0**. Stored configurations load unchanged.

- Added: the `WeatherForecast` device. It gets an hourly forecast from Open-Meteo (`provider` `openMeteo`, no key)
  or FMI open data (`fmi`; the edited forecast, and HARMONIE for solar radiation), and reports `temperature`,
  `precipitation`, `windSpeed`, `cloudCover`, `solarRadiation` and `humidity` as forecast series (`ForecastSeries`).
- Added (Electricity Price): the `prices` report, today's and tomorrow's prices as a confirmed forecast series in
  c/kWh with VAT. Add the report template to an existing device to get it. Tomorrow's prices are asked for once
  today's run out within 13 hours, at most every 30 minutes; a failed request keeps the prices the device has.
- Changed (Electricity Price): it requests yesterday to the day after tomorrow (UTC) and reads every `TimeSeries` and
  `Period`, curve A03 (a missing position repeats the price before it), and both PT15M and PT60M (the finer wins).
  The current price is the slot that contains the current UTC time. It used to assume the node's clock was one hour
  ahead of CET, which was wrong in summer time. The `price` report is otherwise unchanged. The cache is now
  `Data/priceData.json`; the old `Data/priceData.xml` is no longer read.
## [2.0.0] - 2026-11-03

Core 2.0 ([I45](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-3-4.md#i45-core-20-remove-compatibility-layers-m); backlog item 23 done). **Needs Node 2.0.0**, which refuses the 1.x plugins.
Stored configurations load unchanged: every parameter key keeps its spelling.

- Changed: built on `RIoT2.Core.Devices`, `.Http` and `.Mqtt` 2.0.0. The zip now holds
  `Newtonsoft.Json.dll` (Google.Apis needs it), because the node no longer ships it.
- Changed: `Timer` and `Virtual` derive from `AsyncDeviceBase` (`Virtual` implements
  `IAsyncCommandDevice`). No catalog device uses the synchronous `DeviceBase` any more.
- Changed: typed options for every device that reads parameters: `ApSystemsOptions`,
  `ElectricityPriceOptions`, `EufySecurityOptions`, `HueOptions`, `WaterConsumptionOptions`,
  `NetatmoOptions` (both Netatmo devices; `NetatmoBase<TOptions>`) and `NetatmoWeatherOptions`. The
  templates gain `parameterDefinitions`, the UI masks secrets, and a missing required value
  (credentials, the Hue bridge, the eufy service host) puts the device into the error state at start
  with one message, instead of failing later. Defaults: Electricity Price `domain` and `endpoint`,
  Water Consumption `endpoint`, Eufy `port` `3000`.
- Tests: the Virtual and the new Timer tests run the async lifecycle.

## [1.7.0] - 2026-11-02

Async-first devices ([I41](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-3-4.md#i41-async-first-plugins-and-the-plugin-template-m)). Built on RIoT2.Core 1.23.0: **install into Node 1.15.0 or newer**. No
parameter, template or behaviour changes.

- Changed: `Web` derives from `AsyncDeviceBase`, so every catalog device with I/O is async.
- Removed: `LegacySync` and the synchronous `ExecuteCommand` shims of Hue, Mqtt, Messaging, EasyPLC,
  EufySecurity, NetatmoSecurity and Web; `AsyncDeviceBase` supplies `ExecuteCommand`, and the node
  calls `ExecuteCommandAsync`.
- Tests: the EasyPLC, ElectricityPrice and Hue tests call the async members.
- Build: `renovate.json` extends the shared Renovate preset ([I33](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i33-supply-chain-s)). Renovate opens grouped
  update pull requests once the app is installed (MA7).
- Release: the plugin zip gets `<zip>.spdx.json`, an SPDX SBOM of every package the plugin
  depends on, on the GitHub release.

## [1.6.0] - 2026-11-02

Nullable reference types ([I32](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i32-nullable-reference-types-m)). Built on RIoT2.Core 1.17.0: install into Node 1.12.0 or newer.
No template, parameter or report changes.

- Build: nullable is enabled, and CI fails on a nullable warning. The API payload models turn off
  CS8618 in a nested `.editorconfig`; a member the code checks for null is nullable.
- Fixed (found by the annotations): a Netatmo security event or subevent without a snapshot, and
  a module without snapshot events, no longer throw (the event is reported without an image or
  skipped). Stopping the FTP device twice no longer throws. A received FTP file or Azure Relay
  message with no subscriber no longer throws (the relay's HTTP response is closed). A Eufy
  guard-mode report with no value no longer throws.

## [1.5.0] - 2026-11-02

Async device drivers ([I31](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i31-async-cleanup-part-2-and-threading-analyzers-m), plan M11 step 3, backlog item 4). Built on RIoT2.Core 1.16.0:
install into Node 1.11.0 or newer, as 1.4.0. No template, parameter or report changes.

- Changed: ApSystems, ElectricityPrice, WaterConsumption, NetatmoWeather, NetatmoSecurity, Hue,
  Messaging, AzureRelay, FTP and Mqtt derive from `AsyncDeviceBase`: their starts, refreshes and
  commands await the network instead of blocking a scheduler or node thread, and a cancelled
  configuration or shutdown cancels them. Messaging sends mail with `SendMailAsync`.
- Changed: the templates of NetatmoWeather and Hue list the stations and lights read at the last
  start or refresh instead of calling Netatmo or the bridge while the node builds them.
- Fixed: NetatmoSecurity handled no further events after one that matched no report template, and
  its `refresh` command returned before the refresh ended; `set_home` before the home is loaded is
  now rejected (the orchestrator delivers it again) instead of failing.
- Fixed: an APsystems or Netatmo snapshot request that times out puts the device into the error state
  with the reason, also on Node 1.11.0.
- Fixed: AzureRelay and FTP no longer start their listener without observing it: an FTP server that
  can't start (port in use) puts the device into the error state, and stopping waits for it.
- Build: the threading analyzers fail the CI build on a blocking wait, `async void` or an
  unobserved task; the synchronous `ExecuteCommand` shims go through one `LegacySync.Wait`.

## [1.4.0] - 2026-11-02

Typed device options ([I30](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i30-typed-device-options-m), plan M12 step 4). Built on RIoT2.Core 1.16.0: **install into
Node 1.11.0 or newer**. Parameter keys are unchanged, so stored configurations load as before.

- Changed: AzureRelay, EasyPLC, FTP, Mqtt and Messaging read their parameters from options classes
  (`AzureRelayOptions`, `EasyPlcOptions`, `FtpOptions`, `MqttOptions`, `MessagingOptions`). Their
  templates list every parameter with its type, default, description and whether it is required or
  secret, instead of placeholder values, and the UI shows typed fields.
- Changed: a missing required value (`relayNamespace`, `connectionName`, `keyName`, `key`,
  `ipAddress`, `serverUrl`) or a non-numeric port puts the device into the error state, with the
  reason in its status. EasyPLC's `port` defaults to 10001 (it was 0 when missing), FTP's
  `ftpPort` to 21 and Messaging's `smtp_Port` to 587. An empty MQTT `userName` or `password`
  connects anonymously (no empty user name).
- Removed: FTP's commented-out `Storage*` parameter reads.

## [1.3.1] - 2026-11-02

Plugin packaging ([I29](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i29-plugin-packaging-s), plan M12 step 3). Built on RIoT2.Core 1.14.0: install into Node
1.10.0 or newer. The zip holds the same files as 1.3.0; no device, template or parameter changes.

- Changed: the plugin zip, `PluginManifest.json` and the `.sha256` sidecar come from Core's
  `RIoT2.Core.Devices.Build` 1.15.0 (`PrivateAssets="all"`) on `dotnet publish`, so a local publish
  writes the release zip to `bin/plugin/`. The manifest `name` is `RIoT2PluginName` in the csproj;
  the workflows no longer pass the name.

## [1.3.0] - 2026-11-01

Narrow Core references ([I28](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-2.md#i28-narrow-references-and-generated-models-m), plan M1 step 4). Built on RIoT2.Core 1.14.0: install into Node
1.10.0 or newer (an older node doesn't load it). No device, template or parameter changes.

- Changed: references the plugin SDK `RIoT2.Core.Devices`, `RIoT2.Core.Http` (`Web`, for the
  catalog devices that call web APIs) and `RIoT2.Core.Mqtt` (the `Mqtt` device's client) instead
  of the `RIoT2.Core` facade 1.5.0; the tests use `RIoT2.Core.Devices.Testing` 1.14.0.
- Changed: `EufyCommand` serializes with `Json.SerializeIgnoreNulls` instead of the `[Obsolete]`
  `SerializeAutoTypeNameHandling` (the same camelCase JSON without nulls).

## [1.2.0] - 2026-10-14

Plugin configuration and conformance ([I10](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i10-plugin-configuration-and-conformance-m)). Built on `RIoT2.Core` 1.5.0: install into a
Node 1.3.0 or newer, which has `PluginBase`.

- Added: configuration templates for `AzureRelay` (`relayNamespace`, `connectionName`, `keyName`,
  `key`), `EasyPLC` (`ipAddress`, `port`), `FTP` (`ftpUsers`, `ftpPort`) and `Mqtt` (`clientId`,
  `serverUrl`, `userName`, `password`, `subscribeTopics`), so the UI shows their parameters
  (backlog item 16).
- Changed: Netatmo tokens belong to one Netatmo app (client id) instead of static fields, so two
  Netatmo accounts work side by side; devices with the same client id still share tokens, and only
  one of them refreshes at a time. Each account stores its tokens in
  `Data/netatmoAuth-<clientId>.json`. The existing `Data/netatmoAuth.json` stays with the account
  that uses it first (its client id is written into the file), so nobody has to log in again.
- Changed: when Netatmo refuses a refresh token, the configured tokens are used again at once; they
  used to be used only after a restart. A network error during a refresh keeps the stored tokens
  instead of deleting them. The configured tokens are no longer written to the token file at
  start; the file is written after the first refresh.
- Changed: `Plugin` derives from Core's `PluginBase` and no longer calls `AddControllers`; the node
  adds the plugin's controllers.
- Changed: devices that need a remote service or a port are marked `[RequiresHardware]`.
- Fixed: the `Mqtt` device no longer fails to start when `subscribeTopics` is missing, and ignores
  empty topics.
- Tests: the shared conformance test (`RIoT2.Core.Devices.Testing`) runs in CI.

## [1.1.0] - 2026-10-13

Async cleanup, part 1 ([I09](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i09-async-cleanup-part-1-s)). Built on `RIoT2.Core` 1.4.0: install into a Node 1.2.1 or newer.

- Changed: `EufySecurityService` handles the WebSocket messages through a bounded queue (1,000) and
  one worker, in arrival order; a message that fails is logged and the next one is still handled.
  The handler used to be `async void`. `Start`/`Stop` became `StartAsync`/`StopAsync`, which no
  longer block on the connection, and the service is `IAsyncDisposable`.
- Fixed: after its first stop, the Eufy Security service could never connect again, because every
  later start used the already cancelled token. Restarting or reconfiguring the device now works.
- Changed: the `EufySecurity` device derives from `AsyncDeviceBase` and implements
  `IAsyncCommandDevice`; the start waits for the initial properties without blocking a thread. A
  station that hasn't reported its properties yet no longer puts the device into the error state.
- Changed: the `Mqtt` device publishes commands with `MqttClient.PublishAsync` instead of the
  obsolete `Publish`. No behaviour change.
- Fixed: the `Virtual` device reported a command's value still serialized as JSON, so the text
  command `Living room` came back as the report `"\"Living room\""` and every number or boolean as
  text. It now reports the commanded value with its type ([I06](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i06-in-process-end-to-end-harness-m), found by the end-to-end tests).

## [1.0.1] - 2026-10-12

Install into a Node 1.0.0 or newer.

- Changed the plugin zip: it is built from the `dotnet publish` output minus the assemblies the
  node ships, instead of a hand-written list. It now also contains
  `System.IdentityModel.Tokens.Jwt.dll`, which the list had missed. `PluginManifest.json` gains
  `coreVersion`, and the release has a `<zip>.sha256` sidecar.
- Fixed: `GET /api/download/{filename}` rejects `\`, `/` and `:` on every OS. On Linux, names
  such as `..\secret.jpg` and `C:\Windows\win.ini` were accepted as plain file names.
- CI: every push runs the tests, packs the zip as an artifact and scans for secrets; a tag
  validates before it releases.

## [1.0.0] - 2026-10-12

Built on `RIoT2.Core` 1.0.1 and `net10.0`: install it only into a Node `1.0.0` or newer.

- Changed the plugin catalog and tests to `net10.0`, with `MSTest.Sdk` 4.4.1.
- Changed the Core dependency to `RIoT2.Core` 1.0.1 through central package management.
- Updated the release workflow expectations to .NET 10 SDK and `bin\Release\net10.0` outputs.
- Hue: the event stream reads lines asynchronously until the stream ends, instead of polling
  `EndOfStream`, which blocked a thread on the network stream.
- Documentation: `AGENTS.md` is the AI instruction file, `CLAUDE.md` imports it, and version notes
  moved from the README to this file.

## [0.1.48] - 2026-09-23

### Changed

- The plugin catalog targets `RIoT2.Core` 0.1.43 and should be released with the matching Node
  image.
- EasyPLC uses async/cancellable connection, command and refresh I/O with a five-second transaction
  deadline, CRC-checked frames and explicit failure for unsupported non-zero marker-write
  expansions.
- Hue event updates merge partial light state instead of resetting missing fields.

### Added

- Hardware-free regression tests cover Netatmo token persistence, Hue event JSON, EasyPLC streams
  and plugin lifecycle behaviour.
- Netatmo refreshed credentials are persisted in `Data/netatmoAuth.json` and take precedence on
  restart.

### Security

- Download filenames are normalized before storage services are called; traversal, absolute paths,
  encoded slashes and encoded backslashes are rejected.
- `POST /api/webhook/{address}` has a 64 KiB request-size limit.

## Earlier versions

Tags `0.1.0` through `0.1.47`. See `git log` and the tags; there are no release notes for them in
the migrated documentation.
