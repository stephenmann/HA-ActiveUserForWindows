# Plan: Room Occupancy from Windows Activity

A .NET 8 Windows Service that reports **room occupancy per person** to Home Assistant over MQTT discovery. Each PC becomes an HA device pinned to an Area; the primary output is a `binary_sensor` with `device_class: occupancy` per (person × PC). Desktops report occupancy from activity alone; laptops must additionally pass a **network-identity location gate** proving they're physically at home. No `device_tracker`, no custom HA integration.

A room can contain **several devices belonging to the same person** — e.g. a personal desktop and a work laptop in the office, with the user active on either, both, or neither. Each agent reports only what it can see; combining them is Home Assistant's job via a binary_sensor Group helper in "any" mode.

Because the same human may sign in as `DESKTOP-X\stephen` on one machine and `CORP\sflowers` on another, identity is keyed on a configured **`personKey`**, not the raw Windows SID.

**Entity model per PC** (HA device = the PC, `suggested_area` = its home-base room)

| Scope | Entity | Meaning |
|---|---|---|
| per person | `binary_sensor` · `occupancy` | **primary** — person active on this PC AND location gate passed |
| per person | `sensor` | current room name, or `away` / `unknown` |
| per person | `binary_sensor` | session locked |
| per person | `sensor` · `duration`, s | idle seconds |
| device | `sensor` | active username |
| device | `binary_sensor` · `connectivity` | "At home" (location gate result) |
| device | `sensor` · diagnostic | network location label |

The occupancy sensor carries a `json_attributes_topic` exposing **`last_active`** (ISO 8601), plus the room and the contributing Windows account. `last_active` is what lets a template deterministically pick the freshest device when one person is active in two rooms at once — `last_changed` is unreliable for this.

---

### Phase 1 — Session sensing

1. Scaffold the solution: `net8.0-windows` worker service, xUnit test project, `installer/`, `docs/`.
2. Win32 session interop in `src/HaActiveUser.Agent/Windows/Sessions/`: `WTSEnumerateSessionsW` plus per-session `WTSConnectState` and `WTSSessionInfoEx` (→ `WTSINFOEXW.Level1` for lock flag, `LastInputTime`, username, domain). *Critical:* the service runs in session 0, so `GetLastInputInfo` is unusable — `WTSINFOEX.LastInputTime` is the only service-accessible idle source.
3. Define `ISessionProvider` → immutable `SessionSnapshot[]` (sessionId, SID, domain\user, connectState, isLocked, lastInputUtc, isRemote), with a WTS implementation and a fake for tests. *Parallel with step 2.*
4. Identity resolution:
   - Device ID from `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`.
   - `IPersonResolver` maps a session's **SID or `DOMAIN\user`** to a configured `personKey` via the `accounts` config list, returning `null` for untracked accounts. Matching on SID is preferred (rename-safe); `DOMAIN\user` is accepted for convenience.
   - All topics, `unique_id`s and entity names key off `personKey`, so `DESKTOP-X\stephen` and `CORP\sflowers` produce comparably named entities on their respective devices.

### Phase 2 — Device profile and location gate

5. `IDeviceProfileDetector` → `Desktop | Laptop`. Auto-detect via WMI `Win32_SystemEnclosure.ChassisTypes` (portable = 8, 9, 10, 11, 12, 14, 30, 31, 32) with a `GetSystemPowerStatus` battery-presence fallback (`BatteryFlag != 128`). Config `deviceProfile: auto|desktop|laptop` overrides. *Parallel with Phase 1.*
6. `IHomeLocationDetector` → `AtHome | Away | Unknown`, composed of independent strategies, any-match:
   - **Wi-Fi** — `wlanapi.dll`: `WlanOpenHandle` → `WlanEnumInterfaces` → `WlanQueryInterface(wlan_intf_opcode_current_connection)` → `WLAN_CONNECTION_ATTRIBUTES.wlanAssociationAttributes.{dot11Ssid, dot11Bssid}`, matched against a BSSID/SSID allowlist. Prefer BSSID; SSID alone is trivially spoofed.
   - **Gateway MAC** — `GetAdaptersAddresses` for the default gateway IP, then `SendARP` (iphlpapi), matched against an allowlist. Covers wired and docked.
   - **Dock / monitor topology** — match configured monitor EDID device instance IDs or dock USB hardware IDs via SetupAPI. Ship a `--list-devices` CLI verb so the IDs can be discovered for config.

   Have the interface return a **resolved room name** rather than a boolean, even though v1 always resolves to the single configured home-base room. That keeps per-BSSID room mapping a later config change rather than a refactor.
7. Add **hysteresis and a resume-settle window** to the detector. Wi-Fi re-associates several seconds after wake, so without a settle delay every resume-from-sleep publishes a false "away". Roaming between APs changes the BSSID, so debounce before flipping at-home off. *Depends on 6.*
8. Trigger re-evaluation on `NetworkChange.NetworkAddressChanged` / `NetworkAvailabilityChanged` plus the poll timer. *Depends on 6.*

### Phase 3 — Occupancy evaluator

9. `OccupancyEvaluator` — a pure function of `(SessionSnapshot[], LocationState, DeviceProfile, Clock)` → per-**person** `PresenceState { isOccupied, isLocked, idleSeconds, room, lastActiveUtc, sourceAccount }`. Sessions are first grouped by `personKey`, so two Windows accounts mapped to the same person on one PC are OR'd into a single result, taking the most recent `lastInput`. Activity requires a session with `ConnectState == WTSActive`, unlocked, and `now - lastInput < idleThreshold`, with an away-grace debounce. Occupancy = activity **AND** location gate, where the gate is a constant `true` for desktops. *Depends on 3, 4, 5, 6.*
10. Unit-test the evaluator exhaustively against fakes: logoff, lock, RDP-disconnect, idle threshold crossing, grace expiry, multiple concurrent sessions, fast user switching, two accounts mapped to one `personKey`, untracked accounts being ignored, laptop active-but-away, Wi-Fi roam, resume-settle window, and location `Unknown`. *Depends on 9.*

### Phase 4 — MQTT and discovery

11. MQTTnet client with auto-reconnect, MQTT v5, and a **Last Will** of `offline` (retained) on `haactiveuser/<deviceId>/status`; publish `online` on connect.
12. `DiscoveryPayloadBuilder` producing one retained device-discovery message on `homeassistant/device/<deviceId>/config`. Root carries `dev` (identifiers = machine GUID, name, `sa` = home-base room) and `o` (origin) — both mandatory for device discovery — plus a shared `availability_topic`. `cmps` holds the seven entity types above, each with `p` and `unique_id`. *Depends on 4.*

    Note that `suggested_area` is a **creation-time hint on the device**, so a laptop cannot be moved between HA Areas dynamically. This is exactly why the laptop's live room lives in the room-name *sensor*, not in area assignment.
13. Topics under `haactiveuser/<deviceId>/`: `status`, `active_user`, `at_home`, `network_location`, and `person/<personKey>/{occupancy,room,locked,idle,attributes}`. The `attributes` payload carries `last_active`, `room` and `source_account`.
14. Subscribe to `homeassistant/status`; on `online`, wait a short random delay then republish discovery and all states — this is what restores entities after an HA restart. *Depends on 12.*
15. Wire evaluator → publisher: publish on change, with a throttled heartbeat for the idle sensor. *Depends on 9, 11.*

### Phase 5 — Configuration and security

16. `%ProgramData%\HAActiveUser\config.json` bound via `IOptionsMonitor`: broker settings, TLS/mTLS paths, discovery prefix, `deviceProfile`, `room`, idle threshold, away grace, an `accounts` list (each entry `{ sid | account, personKey, displayName }` — this doubles as the tracked-account allowlist, so service and admin accounts never produce entities), and a `homeLocation` block (`requireForOccupancy`, wifi BSSIDs/SSIDs, gateway MACs, dock device IDs, match mode, away grace, resume-settle seconds).

    Add a `--list-accounts` CLI verb that prints local and recently-logged-on accounts with their SIDs, so the mapping can be filled in without hunting through `wmic`.
17. Encrypt the broker password with DPAPI `DataProtectionScope.LocalMachine` (NuGet `System.Security.Cryptography.ProtectedData`); provide a `--set-password` CLI verb so plaintext is never typed into the file. Restrict the ProgramData ACL to SYSTEM + Administrators.
18. **Publish a label, not raw identifiers**, on the network-location sensor — `home` / `away` / a friendly name. Raw BSSIDs and MACs would otherwise be persisted indefinitely in HA's recorder database. Put raw values behind an explicit opt-in.
19. Wire optional TLS / mTLS client certs into the MQTTnet options.

### Phase 6 — Service host wiring

20. Subclass `WindowsServiceLifetime`, set `CanHandleSessionChangeEvent` and `CanHandlePowerEvent`, and register it as `IHostLifetime`. **`UseWindowsService()` alone does not deliver session events** — this subclass is mandatory.
21. Override `OnSessionChange` to push logon/logoff/lock/unlock/connect/disconnect into a channel for immediate re-evaluation, so lock/unlock is near-instant. Override `OnPowerEvent` to publish `offline` before suspend and to start the resume-settle timer on wake. *Depends on 20, 7.*
22. Event Log + rolling file logging; poll timer as the fallback trigger for idle-threshold crossings and dock-state changes.

### Phase 7 — Installer

23. `dotnet publish -r win-x64 --self-contained false /p:PublishSingleFile=true`. *Depends on Phases 1–6.*
24. WiX v5 MSI: install to Program Files, register the service as LocalSystem with delayed auto-start and failure-restart actions, create the ACL'd ProgramData directory, and offer a broker + room configuration dialog. On uninstall, publish an empty retained payload to the discovery topic so HA removes the device.

### Phase 8 — Docs and CI

25. `README.md`: architecture, topic reference, sample discovery payload, config reference, how to find your BSSID and gateway MAC, and a **multi-device setup walkthrough**. The walkthrough must cover:
    - Using the same `personKey` in each machine's config.
    - Creating a **binary_sensor Group helper** per (person × room) in "any" mode (Settings → Devices & Services → Helpers → Group → Binary sensor group, with "all entities" left off) so the office is occupied when Stephen is active on the desktop *or* the work laptop. This is the recommended aggregation path — built-in, UI-driven, and restart-safe.
    - A **template sensor** for "which room is Stephen in", selecting the occupancy entity with the newest `last_active` attribute among those currently `on`, and falling back to `away`. *Parallel with everything.*
26. GitHub Actions: build, test, publish, build the MSI, attach to release.

---

**Verification**

1. `dotnet test` — evaluator covers the full activity × location × profile matrix.
2. `mosquitto_sub -h <broker> -v -t 'haactiveuser/#' -t 'homeassistant/device/#'` — confirm the retained discovery payload and state topics.
3. In HA: the PC appears as a device in the expected Area with all seven entities.
4. **Desktop matrix:** lock → occupancy `off` in seconds; unlock → `on`; sign out → `off`; idle past threshold → `off`; mouse move → `on`.
5. **Laptop matrix:** active at home → occupancy `on`, at-home `on`; tether to a phone hotspot while still active → occupancy `off`, room `away`, at-home `off`; return to home Wi-Fi → recovers; airplane-mode toggle → no flapping; undock/redock; **sleep then resume → no false `away` blip** (the key regression test for the settle window); roam between APs → occupancy holds steady.
6. **Multi-device matrix (the office case):** with the desktop and work laptop both mapped to `stephen` and both in the Office —
   - active on desktop only → group `on`; active on laptop only → group `on`; active on both → group `on`; neither → group `off`.
   - switch from desktop to laptop mid-session → the group never drops to `off` during the handover.
   - kill one agent → the group stays `on` if the other is still active, and that device's entities go `unavailable` without dragging the group down.
   - move the laptop to the living room and be active on both → the room sensor resolves to whichever has the newer `last_active`, and does not oscillate.
7. Map two Windows accounts on one PC to a single `personKey` → one set of entities, OR'd correctly.
8. Kill the agent → entities go `unavailable` via LWT, distinctly from `off`.
9. Restart HA → entities repopulate from the birth-message handler.
10. MSI install/uninstall on a clean VM; confirm HA entities disappear after uninstall.

**Decisions**

- **Dropped:** `device_tracker`, custom HA integration, HACS, config flow, Windows Location API.
- **Out of scope for v1:** mapping individual BSSIDs to individual rooms (a laptop reports its single configured home-base room); agent-side cross-device aggregation.
- Agents are deliberately **independent and unaware of each other**. Each reports only what it observes; combining devices happens in HA. Peer-to-peer aggregation over MQTT would require leader election and distributed state for no real gain.
- Identity is a configured `personKey`, not a SID or username, so mismatched personal and corporate accounts resolve to one person.
- Location gate defaults to required for laptops, not required for desktops — overridable either way.
- Locked sessions never count as occupied; use a generous idle threshold instead if you want "sitting at the desk reading" to register.
- **This is a positive-only signal.** Computer activity proves presence; its absence proves nothing. Treat the output as one input to room occupancy, not the whole answer.

**Further Considerations**

1. **Should a docked laptop skip the Wi-Fi check entirely?** A docked machine is stationary by definition and often on Ethernet with no Wi-Fi association at all. *Recommendation:* treat a dock match as a sufficient standalone signal (any-match already does this), and let the dock optionally override the home-base room. Option A: dock is just another signal / Option B: dock short-circuits the gate and sets the room.
2. **VPN false-positives on gateway-MAC matching.** A full-tunnel VPN can change the default route so the gateway lookup returns the VPN adapter, breaking the match. *Recommendation:* enumerate physical adapters only, skipping tunnel and virtual interfaces. Option A: filter by `NetworkInterfaceType` / `OperationalStatus` / Option B: match against *any* adapter's gateway rather than only the default route / Option C: ignore — Wi-Fi BSSID already covers the mobile case.
3. **Fusing this with a real occupancy sensor.** Because computer activity can only ever prove presence, the office will read unoccupied the moment you turn to a book or a phone. *Recommendation:* combine the Group helper with an mmWave or motion sensor in the same Area, using computer activity to *hold* occupancy on and the motion sensor to establish it. Option A: OR both signals / Option B: motion establishes, computer activity extends the timeout / Option C: computer activity alone, accepting the false negatives.
4. **Group helper creation is manual per (person × room).** With two people and four rooms that is eight helpers to hand-build, and nothing in the repo enforces consistency. *Recommendation:* ship a documented YAML `binary_sensor` group snippet alongside the UI instructions so the set can be version-controlled. Option A: UI helpers only / Option B: ship YAML groups / Option C: both, UI for beginners and YAML in `docs/`.
