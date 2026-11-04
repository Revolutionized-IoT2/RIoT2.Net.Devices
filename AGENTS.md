# AGENTS.md — RIoT2.Net.Devices

Applies to: this repository. Read the platform guide first:
[.github/AGENTS.md](https://github.com/Revolutionized-IoT2/.github/blob/main/AGENTS.md). It covers the
workspace map, platform-wide rules and the documentation rules. In the local workspace, every
`https://github.com/Revolutionized-IoT2/<Repo>/blob/main/<path>` link is the file
`C:\Src\RIoT2\<Repo>\<path>`; read the local file instead of fetching the URL.

## What this is

The default .NET 10 device plugin catalog for the RIoT2 Node. It is built with
`EnableDynamicLoading`, registers network/cloud/default devices and plugin controllers, and is
packaged as a zip consumed by `RIoT2.Net.Node`.

## Commands

Run from the workspace root (`C:\Src\RIoT2`), in PowerShell:

```powershell
dotnet build .\RIoT2.Net.Devices\RIoT2.Net.Devices.csproj
dotnet build .\RIoT2.Net.Devices\RIoT2.Net.Devices.csproj -c Release
dotnet test .\RIoT2.Net.Devices\Tests\RIoT2.Net.Devices.Tests.csproj -c Release
# The eufy-sdk bridge (Node.js 24.5+): tests, and the bundle the plugin embeds. Commit the rebuilt bundle.
cd .\RIoT2.Net.Devices\EufySdkBridge; npm ci; npm test; npm run build
```

- This is not a standalone executable. To run it, load the built package through
  `RIoT2.Net.Node`.
- Every push runs `.github/workflows/ci.yml` (build, tests, plugin zip as an artifact, secret
  scan). A `x.y.z` tag runs `release.yml`: it validates, then the hub's `plugin-release.yml`
  publishes the project and attaches the zip and `.sha256` sidecar that the publish wrote to the
  GitHub release. `RIoT2.Core.Devices.Build` packs them on every `dotnet publish` (into
  `bin/plugin/`): the assemblies minus those the node ships, plus `PluginManifest.json` with
  `coreVersion`. `RIoT2PluginName` in the csproj is the manifest's `name` ([CI guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/ci.md)).
- RIoT2.Tests builds this repository's `main` and loads it into the Node in its
  [end-to-end tests](https://github.com/Revolutionized-IoT2/RIoT2.Tests/blob/main/README.md#end-to-end-tests), which drive the `Virtual` device.
- Device parameters live in the device's options class (`AsyncDeviceBase<TOptions>`,
  `[DeviceParameter]`); every catalog device has one since 2.0.0 (I45), and none reads a string key.
  Keep the keys stored configurations use. `DeviceOptionsTests` pins the definitions.
- `RIoT2.Core.Devices`, `RIoT2.Core.Http`, `RIoT2.Core.Mqtt`, `RIoT2.Core.Devices.Build` (`PrivateAssets="all"`) and `RIoT2.Core.Devices.Testing` come from GitHub Packages, pinned in
  `Directory.Packages.props` (plan M1 step 4: never `RIoT2.Core.Node`). To try an unreleased Core, restore with `C:\Src\RIoT2\.localfeed` as
  an extra NuGet source. A local feed package is not a release.

## Layout

| Path | Contents |
|---|---|
| `Plugin.cs` | Entry point derived from Core's `PluginBase`; registers services and devices |
| `Catalog/` | Device implementations: Web, Timer, Virtual, MQTT, cloud services, EasyPLC, Hue and others |
| `Controllers/` | Plugin HTTP controllers: webhook and file download |
| `Services/` | Integration services for webhooks, storage, FTP, Azure Relay, APsystems, Eufy and clients; `Services/Eufy/` is the C# port of eufy-sdk the `EufySecurity` device uses (`Cloud/` HTTP API and login, `Push/` FCM/MCS events, `P2P/` station sessions, guard mode and snapshot), behind `EufySecurityService`; `Services/EufySdk/` runs eufy-sdk itself for the `EufySecuritySdk` device (`EufySdkService`, the Node.js launcher, the bridge protocol client, and the generated `eufy-sdk-bridge.mjs` bundle, embedded); `Services/MelCloud/` has the classic MELCloud and MELCloud Home backends of the `MelCloud` device; `Services/MyUplink/` has the myUplink API client of the `MyUplink` device; `Services/Themo/` has the Themo API client of the `Themo` device; `Services/Miele/` has the Miele login (`MieleAuth`) and API client of the `Miele` device; `Services/DirectHttp.cs` is the HTTP client without redirects or cookies that `MelCloud` and `Miele` use |
| `EufySdkBridge/` | npm project of the eufy-sdk bridge: `src/bridge.mjs` (the JSON lines protocol), `src/main.mjs`, `test/` (node:test, fake SDK), `build.mjs` (esbuild bundle with the bundled packages' licenses) |
| `Abstracts/` | Shared Netatmo base device logic; `EufySecurityBase`, the templates, reports and commands of both Eufy devices |
| `Models/` | DTOs for devices and services |
| `Tests/` | MSTest hardware-free regression tests and the shared conformance test (`PluginConformanceTests`) |
| `.github/workflows/ci.yml`, `release.yml` | CI on every push; plugin zip release on a tag |

## Contracts consumed here

This repository implements plugin-side pieces of these hub contracts:

- [configuration.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md):
  device classes read `DeviceParameters` and many emit `DeviceConfiguration` templates from
  `Catalog/*.cs`.
- [http-api.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md):
  `Controllers/WebhookController.cs` serves `POST /api/webhook/{address}` and
  `Controllers/DownloadController.cs` serves plugin download endpoints.
- [mqtt-topics.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/mqtt-topics.md):
  the generic `Catalog/Mqtt.cs` device bridges arbitrary MQTT topics through Core's MQTT helper.

## Rules

- Register new devices with `AddDevice<T>()` and services with `AddService<,>()` in
  `Plugin.Register()`; both are host-owned singletons. Do not create a second service provider or
  call `AddControllers`.
- Keep device parameter keys camelCase. The node/orchestrator configuration path camel-cases
  dictionary keys; options binding matches them case-insensitively.
- Do not hard-code secrets. Credentials belong in orchestrator-managed configuration or mounted
  runtime files under `Data/`.
- Devices derive from `AsyncDeviceBase(<TOptions>)` (`DeviceBase`'s constructor is `[Obsolete]` since Core 2.0.0) and await their I/O with the node's cancellation token,
  HTTP through an `internal virtual SendAsync` the lifecycle tests replace (`DeviceLifecycleTests`).
  No `.Result`, `.Wait()` or `GetAwaiter().GetResult()`: the threading analyzers fail CI (I31).
  Since Core 1.23.0 (I41) `AsyncDeviceBase` supplies `ExecuteCommand`: don't add a sync shim (CS0114),
  and test devices through their async members or `DeviceCalls`.
  `GetConfigurationTemplate` is synchronous: build it from cached data, never call the network.
- Nullable is on (I32). `Refresh`/`RefreshAsync` overrides take `ReportTemplate?` (null: the
  whole device); API payload models in `Models/` turn off CS8618 in a nested `.editorconfig`.
- Every device that reads a parameter implements `IDeviceWithConfiguration` and declares the key
  in its template. `PluginConformanceTests` fails otherwise.
- Mark a device that needs a remote service, hardware or a fixed port with
  `[RequiresHardware("reason")]`, so the conformance test doesn't start it (and never calls a real
  service).
- Keep plugin HTTP routes anonymous to match the platform model. If exposed outside a trusted
  network, put the node behind a reverse proxy or gateway instead of adding per-route auth here.
- Release this plugin package after the Node image is updated to `net10.0`. A `net10.0` plugin
  cannot load into a `net9.0` node; the Node compatibility test covers the reverse direction.
- Keep `PackageReference` items versionless; package versions belong in `Directory.Packages.props`.

## Pitfalls

- Netatmo tokens live in `NetatmoAuthClient`, one per client id, from the singleton
  `NetatmoAuthClientFactory`. Netatmo rotates the refresh token, so two clients for the same app
  would invalidate each other: always get the client from the factory.
- `FTP.cs` has a storage-configuration path with PascalCase `Storage*` keys. Because
  `deviceParameters` keys are camel-cased on download, those names are a C1-style bug if enabled;
  report it instead of renaming keys only in docs.
- Netatmo token files are `Data/netatmoAuth-<clientId>.json`; the first account keeps the older
  `Data/netatmoAuth.json` and its client id is written into it. A valid file takes precedence over
  configured tokens so refreshed credentials survive restarts.
- `Messaging.cs` reads a Firebase service account from `Data/service_account.json`. Do not commit
  or bake that file into images.
- MELCloud's APIs are not public. Classic MELCloud follows pymelcloud; MELCloud Home follows the
  melcloudhome Home Assistant integration, including its OAuth PKCE login through AWS Cognito's form.
  Its requests go through `DirectHttp`, which follows no redirects and keeps no cookies (the Home login
  handles both) and checks the outbound URL policy itself. Logins are kept in `Data/melcloud-<cloud>-<hash>.json`; MELCloud
  limits logins and polling, so keep the one-minute poll floor. Only `MelCloudAuthenticationException`
  is a configuration error; `MelCloudUnavailableException` (429, 5xx, timeouts) keeps the last state.
- myUplink uses the official API (swagger at api.myuplink.com/swagger) with the client credentials
  grant. The curated F750 parameter ids in `MyUplinkCatalog` are NIBE register ids, not checked
  against every model or firmware; add fallback ids rather than replacing them. Min and max are
  multiplied by `scaleValue`, the value and step are not. Writes need Premium Manage: a 403 is
  reported as that, not as a configuration error. `MyUplinkRefusedException` (400, 403, 404, 409)
  isn't retried; `MyUplinkUnavailableException` (429, 5xx, timeouts) keeps the last state.
- Themo uses the public API (swagger at connect.themo.io/swagger) pinned to `api-version=2.1`:
  v2.2 dropped the hourly `/data` endpoint. Its "Price data" series' "Consumption" column is in MWh.
  A command message carries one field (`CMT`, `CMode`, ...), so `setTemperature` outside manual
  mode sends `CMode` and then `CMT`. `energyHour` uses `SendReport` with the hour's time stamp, not
  `SendReportIfValueChanged`, so equal hours are all kept.
- Miele's API (api.mcs3.miele.com/v1) is official, but its login without a browser posts Miele's login
  form (`/oauth/auth`, a 302 whose `Location` has the code), as miele-to-mqtt-gw does; it must go
  through `DirectHttp`, which follows no redirects. The pasted-code flow is the fallback, and each code
  is remembered in the token file so it isn't sent twice. Temperatures are 0.01 °C with -32768 for
  none, times are `[hours, minutes]`. The event stream listener runs from start to stop with its own
  cancellation and back-off; every state change, from it or a poll, goes through `ApplyDevice`, which
  follows runs (`MieleRuns`) under one lock.
- `DownloadController` rejects traversal, absolute paths, encoded slashes and encoded backslashes
  before storage services are called. Keep filename normalization defensive.
- `Services/Eufy/` is ported from eufy-sdk (Apache-2.0, `NOTICE`) at commit `5f92bea5`; every ported file names its
  upstream files in its first line. The eufy protocols are reverse-engineered: change wire formats only from a newer
  eufy-sdk, re-porting its spec vectors into `Tests/Eufy/`, and record the new commit here and in `NOTICE`. Keep the
  contracts in `EufyContracts.cs` stable; `EufySecurityService` composes the clients through
  `IEufyComponentFactory`, which the tests replace. Never log the password, tokens or keys. The session and push
  files (`Data/eufy-session-*.json`, `Data/eufy-push-*.json`) are credentials. Repeated logins trigger captchas: the
  cloud client holds re-logins off, and nothing retries a captcha or verification code on its own. Guard mode and
  snapshots go over P2P only; T9000/T8N00 stations and T8223/T8224 doorbells (WebRTC upstream) are refused.
- `EufySecuritySdk` runs the bundle `Services/EufySdk/eufy-sdk-bridge.mjs`, which is generated: change
  `EufySdkBridge/` and run `npm run build`, never edit the bundle. CI (`eufy-sdk-bridge` job) runs the bridge tests,
  rebuilds and fails when the committed bundle differs, so a Renovate bump of eufy-sdk or esbuild needs the rebuilt
  bundle committed. `build.mjs` turns eufy-sdk's `createRequire(...)("protobufjs")` into a bundled require and fails
  when the bundle would load anything but Node built-ins and `node-datachannel` (native, left out: WebRTC stations
  only). The protocol is JSON lines over stdio; stdout carries only protocol lines, so the bridge sends every log to
  stderr, and never logs the password, tokens or thumbnail URLs. The password goes over stdin, never in arguments or
  the environment. Keep `EventKinds` in `EufySdkService` and `SECURITY_EVENTS` in `bridge.mjs` in step with eufy-sdk's
  `DeviceEventMap`. The bridge passes eufy-sdk its own `openudid`, so it doesn't log the C# port's session out.
- The webhook endpoint is anonymous and caps request bodies at 64 KiB.

## Related work

- [M6](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m06-plugin-configuration-discovery.md):
  plugin configuration discovery and no static device state.
- [M11](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m11-async-cleanup.md):
  blocking and `async void` cleanup (done in I31).
- [M12](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m12-device-plugin-sdk.md) ([ADR 0014](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0014-device-plugin-sdk.md)):
  device plugin SDK (`PluginBase`, conformance test, packaging target, typed options).
- [Design 7.2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/desired-state-configuration.md):
  desired-state configuration and plugin updates.
- Backlog items 4, 5 and 23 in
  [open-issues.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md).
