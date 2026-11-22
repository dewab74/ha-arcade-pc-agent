# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.0] - 2026-11-22

First public release.

### Added

- Tray icon: connection status in the icon, tooltip and menu; Settings,
  Open log, About and Quit. Settings edits every option and stores the
  password DPAPI-encrypted; saving reconnects without restarting the app. It
  opens by itself when there are no settings yet.
- Settings `launchBoxDir`, `inputWatchDir`, `muteEntityId` and
  `volumeEntityId`. Last input now comes from Windows' idle timer unless an
  external input watcher is configured.
- Installer runs directly on Windows (`-ExePath`), or over SSH from
  macOS/Linux with settings taken from `.env`.

### Changed

- Mute and volume default to `switch.<nodeId>_mute` and
  `number.<nodeId>_volume`. Set `muteEntityId` / `volumeEntityId` to keep
  earlier ids.
- The "Input watcher" sensor only exists when `inputWatchDir` is set.
- The SSH helper trusts host keys on first use and checks them afterwards,
  and can log in with SSH keys.

## [0.3.3] - 2026-11-21

### Fixed

- Turning the screen off no longer takes focus away from BigBox.

## [0.3.2] - 2026-11-21

### Fixed

- Icon for the "Close BigBox and LaunchBox" button.

## [0.3.1] - 2026-11-21

### Added

- Health sensors: last input, stuck input, last boot, disk free, reboot
  pending, optional network share.

## [0.2.1] - 2026-11-21

### Added

- Screen on/off and a Restart BigBox button.

## [0.1.1] - 2026-11-21

### Fixed

- Runs elevated, so it can watch and close an elevated BigBox. Shut down and
  restart go ahead even if closing the front end fails.

## [0.1.0] - 2026-11-21

### Added

- Volume, mute and power buttons for Home Assistant over MQTT, with discovery.
