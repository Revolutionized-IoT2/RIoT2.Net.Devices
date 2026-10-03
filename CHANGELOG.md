# Changelog

All notable changes to `RIoT2.Net.Devices`. A version is released by pushing a git tag; CI then
uploads a plugin zip as a GitHub release asset.

## [Unreleased]

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
