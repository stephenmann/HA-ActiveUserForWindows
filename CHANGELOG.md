# Changelog

All notable changes to this project are documented here.
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- The per-user input reporter now runs as a tray application at logon instead of briefly opening a
  console window. The icon shows whether it is connected to the service, opens the per-user log on
  demand, and prevents duplicate instances within the same Windows session.

## [1.1.1] - 2026-08-10

### Fixed

- **Occupancy never turned on, and idle time read -1.** The named pipe's DACL granted Authenticated
  Users `Write | ReadAttributes | Synchronize`. Opening a pipe for writing requests `GENERIC_WRITE`,
  which maps to `FILE_GENERIC_WRITE` — including `READ_CONTROL`, which was missing — and the access
  check requires every mapped bit. Every non-elevated helper was denied; running one elevated worked,
  which is what made this look like anything but a permissions problem. `ReadPermissions` is now
  granted. Read and ACL-write rights are still withheld.
- **The helper failed silently.** It swallowed every exception, so a permanent access-denied was
  indistinguishable from a service restart. It now classifies the failure and writes it to
  `%LOCALAPPDATA%\HAActiveUser\session-agent.log` — a per-user path, because the helper runs as you
  and cannot write to the service's log directory, which is exactly why this failure was invisible.
  Repeated messages are suppressed, and recovery is logged. It still never throws out of its loop.
- **Reinstalling an MSI of the same version installed a second copy** alongside the first, leaving two
  entries in Programs and Features and stale files. `AllowSameVersionUpgrades` is now set, and the
  product version is no longer pinned at 1.0.0 — 1.0.1 and 1.1.0 both shipped an MSI identifying
  itself as 1.0.0, so `MajorUpgrade` had nothing to compare and never replaced the previous install.

## [1.1.0] - 2026-08-10

### Added

**Idle detection that works on a local console**

- A per-user helper (`--session-agent`) reports idle time to the service over a named pipe.
  `WTSINFOEX.LastInputTime` is maintained only for remote sessions; on a local console it is frozen
  at logon, so idle time never rose and occupancy never cleared. `GetLastInputInfo` is the accurate
  source but is per-session, and the service runs in session 0.
- The service attributes each report to the SID of the connecting process rather than trusting
  anything in the payload, so one user cannot report activity on another's behalf.

**Signed releases**

- The MSI is signed with Azure Artifact Signing via OIDC, with no stored credentials. Workflow
  actions are pinned to commit SHAs, and the packaging job is bound to a `release` environment so a
  single Azure federated credential covers every release regardless of branch or tag.

### Fixed

- **Devices never appeared in Home Assistant.** The discovery payload declared a `~` base topic at the
  root and referenced it from every component. Device discovery accepts only a fixed set of shared root
  options — availability, `origin`, `command_topic`, `state_topic`, `qos`, `encoding` — so the payload
  failed validation and was discarded without creating a device. Topics are now fully qualified.
- **Fresh installs seeded an unusable account mapping.** The default config took `Environment.UserName`,
  but it is written by the service under LocalSystem, so it recorded the machine account (`MACHINE$`).
  No interactive session could ever match it, and presence silently stayed off. The default is now
  taken from the signed-in session and records the SID.

## [1.0.1] - 2026-08-10

### Fixed

- **A plaintext password in the config crash-looped the service.** `Mqtt.ProtectedPassword` expects a
  DPAPI blob; a hand-typed value threw inside a DI factory during host start, producing a stack trace
  every 30 seconds. Secrets are now validated before startup and reported as a single actionable line,
  with distinct messages for a plaintext value and a blob from another machine.
- Log files are capped at 16 MB with rollover. A crash loop previously grew the daily log without bound.

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

[1.2.0]: https://github.com/stephenmann/HA-ActiveUserForWindows/releases/tag/v1.2.0
[1.1.0]: https://github.com/stephenmann/HA-ActiveUserForWindows/releases/tag/v1.1.0
[1.0.1]: https://github.com/stephenmann/HA-ActiveUserForWindows/releases/tag/v1.0.1
[1.0.0]: https://github.com/stephenmann/HA-ActiveUserForWindows/releases/tag/v1.0.0
