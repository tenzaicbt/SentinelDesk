# Endpoint Monitoring & Management System — Technical Blueprint

> **Project codename:** `SentinelDesk` (rename freely)
> **Scope:** Organization-wide (island-wide, multi-branch) monitoring of company PCs and laptops through a protected tray agent and a central web console.
> **Version:** 1.3.0 — Blueprint (first written as 1.0 on 2026-11-20)
> **Date:** 2026-11-23
>
> **What changed in 1.3.0:** new **§17 One-Click Block / Unblock** for websites, applications and USB: a single
> toggle in the console, confirmed end-to-end by the agent (*Pending → Applied / Failed*) in seconds; stronger
> enforcement layers (local DNS filter + WFP, App Control for Business, Removable Storage Access policies + live device
> eject); advanced options (schedules, temporary unblock, allow-lists, per-user rules, USB whitelist / read-only,
> unblock requests, emergency *Unblock all*); new protocol messages, tables, APIs, test matrix and a 7-week plan.
>
> **What changed in 1.2.1:** policies are now **block policies** (block websites, applications and USB drives) that
> stack on top of one standard monitoring configuration (§4.5, §7.1, §7.2, §8.1); the tray shows a notice for blocked
> programs and lets an administrator reconnect a device with a new enrollment token (§4.4, §5.5).

---

## 1. Goals

| # | Requirement | How it is met |
|---|---|---|
| 1 | Small agent on every PC/laptop, visible in system tray | Windows Service (core) + lightweight Tray App (UI/status) |
| 2 | Install **and** uninstall require admin credentials | Signed MSI with elevation, uninstall password + admin check, service ACL hardening, watchdog, tamper alerts |
| 3 | Advanced encryption | mTLS (TLS 1.3) per-device certificates, AES-256-GCM payload encryption, encrypted local buffer (SQLCipher + DPAPI/TPM key) |
| 4 | Central web system to configure all agents | Policy engine, groups (branch/department), remote config push in real time; block policies for websites, applications and USB drives (§4.5) with **one-click block/unblock confirmed by the device** (§17) |
| 5 | Deep per-device view | Hardware/software inventory, bandwidth, URLs/websites, running apps, app usage time, daily live data usage, logged-in user, idle/active time, USB events, alerts |
| 6 | Works across the whole island (many branches, remote laptops) | Internet-facing HTTPS gateway (or VPN), offline buffering, auto-reconnect, delta sync |

---

## 2. High-Level Architecture

```
 ┌────────────────────────── Branch / Remote Laptop (×N) ──────────────────────────┐
 │                                                                                  │
 │  ┌──────────────────────┐   Named Pipe (ACL'd)   ┌──────────────────────────┐    │
 │  │ SentinelDesk.Tray    │ ◄────────────────────► │ SentinelDesk.Agent       │    │
 │  │ (per user session)   │                        │ Windows Service          │    │
 │  │ - status icon        │                        │ (LocalSystem)            │    │
 │  │ - notices/consent    │                        │ - Collectors (ETW, WMI)  │    │
 │  └──────────────────────┘                        │ - Policy engine          │    │
 │                                                  │ - Encrypted local queue  │    │
 │  ┌──────────────────────┐   watches/restarts     │ - Uploader / Cmd channel │    │
 │  │ SentinelDesk.Guard   │ ◄────────────────────► └────────────┬─────────────┘    │
 │  │ (watchdog service)   │                                     │                  │
 │  └──────────────────────┘                                     │ mTLS / TLS 1.3   │
 └───────────────────────────────────────────────────────────────┼──────────────────┘
                                                                 │ gRPC (HTTP/2) + 
                                                                 │ WebSocket/SignalR (commands)
                                ┌────────────────────────────────▼──────────────────┐
                                │   Edge: Nginx / YARP Gateway (443, mTLS verify)    │
                                └───────────────┬───────────────────────┬────────────┘
                                                │                       │
                      ┌─────────────────────────▼──────┐   ┌────────────▼──────────────┐
                      │ Ingestion API (gRPC)            │   │ Control API (REST)        │
                      │ - validate, decrypt, enqueue    │   │ - devices, policies, users│
                      └─────────────┬───────────────────┘   │ - SignalR hub (live)      │
                                    │                       └────────────┬──────────────┘
                          ┌─────────▼─────────┐                          │
                          │ Message bus       │                          │
                          │ (RabbitMQ / NATS) │                          │
                          └─────────┬─────────┘                          │
                    ┌───────────────▼───────────────┐                    │
                    │ Workers: aggregation, alerts, │                    │
                    │ URL categorization, reports   │                    │
                    └───────┬───────────────┬───────┘                    │
            ┌───────────────▼──┐   ┌────────▼────────┐   ┌───────────────▼──────┐
            │ PostgreSQL +     │   │ Redis           │   │ Object storage       │
            │ TimescaleDB      │   │ (live state,    │   │ (MinIO: reports,     │
            │ (metadata + TS)  │   │  presence)      │   │  agent packages)     │
            └──────────────────┘   └─────────────────┘   └──────────────────────┘
                                                                 ▲
                                ┌────────────────────────────────┴───────────────┐
                                │  Web Console (Blazor Server, C#) — Admin UI     │
                                │  Dashboards · Devices · Policies · Reports      │
                                └─────────────────────────────────────────────────┘
```

---

## 3. Technology Stack

### 3.1 Endpoint Agent (Windows first)

| Layer | Technology | Why |
|---|---|---|
| Language/runtime | **C# / .NET 10 (LTS)**, published *self-contained, single-file, trimmed* (or Native AOT for the service) | No runtime dependency on the PC, small footprint |
| Core service | `Microsoft.Extensions.Hosting.WindowsServices` (Worker Service) running as **LocalSystem** | Runs before login, all users, survives logoff |
| Tray app | WinForms `NotifyIcon` (lightest) or WPF + `H.NotifyIcon` | Visible status, notices, “About/Help” |
| Service ↔ Tray IPC | Named Pipes with strict ACL (SYSTEM + interactive user read-only) | Tray cannot command the service to stop |
| Network monitoring | **ETW** via `Microsoft.Diagnostics.Tracing.TraceEvent` — `Microsoft-Windows-Kernel-Network` (per-process bytes), `Microsoft-Windows-DNS-Client` (domains visited) | Per-process bandwidth + domain list without a kernel driver |
| Whole-adapter bandwidth | `GetIfTable2` / Performance Counters (`Network Interface`) | Accurate daily totals per NIC (Wi-Fi/LAN/Mobile) |
| App usage | `SetWinEventHook` / `GetForegroundWindow` (in tray process, user session) + process start/stop via ETW `Microsoft-Windows-Kernel-Process` | Active app time + full process list |
| Full URLs (optional) | Managed **browser extension** (Chrome/Edge, force-installed by GPO/Intune) → native messaging → agent | Full URL + page title; DNS alone gives only domains |
| Inventory | WMI/CIM (`Win32_ComputerSystem`, `Win32_Processor`, `Win32_DiskDrive`, `Win32_Product` alternative: registry Uninstall keys) | Hardware & installed software |
| Local buffer | **SQLite + SQLCipher** (AES-256) | Offline storage, encrypted at rest |
| Key protection | DPAPI (machine scope) or **TPM 2.0** via CNG `Microsoft Platform Crypto Provider` | Key never stored in plaintext |
| Transport | **gRPC over HTTP/2** with **mTLS** (client cert per device) + payload Protobuf, gzip/zstd | Efficient, strongly typed, secure |
| Live commands | Long-lived gRPC bidirectional stream (or SignalR client) | Real-time policy push, “refresh now” |
| Installer | **WiX Toolset v5** MSI, code-signed (EV cert), custom actions | GPO/Intune/SCCM silent deploy |
| Auto-update | Agent downloads signed MSI from server, verifies signature + SHA-256, Guard service installs | Fleet-wide upgrades without visiting PCs |

> **macOS/Linux later:** keep collectors behind interfaces (`INetworkCollector`, `IAppUsageCollector`) so a Go or .NET port can be added. Phase 1 = Windows only.

### 3.2 Server / Backend

| Layer | Technology |
|---|---|
| APIs | **ASP.NET Core (.NET 10)** — gRPC ingestion service + REST Control API + **SignalR** hub |
| Gateway | Nginx or **YARP** (TLS termination, mTLS client-cert validation, rate-limit) |
| Message bus | RabbitMQ (simple) or NATS JetStream (high throughput) |
| Database | **PostgreSQL 16+ with TimescaleDB** (hypertables for time-series, continuous aggregates, compression, retention) |
| Cache / live state | Redis (online presence, last-seen, live counters) |
| Background jobs | .NET Worker services + Hangfire/Quartz for scheduled reports |
| PKI | Internal CA (step-ca / smallstep or ADCS) to issue per-device certs |
| Object storage | MinIO / S3-compatible (agent packages, PDF/Excel reports) |
| Auth (admins) | ASP.NET Identity + **OIDC** (Keycloak / Entra ID) + **MFA** mandatory, RBAC |
| Observability | OpenTelemetry → Prometheus + Grafana, Serilog → Seq/Loki |
| Deployment | Docker / Docker Compose (small) → Kubernetes (large), behind a firewall with only 443 exposed |

### 3.3 Web Console (Frontend)

| Layer | Technology |
|---|---|
| Framework | **Blazor Server (ASP.NET Core, C#)**, hosted in SentinelDesk.Server: no separate front-end build |
| UI | Razor components with the compiled Tailwind design tokens (`wwwroot/css/tailwind.css`) |
| Tables | Razor tables, filtered and sorted in C# |
| Charts | SVG drawn in C# (monotone area charts, rank bars, sparklines) |
| Maps | Leaflet through a small JS interop file (`wwwroot/js/console.js`) |
| Live updates | .NET SignalR client to `/hubs/console` |
| Data fetching | Typed `HttpClient` calls to the server's own REST API (same authorization and audit as before) |
| Maps (branches) | Leaflet (Sri Lanka branch map, device online status by location) |

---

## 4. Endpoint Agent — Detailed Design

### 4.1 Processes

| Process | Account | Responsibility |
|---|---|---|
| `SentinelDesk.Agent.exe` (service) | LocalSystem | Collect, encrypt, buffer, upload, apply policy, receive commands |
| `SentinelDesk.Guard.exe` (service) | LocalSystem | Watchdog: restart Agent if killed, verify binaries’ signatures, run updates, report tampering |
| `SentinelDesk.Tray.exe` (per user, auto-start via `HKLM\...\Run`) | Logged-in user | Tray icon (green/amber/red), monitoring notice, foreground-window tracking, idle time (`GetLastInputInfo`); "blocked" notice for blocked programs (`--blocked`); elevated *Reconnect to server* window (`--reconnect`) |

### 4.2 Data Collectors

| Collector | Source | Default interval | Data captured |
|---|---|---|---|
| Heartbeat | Agent | 60 s | Online, agent version, IP (local + public), logged-in user, uptime |
| Hardware inventory | WMI/CIM | On start + daily | CPU, RAM, disks, serial, model, BIOS, MAC addresses, OS build |
| Software inventory | Registry Uninstall keys (HKLM + HKU), Appx | Daily + on change | Name, version, publisher, install date |
| Performance | PerfCounters | 30 s (aggregated to 1 min) | CPU %, RAM %, disk %, disk free |
| Process activity | ETW Kernel-Process | Event-driven | Process start/stop, path, hash (SHA-256), signer, user |
| App usage time | Foreground hook (Tray → Agent) | Event-driven, 1-min rollup | App name, window title (configurable / can be masked), active seconds, idle seconds |
| Bandwidth per process | ETW Kernel-Network (TCP/UDP send/recv) | 1-min rollup | Process, bytes up/down, remote IP:port |
| Bandwidth per adapter | `GetIfTable2` | 1 min | Bytes up/down per NIC, connection type (LAN/Wi-Fi/Mobile/VPN) |
| Websites (domain level) | ETW DNS-Client | Event-driven, dedup per 1 min | Domain, process that queried, timestamp |
| Websites (full URL) | Browser extension (optional) | Event-driven | URL, title, browser, active time on tab |
| Network connections | `GetExtendedTcpTable` | 5 min snapshot | Open connections, listening ports |
| USB / removable media | WMI `Win32_VolumeChangeEvent` / device notifications | Event-driven | Device, serial, mount/unmount, (optional) block |
| Login/session events | Windows Event Log 4624/4634/4800/4801 | Event-driven | Logon, logoff, lock, unlock |
| Security posture | WMI SecurityCenter2, BitLocker, Windows Update | Daily | AV status, firewall, BitLocker, pending updates |

All collectors are **toggleable per policy** from the web console.

### 4.3 Data Flow Inside the Agent

```
Collectors ──► In-memory channel (System.Threading.Channels)
          ──► Aggregator (1-min buckets, dedup)
          ──► Encrypted SQLite queue (SQLCipher)
          ──► Uploader (batch ≤ 500 records or every 60 s, zstd + Protobuf)
          ──► gRPC over mTLS ──► Server ACK ──► delete from queue
```

- **Offline mode:** queue keeps up to N days (policy, default 7 days / 200 MB cap; oldest low-priority data dropped first).
- **Backoff:** exponential reconnect (5 s → 10 min) with jitter so 1,000s of PCs don’t reconnect at once after an outage.
- **Resource budget:** target < 1 % CPU average, < 60 MB RAM, < 5 MB/day upload per PC.

### 4.4 Tray App Behaviour

- Icon states: 🟢 Connected · 🟡 Offline (buffering) · 🔴 Error/Policy violation.
- Menu: *Status*, *Monitoring notice* (what is collected), *Contact support*, *About*, *Reconnect to server (administrator)…*. **No “Exit” / “Stop” option.**
- If tray is killed, Agent restarts it in the user session (`WTSQueryUserToken` + `CreateProcessAsUser`) and logs a tamper event.
- When Windows refuses to start a blocked program, it runs `SentinelDesk.Tray.exe --blocked <program>` instead: the tray
  reports the attempt to the agent and shows *"<program> is blocked by your organisation's policy"* (§4.5).
- *Reconnect to server* relaunches the tray elevated (UAC, administrator credentials) to enter a new enrollment token (§5.5).

### 4.5 Enforcement — Block Policies

> Since 1.3.0 the console's one-click controls, the stronger enforcement layers and the advanced options are specified
> in **§17**. This section describes the base layers that §17 builds on.

The agent enforces three kinds of restriction, each delivered as part of the signed policy (§7.2). Everything is
re-applied every minute; a change made on the device is reverted and reported as `tamper.blockList`.

| Restriction | How it is enforced on Windows | What the user sees |
|---|---|---|
| **Websites** | (1) The browsers' enterprise URL block lists — `URLBlocklist` for Chrome, Edge and Brave, `WebsiteFilter` for Firefox — in `HKLM\SOFTWARE\Policies` (path rules such as `youtube.com/shorts` work here). (2) A managed section of the `hosts` file pointing blocked domains at `0.0.0.0` for every other app. (3) The browsers' own secure DNS (DoH) is turned off while websites are blocked, so the hosts file cannot be bypassed. (4) After every change the agent sends the Windows policy-refresh signal (`RefreshPolicyEx`, as `gpupdate` does) so open browsers reload their list. | The browser's own *"This page is blocked — your organisation doesn't allow you to view this site"* page; other apps cannot connect |
| **Applications** | (1) *Image File Execution Options* `Debugger` redirect to `SentinelDesk.Tray.exe --blocked`, so Windows never starts the program. (2) A process watcher (kernel ETW process-start events + a 3-second sweep) closes running copies and catches wildcard rules (`*torrent*`). | A short notice explaining the program is blocked (text set in the policy) |
| **USB drives** | `USBSTOR` service disabled (`Start = 4`) | Pen drives and external disks don't mount; mice, keyboards and printers still work |

- **Rules:** a domain blocks the site and all its subdomains; built-in *categories* (Social, Messaging, Streaming,
  Gaming, Shopping, News, AI Tools, Cloud Storage) expand to their known domains; whole-site rules for big services
  also block their helper domains (e.g. `tiktok.com` → `tiktokcdn.com`, `ttwstatic.com`; `youtube.com` →
  `googlevideo.com`, `ytimg.com`) so an open tab or installed web app stops too.
- **Safety:** Windows, sign-in and SentinelDesk programs and domains (`explorer.exe`, `svchost.exe`, `microsoft.com`,
  `live.com`, …) are rejected by both the console and the agent. Entries written by other tools in the browser lists
  are kept; previous secure-DNS settings are remembered and restored.
- **Reporting:** blocked launches (`block.app`) and lookups of blocked domains (`block.web`) are sent as events,
  rate-limited per app/domain; the alert rule *Blocked application or website* raises alerts.
- **Removal:** unblocking removes the agent's entries and empty policy folders; uninstalling removes every block
  (IFEO entries, browser lists, hosts section) and restores the browsers' secure-DNS settings.
- **Limits:** application rules match the file name (a renamed copy is not caught); website blocking is by domain, so a
  VPN, proxy or a browser with its own DNS outside the four supported browsers can get around it.

---

## 5. Security & Tamper Protection

### 5.1 Installation

1. MSI is **code-signed**; installer has `InstallScope="perMachine"` → UAC elevation (admin credentials) mandatory.
2. Silent mass deployment:
   ```
   msiexec /i SentinelDesk.msi /qn SERVER_URL="https://monitor.company.lk" ENROLL_TOKEN="XXXX-XXXX" UNINSTALL_KEY_HASH="<hash>"
   ```
3. **Enrollment:** agent generates a key pair (in TPM if available) → sends CSR + one-time enrollment token → server CA returns device certificate → device appears as *Pending Approval* in the console (optional admin approval).

### 5.2 Uninstall Protection (admin credentials required)

| Layer | Mechanism |
|---|---|
| UAC | Uninstall is per-machine → requires Windows admin elevation |
| Uninstall password | MSI custom action prompts for **Uninstall Key** (or a one-time code generated in the web console per device); verified against Argon2id hash. Wrong key → abort + alert |
| Service hardening | Service DACL set via `sc sdset` so only SYSTEM can stop/delete; `SERVICE_CONFIG_FAILURE_ACTIONS` = restart always |
| File protection | Install folder ACL: Users = Read/Execute, Admins = Read/Execute (write only via SYSTEM) |
| Watchdog | Guard ↔ Agent restart each other; both verify Authenticode signatures of each other’s binaries |
| Registry | Config keys under `HKLM\SOFTWARE\SentinelDesk` ACL’d to SYSTEM |
| Tamper alerts | Stop attempts, missed heartbeats, clock changes, binary changes → instant alert in console + email/SMS |
| Remote uninstall | Admin can trigger a signed “uninstall” command from the console (audited) |

> **Honest limitation:** a determined local administrator (or someone booting another OS) can always remove software on a machine they fully control. The design goal is therefore: **(a)** normal users cannot stop or remove it, **(b)** admins need the extra Uninstall Key, and **(c)** any removal is **detected and alerted** (device goes silent → “Agent missing” alert). Removing local admin rights from staff (via AD/Intune) is the single most effective control.

### 5.3 Encryption

| Where | How |
|---|---|
| In transit | TLS 1.3 only, **mutual TLS** (device cert + server cert pinning) |
| Payload (defence in depth) | AES-256-GCM per batch; session key wrapped with server public key (RSA-OAEP-3072 or X25519 + HKDF) |
| At rest on PC | SQLCipher (AES-256) database; key sealed with **TPM 2.0** or DPAPI-Machine |
| Config on PC | Policy file signed by server (ECDSA P-256) — agent rejects unsigned/modified policy |
| At rest on server | PostgreSQL on encrypted volumes (LUKS/BitLocker); sensitive columns (window titles, URLs) optionally encrypted with pgcrypto/app-level keys |
| Secrets | HashiCorp Vault / Azure Key Vault / Docker secrets — never in appsettings |
| Admin login | OIDC + MFA, session timeout, IP allow-list for console |
| Audit | Every admin action (view device, change policy, export, uninstall) written to immutable audit log |

### 5.4 Certificate Lifecycle

- Device certs valid 1 year, auto-renewed at 70 % lifetime.
- Revocation list (CRL/OCSP) checked at gateway — revoke a stolen laptop instantly.

### 5.5 Re-enrollment from the Device (administrator)

When a device can no longer authenticate (e.g. it was removed in the console, or its credentials no longer match the
server), an administrator re-enrolls it without reinstalling:

1. Console → *Agent management* → *New token* (name + target unit only; valid 30 days, up to 500 devices, auto-approved).
2. On the device: tray → *Reconnect to server (administrator)…* → Windows UAC prompt (administrator credentials).
3. The elevated tray sends the token over the agent's named pipe. The agent accepts it **only** from the signed tray
   binary running elevated as a member of Administrators (checked from the client process token); anything else is
   refused and reported as `tamper.reenrollDenied`.
4. The agent enrolls again in-process (new key pair + CSR), max 5 attempts per 10 minutes. A failed attempt leaves the
   current server address, certificate pin and credentials untouched. Success is logged as `agent.reconnected`.
5. A new server address is allowed, but trusting its certificate (TOFU) needs an explicit tick-box.

---

## 6. Agent ↔ Server Protocol (gRPC)

```proto
syntax = "proto3";
package sentineldesk.v1;

service AgentGateway {
  rpc Enroll (EnrollRequest) returns (EnrollResponse);
  rpc Heartbeat (HeartbeatRequest) returns (HeartbeatResponse);
  rpc UploadBatch (TelemetryBatch) returns (UploadAck);
  rpc CommandStream (stream AgentMessage) returns (stream ServerCommand); // live channel
  rpc GetPolicy (PolicyRequest) returns (SignedPolicy);
}

message TelemetryBatch {
  string device_id = 1;
  uint64 batch_seq = 2;          // idempotency
  bytes  encrypted_payload = 3;  // AES-256-GCM(Protobuf(Records))
  bytes  wrapped_key = 4;
  bytes  nonce = 5;
}

message ServerCommand {
  string command_id = 1;
  oneof cmd {
    PolicyUpdated policy_updated = 2;
    RefreshInventory refresh_inventory = 3;
    CollectNow collect_now = 4;
    UpdateAgent update_agent = 5;
    Uninstall uninstall = 6;      // signed
    ShowMessage show_message = 7; // tray notification to user
  }
  bytes signature = 20;
}
```

---

## 7. Web Console — Features

### 7.1 Modules & Pages

| Page | Content |
|---|---|
| **Overview Dashboard** | Total devices, online/offline, by branch (map of Sri Lanka), today’s total data usage, top apps, top websites, active alerts |
| **Devices list** | Filter by branch/department/OS/status/user; columns: name, user, IP, last seen, today’s data, agent version |
| **Device detail** (deep view) | Tabs ↓ |
| ├ Summary | Live status, current user, uptime, CPU/RAM/disk live gauges |
| ├ Hardware | Full inventory, serials, warranty fields |
| ├ Software | Installed apps, versions, change history |
| ├ Applications | Running now (live) + usage timeline (active vs idle), top apps by time |
| ├ Bandwidth | Live upload/download graph, per-app bandwidth, per-adapter, daily/weekly/monthly totals |
| ├ Websites | Domains/URLs visited, time spent, category (Social, Streaming, Work…), blocked attempts |
| ├ Sessions | Logon/logoff/lock/unlock timeline, working hours vs active hours |
| ├ USB | Device insertions with serials |
| ├ Security | AV, firewall, BitLocker, updates |
| └ Timeline | Unified chronological activity feed |
| **Live view** | Real-time grid of online devices with current app + current bandwidth (SignalR) |
| **Policies** | Three kinds of **block policy** — *Block websites* (addresses, page paths, categories), *Block applications* (program names, wildcards, user message), *Block USB drives* — each with its own page and applied to all computers, a branch/department or single devices. They stack: a device gets every block policy on itself, its department, branch, region or the organization. Monitoring settings (collectors, intervals, window-title masking, working hours, privacy exclusions) come from the hidden **Default** policy and are not edited in the console since 1.2.1 |
| **Groups & Org structure** | Head office → Regions/Provinces → Branches → Departments |
| **Alerts & Rules** | e.g., “Data usage > 2 GB/day”, “Agent offline > 30 min”, “Unapproved software installed”, “Tamper attempt”, “Blocked category visited”, “Blocked application or website” → email/SMS/Teams |
| **Reports** | Daily/weekly/monthly data usage, app usage, website usage, inventory, compliance; export PDF/Excel; scheduled email |
| **Agent management** | Versions, staged rollout (10 % → 50 % → 100 %; a release is only offered to devices on a lower version), enrollment tokens (name + target unit; 30 days, 500 devices, auto-approved), uninstall keys, office installer |
| **Admin users & RBAC** | Roles: Super Admin, Branch IT Admin (only their branch), Auditor (read-only), HR Viewer (reports only, no URLs) |
| **Audit log** | Who viewed/changed what and when |

### 7.2 Policy JSON (example)

```json
{
  "policyId": "pol-branch-default",
  "version": 12,
  "collectors": {
    "heartbeatSec": 60,
    "performanceSec": 30,
    "appUsage": { "enabled": true, "captureWindowTitles": "masked" },
    "bandwidth": { "perProcess": true, "perAdapter": true, "rollupSec": 60 },
    "web": { "dnsDomains": true, "fullUrlViaExtension": false },
    "usb": { "log": true, "block": false },
    "inventory": { "hardwareDaily": true, "softwareDaily": true }
  },
  "workingHours": { "tz": "Asia/Colombo", "days": "Mon-Fri", "start": "08:30", "end": "17:00", "collectOutsideHours": false },
  "upload": { "batchMax": 500, "intervalSec": 60, "offlineMaxMB": 200 },
  "privacy": { "excludeApps": ["KeePass.exe"], "excludeDomains": ["*.bank.lk"] },
  "block": {
    "urls": ["tiktok.com", "youtube.com/shorts"],
    "categories": ["Social"],
    "apps": ["steam.exe", "*torrent*.exe"],
    "notifyUser": true,
    "message": "This application is blocked by your organisation's policy."
  },
  "signature": "MEUCIQ..."
}
```

**How the delivered policy is built (since 1.2.1).** Each stored policy has a *kind*: `monitoring` (the Default
policy: everything above except `block`) or a restriction — `websites` (keeps `block.urls` + `block.categories`),
`apps` (keeps `block.apps`, `notifyUser`, `message`) or `usb` (sets `collectors.usb.block`). For each device the server
takes the Default policy and merges in **every** restriction policy assigned to the device or any org unit above it
(lists are unioned; USB block is on if any USB policy applies), then signs the result. The effective version is a hash
of the Default policy and all applied restriction policies (id + version), so any change triggers a re-download.

---

## 8. Database Design (PostgreSQL + TimescaleDB)

### 8.1 Relational (metadata)

```sql
organizations(id, name)
org_units(id, org_id, parent_id, name, type)          -- region / branch / department
devices(id, org_unit_id, hostname, serial, os, os_build, agent_version,
        cert_thumbprint, status, enrolled_at, last_seen_at, last_ip, public_ip)
device_users(id, device_id, username, domain, last_logon_at)
hardware_inventory(device_id, collected_at, json_data)
software_inventory(device_id, name, version, publisher, installed_at, removed_at)
policies(id, name, kind, version, json, signed_blob, created_by, created_at)
                                                      -- kind: monitoring | websites | apps | usb
policy_assignments(policy_id, target_type, target_id, priority)
                                                      -- one monitoring policy per target; any number of restrictions
alert_rules(id, name, condition_json, severity, channels)
alerts(id, rule_id, device_id, triggered_at, status, details)
admin_users(id, email, role, org_unit_scope)
audit_log(id, admin_id, action, target, at, ip, details)
enrollment_tokens(id, token_hash, org_unit_id, expires_at, uses_left)
```

### 8.2 Time-series (hypertables)

```sql
CREATE TABLE ts_perf (time timestamptz, device_id uuid, cpu real, ram real, disk real);
CREATE TABLE ts_net_adapter (time timestamptz, device_id uuid, adapter text, type text,
                             bytes_in bigint, bytes_out bigint);
CREATE TABLE ts_net_process (time timestamptz, device_id uuid, process text,
                             bytes_in bigint, bytes_out bigint);
CREATE TABLE ts_app_usage (time timestamptz, device_id uuid, username text, app text,
                           window_title text, active_sec int, idle_sec int);
CREATE TABLE ts_web (time timestamptz, device_id uuid, username text, domain text,
                     url text, browser text, category text, duration_sec int);
CREATE TABLE ts_events (time timestamptz, device_id uuid, type text, data jsonb);

SELECT create_hypertable('ts_net_adapter', 'time', chunk_time_interval => interval '1 day');
-- repeat for others

-- Daily data usage per device (continuous aggregate)
CREATE MATERIALIZED VIEW daily_data_usage WITH (timescaledb.continuous) AS
SELECT time_bucket('1 day', time) AS day, device_id,
       sum(bytes_in) AS total_in, sum(bytes_out) AS total_out
FROM ts_net_adapter GROUP BY day, device_id;

-- Compression & retention
ALTER TABLE ts_net_process SET (timescaledb.compress, timescaledb.compress_segmentby = 'device_id');
SELECT add_compression_policy('ts_net_process', interval '7 days');
SELECT add_retention_policy('ts_net_process', interval '180 days');
```

### 8.3 Capacity Estimate

| Devices | Raw telemetry/day | After compression (≈10×) | 6-month storage |
|---|---|---|---|
| 500 | ~2.5 GB | ~250 MB | ~45 GB |
| 2,000 | ~10 GB | ~1 GB | ~180 GB |
| 10,000 | ~50 GB | ~5 GB | ~0.9 TB |

---

## 9. Island-wide Network Topology

```
   Branch (Jaffna) ─┐
   Branch (Galle)  ─┤        Internet / MPLS / Site-to-site VPN
   Branch (Kandy)  ─┼──────────────────────────────────────────►  HQ / Data Centre (Colombo)
   Remote laptops  ─┘   HTTPS 443 only (mTLS), outbound from agents     │
                                                                      ├─ DMZ: Gateway (Nginx/YARP) + WAF
                                                                      └─ Internal: APIs, DB, Bus, Redis
```

- Agents only make **outbound** connections → no inbound ports on branch PCs.
- Laptops off-site still report via internet (or only via VPN — policy choice).
- Optional DR site / cloud replica (PostgreSQL streaming replication).

### 9.1 Server Sizing (starting point, ~2,000 devices)

| Role | Spec |
|---|---|
| Gateway + APIs (×2, load-balanced) | 4 vCPU, 8 GB RAM |
| Workers (×2) | 4 vCPU, 8 GB RAM |
| PostgreSQL + TimescaleDB (primary + replica) | 8 vCPU, 32 GB RAM, NVMe SSD 1 TB |
| Redis + RabbitMQ | 2 vCPU, 4 GB RAM each |
| Web console | 2 vCPU, 4 GB RAM |

---

## 10. Solution / Repository Structure

```
SentinelDesk/
├─ agent/
│  ├─ SentinelDesk.Agent/            # Windows Service (Worker)
│  │  ├─ Collectors/                 # Etw/, Wmi/, Perf/, Usb/, Session/
│  │  ├─ Pipeline/                   # Aggregator, Queue (SQLCipher), Uploader
│  │  ├─ Policy/                     # PolicyStore, SignatureVerifier
│  │  ├─ Security/                   # CertStore, TpmKeyProvider, PayloadCrypto
│  │  ├─ Ipc/                        # NamedPipeServer
│  │  └─ Commands/                   # CommandStream handlers
│  ├─ SentinelDesk.Guard/            # Watchdog + updater service
│  ├─ SentinelDesk.Tray/             # NotifyIcon app, foreground tracker
│  ├─ SentinelDesk.Shared/           # Protobuf contracts, models
│  ├─ SentinelDesk.BrowserExt/       # Chrome/Edge extension + native host
│  └─ SentinelDesk.Installer/        # WiX v5 project, custom actions (uninstall key)
├─ server/
│  ├─ SentinelDesk.Gateway/          # YARP + mTLS validation
│  ├─ SentinelDesk.Ingestion/        # gRPC AgentGateway
│  ├─ SentinelDesk.Api/              # REST Control API + SignalR hub
│  ├─ SentinelDesk.Workers/          # aggregation, alerts, reports, URL categorizer
│  ├─ SentinelDesk.Domain/           # entities, rules
│  ├─ SentinelDesk.Infrastructure/   # EF Core, Timescale, Redis, RabbitMQ, CA client
│  └─ SentinelDesk.Pki/              # step-ca integration
├─ web/
│  └─ (console is in SentinelDesk.Server/Web: Blazor Server components)
├─ deploy/
│  ├─ docker-compose.yml
│  ├─ k8s/                           # Helm charts
│  └─ nginx/
├─ proto/                            # *.proto shared contracts
└─ docs/
```

---

## 11. Key Implementation Snippets

### 11.1 Per-process bandwidth via ETW (agent)

```csharp
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

using var session = new TraceEventSession("SentinelDeskNet");
session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

session.Source.Kernel.TcpIpRecv += e => _agg.AddBytes(e.ProcessID, rx: e.size, tx: 0);
session.Source.Kernel.TcpIpSend += e => _agg.AddBytes(e.ProcessID, rx: 0, tx: e.size);
session.Source.Kernel.UdpIpRecv += e => _agg.AddBytes(e.ProcessID, rx: e.size, tx: 0);
session.Source.Kernel.UdpIpSend += e => _agg.AddBytes(e.ProcessID, rx: 0, tx: e.size);

session.Source.Process(); // blocking — run on dedicated thread
```

### 11.2 DNS domains visited via ETW

```csharp
var dns = new TraceEventSession("SentinelDeskDns");
dns.EnableProvider("Microsoft-Windows-DNS-Client");
dns.Source.Dynamic.All += e =>
{
    if ((int)e.ID == 3008) // query completed
    {
        var name = (string)e.PayloadByName("QueryName");
        _web.RecordDomain(e.ProcessID, name, e.TimeStamp);
    }
};
dns.Source.Process();
```

### 11.3 Foreground app tracking (tray, user session)

```csharp
[DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);

// every 1 s: resolve foreground PID → process name; if idle > policy.idleSec mark idle
// send 1-min rollups to Agent via named pipe
```

### 11.4 Service hardening (custom action, after install)

```bat
sc sdset SentinelDeskAgent "D:(A;;CCLCSWRPLOCRRC;;;AU)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)"
sc failure SentinelDeskAgent reset= 0 actions= restart/5000/restart/5000/restart/5000
```
(Authenticated Users can only query status; only SYSTEM can stop/configure.)

---

## 12. Legal, Privacy & Ethics (Sri Lanka)

Monitoring employees is legal for **company-owned devices** when done transparently and proportionately. Build these in from day one:

1. **Personal Data Protection Act, No. 9 of 2022 (Sri Lanka)** — treat collected data as personal data: lawful basis, purpose limitation, data minimisation, retention limits, security, and a named data-protection contact.
2. **Written IT / Acceptable-Use policy** signed by employees stating what is monitored.
3. **Visible tray icon + “Monitoring Notice”** screen (already in design) — no hidden/stealth mode.
4. **Privacy controls in policy:** mask window titles, exclude banking/health domains, domain-only logging by default (full URL only where justified), no keystroke logging, no screenshots unless a separate, approved policy.
5. **RBAC + audit log** so only authorised people view individual activity.
6. **Retention:** e.g., raw 90–180 days, aggregates 1–2 years, then auto-delete.
7. Company devices only — do not install on personal/BYOD devices.

> Get the final policy reviewed by your legal/HR team before rollout.

---

## 13. Development Roadmap

| Phase | Duration | Deliverables |
|---|---|---|
| **0. Foundations** | 2 wks | Repo, CI/CD (GitHub Actions/Azure DevOps), code-signing cert, PKI (step-ca), proto contracts |
| **1. Agent MVP** | 4 wks | Service + Tray + heartbeat + hardware/software inventory + encrypted queue + mTLS upload |
| **2. Server MVP** | 4 wks | Ingestion gRPC, PostgreSQL/Timescale, Control API, device list & device summary page |
| **3. Network & Apps** | 4 wks | ETW bandwidth (process + adapter), DNS domains, app usage, live SignalR view, daily usage charts |
| **4. Policies & Config** | 3 wks | Policy editor, signed policy push, org/branch hierarchy, RBAC |
| **5. Protection** | 3 wks | WiX installer, uninstall key, service ACLs, Guard watchdog, tamper alerts, auto-update with staged rollout |
| **6. Alerts & Reports** | 3 wks | Rule engine, email/SMS, scheduled PDF/Excel reports, audit log |
| **7. Optional extensions** | 3 wks | Browser extension (full URL), USB control, URL categorization, branch map |
| **8. Hardening & Pilot** | 3 wks | Pen-test, load test (simulate 10k agents), pilot 1 branch → staged island-wide rollout |

**Total:** ~6–7 months for a 3–4 person team (1 agent/Windows dev, 1 backend, 1 frontend, 0.5 DevOps/QA).

**Delivered in 1.2.1 (from phases 4, 5 and 7):** block policies for websites (browser block lists + hosts file +
secure-DNS control + categories and helper domains), applications (IFEO + process watcher) and USB drives, with
tamper repair, attempt logging and an alert rule; stacking policy model; tray re-enrollment by an administrator.
Still open from phase 7: browser extension for full URLs.

**Planned for 1.3.0 (+7 weeks, see §17.11):** one-click block/unblock with device confirmation, local DNS filter + WFP,
App Control for Business rules, Removable Storage Access policies with live eject, advanced options.

---

## 14. Testing Strategy

- **Agent:** unit tests per collector (mock ETW sources), resource-usage benchmarks, offline/online soak tests (72 h), tamper tests (kill process, stop service as user/admin, delete files).
- **Load:** agent simulator (Go/.NET) generating 10,000 virtual devices against staging.
- **Security:** external pen-test of gateway & console, cert revocation drills, OWASP ASVS L2 checklist for the web console.
- **Deployment:** GPO/Intune silent install on clean Windows 10/11 images (x64 + ARM64).

---

## 15. Build-vs-Buy Note

Commercial products already do much of this (e.g., ManageEngine Endpoint Central, Microsoft Intune + Defender, Teramind, ActivTrak). Building your own makes sense when you need: full data ownership on-premises in Sri Lanka, custom reports/integration with your existing systems, and no per-device licence cost. Compare total cost (team × 6–7 months + maintenance) against licences before committing.

---

## 16. Next Steps

1. Confirm device count, OS mix (Windows 10/11 only?), and whether branches connect via VPN/MPLS or open internet.
2. Decide URL logging level (domain only vs full URL) with HR/legal.
3. Approve stack (.NET 10 + PostgreSQL/TimescaleDB + Next.js).
4. Start Phase 0 & 1 — agent MVP with heartbeat + inventory on 5 pilot PCs.

---

## 17. One-Click Block / Unblock — Websites, Applications, USB (v1.3.0)

### 17.1 Goal and "Done" definition

An administrator blocks or unblocks a **website**, an **application** or **USB storage** with **one click** — for one
device, a department, a branch or the whole organization — and the console shows, per device, whether it **really
took effect**.

| Requirement | Target (online device) |
|---|---|
| Click → change active on device | **≤ 5 s** (p95), ≤ 15 s worst case |
| Click → console shows *Applied* / *Failed* | ≤ 10 s |
| Unblock fully restores previous state | 100 % (no leftover registry, hosts, IFEO, WDAC, device-disabled state) |
| Offline device | Shown as *Queued*; applied within 60 s of reconnect; never "silently not applied" |
| Website block covers | Chrome, Edge, Brave, Firefox, other apps (Teams web views, curl, etc.), already-open tabs |
| App block covers | New launches, already running copies, renamed copies (hash/publisher rules), 32- and 64-bit |
| USB block covers | Pen drives, external HDD/SSD (incl. USB 3 **UASP**), SD readers, phones (MTP), CD/DVD — mice, keyboards, printers, headsets keep working; already-plugged drives are ejected |

A click is only "done" when the agent has **applied and verified** the change and reported it back (§17.4).

### 17.2 Console UX — what "one click" looks like

**A. Device detail → Websites / Applications tabs:** every row gets a **Block** button; blocked rows show **Unblock**.

```
 Websites (today)                                   Applications (running / today)
 ┌──────────────────────┬────────┬────────────┐     ┌────────────────┬────────┬──────────────┐
 │ youtube.com    1h 12m│Social  │ [ Block ▾ ]│     │ steam.exe  42m │ Gaming │ [ Block ▾ ]  │
 │ facebook.com   ⛔    │Social  │ [Unblock]  │     │ utorrent.exe ⛔│ P2P    │ [Unblock]    │
 └──────────────────────┴────────┴────────────┘     └────────────────┴────────┴──────────────┘
   Block ▾ = This device · This department · This branch · Whole organization · Options…
```

**B. Device header — quick toggles (one click each):**

```
 PC-GALLE-014  🟢 Online   User: nimal.p
 [ 🌐 Websites: Policy ▾ ]   [ 🧩 Apps: Policy ▾ ]   [ 🔌 USB storage:  ● Blocked  ○ Allowed ]
   status chip →  ✅ Applied 3 s ago   |   ⏳ Pending   |   ❌ Failed: "UASPStor locked by device" [Retry]
```

**C. Group / Organization page — bulk toggle:** same toggles for a whole org unit, with a live progress bar
`Applied 412 / 430 · Pending 15 (offline) · Failed 3 [view]`.

**D. "Options…" dialog (advanced, optional — the default is a plain one click):**
duration (permanent / 15 min / 1 h / until end of day / until date), schedule (working hours only), match type
(domain + subdomains / exact path / category; file name / hash / publisher), mode (*Block* / *Warn* / *Log only*),
users (all / AD group / specific user), note shown to the user, reason (stored in audit log).

**Rules for the buttons**
- Optimistic UI: the chip turns ⏳ *Pending* instantly; it only turns ✅ when the device's `EnforcementReport` arrives.
- **Undo** toast for 10 s after each click (sends the opposite action).
- Inherited blocks (from branch/organization) show a 🔒 icon and "Blocked by *Galle Branch — Social*"; *Unblock* on a
  single device then creates an **exception** (§17.6) instead of editing the parent policy.
- Protected items (Windows, sign-in, SentinelDesk — §4.5 *Safety*) are greyed out with a tooltip.

### 17.3 How it maps onto the stacking policy model

No new concept for the agent: each org unit and device gets three auto-managed **"Quick" restriction policies**
(`kind = websites | apps | usb`, `managed = true`, hidden from the policy list but visible in the audit log). A click
adds/removes an entry in the Quick policy of the chosen target, bumps its version, and the existing merge (§7.2)
rebuilds the effective policy. New fields per entry:

```json
"block": {
  "urls": [
    { "id": "r-91", "value": "youtube.com", "match": "domain", "mode": "block",
      "users": ["*"], "schedule": "working-hours", "expiresAt": "2026-11-23T17:00:00+05:30" }
  ],
  "apps": [
    { "id": "r-92", "value": "utorrent.exe", "match": "name|hash|publisher",
      "sha256": ["9f2c…"], "publisher": "CN=BitTorrent Inc", "mode": "block" }
  ],
  "usb": { "mode": "block | readOnly | allow", "allowSerials": ["0701ABCD1234"], "allowVidPid": ["0781:5581"],
           "classes": ["massStorage", "uasp", "mtp", "sd", "cdrom"] },
  "exceptions": [ { "ruleId": "r-91", "target": "device:PC-GALLE-014", "until": null } ]
}
```

Merge rules: lists are unioned; **exceptions beat inherited blocks**; for USB the strictest mode wins
(`block` > `readOnly` > `allow`) unless an exception applies; expired entries are removed by the server **and** ignored by
the agent (agent clock checked against server time on each heartbeat, so changing the PC clock does not extend/shorten).

### 17.4 End-to-end flow (the part that makes it "properly working")

```
 Console click
   │  POST /api/v1/quick-blocks   (idempotency key = click id)
   ▼
 Control API ── validate (protected list, RBAC scope) ── update Quick policy ── audit_log
   │  recompute effective policy for affected devices (worker, batched)  ── sign (ECDSA P-256)
   ▼
 Redis presence: online?  ──yes──► CommandStream: PolicyUpdated{effective_version}
   │                       no ───► enforcement_state = QUEUED
   ▼
 Agent: GetPolicy → verify signature → EnforcementEngine.Reconcile(desired)
   │     per enforcer: Plan → Apply (journaled) → Verify → (Rollback on error)
   ▼
 Agent → EnforcementReport{version, results[rule, status, error, verifiedAt]}
   ▼
 Server: enforcement_state upsert → SignalR "enforcement.updated" → chip turns ✅ / ❌
```

Reliability rules:
1. **Desired-state, not commands.** The agent always converges to the signed effective policy; a lost message cannot
   leave a device half-blocked. The 60-second reconcile loop (§4.5) also repairs tampering.
2. **Ack with verification.** Each enforcer proves the result (e.g. the local resolver answers `0.0.0.0` for the
   domain; the IFEO + App Control rule is present and the test spawn is refused; `USBSTOR`/`UASPStor` are disabled and no
   storage volume is mounted). Status is `applied`, `applied_partial` (e.g. Firefox needs restart) or `failed` + reason.
3. **Journal + rollback.** Every change is written to an encrypted journal *before* applying (old value, new value).
   Unblock and uninstall replay the journal backwards, so the original state is restored exactly.
4. **Timeout handling.** No report within 30 s for an online device → server re-sends once, then marks
   `failed: no-response` and raises an alert.
5. **Idempotent and ordered.** Policy versions are monotonic; an older version arriving late is ignored.

### 17.5 Enforcement layers (v1.3.0)

#### 17.5.1 Websites

| Layer | Technology | Block | Unblock |
|---|---|---|---|
| 1. Browser policy | `URLBlocklist` (Chrome/Edge/Brave), `WebsiteFilter` (Firefox) under `HKLM\SOFTWARE\Policies`, then `RefreshPolicyEx(TRUE, RP_FORCE)` | Chromium browsers pick up registry policy changes within seconds; Firefox reads policy at start → report `applied_partial` + optional tray prompt "restart Firefox" | Remove only our entries; refresh |
| 2. **Local DNS filter (new)** | Agent runs a DNS resolver on `127.0.0.1:53` (.NET, `System.Net.Sockets` + DNS parser such as `DnsClient`/`ARSoft.Tools.Net`), sets every adapter's DNS to it, forwards allowed queries to the original servers | Blocked domain → `0.0.0.0` / `::`; replaces the hosts file as primary (hosts kept as fallback) | Domain removed from in-memory list instantly |
| 3. **WFP firewall (new)** | Windows Filtering Platform user-mode filters (`FwpmFilterAdd0`, dynamic session owned by the agent's provider) | Block outbound UDP/TCP 53 and 853 to anything except the local resolver; block known DoH endpoints (8.8.8.8, 1.1.1.1, dns.google, … list in policy); block IPs resolved for blocked domains (refreshed from DNS answers) | Filters removed by filter key |
| 4. Kill open sessions | `SetTcpEntry(MIB_TCP_STATE_DELETE_TCB)` for connections to blocked IPs + `DnsFlushResolverCache` | Already-open tabs/apps lose the connection immediately | — |
| 5. Browser extension (optional, recommended) | Force-installed Chrome/Edge extension (`declarativeNetRequest` dynamic rules) via native messaging | Rule added and matching open tabs reloaded → block page appears in open tabs within ~1 s | Rule removed |

Notes: Chromium's own DoH is disabled by policy (`DnsOverHttpsMode = off`) while any website block applies; with the
local resolver this is a second guarantee. Path rules (`youtube.com/shorts`) are only enforceable in layers 1 and 5
(DNS sees domains only) — the console says so next to the rule.

#### 17.5.2 Applications

| Layer | Technology | Catches |
|---|---|---|
| 1. IFEO redirect | `Image File Execution Options\<name>\Debugger` → `SentinelDesk.Tray.exe --blocked`, written to **both** the 64-bit key and `WOW6432Node` | New launches by file name, instantly, no reboot |
| 2. **App Control for Business (WDAC) supplemental policy (new)** | Policy built server-side (ConfigCI cmdlets / WDAC Wizard XML → binary), deployed with `CiTool.exe --update-policy` (Windows 11 22H2+, no reboot); on Windows 10 copy to `CodeIntegrity\CiPolicies\Active` + refresh tool | **Renamed copies** (hash and publisher/`OriginalFileName` rules), launches by any method |
| 3. Process watcher | ETW `Microsoft-Windows-Kernel-Process` start events + 3 s sweep; match name, wildcard, SHA-256, signer, `OriginalFilename` from version resource | Already-running copies (terminated with `TerminateProcess` after a 5 s notice), wildcards, portable apps |
| 4. Store / UWP apps | AppX package family name rules (WDAC/AppLocker packaged-app rules) | Microsoft Store apps |

Unblock: remove IFEO keys, re-deploy WDAC supplemental policy without the rule (or remove it when empty), stop watcher
rule. Nothing remains to undo for already-terminated processes. Option *Warn* mode shows the notice but lets the app run
(and logs `block.warned`).

> One-click from the Applications tab sends the program's **hash, signer and original file name** collected by the
> process collector, so the rule is name **+** hash **+** publisher by default — renaming the .exe no longer helps.

#### 17.5.3 USB storage

| Layer | Technology | Effect |
|---|---|---|
| 1. Removable Storage Access policy | `HKLM\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices` — `Deny_All` (block) or `{53f5630d-…}\Deny_Write` (read-only) for removable disks, CD/DVD, WPD (phones/MTP), tape | Windows refuses access to storage classes |
| 2. Storage drivers | `USBSTOR` **and** `UASPStor` `Start = 4` (UASP USB 3 drives do not use `USBSTOR`) | New drives don't get a driver |
| 3. **Live eject (new)** | SetupAPI / CfgMgr32: enumerate `GUID_DEVINTERFACE_DISK` with removable/USB bus, `CM_Request_Device_Eject`, fall back to `SetupDiChangeState(DICS_DISABLE)` | Drives already plugged in disappear within seconds |
| 4. Arrival guard | `RegisterDeviceNotification` / WMI `Win32_DeviceChangeEvent` in the service; non-whitelisted storage device → disable + `usb.blocked` event + tray notice | Whitelist by **serial / VID:PID**, rejects anything that slips through |

Unblock: remove deny values, restore `Start = 3` for both drivers, re-enable devices we disabled (from the journal),
`CM_Reenumerate_DevNode` on the root → drives mount again **without unplugging**. HID, printers, audio and
Bluetooth input are never touched.

### 17.6 Advanced options

| Option | Websites | Apps | USB | How |
|---|:-:|:-:|:-:|---|
| Temporary block / temporary unblock (15 min, 1 h, end of day, date) | ✅ | ✅ | ✅ | `expiresAt` per entry/exception; agent enforces with server-time offset |
| Schedules (working hours, lunch break open, weekends) | ✅ | ✅ | ✅ | `schedule` = named schedule from org settings (tz `Asia/Colombo`) |
| Exceptions (allow one device/user under a parent block) | ✅ | ✅ | ✅ | `exceptions[]`, exceptions beat inherited blocks |
| Allow-list mode ("only these sites/apps") | ✅ | ✅ | — | Websites: resolver default-deny + allow list; Apps: WDAC allow policy (advanced, pilot first) |
| Per user / AD group | ✅ | ✅ | ✅ | Agent maps active session SID → groups (`users` field); applied at logon/switch |
| Modes: Block / Warn / Log-only | ✅ | ✅ | ✅ (read-only) | Try a rule in *Log-only* to see impact before blocking |
| Categories (Social, Streaming, AI Tools …) incl. helper domains | ✅ | ✅ (Gaming, P2P, Remote-access tools) | — | Server-side category lists, versioned, pushed in policy |
| USB whitelist (company-issued drives) | — | — | ✅ | Serial / VID:PID; one click "Allow this drive" from the USB tab |
| USB read-only | — | — | ✅ | `Deny_Write` only |
| Unblock request from user | ✅ | ✅ | ✅ | Tray notice has *Request access* → reason → console inbox → **Approve 1 h / Approve / Reject** (one click) |
| Offline one-time code | ✅ | ✅ | ✅ | Console generates a code (HMAC of device id + rule + window, valid 1 h); admin types it in elevated tray |
| Emergency **Unblock all** | ✅ | ✅ | ✅ | Super Admin only, MFA re-prompt, scope selectable, audited |
| Bulk import | ✅ | ✅ | ✅ | CSV paste of domains / exe names / serials |
| Effect preview | ✅ | ✅ | ✅ | "This will affect 430 devices, 312 users; 27 visited this site today" before confirming bulk actions |

### 17.7 Protocol additions (`proto/sentineldesk/v1`)

```proto
// Agent → server, sent on CommandStream after every reconcile that changed anything (and every 15 min as summary)
message EnforcementReport {
  string device_id = 1;
  string effective_version = 2;
  repeated RuleResult results = 3;
  google.protobuf.Timestamp reported_at = 4;
}
message RuleResult {
  string rule_id = 1;                 // r-91 or "usb"
  enum Kind { WEBSITE = 0; APP = 1; USB = 2; }
  Kind kind = 2;
  enum Status { APPLIED = 0; APPLIED_PARTIAL = 1; REMOVED = 2; FAILED = 3; SKIPPED_SCHEDULE = 4; }
  Status status = 3;
  string detail = 4;                  // e.g. "firefox restart required", "UASPStor access denied"
  repeated string layers_ok = 5;      // ["browser","dns","wfp"]
}

// Server → agent (added to ServerCommand.oneof)
message ReconcileNow { string reason = 1; }          // "Retry" button
message UnlockCode   { string code = 1; }            // pushed when approved request
```

### 17.8 REST API

| Method & path | Purpose |
|---|---|
| `POST /api/v1/quick-blocks` | `{ kind: "website"|"app"|"usb", action: "block"|"unblock"|"allow", value, target: {type, id}, options? }` → `202 { operationId }` |
| `DELETE /api/v1/quick-blocks/{ruleId}` | Unblock (same as action `unblock`) |
| `POST /api/v1/quick-blocks/{ruleId}/exceptions` | Exception for a device/user |
| `GET  /api/v1/operations/{operationId}` | Progress: applied / pending / failed counts |
| `GET  /api/v1/devices/{id}/enforcement` | Current per-rule state for the device chips |
| `POST /api/v1/devices/{id}/reconcile` | Retry |
| `GET/POST /api/v1/access-requests` · `POST …/{id}/approve` | Unblock request workflow |
| `POST /api/v1/emergency/unblock-all` | Kill switch (Super Admin + MFA step-up) |
| SignalR `enforcement.updated`, `operation.progress`, `accessRequest.created` | Live chips and progress bars |

All endpoints: RBAC-scoped (Branch IT Admin only inside their branch), idempotency key header, audited.

### 17.9 Database additions

```sql
-- policies: add managed flag for the auto "Quick" policies
ALTER TABLE policies ADD COLUMN managed boolean NOT NULL DEFAULT false;

block_rules(id, policy_id, kind, value, match_type, sha256[], publisher, mode,
            users jsonb, schedule_id, expires_at, created_by, created_at)
block_exceptions(id, rule_id, target_type, target_id, until, created_by, reason)
usb_allow_list(id, org_unit_id, serial, vid_pid, label, created_by)
enforcement_state(device_id, rule_id, desired, status, detail, layers_ok text[],
                  effective_version, updated_at, PRIMARY KEY (device_id, rule_id))
operations(id, admin_id, kind, action, target, total, applied, failed, pending, created_at)
access_requests(id, device_id, username, kind, value, reason, status, decided_by, decided_at, grant_until)
schedules(id, org_id, name, tz, json)
```

`enforcement_state` is also mirrored in Redis for the live chips; Postgres is the source of truth for reports
("which devices still have Facebook unblocked?").

### 17.10 Technologies (added in 1.3.0)

| Area | Technology | Use |
|---|---|---|
| Agent – enforcement engine | .NET 10, `IEnforcer` interface (`Plan / Apply / Verify / Rollback`), journal in SQLCipher | Desired-state reconciler for the three kinds |
| Agent – DNS filter | .NET UDP/TCP listener + `DnsClient` (or `ARSoft.Tools.Net`) for parsing/forwarding | Local resolver on 127.0.0.1 |
| Agent – firewall | WFP via P/Invoke (`fwpuclnt.dll`: `FwpmEngineOpen0`, `FwpmFilterAdd0`, own provider + sublayer) | DNS/DoH lockdown, IP blocks |
| Agent – connections | `iphlpapi.dll` (`GetExtendedTcpTable`, `SetTcpEntry`), `dnsapi.dll` (`DnsFlushResolverCache`) | Cut open sessions |
| Agent – apps | IFEO registry, **App Control for Business** (`CiTool.exe`), ETW `TraceEvent`, `WinVerifyTrust` for signer | Name + hash + publisher blocking |
| Server – WDAC builder | PowerShell `ConfigCI` module in a Windows worker container, or prebuilt XML templates + `ConvertFrom-CIPolicy` | Build signed supplemental policies |
| Agent – USB | `CfgMgr32` (`CM_Request_Device_Eject`, `CM_Reenumerate_DevNode`), `SetupAPI` (`SetupDiChangeState`), `RegisterDeviceNotification` | Live eject, re-enable, arrival guard |
| Browser | Chrome/Edge MV3 extension (`declarativeNetRequest`, `tabs`), native messaging host | Instant block in open tabs |
| Console | Next.js toggle/chip components (shadcn `Switch`, `Badge`, `Toast` with Undo), TanStack Query optimistic updates, SignalR | One-click UX with live status |
| Backend | ASP.NET Core minimal APIs, Hangfire jobs for expiries/schedules, Redis pub/sub, existing ECDSA signer | Quick policy updates, expiry, push |
| Testing | Windows VM pool (Hyper-V/Azure) with Pester + xUnit, Playwright for console and browser block page checks | Automated test matrix §17.12 |

### 17.11 Technical plan (≈ 7 weeks, same team)

| Week | Work | Exit criteria |
|---|---|---|
| 1 | Enforcement engine skeleton: `IEnforcer`, journal, reconcile loop, `EnforcementReport` proto, server `enforcement_state` + SignalR | Dummy enforcer reports *Applied* to console in < 10 s |
| 2 | Console one-click: Quick policies, `/quick-blocks` API, buttons on Websites/Applications/USB tabs, header toggles, chips, Undo, protected-list checks | Click → signed policy → device in < 5 s on LAN |
| 3 | Websites: local DNS resolver, WFP DNS/DoH lockdown, TCP kill, verification, Firefox partial status | Block/unblock test matrix (websites) green on Win10/11 |
| 4 | Applications: IFEO both views, hash/publisher capture, WDAC supplemental builder + `CiTool` deploy, watcher with `OriginalFilename`, Store apps | Renamed-copy and running-copy tests green |
| 5 | USB: storage access policy, USBSTOR + UASPStor, live eject, arrival guard, whitelist, read-only, full restore on unblock | Plug-in / already-plugged / UASP / MTP tests green |
| 6 | Advanced: expiries & schedules (server + agent), exceptions, per-user, access requests, unlock codes, emergency unblock-all, bulk progress page | Feature tests + RBAC tests green |
| 7 | Hardening: tamper tests, rollback/uninstall cleanliness, 10k-agent bulk toggle load test, browser extension, docs, pilot branch | p95 ≤ 5 s at 2,000 devices; zero leftovers after uninstall |

### 17.12 Test matrix (must pass before release)

| # | Scenario | Expected |
|---|---|---|
| W1 | Block `facebook.com` on device; open new tab in Chrome, Edge, Brave | Browser block page ≤ 5 s |
| W2 | Same with a Facebook tab already open | Connection dropped; with extension: block page ≤ 2 s |
| W3 | Firefox open | `applied_partial`, DNS/WFP still stop loading; full after restart |
| W4 | User edits hosts file / sets DNS 8.8.8.8 / enables DoH | Still blocked; `tamper.blockList` within 60 s |
| W5 | Unblock | Site loads ≤ 5 s, original DNS + DoH settings restored |
| A1 | Block `steam.exe` while running | Notice shown, process closed ≤ 8 s |
| A2 | Copy to `notsteam.exe` and run | Refused (hash/publisher rule) |
| A3 | 32-bit app on 64-bit Windows; Store app | Both refused |
| A4 | Unblock | Launches normally, IFEO/WDAC entries gone |
| U1 | Block USB with pen drive already plugged | Drive disappears ≤ 5 s |
| U2 | Plug USB 3 UASP SSD, phone (MTP), SD card | None mount; mouse/keyboard/printer still work |
| U3 | Whitelisted serial | Mounts normally |
| U4 | Read-only mode | Files readable, write denied |
| U5 | Unblock | Already-plugged drive mounts without unplugging |
| G1 | Branch-wide block on 400 devices, 20 offline | Progress shows applied/pending; offline apply on reconnect |
| G2 | Inherited block + device exception | Exception device unblocked, others blocked |
| G3 | Temporary unblock 15 min; PC clock moved forward/back | Re-blocks at server time, not PC time |
| G4 | Agent killed mid-apply | Journal replays; final state = desired policy |
| G5 | Uninstall | All layers removed, original settings restored |

### 17.13 Known limits (be honest with HR/management)

- A **local administrator** can still undo enforcement (and the agent will detect and report it). Remove local admin
  rights from staff — §5.2.
- Website blocking is domain-based outside the browser extension; a **VPN, Tor or proxy app** can bypass DNS/WFP
  layers → block those apps (category *VPN/Proxy* in §17.6) and alert on unknown VPN adapters.
- App Control for Business rules on Windows 10 may need a refresh tool or reboot for some policy types; Windows 11
  22H2+ applies them live.
- Firefox applies its policy list only after restart (DNS/WFP layers still block meanwhile).
- Phones in **USB tethering** mode are network adapters, not storage — block via a separate *Network adapters* option if
  needed.
