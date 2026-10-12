# PhoneOrchestrator

Watches every `agent_hosts` machine and, when one stops answering, moves its
phones to a healthy host by flipping `phones.host_id`. WhatsAppDockerManager on
the target host picks the phone up on its next sync and rebuilds the container
from `creds_base64`, so the Baileys session survives the move.

## Two separate decisions

These are often conflated. They are not the same rule.

**When to evict** — a host loses its phones only when it stops answering:
`status <> 'active'`, or `FailuresBeforeDrain` consecutive failed probes of
`GET /api/host/heartbeat?staleAfterSeconds=90`. A merely *busy* host is never
drained.

**Where to place** — the five gates in `rpc_orch_pick_host`:

| | gate | dev | preprod |
|-|------|-----|---------|
| 1 | `status = 'active'` | yes | yes |
| 2 | heartbeat fresher than `heartbeat_timeout_sec` | yes | yes |
| 3 | RAM below `ram_max_pct` (85) | — | yes |
| 4 | CPU below `cpu_max_pct` (85) | — | yes |
| 5 | fewest phones wins | yes | yes |

Plus a capacity check against `max_containers`. Phone counts are read live from
`phones`, not from `agent_hosts.phone_count`, which only refreshes on heartbeat.

## Deploy

Run `phone_orchestrator.sql` first, then:

```bash
IMAGE_TAG=$(date +%Y%m%d-%H%M%S)
docker build -t 10.186.0.3:5000/phone-orchestrator:$IMAGE_TAG .
docker push 10.186.0.3:5000/phone-orchestrator:$IMAGE_TAG

export SUPABASE_URL=https://xxxx.supabase.co
export SUPABASE_SERVICE_KEY=...
IMAGE_TAG=$IMAGE_TAG docker stack deploy -c docker-compose.yml orchestrator
```

Confirm the right build is live:

```bash
curl http://10.186.0.3:8090/version   # marker must match Models/Dtos.cs
```

Dashboard: `http://10.186.0.3:8090/`

## Endpoints

| method | path | does |
|--------|------|------|
| GET  | `/api/hosts?page=&pageSize=` | host list, DB view merged with the live probe |
| GET  | `/api/hosts/{hostId}/phones?page=&pageSize=` | drill-down, paged |
| GET  | `/api/orchestrator/status` | loop state, config, recent drains |
| GET  | `/api/orchestrator/pick-host` | preview the gates' choice, read-only |
| POST | `/api/orchestrator/hosts/{hostId}/drain` | manual drain |
| POST | `/api/orchestrator/phones/{phoneId}/migrate` | manual single move |
| GET  | `/health`, `/version` | Swarm liveness, build marker |

Page sizes default to `bot_config['orchestrator.hosts.paging']` and
`['orchestrator.phones.paging']`.

## AutoDrain ships off

`Orchestrator__AutoDrain=false` means the loop probes, counts failures and
reports on the dashboard but never writes to `phones`. Watch a real host go
down first, confirm the verdict was right, then turn it on.

## Known gaps

- **No leader lock.** During a Swarm reschedule two tasks can briefly overlap
  and both drain. Low blast radius (`rpc_orch_migrate_phone` is idempotent per
  phone) but not zero. A `pg_advisory_lock` in the drain RPC would close it.
- **HostAgent response shape is unverified.** The probe treats HTTP 200 as
  healthy and parses the body only to enrich the dashboard, so an upstream
  shape change degrades the display rather than the logic. If the endpoint
  returns 200 with a stale flag, `HeartbeatPayload.IsStale` / `Healthy` already
  cover it — check the real field names.
- **`agent_hosts.port_range_start/end` is dead config.** Observed ports sit
  outside it. Migration nulls `api_port` and lets WhatsAppDockerManager
  reallocate.

## Canary v2 — CHECK ID / ACK

```
botinbound (stack canary, on whqueue-net)                    michal
┌───────────────────────────────────────────────┐            ┌──────────────┐
│ canary-runner (this image, Mode=canary)        │            │ orchestrator │
│   reads canary_control every 30s ──────────────┼── DB ──────┤ dashboard    │
│   → /send/template check_id {code}             │            │ (read/write  │
│ canary-prober (whatsapp-cloudapi)              │            │  the row)    │
│   → Meta → every phones.status='active'        │            └──────────────┘
│   ← "ACK {code}" ← Meta ← whqueue ← NATS       │
│   → POST canary-runner/canary/inbound/{token}  │
└───────────────────────────────────────────────┘
```

The two VMs never talk to each other - both only talk to Supabase.

**On/off and schedule: the `canary_control` row.** `enabled`, `interval_sec`,
`active_from`/`active_to` (daily window, `timezone`), `timeout_sec`,
`run_now_at` (one sweep now, even when disabled). The runner re-reads it every
30s and writes its own status back into it (`runner_seen_at`, `runner_mode`,
`prober_ready`, `last_result`, `next_sweep_at`). The dashboard edits the row.

| status | meaning |
|---|---|
| `passed` | ACK from the right number within `timeout_sec` |
| `timeout` | no ACK; `delivery_status` says whether it reached the phone |
| `late_ack` | ACK after the timeout - alive but slow |
| `failed` | ACK from a different number (routing bug) |
| `send_failed` | Meta refused the CHECK - says nothing about the target |
| `skipped` | Cloud API target (`check_cloudapi=false`) |

The sweep does not start when the prober reports `nats_connected`,
`token_ok`, `registered` false or zero webhooks; that sweep is recorded as an
`aborted` run. Notifications fire on transition only (down / recovered).

**Meta.** Template `check_id`, UTILITY, `he`, in the prober's WABA, body
containing `CHECK ID: {{1}}`. Token: System User, expiration Never.
