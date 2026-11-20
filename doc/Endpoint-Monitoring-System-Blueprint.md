# Endpoint Monitoring & Management System — Technical Blueprint

> **Project codename:** `SentinelDesk` (rename freely)
> **Scope:** Organization-wide (island-wide, multi-branch) monitoring of company PCs and laptops through a protected tray agent and a central web console.
> **Version:** 1.0 — Blueprint
> **Date:** 2026-11-20

---

## 1. Goals

| # | Requirement | How it is met |
|---|---|---|
| 1 | Small agent on every PC/laptop, visible in system tray | Windows Service (core) + lightweight Tray App (UI/status) |
| 2 | Install **and** uninstall require admin credentials | Signed MSI with elevation, uninstall password + admin check, service ACL hardening, watchdog, tamper alerts |
| 3 | Advanced encryption | mTLS (TLS 1.3) per-device certificates, AES-256-GCM payload encryption, encrypted local buffer (SQLCipher + DPAPI/TPM key) |
| 4 | Central web system to configure all agents | Policy engine, groups (branch/department), remote config push in real time |
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
                                │  Web Console (Next.js / React) — Admin UI       │
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
| Framework | **Next.js (App Router) + TypeScript** |
| UI | Tailwind CSS + shadcn/ui |
| Tables | TanStack Table (virtualized, server-side paging) |
| Charts | Apache ECharts or Recharts |
| Live updates | `@microsoft/signalr` client |
| Data fetching | TanStack Query |
| Maps (branches) | Leaflet (Sri Lanka branch map, device online status by location) |

---

## 4. Endpoint Agent — Detailed Design

### 4.1 Processes

| Process | Account | Responsibility |
|---|---|---|
| `SentinelDesk.Agent.exe` (service) | LocalSystem | Collect, encrypt, buffer, upload, apply policy, receive commands |
| `SentinelDesk.Guard.exe` (service) | LocalSystem | Watchdog: restart Agent if killed, verify binaries’ signatures, run updates, report tampering |
| `SentinelDesk.Tray.exe` (per user, auto-start via `HKLM\...\Run`) | Logged-in user | Tray icon (green/amber/red), monitoring notice, foreground-window tracking, idle time (`GetLastInputInfo`) |

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
- Menu: *About*, *Monitoring Notice* (what is collected), *Status*, *Contact IT*. **No “Exit” / “Stop” option.**
- If tray is killed, Agent restarts it in the user session (`WTSQueryUserToken` + `CreateProcessAsUser`) and logs a tamper event.

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
| **Policies** | Create policy → assign to org/branch/department/device; collectors on/off, intervals, URL logging level, window-title masking, data retention, USB rules, working hours |
| **Groups & Org structure** | Head office → Regions/Provinces → Branches → Departments |
| **Alerts & Rules** | e.g., “Data usage > 2 GB/day”, “Agent offline > 30 min”, “Unapproved software installed”, “Tamper attempt”, “Blocked category visited” → email/SMS/Teams |
| **Reports** | Daily/weekly/monthly data usage, app usage, website usage, inventory, compliance; export PDF/Excel; scheduled email |
| **Agent management** | Versions, staged rollout (10 % → 50 % → 100 %), enrollment tokens, uninstall keys |
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
  "signature": "MEUCIQ..."
}
```

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
policies(id, name, version, json, signed_blob, created_by, created_at)
policy_assignments(policy_id, target_type, target_id, priority)
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
│  └─ console/                       # Next.js App Router + TS + Tailwind + shadcn
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
