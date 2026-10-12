# Changelog

All notable changes to `RIoT2.Net.Devices`. A version is released by pushing a git tag; CI then
uploads a plugin zip as a GitHub release asset.

## [Unreleased]

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
