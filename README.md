# ha-cocktail-agent

[![build and test](https://github.com/dewab74/ha-cocktail-agent/actions/workflows/ci.yml/badge.svg)](https://github.com/dewab74/ha-cocktail-agent/actions/workflows/ci.yml)
[![pre-commit backstop](https://github.com/dewab74/ha-cocktail-agent/actions/workflows/pre-commit.yml/badge.svg)](https://github.com/dewab74/ha-cocktail-agent/actions/workflows/pre-commit.yml)
[![CodeQL](https://github.com/dewab74/ha-cocktail-agent/actions/workflows/codeql.yml/badge.svg)](https://github.com/dewab74/ha-cocktail-agent/actions/workflows/codeql.yml)
[![dependency review](https://github.com/dewab74/ha-cocktail-agent/actions/workflows/dependency-review.yml/badge.svg)](https://github.com/dewab74/ha-cocktail-agent/actions/workflows/dependency-review.yml)
[![release](https://github.com/dewab74/ha-cocktail-agent/actions/workflows/release.yml/badge.svg)](https://github.com/dewab74/ha-cocktail-agent/actions/workflows/release.yml)

A small Windows tray app that lets [Home Assistant](https://www.home-assistant.io/)
control an arcade or game PC running [LaunchBox / BigBox](https://www.launchbox-app.com/)
over MQTT.

Point it at your MQTT broker and a **Cocktail PC** device appears in Home
Assistant through
[MQTT discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery),
with no YAML:

- **Volume and mute** for Windows' default speakers, two-way and event-driven:
  a change on the PC shows up in Home Assistant within about a second.
- **Screen** on/off (blanks the monitor without sleeping the PC).
- Buttons to **close the emulators**, **close BigBox and LaunchBox**,
  **restart BigBox**, **shut down** and **restart** the PC. Every close asks
  nicely first, so MAME saves high scores and BigBox saves its settings.
- **Health sensors**: last real input, stuck buttons or sticks on the
  controls, last boot, free disk space, Windows reboot pending, and an
  optional network share check.

It complements the [launchbox-mqtt](https://github.com/dewab74/launchbox-mqtt)
plugin. That one reports what happens inside LaunchBox (what's playing, attract
mode). This agent controls Windows itself and keeps working when the front end
is closed or has crashed. They don't depend on each other.

## Install

You need Windows 10 or 11 x64 and an MQTT broker, for example Home
Assistant's Mosquitto app. The exe is self-contained, so no .NET runtime is
needed.

1. Download `CocktailAgent.exe` and `install-cocktail-agent.ps1` from the
   [latest release](https://github.com/dewab74/ha-cocktail-agent/releases/latest).
   Check the exe against `SHA256SUMS` (or
   `gh attestation verify CocktailAgent.exe -R dewab74/ha-cocktail-agent`).
2. Signed in as the user who plays on the PC, open an **elevated**
   PowerShell in the download folder and run:

   ```powershell
   .\install-cocktail-agent.ps1 -ExePath .\CocktailAgent.exe
   ```

   It copies the exe to `C:\Program Files\CocktailAgent` and registers the
   scheduled task **Cocktail agent**: it starts at sign-in, elevated, and
   restarts after a crash. Then it starts the agent.
3. The tray icon opens **Settings** on first run. Enter the broker host,
   port, user and password, plus a CA file if your broker uses a private CA.
   Save, and the icon turns green.

To update, run the same command with the new exe. To install from a Mac or
Linux box over SSH, see [docs/reference.md](docs/reference.md#install-over-ssh).

## The tray icon

The joystick icon's dot shows the connection: green when connected, red when
disconnected (it keeps retrying), grey while connecting or not yet
configured. Its menu has:

- the version, and the connection status with the reason when it fails;
- **Settings…**: every option, with the password stored encrypted for your
  Windows user (DPAPI). Saving restarts the connection;
- **Open log**;
- **About…**;
- **Quit**: stops the agent until the next sign-in.

## Home Assistant

The entity ids below assume the default node id `cocktail_pc`:

| Entity | Does |
| --- | --- |
| `switch.cocktail_pc_mute` | Master mute |
| `number.cocktail_pc_volume` | Master volume 0-100 |
| `switch.cocktail_pc_screen` | The monitor (off blanks it, on wakes it) |
| `button.cocktail_pc_close_emulators` | Closes everything running from `<LaunchBox>\Emulators\` |
| `button.cocktail_pc_close_frontend` | Emulators first, then BigBox and LaunchBox |
| `button.cocktail_pc_restart_bigbox` | Closes the front end, then starts BigBox again |
| `button.cocktail_pc_shutdown` / `button.cocktail_pc_reboot` | Closes everything, mutes, shuts down / restarts Windows |
| `sensor.cocktail_pc_last_input` and other diagnostics | Last input, stuck input, last boot, disk free, reboot pending |

When the PC goes away, the broker's Last Will marks everything unavailable.
[docs/home-assistant.md](docs/home-assistant.md) has example automations:
blank the screen when idle in BigBox, alert on stuck controls or low disk,
and cut a smart plug after the PC has shut down.

## Settings

Settings live in `%LOCALAPPDATA%\CocktailAgent\agent.json`. Edit them from
the tray, or by hand; unknown keys are refused. All keys, with their
defaults, are in [docs/reference.md](docs/reference.md#settings). The
most-used ones:

| Key | Default | Meaning |
| --- | --- | --- |
| `host` / `port` / `useTls` | (required) / `8883` / `true` | Broker |
| `username` | (required) | MQTT user; the password is stored separately, DPAPI-encrypted |
| `caFile` | none (Windows trust store) | PEM of a private CA the broker certificate must chain to |
| `nodeId` / `deviceName` / `suggestedArea` | `cocktail_pc` / `Cocktail PC` / none | Home Assistant device |
| `launchBoxDir` | `C:\LaunchBox` | Where LaunchBox is installed |

## Security

- Commands only act on fixed things: mute, volume, the screen, closing
  processes under the LaunchBox folder, starting BigBox, and shutting down
  or restarting Windows. No command runs a path or command line from MQTT.
- Retained commands are refused, so a stale retained "shutdown" is never
  replayed. A button pressed twice within 5 s acts once.
- TLS by default. With `caFile`, the broker certificate must chain to that CA
  and match the host name.
- The password never sits in `agent.json` and never appears in the log.

Give the agent its own MQTT user. Report vulnerabilities as described in
[SECURITY.md](SECURITY.md).

## Development

```bash
pre-commit install      # once per clone: hooks live in .git/, which is not cloned
dotnet build CocktailAgent.slnx
dotnet test tests/CocktailAgent.Core.Tests
```

`src/CocktailAgent.Core` (MQTT, discovery, commands, settings) builds and
tests on any OS. `src/CocktailAgent` (Windows audio, processes, display,
tray) targets `net10.0-windows`; it builds on any OS but only runs on
Windows. See [CONTRIBUTING.md](CONTRIBUTING.md) for the hooks, CI and
releases.

## License

[MIT](LICENSE).
