# Changelog

All notable changes to `RIoT2.Net.Devices`. A version is released by pushing a git tag; CI then
uploads a plugin zip as a GitHub release asset.

## [Unreleased]

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
