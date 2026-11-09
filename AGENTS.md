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
| `Services/` | Integration services for webhooks, storage, FTP, Azure Relay, APsystems, Eufy and clients; `Services/Eufy/` is the C# port of eufy-sdk the `EufySecurity` device uses (`Cloud/` HTTP API and login, `Push/` FCM/MCS events, `P2P/` station sessions, guard mode and snapshot), behind `EufySecurityService`; `Services/EufySdk/` runs eufy-sdk itself for the `EufySecuritySdk` device (`EufySdkService`, the Node.js launcher, the bridge protocol client, and the generated `eufy-sdk-bridge.mjs` bundle, embedded); `Services/MelCloud/` has the classic MELCloud and MELCloud Home backends of the `MelCloud` device; `Services/MyUplink/` has the myUplink API client of the `MyUplink` device; `Services/Themo/` has the Themo API client of the `Themo` device; `Services/Govee/` has the Govee Open API client of the `Govee` device; `Services/Miele/` has the Miele login (`MieleAuth`) and API client of the `Miele` device; `Services/Harvia/` has the Cognito SRP login (`CognitoSrp`), the MyHarvia GraphQL client (`HarviaClient`) and the AppSync push connections (`HarviaPush`) of the `Harvia` device; `Services/DefaPower/` has the CloudCharge API client and the energy and cost accounting of the `DefaPower` device; `Services/Netatmo/` has the Netatmo API client and the webhook hub; `Services/GoogleCast/` has the Cast session (`ICastSession`, `SharpCasterSession`), the speech engines (`CastSpeech`) and the audio cache of the `GoogleCast` device; `Services/DirectHttp.cs` is the HTTP client without redirects or cookies that `MelCloud`, `Miele`, `DefaPower` and `Harvia` use |
| `EufySdkBridge/` | npm project of the eufy-sdk bridge: `src/bridge.mjs` (the JSON lines protocol), `src/main.mjs`, `test/` (node:test, fake SDK), `build.mjs` (esbuild bundle with the bundled packages' licenses) |
| `Abstracts/` | Shared Netatmo base device logic; `EufySecurityBase`, the templates, reports and commands of both Eufy devices |
| `Models/` | DTOs for devices and services |
| `Tests/` | MSTest hardware-free regression tests and the shared conformance test (`PluginConformanceTests`) |
| `docs/` | `devices.md`, the device catalog (parameters, reports, commands; Netatmo and Eufy notes), and `cloud-devices.md` (MELCloud, myUplink, Themo, Miele, DEFA Power, Harvia and Govee notes), and `integration-pitfalls.md` (each integration's pitfalls, for changing its code); a new or changed device updates its row there |
| `.github/workflows/ci.yml`, `release.yml` | CI on every push; plugin zip release on a tag |
| `.gitleaks.toml` | The secret scan's reviewed allowlist: synthetic test fixtures, one false positive and the eufy app's public constants, each by rule, path and exact value |

## Contracts consumed here

This repository implements plugin-side pieces of these hub contracts:

- [configuration.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md):
  device classes read `DeviceParameters` and many emit `DeviceConfiguration` templates from
  `Catalog/*.cs`.
- [http-api.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md):
  `Controllers/WebhookController.cs` serves `POST /api/webhook/{address}`,
  `Controllers/NetatmoWebhookController.cs` serves `POST /api/netatmo/webhook/{secret}`,
  `Controllers/CastAudioController.cs` serves `GET /api/cast/audio/{token}`, and
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

- The per-integration pitfalls (Netatmo, MELCloud, myUplink, Themo, Govee, Miele, DEFA Power, Harvia, Eufy and Google
  Cast) are in [docs/integration-pitfalls.md](docs/integration-pitfalls.md). Read the integration's section before
  changing it, and add a new integration's pitfalls there.
- `FTP.cs` has a storage-configuration path with PascalCase `Storage*` keys. Because
  `deviceParameters` keys are camel-cased on download, those names are a C1-style bug if enabled;
  report it instead of renaming keys only in docs.
- `Messaging.cs` reads a Firebase service account from `Data/service_account.json`. Do not commit
  or bake that file into images.
- `DownloadController` rejects traversal, absolute paths, encoded slashes and encoded backslashes
  before storage services are called. Keep filename normalization defensive.
- The webhook endpoints are anonymous and cap request bodies at 64 KiB.

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
