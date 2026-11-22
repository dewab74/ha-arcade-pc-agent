# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.0] - 2026-11-22

### Added

- `ArcadePcAgent-Setup-<version>.exe`, an Inno Setup installer built by CI.
  It has a wizard with a broker page, and runs fully silent with switches
  (`/HOST=`, `/PORT=`, `/TLS=`, `/VERIFY=`, `/USERNAME=`, `/PASSWORDFILE=`,
  `/CAFILE=`, `/NODEID=`, `/DEVICENAME=`, `/AREA=`, `/LAUNCHBOXDIR=`…). A
  silent setup that fails exits with 1.
- `verifyCertificate` (default `true`): turn it off for a self-signed broker
  certificate. Also in Settings, as a checkbox.
- `install-arcade-pc-agent.ps1` takes `-SettingsJson`, `-CaPem` and
  `-PasswordFile`, for scripted installs.

### Changed

- **Renamed** from "Cocktail agent" to **Arcade PC agent**. This affects the
  repo (`ha-arcade-pc-agent`), the exe (`ArcadePcAgent.exe`), the folders
  (`C:\Program Files\ArcadePcAgent`, `%LOCALAPPDATA%\ArcadePcAgent`), the
  scheduled task, and the MQTT topics (`arcade-pc-agent/<nodeId>/…`). The
  default node id is `arcade_pc` and the device "Arcade PC". To keep the
  Home Assistant entities of a 0.4.0 install, set `nodeId` to its old value.
  Then remove the old install: end and delete the task "Cocktail agent",
  delete `C:\Program Files\CocktailAgent`, and copy your settings from
  `%LOCALAPPDATA%\CocktailAgent`.
- Plain MQTT on port 1883 by default (was TLS on 8883), matching Home
  Assistant's Mosquitto app out of the box. `username` is optional.

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
