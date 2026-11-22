# Reference

## How it runs

| What | Where |
| --- | --- |
| Executable | `C:\Program Files\CocktailAgent\CocktailAgent.exe`: one file, self-contained (no .NET runtime needed) |
| Start | Scheduled task **Cocktail agent**: at sign-in, as that user, **elevated**, no time limit, restarts every minute after a crash. It runs in the user's desktop session because Windows audio and the display are per session. It is elevated because LaunchBox/BigBox usually are, and a non-elevated process can neither watch nor close an elevated one |
| Settings | `%LOCALAPPDATA%\CocktailAgent\agent.json` |
| MQTT password | `%LOCALAPPDATA%\CocktailAgent\agent.password`, DPAPI-encrypted for that Windows user |
| CA | Optional; `caFile` in the settings (the SSH installer puts it at `%LOCALAPPDATA%\CocktailAgent\ca.pem`) |
| Log | `%LOCALAPPDATA%\CocktailAgent\agent.log`, rotates at 1 MB |
| Single instance | A second copy exits at once |

`src/CocktailAgent.Core` holds MQTT, discovery, commands, settings, health
logic and the TLS check, and is tested on any OS. `src/CocktailAgent` is the
Windows side: Core Audio, process control, display power, input and the tray
icon.

## Settings

`agent.json`, camelCase. Unknown keys are refused, so a typo fails loudly. So
is a `password` key: the password belongs in `agent.password`, which the tray
Settings window and the installer write.

| Key | Default | Meaning |
| --- | --- | --- |
| `host` | (required) | Broker host |
| `port` | `8883` | Broker port |
| `useTls` | `true` | TLS to the broker |
| `username` | (required) | MQTT user |
| `caFile` | none | PEM file of a private CA. The broker certificate must chain to it, and the host name is checked. Without it, the Windows trust store decides. Revocation is not checked: private CAs such as step-ca or a self-made Mosquitto CA usually publish no CRL or OCSP |
| `nodeId` | `cocktail_pc` | Topic segment, Home Assistant device identifier and `unique_id` prefix (`a-z`, `0-9`, `_`) |
| `deviceName` | `Cocktail PC` | Home Assistant device name |
| `suggestedArea` | none | Area Home Assistant suggests for the device |
| `discoveryPrefix` | `homeassistant` | Home Assistant's discovery prefix |
| `muteEntityId` | `switch.<nodeId>_mute` | Entity id Home Assistant creates for mute. Set it to keep the id of an entity the agent replaces (dashboards, automations, voice assistants) |
| `volumeEntityId` | `number.<nodeId>_volume` | Same, for volume |
| `launchBoxDir` | `C:\LaunchBox` | LaunchBox folder: emulators are processes under `Emulators\`, BigBox is `Core\BigBox.exe` |
| `inputWatchDir` | none | Folder of an external input watcher; see [Last input](#last-input) |
| `nasShare` | none | UNC path (`\\nas\games`) listed every 5 min for the "NAS share" connectivity sensor. Hidden when unset |

Example:

```json
{
  "host": "homeassistant.local",
  "port": 8883,
  "useTls": true,
  "username": "cocktail-agent",
  "caFile": "C:\\Users\\arcade\\AppData\\Local\\CocktailAgent\\ca.pem",
  "suggestedArea": "Game Room"
}
```

## MQTT

Topics are under `cocktail-agent/<nodeId>/`:

| Topic | Direction | Payload |
| --- | --- | --- |
| `availability` | agent → | `online` / `offline`, retained; `offline` is also the Last Will |
| `state` | agent → | `{"muted":false,"volume":16,"screen":true}`, retained |
| `health` | agent → | Health JSON, retained, published when it changes (checked every 30 s) |
| `mute/set` | → agent | `ON` / `OFF` |
| `volume/set` | → agent | `0`-`100` |
| `screen/set` | → agent | `ON` / `OFF` |
| `command/<button>` | → agent | `PRESS` |

Discovery configs are retained under `<discoveryPrefix>/<component>/<nodeId>/<object>/config`.
When Home Assistant's own status comes back (`homeassistant/status` →
`online`), the agent republishes everything.

Commands must not be retained. A retained command is logged and ignored,
never replayed: think of a retained "shutdown". A second press of the same
button within 5 s is ignored.

## Entities

| Entity | Does |
| --- | --- |
| `switch.<nodeId>_mute` | Master mute of the default playback device (it follows a change of default device) |
| `number.<nodeId>_volume` | Master volume 0-100 (slider) |
| `switch.<nodeId>_screen` | The monitor, as Windows reports it. Off blanks it without sleeping the PC; on wakes it. See [Screen](#screen) |
| `button.<nodeId>_close_emulators` | Closes every process running from `<launchBoxDir>\Emulators\` |
| `button.<nodeId>_close_frontend` | Emulators first, then BigBox and LaunchBox |
| `button.<nodeId>_restart_bigbox` | Closes like the one above, then starts BigBox again and waits until it runs |
| `button.<nodeId>_shutdown` | Closes everything, mutes, `shutdown /s` |
| `button.<nodeId>_reboot` ("Restart PC") | Closes everything, mutes, `shutdown /r` |

"Close" means a normal close request first (MAME writes its high scores and
nvram, BigBox saves its settings), and a forced end only after 30 s. A close
that fails never cancels Shut down or Restart; Windows ends what is left.

Diagnostics:

| Entity | Shows |
| --- | --- |
| `sensor.<nodeId>_last_input` | Last real input; see [Last input](#last-input) |
| `binary_sensor.<nodeId>_input_watch` | The external input watcher is running (only with `inputWatchDir`) |
| `binary_sensor.<nodeId>_stuck_input` | Problem: a joystick button or stick direction held for 10 min or more. The attribute `stuck_inputs` names it, e.g. "joystick 2 button 11 held since 10:31". All joysticks are polled every second |
| `sensor.<nodeId>_last_boot` | When Windows last started |
| `sensor.<nodeId>_disk_free` | Free space on C: in GB (decimal), attribute `percent_free` |
| `binary_sensor.<nodeId>_reboot_pending` | Windows Update or servicing wants a restart |
| `binary_sensor.<nodeId>_nas_share` | The `nasShare` answered a directory listing (only with `nasShare`) |

## Last input

By default, last input is Windows' own idle timer (`GetLastInputInfo`) for the
agent's session.

Some cabinets reset that timer by themselves. Zero-delay encoders or their
drivers can report phantom input every minute or two with nobody there, which
also keeps waking a blanked screen. For those, point `inputWatchDir` at a
folder kept up to date by a watcher of your own that records only real
changes (a button newly pressed, a stick leaving centre, a key, mouse
movement). The contract is two files:

- `input-watch.heartbeat`: rewritten at least every 3 minutes while the
  watcher runs. Its modification time is what counts;
- `last-input.txt`: the last real input as an ISO 8601 timestamp, e.g.
  `2026-11-21T22:31:05.1234567-06:00`.

A missing or stale heartbeat makes last input unknown and turns the "Input
watcher" sensor off.

## Screen

Off sends `SC_MONITORPOWER` to the agent's own hidden window. It is not
broadcast to every window: that reached BigBox too, which then lost focus
after the next wake. When the display comes back on, the agent gives focus
back to the running game, or else BigBox, and logs which app is in front.
The state comes from Windows' `GUID_CONSOLE_DISPLAY_STATE` notification, so
Windows' own display timeout shows up too.

A screen turned off from Home Assistant is kept off against phantom wakes.
When it wakes, the agent waits 6 s; if there was no real input since it was
turned off, it blanks it again, at most every 10 s. Real input, or On from
Home Assistant, ends that. With a configured watcher that is not running,
any wake is accepted.

## Install over SSH

From macOS or Linux, `scripts/install-cocktail-agent.sh` builds, tests and
installs on the PC over SSH. The PC needs the OpenSSH server, with
PowerShell as its default shell, and an administrator login: the signed-in
user, because the settings and password belong to that user.

```bash
cp .env.example .env      # SSH login of the PC, broker, optional settings
bash scripts/install-cocktail-agent.sh
```

The script:

1. runs the tests and publishes the exe (deterministic: same source, same
   hash);
2. builds `agent.json` from the `AGENT_*` values in `.env`;
3. stages the exe, `agent.json` and an optional CA file on the PC;
4. if `AGENT_MQTT_PASSWORD_COMMAND` is set (a password manager or Vault
   command), runs it and pipes the password over SSH stdin, never on a
   command line;
5. runs `install-cocktail-agent.ps1 -FromStaging`. That checks the exe's
   hash, installs, DPAPI-encrypts the password, registers the task, and
   only returns once the agent has logged `Connected`.

`scripts/cocktail-remote.sh` is the SSH helper it uses. It logs in with SSH
keys, or with `COCKTAIL_PASSWORD` through `sshpass`. Host keys are trusted on
first use.

## Behaviour worth knowing

- Volume and mute changes from anywhere (Home Assistant, a game, Windows) are
  pushed within about a second. The agent also re-reads them every 60 s, in
  case a change notification was missed.
- Saving Settings from the tray restarts the connection with the new
  settings; there's no need to restart the app.
- **Quit** from the tray exits cleanly, so the task does not restart it until
  the next sign-in. A crash is restarted within a minute.

## Why not HASS.Agent

[HASS.Agent](https://github.com/hass-agent/HASS.Agent) does far more, and if
you only need volume and media keys it may suit you better.

This agent exists because HASS.Agent could not verify a broker certificate
from a private CA without a CRL. .NET's revocation check fails on such a
certificate, and the only switch that avoids it disables every check. With
Home Assistant's Mosquitto app, a broker login can typically reach every
topic, including Zigbee2MQTT's, so an unverified connection was not
acceptable.
