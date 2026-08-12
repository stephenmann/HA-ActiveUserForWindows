# HA Active User for Windows

A Windows service that turns "someone is actually using this PC" into a **room occupancy** signal in
Home Assistant, published over plain MQTT discovery. No custom integration, no HACS, no config flow.

Install it on every machine in a room. Each machine publishes its own device and per-person
occupancy entities; Home Assistant combines them.

---

## Why occupancy and not `device_tracker`

`device_tracker` looks like the obvious fit and is the wrong tool:

- Its state space is `home` / `not_home` / a zone. Zones are GPS circles, so it cannot express "in
  the office".
- A tracker published over MQTT is a **connection tracker**. In the Person integration, connection
  trackers outrank GPS trackers whenever they report `home`. An unlocked desktop would pin the
  person `home` and permanently override their phone's location.

So the agent publishes `binary_sensor` entities with `device_class: occupancy` instead. They
describe a room, not a person's global whereabouts, and they compose cleanly with everything else.

---

## What gets created

Each machine becomes one Home Assistant device with these entities.

| Entity | Platform | Notes |
| --- | --- | --- |
| `<Person> occupancy` | `binary_sensor` (`occupancy`) | The signal you automate on. Input and lock state only. |
| `<Person> room` | `sensor` | Resolved room, or `away` / `unknown`. |
| `<Person> screen locked` | `binary_sensor` | Diagnostic. |
| `<Person> idle time` | `sensor` (`duration`, seconds) | Diagnostic. |
| `Active user` | `sensor` | Who is currently active on this machine, or `none`. |
| `At home location` | `binary_sensor` | Whether the location gate passed. |
| `Network location` | `sensor` | `home`, `away` or `unknown`. |

The occupancy and room entities carry a `last_active` attribute (ISO-8601 UTC). That is what lets
Home Assistant break ties when the same person is reported by more than one machine.

---

## How presence is decided

```mermaid
flowchart TD
    A[Per-session helper<br/>GetLastInputInfo] --> B[Service session 0<br/>WTSEnumerateSessions + lock state]
    B --> C{Attached, unlocked,<br/>input within idle threshold?}
    C -- no --> D[Away grace timer]
    C -- yes --> E[Active]
    D -- expired --> F[Not active]
    D -- still within grace --> E

    G[Wi-Fi BSSID / SSID] --> J[Composite detector]
    H[Default gateway MAC] --> J
    I[Dock / monitor device ID] --> J
    J --> K[Location stabilizer<br/>away grace + resume settle]

    E --> M[occupancy ON]
    K --> L{Location gate required<br/>and at home?}
    L -- yes --> N[room = configured room]
    L -- no --> O[room = away / unknown]
```

Occupancy answers one question: *is someone using this machine right now?* It depends only on input
and lock state. Location decides which **room** that activity is attributed to, not whether it
counts.

### Session sensing

This is the part Windows makes awkward. `GetLastInputInfo` is per-session, and the service runs in
**session 0**, so from there it only ever reports session 0's own input. The obvious alternative,
`WTSQuerySessionInformationW(..., WTSSessionInfoEx)`, returns a `LastInputTime` that is **frozen at
logon for local console sessions** — it only advances for remote/RDP sessions.

So the installer registers a lightweight per-user tray application at logon. It runs inside the
interactive session, where `GetLastInputInfo` works, and reports its own idle time to the service
every few seconds over a named pipe. Its tray icon shows whether it is connected to the service and
provides access to its per-user log without opening a console window. The service attributes each
report to the SID of the connecting process, so a user can only ever report their own state.

The service still uses WTS for everything that *is* reliable from session 0: which sessions exist,
their accounts, connect state and lock flag.

Reports older than 30 seconds are discarded, so a helper that dies reads as "no data" rather than
"idle forever". A session with no helper reporting never counts as occupied.

A session counts as active when all of the following hold:

- `WTS_CONNECTSTATE_CLASS` is `Active` — a disconnected RDP session keeps running with nobody in
  front of it.
- The session is not locked.
- The last input is within `IdleThresholdSeconds`.

When that stops being true, occupancy is held for `AwayGraceSeconds` so a brief lock or an idle blip
does not flap the sensor.

### The location gate

A desktop that is in use is, by definition, in its room. A laptop is not — it might be in a café. So
for laptops the agent requires proof that the machine is physically at home before it will attribute
activity to the configured room; without that proof the room is reported as `away` or `unknown`.

`Windows.Devices.Geolocation` is deliberately **not** used: it needs interactive per-user consent
that cannot be granted from session 0, and its Wi-Fi/IP-derived accuracy is 100 m to 5 km — useless
for room-level presence. Network identity is used instead:

| Strategy | Config | Notes |
| --- | --- | --- |
| Wi-Fi | `HomeLocation.Wifi.Bssids` / `.Ssids` | BSSID is preferred: it identifies the actual access point rather than a name anyone can clone. |
| Default gateway MAC | `HomeLocation.GatewayMacs` | Best for wired desks. Virtual, VPN and tunnel adapters are excluded so a tunnel cannot fake "home". |
| Dock or fixed monitor | `HomeLocation.DockDeviceIds` | Survives the Wi-Fi being off. Matched as a prefix. |

Strategies that cannot form an opinion (no Wi-Fi adapter, no gateway) return *indeterminate* and are
excluded from the decision rather than voting "away". If every strategy is indeterminate, the
location is `unknown` and a gated device reports its room as `unknown`.

`MatchMode` is `Any` (default) or `All`.

### Debouncing

Two effects make the raw readings untrustworthy for short windows:

- **Resume from sleep.** Wi-Fi re-associates several seconds after wake. Without a settle window,
  every single resume would publish a false "away". On resume the agent holds the previous location
  for `ResumeSettleSeconds`.
- **Roaming.** Moving between access points briefly drops the association.

So *becoming* home applies immediately, but *leaving* home only applies after
`HomeLocation.AwayGraceSeconds` of continuous non-home readings.

---

## Multi-device rooms

The scenario this is built for: a personal desktop and a work laptop on the same desk. You may be
active on either, both, or neither.

Agents cannot see each other — peer-to-peer aggregation would need leader election and distributed
state on every desk. Aggregation is pushed into Home Assistant instead, where it is a two-minute
helper.

**1. Use the same `personKey` on every machine.** The Windows accounts differ
(`DESKTOP-PC\stephen` vs `CORP\sflowers`) and so do their SIDs, but both map to one person:

```jsonc
// on the desktop
{ "Account": "DESKTOP-PC\\stephen", "PersonKey": "stephen", "DisplayName": "Stephen" }

// on the work laptop
{ "Account": "CORP\\sflowers", "PersonKey": "stephen", "DisplayName": "Stephen" }
```

**2. Combine occupancy with a Group helper.** Settings → Devices & Services → Helpers → Create
helper → Group → Binary sensor group. Add every `<Person> occupancy` entity and leave *"all
entities"* **off**, so the group is `on` when *any* member is `on`.

```yaml
# configuration.yaml equivalent
binary_sensor:
  - platform: group
    name: Stephen office occupancy
    device_class: occupancy
    entities:
      - binary_sensor.office_pc_stephen_occupancy
      - binary_sensor.work_laptop_stephen_occupancy
```

**3. Resolve the room with a template sensor.** Picks the machine with the newest `last_active`
among those currently reporting occupancy, and falls back to `away`:

```yaml
template:
  - sensor:
      - name: Stephen room
        state: >
          {% set sources = [
               'sensor.office_pc_stephen_room',
               'sensor.work_laptop_stephen_room'
             ] %}
          {% set active = sources
               | select('has_value')
               | map('states')
               | list %}
          {% set candidates = namespace(best=none, at=none) %}
          {% for source in sources %}
            {% set room = states(source) %}
            {% set seen = state_attr(source, 'last_active') %}
            {% if room not in ['away', 'unknown', 'unknown', 'unavailable'] and seen is not none %}
              {% if candidates.at is none or seen > candidates.at %}
                {% set candidates.at = seen %}
                {% set candidates.best = room %}
              {% endif %}
            {% endif %}
          {% endfor %}
          {{ candidates.best if candidates.best is not none else 'away' }}
```

---

## Installation

1. Download `HAActiveUser.msi` from the releases page and install it (elevated).
2. Edit `C:\ProgramData\HAActiveUser\config.json`.
3. Set the broker password: `"C:\Program Files\HA Active User\HaActiveUser.Agent.exe" --set-password`
4. Restart the service: `Restart-Service HAActiveUser`
5. Sign out and back in, so the per-user idle helper starts. Without it the service has no idle
   data and reports no occupancy.

Releases are Authenticode-signed through Azure Artifact Signing when the repository is configured
for it — see [Release signing](#release-signing). A build made before that was set up, or from a
fork without the credentials, is unsigned and will raise a SmartScreen warning on install.

The service runs as `LocalSystem`. `C:\ProgramData\HAActiveUser` is ACLed to SYSTEM and
Administrators only, because the config holds a machine-scope DPAPI secret that anything running on
the box could otherwise decrypt.

### Building from source

```powershell
dotnet test                      # unit tests
.\build\publish-agent.ps1        # single-file publish + MSI into installer\bin\Release
```

---

## Configuration

`C:\ProgramData\HAActiveUser\config.json`:

```jsonc
{
  "Agent": {
    "Room": "Office",              // becomes the device's suggested_area and the reported room
    "DeviceProfile": "Auto",       // Auto | Desktop | Laptop
    "DeviceName": null,            // defaults to the machine name
    "DiscoveryPrefix": "homeassistant",
    "TopicPrefix": "haactiveuser",

    "IdleThresholdSeconds": 600,   // no input for this long = not active
    "AwayGraceSeconds": 60,        // hold occupancy this long after activity stops
    "PollIntervalSeconds": 10,
    "IdleHeartbeatSeconds": 60,    // republish the idle sensor at least this often

    "Accounts": [
      {
        "Sid": null,                       // most reliable; survives renames
        "Account": "DESKTOP-PC\\stephen",  // or a bare username to match in any domain
        "PersonKey": "stephen",            // must match on every machine for the same person
        "DisplayName": "Stephen"
      }
    ],

    "HomeLocation": {
      "RequireForOccupancy": null,   // null = required for laptops, skipped for desktops
      "MatchMode": "Any",            // Any | All
      "Wifi": {
        "Bssids": ["aa:bb:cc:dd:ee:ff"],
        "Ssids": []
      },
      "GatewayMacs": ["11:22:33:44:55:66"],
      "DockDeviceIds": [],
      "AwayGraceSeconds": 120,
      "ResumeSettleSeconds": 30,
      "PublishRawIdentifiers": false // keep BSSIDs and MACs out of the HA recorder
    },

    "Mqtt": {
      "Host": "homeassistant.local",
      "Port": 1883,
      "ClientId": null,
      "Username": "mqtt-user",
      "ProtectedPassword": "",       // written by --set-password, never by hand
      "ReconnectDelaySeconds": 5,
      "KeepAliveSeconds": 60,
      "Tls": {
        "Enabled": false,
        "CaCertificatePath": null,
        "ClientCertificatePath": null,
        "ProtectedClientCertificatePassword": null,
        "AllowUntrustedCertificates": false,
        "IgnoreCertificateChainErrors": false,
        "IgnoreCertificateRevocationErrors": false
      }
    }
  }
}
```

Only accounts listed in `Accounts` are tracked. Everyone else is ignored entirely — no entity, no
attribute, nothing published.

`PersonKey` is slugged to `[a-z0-9_-]` for use in unique IDs and topics, so `"Stephen Flowers"`
becomes `stephen_flowers`.

Changes are picked up on save; restart the service if the entity set changed.

### Broker transport security

**The defaults are cleartext.** `Port` is 1883 and `Tls.Enabled` is `false`, so the broker username
and password are sent unencrypted on every connect, and every presence update is readable by anyone
who can see the traffic — which also tells them exactly when you are at your desk.

That is a reasonable default on a trusted home LAN. Turn TLS on if the broker is reachable from
anything you do not control, or if the network carries guest, rented or IoT devices:

```jsonc
"Mqtt": {
  "Port": 8883,
  "Tls": { "Enabled": true }
}
```

If your broker uses a private CA (the usual case for a self-hosted Home Assistant), install that CA
into the **Windows machine trust store** (`Cert:\LocalMachine\Root`). The service runs as
`LocalSystem` and validates against the OS trust store, so this is what actually makes validation
succeed:

```powershell
Import-Certificate -FilePath .\ca.crt -CertStoreLocation Cert:\LocalMachine\Root
```

Do **not** reach for `AllowUntrustedCertificates`, `IgnoreCertificateChainErrors` or
`IgnoreCertificateRevocationErrors` to make a stubborn broker connect. Each of them disables the
check that TLS exists to perform, which re-opens the machine-in-the-middle you just turned TLS on to
close. They are there for lab use.

Two further notes:

- Use a **dedicated MQTT account** for the agent, restricted to the `haactiveuser/#` and
  `homeassistant/#` topics. The broker password is stored with machine-scope DPAPI, which means any
  administrator on the machine can recover it — so it should not be an account you reuse elsewhere.
- `ProtectedPassword` is written only by `--set-password`. The secret is bound to the machine that
  created it; copying a config file to another machine will not work.

### Command-line helpers

```powershell
$agent = "C:\Program Files\HA Active User\HaActiveUser.Agent.exe"

& $agent --set-password        # encrypt the broker password (machine-bound; run on each machine)
& $agent --list-accounts       # signed-in accounts with their SIDs
& $agent --list-devices dock   # present PnP devices, filtered
& $agent --remove-from-ha      # delete this device and its entities from Home Assistant
& $agent --session-agent       # report this session's idle time; started at logon by the installer
```

### Finding the identifiers

**Wi-Fi BSSID and SSID:**

```powershell
netsh wlan show interfaces
# SSID  : MyNetwork
# BSSID : aa:bb:cc:dd:ee:ff      <- this one
```

List every access point on the network so you can add each one you roam between:

```powershell
netsh wlan show networks mode=bssid
```

**Default gateway MAC:**

```powershell
$gw = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).NextHop
ping -n 1 $gw | Out-Null
Get-NetNeighbor -IPAddress $gw | Select-Object IPAddress, LinkLayerAddress
```

**Dock or monitor device ID:** `HaActiveUser.Agent.exe --list-devices dock`. Copy the instance ID,
or a stable prefix of it, into `DockDeviceIds`.

---

## MQTT reference

`<device>` is a slug of the machine's `MachineGuid`, so it survives renames.

| Topic | Payload |
| --- | --- |
| `homeassistant/device/<device>/config` | Discovery, retained. Empty payload deletes the device. |
| `haactiveuser/<device>/status` | `online` / `offline` (LWT), retained |
| `haactiveuser/<device>/active_user` | Display name or `none` |
| `haactiveuser/<device>/at_home` | `ON` / `OFF` |
| `haactiveuser/<device>/network_location` | `home` / `away` / `unknown` |
| `haactiveuser/<device>/person/<key>/occupancy` | `ON` / `OFF` |
| `haactiveuser/<device>/person/<key>/room` | Room name, `away` or `unknown` |
| `haactiveuser/<device>/person/<key>/locked` | `ON` / `OFF` |
| `haactiveuser/<device>/person/<key>/idle` | Seconds |
| `haactiveuser/<device>/person/<key>/attributes` | JSON, includes `last_active` |

All state is retained and published at QoS 1. Values are only republished when they change, except
the idle counter which gets a heartbeat.

The agent subscribes to `homeassistant/status` and republishes discovery when Home Assistant sends
its birth message, after a short random delay so a house full of agents does not stampede the broker.

### Sample discovery payload

```jsonc
{
  "dev": {
    "ids": ["haau_a1b2c3d4"],
    "name": "OFFICE-PC",
    "mf": "HA Active User for Windows",
    "mdl": "Microsoft Windows NT 10.0.26100.0",
    "sw": "1.0.0",
    "sa": "Office"
  },
  "o": { "name": "ha-activeuser-windows", "sw": "1.0.0", "url": "https://github.com/stephenmann/HA-ActiveUserForWindows" },
  "avty_t": "haactiveuser/a1b2c3d4/status",
  "pl_avail": "online",
  "pl_not_avail": "offline",
  "qos": 1,
  "cmps": {
    "stephen_occupancy": {
      "p": "binary_sensor",
      "name": "Stephen occupancy",
      "uniq_id": "haau_a1b2c3d4_stephen_occupancy",
      "stat_t": "haactiveuser/a1b2c3d4/person/stephen/occupancy",
      "pl_on": "ON",
      "pl_off": "OFF",
      "dev_cla": "occupancy",
      "json_attr_t": "haactiveuser/a1b2c3d4/person/stephen/attributes"
    }
    // ... one entry per entity
  }
}
```

Topics are written out in full. Device discovery only accepts a fixed set of shared root options —
availability, `origin`, `command_topic`, `state_topic`, `qos` and `encoding` — and the `~` base-topic
abbreviation is **not** one of them. Including it makes Home Assistant discard the entire payload.

`sa` (`suggested_area`) is only a **creation-time hint**. It cannot move a device between areas
later; do that in the Home Assistant UI.

---

## Privacy

- Only configured accounts are tracked.
- Raw BSSIDs and gateway MACs are **not** published unless `PublishRawIdentifiers` is set. The
  network location sensor publishes a label (`home` / `away` / `unknown`) instead, because the Home
  Assistant recorder keeps attribute history indefinitely.
- No latitude, longitude or GPS accuracy is ever published.
- Logs live in `C:\ProgramData\HAActiveUser\logs`, roll daily and are kept for 14 days.

---

## Troubleshooting

**Occupancy never turns on.** The per-user helper is not running, so the service has no idle data and
deliberately reports nobody home. It is registered at logon by the installer, so sign out and back in
after installing, or start it once by hand:

```powershell
& "C:\Program Files\HA Active User\HaActiveUser.Agent.exe" --session-agent
```

**The room says `away` or `unknown` on a laptop.** The location gate applies to laptops but no Wi-Fi,
gateway or dock identifiers are configured. The log warns about this at startup. Configure
`HomeLocation`, or set `"RequireForOccupancy": false`.

**No entities appear.** Check `Accounts` is not empty, then check the broker: the retained discovery
message should be on `homeassistant/device/<device>/config`.

**A person shows as occupied on the wrong machine.** Both machines are reporting truthfully — use
the Group helper and the template sensor above rather than trying to make one agent yield.

**Idle time never rises, or reads -1.** The idle figure comes from the per-user helper, not from
Windows' per-session bookkeeping, and `-1` means the service has no reports at all. Note that
`--list-accounts` is no help here: it reads the raw Terminal Services value, which stays frozen at
logon on a local console session. Check the helper is running (`--session-agent`, one instance per
signed-in user), then read `%LOCALAPPDATA%\HAActiveUser\session-agent.log` — the helper records why
it could not connect there, because it runs as you and cannot write to the service's log directory.
The service log will say either `No idle reports from <account>` or `Receiving idle reports from
<account>`.

**The device is stuck in Home Assistant after uninstall.** Publish an empty retained payload to
`homeassistant/device/<device>/config`, or reinstall and run `--remove-from-ha`.

---



## License

MIT
