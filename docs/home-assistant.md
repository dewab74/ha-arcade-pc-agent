# Home Assistant examples

The agent needs no YAML: its device and entities come from MQTT discovery.
These automations are optional extras. They assume the default node id
`cocktail_pc`; replace `notify.notify` with your own notifier. The ones
marked *launchbox-mqtt* also use the
[launchbox-mqtt](https://github.com/dewab74/launchbox-mqtt) plugin's entities
(its node id here is `arcade`).

## Blank the screen when nobody is using the menu (*launchbox-mqtt*)

This turns the monitor off after 20 minutes at the BigBox menu with no input;
the PC keeps running. Any real input wakes it. The check runs every 5 minutes,
because a template trigger that is already true when it's loaded never fires.
An unknown last input never blanks the screen.

```yaml
automation:
  - alias: Arcade - screen off when idle in the menu
    mode: single
    triggers:
      - trigger: time_pattern
        minutes: "/5"
    conditions:
      - condition: state
        entity_id: binary_sensor.launchbox_arcade_bigbox_running
        state: "on"
      - condition: state
        entity_id: binary_sensor.launchbox_arcade_playing
        state: "off"
      - condition: state
        entity_id: switch.cocktail_pc_screen
        state: "on"
        for: "00:20:00"
      - condition: template
        value_template: >-
          {% set last = states('sensor.cocktail_pc_last_input') | as_datetime %}
          {{ last is not none and now() - last > timedelta(minutes=20) }}
    actions:
      - action: switch.turn_off
        target:
          entity_id: switch.cocktail_pc_screen

  - alias: Arcade - screen on when a game starts
    mode: single
    triggers:
      - trigger: state
        entity_id: binary_sensor.launchbox_arcade_playing
        to: "on"
    conditions:
      - condition: state
        entity_id: switch.cocktail_pc_screen
        state: "off"
    actions:
      - action: switch.turn_on
        target:
          entity_id: switch.cocktail_pc_screen
```

## Alert on a stuck control

A button or stick held for 10 minutes usually means a jammed microswitch or a
shorted wire. While it is held, the PC never looks idle.

```yaml
automation:
  - alias: Arcade - stuck input
    triggers:
      - trigger: state
        entity_id: binary_sensor.cocktail_pc_stuck_input
        to: "on"
    actions:
      - action: notify.notify
        data:
          title: Arcade input stuck
          message: >-
            {{ state_attr('binary_sensor.cocktail_pc_stuck_input', 'stuck_inputs')
               | default([], true) | join('; ') or 'An input' }} is held down.
```

## Alert on low disk

This uses separate thresholds for alerting and clearing, so it can't flap
around a single value.

```yaml
automation:
  - alias: Arcade - low disk
    triggers:
      - trigger: template
        value_template: >-
          {{ state_attr('sensor.cocktail_pc_disk_free', 'percent_free') | float(100) < 10 }}
        for: "00:10:00"
    actions:
      - action: persistent_notification.create
        data:
          notification_id: arcade_low_disk
          title: Arcade PC disk almost full
          message: "{{ states('sensor.cocktail_pc_disk_free') }} GB free on C:."

  - alias: Arcade - low disk cleared
    triggers:
      - trigger: template
        value_template: >-
          {{ state_attr('sensor.cocktail_pc_disk_free', 'percent_free') | float(0) > 12 }}
    actions:
      - action: persistent_notification.dismiss
        data:
          notification_id: arcade_low_disk
```

## Cut a smart plug after the PC has shut down

If the PC sits behind a smart plug with power metering, Shut down can end
with the plug switched off. The plug is only cut once Windows is really
gone: the agent has been unavailable for 2 minutes and the draw is at
standby level. Adjust the 15 W to your monitor's and amplifier's standby
draw.

```yaml
automation:
  - alias: Arcade - cut power after Windows shuts down
    mode: single
    triggers:
      - trigger: state
        entity_id: switch.cocktail_pc_screen
        to: unavailable
        for: "00:02:00"
    conditions:
      - condition: state
        entity_id: switch.arcade_plug
        state: "on"
      - condition: numeric_state
        entity_id: sensor.arcade_plug_power
        below: 15
    actions:
      - action: switch.turn_off
        target:
          entity_id: switch.arcade_plug
```

Switching the plug on again boots the PC, if its BIOS is set to power on
after AC loss. The agent starts at sign-in.
