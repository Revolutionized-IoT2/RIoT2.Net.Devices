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
| `Controllers/` | `WebhookController`, `NetatmoWebhookController`, `DownloadController` and `CastAudioController` |
| `Services/` | Supporting services for integrations and storage |
| `Models/` | DTOs used by devices and services |
| `Tests/` | Hardware-free regression tests and the plugin conformance test |
| `docs/` | The [device catalog](docs/devices.md) and the [cloud device notes](docs/cloud-devices.md) |

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
| `GET` | `/api/cast/audio/{token}` | The Google Cloud speech a Google Cast speaker device made, for the speaker to download; the random token is the credential, kept an hour |

These routes are anonymous. Keep the node behind a trusted network, reverse proxy or gateway when
exposing webhooks.

## Device catalog

Every device, with its parameters, reports and commands, and the setup notes of the cloud devices:
[docs/devices.md](docs/devices.md). The [hub's guides](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/README.md)
cover Netatmo and Google Cast speakers step by step.

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
