# RIoT2.Net.Devices

Default device plugin catalog for the [RIoT2](https://github.com/Revolutionized-IoT2) platform.
The assembly is a .NET 9 class library with dynamic loading enabled so `RIoT2.Net.Node` can load it
from a plugin package at startup.

- Type: device plugin library
- Target framework: `net9.0`
- Core package: `RIoT2.Core` 0.1.43
- Plugin entry point: `Plugin.cs`

How plugins fit into the platform: [configuration contract](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md).

## Contents

| Path | Contents |
|---|---|
| `Plugin.cs` | Registers plugin services, devices and controllers with the Node host |
| `Catalog/` | Device implementations |
| `Controllers/` | `WebhookController` and `DownloadController` |
| `Services/` | Supporting services for integrations and storage |
| `Models/` | DTOs used by devices and services |
| `Tests/` | Hardware-free regression tests |

## Build and test

From the workspace root (`C:\Src\RIoT2`):

```powershell
dotnet build .\RIoT2.Net.Devices\RIoT2.Net.Devices.csproj
dotnet test .\RIoT2.Net.Devices\Tests\RIoT2.Net.Devices.Tests.csproj -c Release
```

The tests use synthetic tokens, in-memory Hue and EasyPLC streams, and isolated test-output files.
They do not contact Netatmo, Hue bridges, PLCs or cloud services.

If `RIoT2.Core` 0.1.43 is not available from the trusted feed, use the local feed at
`C:\Src\RIoT2\.localfeed` while validating. A local package is not a published release.

## Packaging and deployment

This library is not run directly. Build or release a plugin zip and let `RIoT2.Net.Node` load it
from the Node's `Plugins/` directory. The release workflow writes `PluginManifest.json`, zips the
Release output and uploads the zip as a GitHub release asset.

Release this plugin with the Node image that hosts it. Plugin assemblies share the host
`RIoT2.Core` assembly, so package/runtime version drift can show up as missing members or changed
runtime behaviour.

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

The list below is verified against `Catalog/*.cs`.

| Device | Parameters | Reports and commands |
|---|---|---|
| AP Systems | `appId`, `appSecret`, `sid`, `ecuId` | Hourly energy summaries: `today`, `month`, `year`, `lifetime` with `unit` and `precision` report parameters. |
| Azure Relay | No template is emitted; code reads `relayNamespace`, `connectionName`, `keyName`, `key`. | Starts/stops an Azure Relay listener. Message handling is not implemented in code. |
| EasyPLC | No template is emitted; code reads `ipAddress`, `port`. | Async/cancellable PLC marker read/write driver. Marker command addresses are `M-{netId}-{expansion}-{marker}`; `refresh` reads markers. Non-zero write expansions are rejected. |
| Electricity Price | `securityToken`, `domain`, `endpoint`, `vat` | Scheduled current price report at address `price`, with `unit=c/kWh` and `precision`. Cached XML is stored at `Data/priceData.xml`. |
| Eufy Security | `serviceIp`, `port` | Dynamic camera/security reports and a station `guardMode` command after the external eufy-security-ws service is connected. |
| FTP | No template is emitted; code reads `ftpUsers`, `ftpPort`. | File uploads become in-memory photo `SecurityReport` values keyed by FTP username. |
| Hue | `bridgeIpAddress`, `apiKey` | Dynamic light command/report templates when running; event-stream updates merge partial state and include Matter endpoint metadata. |
| Messaging | `firebaseProjectName`, `smtp_Server`, `smtp_User`, `smtp_Password`, `smtp_Port` | Commands `fb` (Firebase topic message) and `mail` (SMTP email). Firebase service account JSON is read from `Data/service_account.json`. |
| MQTT | No template is emitted; code reads `clientId`, `serverUrl`, `userName`, `password`, `subscribeTopics`. | Publishes command payloads to command addresses and reports subscribed topic payloads as text. |
| Netatmo Security | `token`, `refresh_token`, `clientId`, `clientSecret` | Dynamic security camera reports plus `refresh` and `set_home` commands; rotated tokens are persisted in `Data/netatmoAuth.json`. |
| Netatmo Weather | `token`, `refresh_token`, `clientId`, `clientSecret`, `stationId` | Weather station and module measurements; rotated tokens are persisted in `Data/netatmoAuth.json`. |
| Timer | No device parameters; uses report-level schedules | Emits the report address whenever the scheduler refreshes that report. |
| Virtual | No device parameters | Stores command values by address and republishes matching report values. |
| Water Consumption | `securityToken`, `endpoint` | Scheduled latest water meter reading at address `watermeter`, with `unit` and `precision` report parameters. |
| Web Device | No device parameters | Commands perform HTTP GET/POST through the webhook service; `POST /api/webhook/{address}` publishes matching reports. |

Devices and plugin controllers use the platform configuration and HTTP contracts:

- [Node configuration, templates and plugins](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md)
- [HTTP and gRPC APIs](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md)

## Writing a new device

1. Add a class under `Catalog/`.
2. Derive from `DeviceBase`, or `AsyncDeviceBase` for network or hardware I/O that must be
   awaited/cancelled by the node.
3. Implement `IDeviceWithConfiguration` when the UI should discover parameters/templates.
4. Implement `ICommandDevice` / `IAsyncCommandDevice` for commands, and
   `IRefreshableReportDevice` for scheduled reports.
5. Register the device in `Plugin.Initialize(IServiceCollection)` as a host-owned singleton.

Configuration parameter keys should be camelCase because the platform serializes dictionary keys as
camelCase and `DeviceBase.GetConfiguration<T>(key)` is case-sensitive.

## Versions and releases

- Release notes are in [CHANGELOG.md](CHANGELOG.md).
- CI releases a zip when a version tag is pushed.
- Release the plugin package with the compatible Node image.

## Contributing

- Instructions for AI coding agents: [AGENTS.md](AGENTS.md).
- Platform documentation: [.github/docs](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/README.md).

## License

See [LICENSE](LICENSE).
