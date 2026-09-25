# CLAUDE.md

This file provides guidance to Claude Code (and other AI coding assistants) when working with code in this repository.

## Overview

**RIoT2.Net.Devices** is a .NET class library that provides a collection of device plugins for the RIoT2 IoT platform ("Net Node"). Each plugin exposes one or more devices that can report data, execute commands, and be refreshed periodically.

## Tech Stack

- **Language:** C#
- **Target Framework:** `net9.0`
- **SDK:** `Microsoft.NET.Sdk` (with `Microsoft.AspNetCore.App` framework reference)
- **Type:** Class library with dynamic loading enabled (`EnableDynamicLoading`)
- **Implicit usings:** enabled

### Key NuGet Dependencies

- `RIoT2.Core` 0.1.43 — core abstractions, interfaces, and base classes
- `FluentFTP` / `Zhaobang.FtpServer` — FTP client/server support
- `Google.Apis.Auth` — Google/Firebase authentication
- `Microsoft.Azure.Relay` — Azure Relay messaging

## Common Commands

```powershell
# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Build in Release
dotnet build -c Release
```

## Project Structure

- `Plugin.cs` — parameterless plugin entry point implementing `IDevicePlugin`; `Initialize(IServiceCollection)` registers services and `IDevice` singletons.
- `Catalog/` — concrete device implementations (e.g. `Hue`, `Mqtt`, `Timer`, `Virtual`, `Web`, `ElectricityPrice`, `NetatmoWeather`, `NetatmoSecurity`, `EasyPLC`, `WaterConsumption`, `ApSystems`, `FTP`, `AzureRelay`, `Messaging`, `EufySecurity`).
- `Abstracts/` — shared base classes (e.g. `NetatmoBase`).
- `Services/` — supporting services (Azure Relay, FTP, webhooks, storage, Eufy security, ApSystems client, WebSocket client).
  - `Services/Interfaces/` — service contracts.
  - `Services/FTP/` — FTP server infrastructure (users, authenticator, in-memory file provider, data connections).
- `Models/` — DTOs and data models for the various integrations.
- `Controllers/` — ASP.NET Core controllers (`WebhookController`, `DownloadController`).

## Device Development Conventions

When creating a new device, follow these patterns (see `README.md` for full details):

- At minimum, a device implements the `IDevice` interface.
- Prefer extending the abstract `DeviceBase` class for easier implementation:
  - `ConfigureDevice()` — configuration logic.
  - `StartDevice()` — device start logic.
  - `StopDevice()` — device stop logic.
  - `Refresh(ReportTemplate report)` — refresh logic when the device is an `IRefreshableReportDevice`.
  - Throw an error from overridden functions to move the device to the error state (message is exposed via `StateMessage`).
- Optional interfaces:
  - `IDeviceWithConfiguration` — device can provide a configuration template.
  - `ICommandDevice` — device can execute commands (switch, etc.).
  - `IRefreshableReportDevice` — device data is refreshed periodically.
  - `IAsyncCommandDevice` / `AsyncDeviceBase` — preferred for network or hardware I/O that must be awaited/cancelled by the node.

## Current device catalog and configuration notes

- `Web`, `Timer`, and `Virtual` have no device parameters in code. Web commands call HTTP endpoints and the plugin also maps `POST /api/webhook/{address}`; that webhook route is unauthenticated and capped at 64 KiB.
- `Mqtt` has no custom template; manual parameters expected by code are `clientId`, `serverUrl`, `userName`, `password`, and `subscribeTopics`.
- `ElectricityPrice`: `securityToken`, `domain`, `endpoint`, `vat`; stores `Data/priceData.xml`; report `precision` is honored on refresh.
- `WaterConsumption`: `securityToken`, `endpoint`; report `precision` and `unit`.
- `Hue`: `bridgeIpAddress`, `apiKey`; running bridge generates light templates and Matter endpoint metadata.
- `NetatmoWeather`/`NetatmoSecurity`: `token`, `refresh_token`, `clientId`, `clientSecret` (plus `stationId` for weather); rotated OAuth tokens persist in `Data/netatmoAuth.json`.
- `Messaging`: `firebaseProjectName`, `smtp_Server`, `smtp_User`, `smtp_Password`, `smtp_Port`; Firebase service account JSON is read from `Data/<project>.json`.
- `ApSystems`: `appId`, `appSecret`, `sid`, `ecuId`. `EufySecurity`: `serviceIp`, `port`. `EasyPLC`: `ipAddress`, `port`.
- `FTP` and `AzureRelay` do not emit custom templates but their code expects `ftpUsers`/`ftpPort` and `relayNamespace`/`connectionName`/`keyName`/`key` respectively.
- `DownloadController` only accepts simple filenames after URL decoding; traversal, absolute paths, encoded slashes, and encoded backslashes return 400 before storage services are called.

## Notes for AI Assistants

- Keep new devices under `Catalog/` and register them via `Plugin.cs`.
- Place shared logic in `Abstracts/` or `Services/`; expose services through interfaces in `Services/Interfaces/`.
- Some plugins require external configuration/credentials (Azure, Firebase, entsoe.eu, Netatmo, wrm-systems) — do not hard-code secrets.
- Do not edit generated files under `obj/`.
