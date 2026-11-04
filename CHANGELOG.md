# Changelog

All notable changes to `RIoT2.Net.Devices`. A version is released by pushing a git tag; CI then
uploads a plugin zip as a GitHub release asset.

## [Unreleased]

**New: Google Cast speaker** (additive).

- Added: **Google Cast speaker** (`GoogleCast`), for voice announcements on Google Home, Nest and other Cast
  speakers and speaker groups from workflows ("Sauna is ready"). Parameters `ipAddress`, `port` (default `8009`),
  `ttsEngine` (`googleTranslate` or `googleCloud`), `language` (default `en-GB`), `voice`, `cloudTtsApiKey`,
  `nodeBaseUrl` (default the node's `RIOT2_NODE_URL`), `announcementVolume` and `restoreVolume`. Commands `speak`,
  `playUrl`, `setVolume` and `stop`. Each command connects to the speaker; `speak` and `playUrl` set the announcement
  volume and set it back afterwards, also after a failure. A failed command fails with the reason, which the device
  shows while it keeps running. Uses SharpCaster 3.0.0 (MIT; with Google.Protobuf, Zeroconf and System.Reactive in
  the zip). Guide: [google-cast.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/google-cast.md).
- Added: `GET /api/cast/audio/{token}` (anonymous; the random token is the credential) serves the Google Cloud
  Text-to-Speech audio the speaker downloads, kept in memory for an hour.
- Changed: the device catalog moved from the README to `docs/devices.md` and `docs/cloud-devices.md`.

**Breaking: Netatmo Security and Netatmo Weather** (needs Node with Core 2.4.0). They follow the current Netatmo
Connect API: security events arrive through Netatmo's webhook, both devices can be connected to Netatmo from the UI,
and their templates change, so add them again from their templates. Guide:
[netatmo.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/netatmo.md).

- Added: both Netatmo devices implement Core 2.4.0's `IDeviceWithAuthorization`. The UI's **Connect to Netatmo**
  signs in with an OAuth authorization code; the node exchanges it with the app's secret and stores the tokens in
  `Data/netatmoAuth-<clientId>.json`, as refreshed tokens before. One sign-in asks for the scopes of every device of
  the same app. `token` and `refresh_token` are optional now; a configured refresh token alone is used too (before,
  only an access token was).
- Added: Netatmo Security receives Netatmo's webhook at `POST /api/netatmo/webhook/{secret}` (anonymous; the secret
  is the credential; answered at once and handled off the request). With `webhookUrl` and `webhookSecret` it
  registers the app's one webhook through `webhooks/v1`, leaves another integration's webhook alone unless
  `webhookTakeover` is set, backs off when Netatmo refuses, and registers again after a deactivation. While the
  webhook works, refreshes poll every 15 minutes only, to catch missed pushes; pushed and polled events are reported
  once.
- Changed: Netatmo Security's templates: per module `<id>|event` for cameras (`NACamera`, `NOC`, `NPC`), the doorbell
  (`NDB`), door tags, the siren and smoke and CO alarms; `<id>|monitoring`, `<id>|connection`, `<id>|floodlight`
  (outdoor cameras) and `<id>|battery`; `person|<id>` for named persons; `home|webhook`. Commands: `refresh`,
  `<camera>|monitoring`, `<outdoor camera>|floodlight` (`on`, `off`, `auto`), `person|<id>` and `home|away`; `set_home`
  (which set the first person home) is gone. New parameters: `homeId` (default: the home with security modules,
  before the first home), `webhookUrl`, `webhookSecret`, `webhookTakeover`, `downloadSnapshots` (default `true`).
- Changed: `SecurityReport` gains `moduleId`, `eventId`, `time` and `personId`, and `SecurityEventType` gains
  `smokeDetected`, `coDetected`, `doorOpened`, `tagMoved`, `sirenSounding`, `tampered`, `doorbellCallAccepted`,
  `doorbellCallMissed` and `connectionLost` (existing values unchanged). Netatmo reports carry the event's time.
  An unknown Netatmo event type is no longer reported as motion, and past events aren't reported again at a start.
- Changed: Netatmo Weather reports per module what its `data_type` lists, so an outdoor module no longer reports
  `CO2` and `Noise` as 0, plus wind (`WindStrength`, `WindAngle`, `GustStrength`, `GustAngle`, `max_wind_str`), rain
  (`Rain`, `sum_rain_1`, `sum_rain_24`) and `min_temp`/`max_temp`, and `<id>|battery`, `<id>|reachable` and
  `<id>|signal`. A value Netatmo didn't send isn't reported, an upload already reported is skipped, and reports carry
  the measurement's time. `stationId` is optional (empty: every station); `includeHomeCoach` adds Healthy Home
  Coaches with `health_idx`. Template names include the module name. The `<id>|<field>` addresses are unchanged.
- Changed: when Netatmo is unreachable, limits the calls (code 26) or refuses the tokens, a Netatmo device keeps
  running with the reason as its state message (before: the error state until a restart), waits 15 minutes after a
  rate limit, and shows "connect again" in its account status. A snapshot that can't be downloaded keeps Netatmo's
  URL instead of failing the device.
- Fixed: the Netatmo devices' default refresh schedules ran only between 00:00 and 00:59; both run every 5 minutes.
- Fixed: a rate limit, an outage or a proxy's error page at Netatmo's token endpoint no longer deletes the stored
  Netatmo tokens; only a refused grant (`invalid_grant`, `invalid_client`) does.

**Breaking: Eufy Security.** The device no longer needs the eufy-security-ws container; it connects to the eufy
cloud and the stations itself, through a C# port of part of [eufy-sdk](https://github.com/mega-yfue/eufy-sdk)
(Apache-2.0, see `NOTICE`). Release as 3.0.0.

- Changed: Eufy Security's parameters are `email`, `password` (secret), `countryCode` (default `US`) and `ffmpegPath`
  (default `ffmpeg`); `serviceIp` and `port` are gone, so a configured device fails to start until it is set up
  again. Its templates: per station `<stationSn>|guardMode` (report and command, eufy values as before) and the new
  `<stationSn>|alarm`; per camera `<deviceSn>` and the new `<deviceSn>|snapshot` command; `eufy|status` and the
  `eufy|verify` command for the first login's captcha or verification code. Several stations are supported (before:
  the first one). The session and push registration are kept in `Data/eufy-session-<account>.json` and
  `Data/eufy-push-<account>.json`.
- Added: Eufy Security reports crying, dog, doorbell press and package delivered, taken and left events, a known
  person's name, and the event's thumbnail; `SecurityEventType` gains `doorbellPressed`, `packageDelivered`,
  `packageTaken`, `packageStranded`, `cryingDetected` and `snapshot` (existing values unchanged).
- Fixed: a Eufy pet detection no longer throws (`petDetected` isn't a `SecurityEventType`; it is `animalDetected`).
- Removed: the eufy-security-ws WebSocket client and its message models (`EufyCommand`, `EufyMessage`,
  `EufyEventMessage`, `EufyResult`, `EufyVersionInfo`).
- Not supported: guard mode and snapshots on T9000 and T8N00 stations and T8223/T8224 doorbells (WebRTC in
  eufy-sdk). The snapshot needs ffmpeg, which the Node images include from their next release.
- Added: the `EufySecuritySdk` device ("Eufy Security (eufy-sdk)"), a parallel Eufy Security device that runs eufy-sdk
  itself (`@mega-yfue/eufy-sdk` 0.5.0) in a Node.js child process instead of the C# port. Its parameters, templates,
  reports and commands are those of Eufy Security (both derive from `EufySecurityBase`). The plugin embeds the bundled
  bridge (`EufySdkBridge/`, built into `Services/EufySdk/eufy-sdk-bridge.mjs`); it needs Node.js 24.5 or newer, which the
  Node images include from their next release. It keeps its own session and push files
  (`Data/eufy-sdk-session-<account>.json`, `Data/eufy-sdk-push-<account>.json`) and install id, so it can run next to
  Eufy Security on one account.

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
- Added: the `Themo` device for Themo smart thermostats through the Themo Public API, with the Themo app's `email`
  and `password`. It reports each thermostat's temperatures, target, mode, heating state, backlight, child lock,
  frost protection and active schedule, and its energy: every finished hour (`energyHour`, time-stamped to the
  hour, missed hours caught up after a restart), the last 48 hours as a series, `energyToday` and
  `heatingShareToday`. Its commands set the target temperature (switching to manual mode, so the heat pump
  workflow templates can drive it), the mode, the schedule, the backlight, the child lock, frost protection and
  the timer boost. Each thermostat is a Matter thermostat.
- Added: the `Miele` device for Miele appliances through the Miele 3rd Party API, with the `clientId` and
  `clientSecret` of an app registered at developer.miele.com. It signs in with the account's `email`, `password`
  and `country`, or a pasted `authorizationCode`, and keeps and refreshes the tokens. Miele's event stream brings
  changes as they happen. It reports each appliance's status, program, phase, times and finish time, signals,
  temperatures, spinning speed and TwinDos levels, program events, each run's energy and water (`lastRun`) and the
  day's totals. Its commands start, stop and pause, delay the start (`startDelay`, `startAt`), switch on or off,
  choose a program and set the temperature, checked against what Miele allows at the time. Oven temperatures are
  Matter temperature sensors.
- Changed: the HTTP client without redirects and cookies that `MelCloud` used is now `DirectHttp`, shared with
  `Miele`.
- Added: the `DefaPower` device for DEFA Power EV chargers through the CloudCharge API that the DEFA Power app
  uses, with the account's `phoneNumber`. It logs in with an SMS code (the `requestSmsCode` and `login` commands,
  reported in `loginState`) as the CloudCharge app, so the DEFA Power app stays logged in, and keeps the login in
  `Data/`. It reports each connector's status, charging state, power, current limit, connection, error code, meter
  and session energy, eco mode and what pauses the charging. From the meter it works out the energy of every
  quarter-hour and hour (time-stamped to their start, missed ones caught up after a restart), the last 48 hours as
  a 15-minute series, `energyToday`, and a `session` record when a car is unplugged; with ElectricityPrice's prices
  sent to its `prices` command, the cost of each quarter, of the day and of each session. Its commands start and
  stop the charging, set the current limit, eco mode and its hours to charge and the manual schedules, charge now
  and restart the charger. It polls every 15 seconds while charging and every minute otherwise. It shares
  `DirectHttp`.
- Added: the `Harvia` device for Harvia saunas with a Xenio WiFi panel through the MyHarvia cloud that the MyHarvia
  app uses, with the account's `email` and `password` (AWS Cognito SRP login; the refresh token is kept in
  `Data/`). MyHarvia pushes changes over AppSync WebSockets, renewed every 5 minutes, and a poll every 5 minutes
  reads everything again. It reports each sauna's state, temperature, humidity, target, remaining time, heating,
  door, light and connection, its sessions (`sessionEvent`, `lastSession`, `sessionsToday`, `bathingMinutesToday`),
  readiness (`ready`, `timeToReady`, `readyAt`) and the panel's own schedule, and with `heaterPower` an energy
  estimate (`heaterPowerNow`, `energyLastSession`, `energyToday`). Its commands switch the sauna on or off (refused
  while the door is open), set the target temperature (40-110 °C, a bare number too) and switch the light. Each
  sauna is a Matter thermostat (setpoint 40-110 °C, needs the Orchestrator that takes thermostat limits from the
  setpoint command's range) with temperature, humidity and door sensors and an on/off light.

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
