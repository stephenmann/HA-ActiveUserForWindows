# Changelog

All notable changes to this project are documented here.
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-08-10

First public release.

A Windows service that turns "someone is actually using this PC" into a **room occupancy** signal in
Home Assistant, published over plain MQTT discovery. No custom integration, no HACS, no config flow —
install the MSI, point it at your broker, and the device and its entities appear automatically.

### Added

**Room occupancy, not device tracking**

- Per-person `binary_sensor` entities with `device_class: occupancy`, plus room, screen-locked and
  idle-time sensors, and device-level active-user, at-home and network-location diagnostics.
- `device_tracker` is deliberately not used. Its state space is `home` / `not_home` / GPS zone, so it
  cannot express "in the office", and an MQTT tracker is a *connection tracker* — it would outrank
  your phone's GPS in the Person integration and pin you `home` whenever a PC was unlocked.

**Session sensing that works from a service**

- Reads `WTSQuerySessionInformationW(..., WTSSessionInfoEx)` for last input, lock state and account
  across every session. The service runs in session 0, where `GetLastInputInfo` only ever reports
  session 0's own input and is therefore useless.
- A session counts only when it is `Active` (a disconnected RDP session keeps running with nobody in
  front of it), unlocked, and has had input within the idle threshold.
- Handles fast user switching, multiple concurrent sessions, and RDP.
- Subscribes to real session and power notifications rather than polling alone, by replacing the
  default `WindowsServiceLifetime` with one that opts into session-change and power events.

**Location gating for laptops**

- A desktop in use is in its room by definition; a laptop might be in a café. For laptops, occupancy
  additionally requires proof the machine is physically at home.
- Three strategies, combinable with `Any` or `All`: Wi-Fi BSSID/SSID, default-gateway MAC, and
  dock/monitor device ID. VPN, tunnel and hypervisor adapters are excluded so a tunnel cannot fake
  "home".
- `Windows.Devices.Geolocation` is not used: it needs interactive per-user consent that cannot be
  granted from session 0, and its accuracy is 100 m–5 km — useless for room-level presence.
- Strategies that cannot form an opinion return *indeterminate* and are excluded from the vote rather
  than counting as "away".

**Debouncing for the things that actually break presence**

- **Resume from sleep** holds the previous location for a settle window, because Wi-Fi re-associates
  several seconds after wake and would otherwise publish a false "away" on *every* resume.
- **Roaming between access points** is absorbed: becoming home applies immediately, leaving home only
  after a sustained grace period.
- Brief locks and idle blips are smoothed by an activity grace period.

**Multi-device rooms**

- Map different Windows accounts on different machines (`DESKTOP-PC\stephen`, `CORP\sflowers`) to one
  shared `personKey`.
- Every reading carries a `last_active` attribute so Home Assistant can break ties when the same
  person is reported by more than one machine. The README includes a Group helper and template sensor
  recipe for combining them.

**MQTT**

- One retained device-discovery message per machine with `dev`, `o` and `cmps`, using the `~` base
  topic. Entities are declared from configuration, so the set is stable and there is no entity churn.
- Last-will `offline`, explicit `offline` before suspend, and automatic reconnect.
- Republishes discovery when Home Assistant sends its birth message, after a short random delay so a
  house full of agents does not stampede the broker.
- Optional TLS with CA and client certificates.

**Setup and packaging**

- WiX MSI: installs and starts the service as `LocalSystem`, configures restart-on-failure, and ACLs
  `C:\ProgramData\HAActiveUser` to SYSTEM and Administrators.
- Uninstall clears the retained discovery topic, so no orphaned device or entities are left behind in
  Home Assistant.
- CLI helpers: `--set-password` (DPAPI, machine-scoped), `--list-accounts`, `--list-devices` and
  `--remove-from-ha`.
- Warns at startup when a laptop requires the location gate but no identifiers are configured — the
  one configuration mistake that would silently leave occupancy permanently off.

### Privacy

- Only accounts listed in `Accounts` are tracked; everyone else is ignored entirely.
- Raw BSSIDs and gateway MACs are **not** published unless `PublishRawIdentifiers` is enabled. The
  network-location sensor publishes `home` / `away` / `unknown` instead, because the Home Assistant
  recorder retains attribute history indefinitely.
- No latitude, longitude or GPS accuracy is ever published.

### Known limitations

- Windows 10/11 and Server 2016+ on x64. The `WTS_SESSIONSTATE_LOCK` flag is inverted on Windows 7
  and Server 2008 R2; that inversion is handled, but those versions are untested.
- `suggested_area` is a creation-time hint only. Moving a device between areas afterwards must be
  done in the Home Assistant UI.
- One room per machine. Per-BSSID room mapping is not exposed yet, though the detector already
  resolves a room name rather than a boolean so it is a configuration change rather than a redesign.
- `WTSINFOEX.LastInputTime` can be zero on some session types; idle time is published as `0` in that
  case.

[1.0.0]: https://github.com/stephenmann/HA-ActiveUserForWindows/releases/tag/v1.0.0
