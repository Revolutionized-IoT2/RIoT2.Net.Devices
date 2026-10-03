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
- The tag workflow in `.github/workflows/main.yml` builds Release, writes `PluginManifest.json`,
  zips a hand-maintained dependency list and uploads the zip as a GitHub release asset.
- `RIoT2.Core` 1.0.1 is published on GitHub Packages. To try an unreleased Core, restore with
  `C:\Src\RIoT2\.localfeed` as an extra NuGet source. A local feed package is not a release.

## Layout

| Path | Contents |
|---|---|
| `Plugin.cs` | `IDevicePlugin` entry point; registers services, devices and controllers |
| `Catalog/` | Device implementations: Web, Timer, Virtual, MQTT, cloud services, EasyPLC, Hue and others |
| `Controllers/` | Plugin HTTP controllers: webhook and file download |
| `Services/` | Integration services for webhooks, storage, FTP, Azure Relay, APsystems, Eufy and clients |
| `Abstracts/` | Shared Netatmo base device logic |
| `Models/` | DTOs for devices and services |
| `Tests/` | MSTest hardware-free regression tests |
| `.github/workflows/main.yml` | Tag-triggered release asset workflow |

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

- Register new devices in `Plugin.Initialize(IServiceCollection)` as host-owned `IDevice`
  singletons. Do not create a second service provider.
- Keep device parameter keys camelCase. The node/orchestrator configuration path camel-cases
  dictionary keys and `DeviceBase.GetConfiguration<T>(key)` is case-sensitive.
- Do not hard-code secrets. Credentials belong in orchestrator-managed configuration or mounted
  runtime files under `Data/`.
- Prefer `AsyncDeviceBase` and async command/refresh interfaces for network I/O. Keep synchronous
  compatibility shims only where the legacy Core interfaces require them.
- Device templates must match the keys read by `GetConfiguration<T>`. Missing templates block UI
  configuration discovery and are tracked by M6.
- Keep plugin HTTP routes anonymous to match the platform model. If exposed outside a trusted
  network, put the node behind a reverse proxy or gateway instead of adding per-route auth here.
- Release this plugin package after the Node image is updated to `net10.0`. A `net10.0` plugin
  cannot load into a `net9.0` node; the Node compatibility test covers the reverse direction.
- Keep `PackageReference` items versionless; package versions belong in `Directory.Packages.props`.

## Pitfalls

- `AzureRelay`, `EasyPLC`, `FTP` and `Mqtt` read configuration but do not implement
  `IDeviceWithConfiguration`; the UI cannot discover their parameters (backlog item 16 / M6).
- `NetatmoBase` stores tokens, client id/secret, configured state and logger in static fields.
  `NetatmoWeather` and `NetatmoSecurity` therefore share one account implicitly.
- `FTP.cs` has a storage-configuration path with PascalCase `Storage*` keys. Because
  `deviceParameters` keys are camel-cased on download, those names are a C1-style bug if enabled;
  report it instead of renaming keys only in docs.
- The Netatmo token file is `Data/netatmoAuth.json`. A valid file takes precedence over configured
  tokens so refreshed credentials survive restarts.
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
- [Design 7.2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/desired-state-configuration.md):
  desired-state configuration and plugin updates.
- Backlog items 4, 5, 16, 18 and 20 in
  [open-issues.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md).
