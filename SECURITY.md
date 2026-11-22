# Security policy

## Reporting a vulnerability

Please report security problems privately through
[GitHub private vulnerability reporting](https://github.com/dewab74/ha-arcade-pc-agent/security/advisories/new),
not in a public issue. Expect an acknowledgement within a week.

## What counts

- Anything that lets an MQTT user make the PC run a program, open a file, or
  close a process other than the emulators under the configured LaunchBox
  folder, BigBox and LaunchBox. Commands accept fixed payloads only, never a
  path or command line, and retained commands are ignored.
- A command acting more than once from one press, or acting on a retained
  message.
- The MQTT password stored anywhere other than the DPAPI-encrypted
  `agent.password`, or appearing in the log or `agent.json`.
- TLS certificate validation that accepts a certificate it should not (wrong
  host name, untrusted chain).
- The installer or the tray app widening access (file permissions, the
  scheduled task's account) beyond what is documented.

## Supported versions

Only the latest release is supported.
