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
```

- This is not a standalone executable. To run it, load the built package through
  `RIoT2.Net.Node`.
- Every push runs `.github/workflows/ci.yml` (build, tests, plugin zip as an artifact, secret
  scan). A `x.y.z` tag runs `release.yml`: it validates, then the hub's `plugin-release.yml`
  publishes the project, zips its assemblies minus those the node ships, writes
  `PluginManifest.json` (with `coreVersion`) and attaches the zip and a `.sha256` sidecar to the
  GitHub release ([CI guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/ci.md)).
- RIoT2.Tests builds this repository's `main` and loads it into the Node in its
  [end-to-end tests](https://github.com/Revolutionized-IoT2/RIoT2.Tests/blob/main/README.md#end-to-end-tests), which drive the `Virtual` device.
- `RIoT2.Core` and `RIoT2.Core.Devices.Testing` come from GitHub Packages, pinned in
  `Directory.Packages.props`. To try an unreleased Core, restore with `C:\Src\RIoT2\.localfeed` as
  an extra NuGet source. A local feed package is not a release.

## Layout

| Path | Contents |
|---|---|
| `Plugin.cs` | Entry point derived from Core's `PluginBase`; registers services and devices |
| `Catalog/` | Device implementations: Web, Timer, Virtual, MQTT, cloud services, EasyPLC, Hue and others |
| `Controllers/` | Plugin HTTP controllers: webhook and file download |
| `Services/` | Integration services for webhooks, storage, FTP, Azure Relay, APsystems, Eufy and clients |
| `Abstracts/` | Shared Netatmo base device logic |
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
  dictionary keys and `DeviceBase.GetConfiguration<T>(key)` is case-sensitive.
- Do not hard-code secrets. Credentials belong in orchestrator-managed configuration or mounted
  runtime files under `Data/`.
- Prefer `AsyncDeviceBase` and async command/refresh interfaces for network I/O. Keep synchronous
  compatibility shims only where the legacy Core interfaces require them.
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
- `DownloadController` rejects traversal, absolute paths, encoded slashes and encoded backslashes
  before storage services are called. Keep filename normalization defensive.
- The webhook endpoint is anonymous and caps request bodies at 64 KiB.

## Related work

- [M6](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m06-plugin-configuration-discovery.md):
  plugin configuration discovery and no static device state.
- [M11](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m11-async-cleanup.md):
  remaining blocking and `async void` cleanup.
- [M12](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m12-device-plugin-sdk.md) ([ADR 0014](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0014-device-plugin-sdk.md)):
  device plugin SDK (`PluginBase`, conformance test, packaging target, typed options).
- [Design 7.2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/desired-state-configuration.md):
  desired-state configuration and plugin updates.
- Backlog items 4, 5 and 23 in
  [open-issues.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md).
