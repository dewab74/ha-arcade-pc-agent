# ha-arcade-pc-agent

[![build and test](https://github.com/dewab74/ha-arcade-pc-agent/actions/workflows/ci.yml/badge.svg)](https://github.com/dewab74/ha-arcade-pc-agent/actions/workflows/ci.yml)
[![pre-commit backstop](https://github.com/dewab74/ha-arcade-pc-agent/actions/workflows/pre-commit.yml/badge.svg)](https://github.com/dewab74/ha-arcade-pc-agent/actions/workflows/pre-commit.yml)
[![CodeQL](https://github.com/dewab74/ha-arcade-pc-agent/actions/workflows/codeql.yml/badge.svg)](https://github.com/dewab74/ha-arcade-pc-agent/actions/workflows/codeql.yml)
[![dependency review](https://github.com/dewab74/ha-arcade-pc-agent/actions/workflows/dependency-review.yml/badge.svg)](https://github.com/dewab74/ha-arcade-pc-agent/actions/workflows/dependency-review.yml)
[![release](https://github.com/dewab74/ha-arcade-pc-agent/actions/workflows/release.yml/badge.svg)](https://github.com/dewab74/ha-arcade-pc-agent/actions/workflows/release.yml)

A small Windows tray app that lets [Home Assistant](https://www.home-assistant.io/)
control an arcade or game PC running [LaunchBox / BigBox](https://www.launchbox-app.com/)
over MQTT.

Point it at your MQTT broker and a **Arcade PC** device appears in Home
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

1. Download `ArcadePcAgent-Setup-<version>.exe` from the
   [latest release](https://github.com/dewab74/ha-arcade-pc-agent/releases/latest).
   Check it against `SHA256SUMS`, or with
   `gh attestation verify ArcadePcAgent-Setup-<version>.exe -R dewab74/ha-arcade-pc-agent`.
   The installer isn't code-signed, so Windows SmartScreen may ask you to confirm.
2. Run it while signed in as the user who plays on the PC. It asks for
   administrator rights. Enter your broker on the "MQTT broker" page, or leave
   it empty and use the tray icon's Settings later.

The installer puts the agent in `C:\Program Files\ArcadePcAgent` and
registers the scheduled task **Arcade PC agent**, which starts at sign-in,
elevated, and restarts after a crash. Then it starts the agent. To update,
run a newer setup; your settings stay. Uninstall from Windows' Installed apps.

### Scripted install

The same setup runs silently, so it suits deployment scripts. Run it
elevated, as the user who plays on the PC:

```powershell
.\ArcadePcAgent-Setup-0.5.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART `
  /HOST=homeassistant.local /PORT=1883 /USERNAME=arcade-pc-agent /PASSWORDFILE=C:\temp\mqtt.txt `
  /AREA="Game Room" /LOG=C:\temp\arcade-pc-agent-setup.log
```

| Switch | Default | Meaning |
| --- | --- | --- |
| `/HOST=` | none | Broker host. Without it, existing settings are kept (an update) |
| `/PORT=` | `1883` | Broker port |
| `/TLS=1` | off | Use TLS |
| `/VERIFY=0` | on | With TLS, don't verify the broker's certificate (for self-signed brokers) |
| `/CAFILE=` | none | PEM of a private CA the broker certificate must chain to |
| `/USERNAME=` | none | MQTT user |
| `/PASSWORDFILE=` | none | File with the MQTT password. It's encrypted for the user, then deleted. Prefer it to `/PASSWORD=`, which is visible in the process list |
| `/NODEID=`, `/DEVICENAME=`, `/AREA=` | `arcade_pc`, `Arcade PC`, none | Home Assistant device |
| `/LAUNCHBOXDIR=` | `C:\LaunchBox` | LaunchBox folder |
| `/NOWAIT=1` | off | Don't wait for the agent to connect |

A silent setup exits with code 0 once the agent has connected, or with 1
if setting it up failed (the log says why).

Prefer no installer? `ArcadePcAgent.exe` and `install-arcade-pc-agent.ps1`
are in the release too: run `.\install-arcade-pc-agent.ps1 -ExePath .\ArcadePcAgent.exe`
from an elevated PowerShell. To install from a Mac or Linux box over SSH, see
[docs/reference.md](docs/reference.md#install-over-ssh).

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

The entity ids below assume the default node id `arcade_pc`:

| Entity | Does |
| --- | --- |
| `switch.arcade_pc_mute` | Master mute |
| `number.arcade_pc_volume` | Master volume 0-100 |
| `switch.arcade_pc_screen` | The monitor (off blanks it, on wakes it) |
| `button.arcade_pc_close_emulators` | Closes everything running from `<LaunchBox>\Emulators\` |
| `button.arcade_pc_close_frontend` | Emulators first, then BigBox and LaunchBox |
| `button.arcade_pc_restart_bigbox` | Closes the front end, then starts BigBox again |
| `button.arcade_pc_shutdown` / `button.arcade_pc_reboot` | Closes everything, mutes, shuts down / restarts Windows |
| `sensor.arcade_pc_last_input` and other diagnostics | Last input, stuck input, last boot, disk free, reboot pending |

When the PC goes away, the broker's Last Will marks everything unavailable.
[docs/home-assistant.md](docs/home-assistant.md) has example automations:
blank the screen when idle in BigBox, alert on stuck controls or low disk,
and cut a smart plug after the PC has shut down.

## Settings

Settings live in `%LOCALAPPDATA%\ArcadePcAgent\agent.json`. Edit them from
the tray, or by hand; unknown keys are refused. All keys, with their
defaults, are in [docs/reference.md](docs/reference.md#settings). The
most-used ones:

| Key | Default | Meaning |
| --- | --- | --- |
| `host` / `port` | (required) / `1883` | Broker |
| `useTls` / `verifyCertificate` | `false` / `true` | TLS, and whether to check the broker's certificate |
| `username` | none | MQTT user, if the broker needs one; the password is stored separately, DPAPI-encrypted |
| `caFile` | none (Windows trust store) | PEM of a private CA the broker certificate must chain to |
| `nodeId` / `deviceName` / `suggestedArea` | `arcade_pc` / `Arcade PC` / none | Home Assistant device |
| `launchBoxDir` | `C:\LaunchBox` | Where LaunchBox is installed |

## Security

- Commands only act on fixed things: mute, volume, the screen, closing
  processes under the LaunchBox folder, starting BigBox, and shutting down
  or restarting Windows. No command runs a path or command line from MQTT.
- Retained commands are refused, so a stale retained "shutdown" is never
  replayed. A button pressed twice within 5 s acts once.
- TLS is optional. With it on, the broker certificate is verified by
  default: against `caFile` if set, otherwise against the Windows trust store,
  and the host name must match. Turning verification off keeps the
  connection encrypted but no longer authenticates the broker; the agent logs
  a warning.
- The password never sits in `agent.json` and never appears in the log.

Give the agent its own MQTT user. Report vulnerabilities as described in
[SECURITY.md](SECURITY.md).

## Development

```bash
pre-commit install      # once per clone: hooks live in .git/, which is not cloned
dotnet build ArcadePcAgent.slnx
dotnet test tests/ArcadePcAgent.Core.Tests
```

`src/ArcadePcAgent.Core` (MQTT, discovery, commands, settings) builds and
tests on any OS. `src/ArcadePcAgent` (Windows audio, processes, display,
tray) targets `net10.0-windows`; it builds on any OS but only runs on
Windows. See [CONTRIBUTING.md](CONTRIBUTING.md) for the hooks, CI and
releases.

## License

[MIT](LICENSE).
