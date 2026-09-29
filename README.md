# F4Pool Miningcore

Custom Miningcore fork maintained for **F4Pool**.

This repository is based on the Miningcore project and contains additional pool, API, Redis miner-statistics, and coin support used by F4Pool.

---

# Features

- High-performance asynchronous Stratum server
- Multi-pool / multi-coin cluster support
- Variable difficulty (VarDiff)
- Native Proof-of-Work validation
- PostgreSQL persistence
- Redis live miner statistics
- Per-wallet statistics
- Per-worker statistics
- Accepted share counters
- Rejected share counters
- Stale share counters
- VarDiff retarget counters
- Current worker difficulty
- Worker activity timestamps
- Automatic worker-session reset after inactivity
- Automatic Redis expiration for inactive miners
- REST API
- Pool statistics
- Miner statistics
- Worker statistics
- Payments
- Blocks
- Earnings
- Miner performance history
- PROP payout support
- SOLO payout support
- PPLNS payout support
- Linux production deployment
- Nginx reverse-proxy support
- Systemd service support

---

# F4Pool Redis Live Miner Statistics

This fork adds a Redis-backed live miner-statistics system.

Statistics are recorded separately for each:

```text
pool
└── wallet
    └── worker
```

Example:

```text
alph-prop
└── WALLET_ADDRESS
    ├── worker1
    ├── worker2
    └── worker3
```

Redis records live counters including:

```text
accepted
rejected
stale
retargets
currentDifficulty
lastShare
lastReject
lastStale
lastRetarget
lastActivity
sessionStarted
```

The Stratum share-processing path does not directly wait for Redis writes.

Events are queued and periodically flushed to Redis so Redis statistics do not unnecessarily block share submission processing.

---

# Worker Session Reset

Worker counters represent the worker's current **global live mining session**.
Multiple Miningcore instances can share one Redis database safely.

Example configuration:

```json
"redis": {
  "enabled": true,
  "connectionString": "10.20.30.10:6379,password=REPLACE_ME,abortConnect=false",
  "database": 0,
  "keyPrefix": "miningcore:minerstats",
  "retentionDays": 30,
  "sessionResetMinutes": 5,
  "nodeName": "us",
  "sessionHeartbeatSeconds": 30,
  "sessionTimeoutSeconds": 120,
  "disconnectedRetentionMinutes": 30
}
```

Each authorized Stratum socket receives a unique Redis session id. Nodes refresh
their live sessions every `sessionHeartbeatSeconds`. If a node crashes or becomes
unreachable, its sessions are treated as dead after `sessionTimeoutSeconds`.

For a worker such as:

```text
WALLET.worker1
```

if the final live session disconnects from every region, the worker is removed
from live API results immediately. When it connects again, its counters begin at
zero:

```text
accepted: 0
rejected: 0
stale: 0
retargets: 0
```

If the same `WALLET.worker1` still has another active session on another Miningcore
instance, disconnecting one region does **not** reset the worker.

Wallet-level accepted/rejected/stale/retarget totals are calculated from the
currently active worker sessions rather than lifetime Redis totals. PostgreSQL
share/payment history is unaffected.

`sessionResetMinutes` remains accepted for backwards-compatible configurations;
the multi-region live-session logic is driven by heartbeat/timeout settings.

---

# Redis Retention

Redis miner statistics use an expiration timer.

Example:

```json
"retentionDays": 30
```

Thirty days is:

```text
2,592,000 seconds
```

Active miner keys continuously receive new activity and their Redis expiration is refreshed.

If a miner disappears permanently:

```text
miner stops
    ↓
no new Redis activity
    ↓
30-day TTL counts down
    ↓
Redis removes the expired keys
```

This prevents old miner-stat keys from remaining forever.

Redis statistics are separate from PostgreSQL payment/share/accounting information.

---

# Redis Key Layout

Wallet addresses and worker names are SHA-256 hashed when used in Redis key names.

Example layout:

```text
miningcore:minerstats:<poolId>:<walletHash>:totals

miningcore:minerstats:<poolId>:<walletHash>:workers

miningcore:minerstats:<poolId>:<walletHash>:worker:<workerHash>
```

Example:

```text
miningcore:minerstats:alph-prop:abc123...:totals
miningcore:minerstats:alph-prop:abc123...:workers
miningcore:minerstats:alph-prop:abc123...:worker:def456...
```

The worker name itself is stored inside the worker hash.

---

# Requirements

Recommended production environment:

- Linux
- Ubuntu 22.04 or newer
- Ubuntu 24.04 supported
- .NET SDK 8.0.423 for building
- .NET 8 runtime
- PostgreSQL
- Redis
- C/C++ build tools
- CMake
- Clang
- Boost
- ZeroMQ development libraries
- Sodium development libraries
- GMP development libraries

Windows may be useful for development, but Linux is recommended for production pool servers.

---

# .NET Version

This repository targets:

```text
.NET 8
```

The project target is:

```text
net8.0
```

The repository `global.json` currently requests:

```json
{
  "sdk": {
    "version": "8.0.423",
    "rollForward": "latestPatch"
  }
}
```

Check your installed SDK:

```bash
dotnet --version
```

Expected:

```text
8.0.423
```

List all installed SDKs:

```bash
dotnet --list-sdks
```

---

# Install .NET 8 Manually

If .NET 8.0.423 is not installed:

```bash
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh

./dotnet-install.sh \
  --version 8.0.423 \
  --install-dir "$HOME/.dotnet"
```

Add it to your shell:

```bash
echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
echo 'export PATH=$HOME/.dotnet:$PATH' >> ~/.bashrc

source ~/.bashrc
```

Verify:

```bash
dotnet --version
```

---

# Ubuntu Build Dependencies

Install the common native build dependencies:

```bash
sudo apt update

sudo apt install -y \
  build-essential \
  cmake \
  clang \
  ninja-build \
  pkg-config \
  libssl-dev \
  libboost-all-dev \
  libsodium-dev \
  libzmq5 \
  libzmq3-dev \
  libgmp-dev \
  libc++-dev \
  zlib1g-dev \
  golang-go
```

Package names can vary slightly between Linux distributions.

---

# Clone the Repository

```bash
git clone https://github.com/emanwrxsti/mc1.git
cd mc1
```

Check the branch:

```bash
git status
git branch --show-current
```

Production changes should normally be deployed from a known commit on `main`.

---

# Restore Dependencies

```bash
dotnet restore src/Miningcore/Miningcore.csproj
```

---

# Build

```bash
dotnet build src/Miningcore/Miningcore.csproj
```

A successful build ends with:

```text
0 Error(s)
```

Native-library compiler warnings may be displayed during Linux builds.

Warnings do not necessarily indicate a failed build.

Always check the final:

```text
Warning(s)
Error(s)
```

lines.

---

# Publish

Create the production build:

```bash
dotnet publish src/Miningcore/Miningcore.csproj \
  -c Release \
  -f net8.0 \
  -o ./build
```

The Linux build process also builds/copies the required native libraries.

Check:

```bash
ls -lah build
```

You should see files including:

```text
Miningcore
Miningcore.dll
Miningcore.runtimeconfig.json
coins.json
config.schema.json
```

as well as required native `.so` libraries.

---

# PostgreSQL Installation

Ubuntu:

```bash
sudo apt update
sudo apt install -y postgresql postgresql-contrib
```

Enable PostgreSQL:

```bash
sudo systemctl enable --now postgresql
```

Check:

```bash
systemctl status postgresql
```

---

# PostgreSQL Database Setup

Open PostgreSQL:

```bash
sudo -u postgres psql
```

Create the Miningcore user and database:

```sql
CREATE ROLE miningcore
WITH LOGIN
ENCRYPTED PASSWORD 'REPLACE_WITH_A_STRONG_PASSWORD';

CREATE DATABASE miningcore OWNER miningcore;
```

Exit:

```text
\q
```

Import the database schema:

```bash
sudo -u postgres psql \
  -d miningcore \
  -f src/Miningcore/Persistence/Postgres/Scripts/createdb.sql
```

Do not put real database passwords into GitHub.

Use production-only configuration files outside the public repository whenever possible.

---

# Redis Installation

Ubuntu:

```bash
sudo apt update
sudo apt install -y redis-server
```

Enable Redis:

```bash
sudo systemctl enable --now redis-server
```

Check:

```bash
systemctl status redis-server
```

Test:

```bash
redis-cli ping
```

Expected:

```text
PONG
```

---

# Redis Security

Redis should normally listen only on localhost:

```text
127.0.0.1:6379
```

Do not expose Redis port `6379` directly to the public internet.

Miningcore connects locally to Redis.

Recommended connection:

```text
127.0.0.1:6379,abortConnect=false
```

---

# Redis Memory Usage

Check memory:

```bash
redis-cli INFO memory | grep -E 'used_memory_human|maxmemory_human'
```

Check number of keys:

```bash
redis-cli DBSIZE
```

List F4Pool miner-stat keys:

```bash
redis-cli --scan --pattern 'miningcore:minerstats:*'
```

Check a key's TTL:

```bash
redis-cli TTL 'PASTE_REDIS_KEY_HERE'
```

Values:

```text
positive number = seconds until expiration
-1              = key has no expiration
-2              = key does not exist
```

---

# Redis Persistence

Check Redis disk-persistence configuration:

```bash
redis-cli CONFIG GET save
redis-cli CONFIG GET appendonly
redis-cli CONFIG GET dir
redis-cli CONFIG GET dbfilename
```

Redis uses RAM as its active data store.

Depending on Redis configuration, snapshots and/or append-only persistence may also save data to disk.

---

# Base Miningcore Configuration

Example:

```json
{
  "clusterName": "F4Pool",
  "instanceId": 1,

  "logging": {
    "level": "info",
    "enableConsoleLog": true,
    "enableConsoleColors": true,
    "logFile": "miningcore.log",
    "apiLogFile": "api.log",
    "logBaseDirectory": "logs",
    "perPoolLogFile": true
  },

  "persistence": {
    "postgres": {
      "host": "127.0.0.1",
      "port": 5432,
      "user": "miningcore",
      "password": "REPLACE_ME",
      "database": "miningcore",
      "commandTimeout": 6000
    },

    "redis": {
      "enabled": true,
      "connectionString": "127.0.0.1:6379,abortConnect=false",
      "database": 0,
      "keyPrefix": "miningcore:minerstats",
      "retentionDays": 30,
      "sessionResetMinutes": 5,
      "nodeName": "us",
      "sessionHeartbeatSeconds": 30,
      "sessionTimeoutSeconds": 120,
      "disconnectedRetentionMinutes": 30
    }
  },

  "api": {
    "enabled": true,
    "listenAddress": "127.0.0.1",
    "port": 4000,
    "rateLimiting": {
      "disabled": true
    }
  },

  "paymentProcessing": {
    "enabled": false,
    "interval": 600
  },

  "pools": []
}
```

Do not commit production RPC passwords, wallet passwords, database passwords, API keys, or private keys to GitHub.

---

# Starting Miningcore Manually

Copy your production configuration into the build directory:

```bash
cp /path/to/config.json ./build/config.json
```

Start:

```bash
cd build

./Miningcore -c config.json
```

or:

```bash
dotnet Miningcore.dll -c config.json
```

---

# Systemd Production Service

Example:

```ini
[Unit]
Description=F4Pool Miningcore
After=network.target postgresql.service redis-server.service

[Service]
Type=simple
User=miningcore
WorkingDirectory=/opt/miningcore/build

ExecStart=/usr/bin/dotnet \
  /opt/miningcore/build/Miningcore.dll \
  -c /opt/miningcore/build/config.json

Restart=always
RestartSec=5

TimeoutStopSec=15
KillMode=control-group
SendSIGKILL=yes

[Install]
WantedBy=multi-user.target
```

Save as:

```text
/etc/systemd/system/miningcore.service
```

Then:

```bash
sudo systemctl daemon-reload
sudo systemctl enable miningcore
sudo systemctl start miningcore
```

Status:

```bash
systemctl status miningcore --no-pager -l
```

Logs:

```bash
journalctl -u miningcore -f
```

Replace paths and users with the correct production values for your server.

---

# API Configuration

Recommended:

```json
"api": {
  "enabled": true,
  "listenAddress": "127.0.0.1",
  "port": 4000,
  "rateLimiting": {
    "disabled": true
  }
}
```

Binding to:

```text
127.0.0.1
```

keeps Miningcore's API private to the machine.

A public web server such as Nginx can selectively proxy the public API.

---

# Nginx API Proxy

Example:

```nginx
location /api/ {
    proxy_pass http://127.0.0.1:4000;

    proxy_http_version 1.1;

    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;

    proxy_connect_timeout 10s;
    proxy_send_timeout 60s;
    proxy_read_timeout 60s;

    proxy_buffering off;
}
```

Test Nginx:

```bash
sudo nginx -t
```

Reload:

```bash
sudo systemctl reload nginx
```

---

# API Health Check

Local:

```bash
curl http://127.0.0.1:4000/api/health-check
```

The API should return a successful response.

---

# API Route List

Miningcore can display its currently registered API routes itself:

```bash
curl http://127.0.0.1:4000/api/help
```

This is the best way to verify the API routes supported by the exact build currently running.

---

# REST API

Default local base URL:

```text
http://127.0.0.1:4000
```

Example public F4Pool base URL:

```text
https://f4pool.com
```

The following routes are available in this fork.

---

## General API

### Health Check

```http
GET /api/health-check
```

Example:

```bash
curl -s http://127.0.0.1:4000/api/health-check
```

---

### API Help

Lists registered API routes:

```http
GET /api/help
```

Example:

```bash
curl -s http://127.0.0.1:4000/api/help
```

---

### Cluster Blocks

```http
GET /api/blocks
```

Parameters:

```text
page
pageSize
state
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/blocks?page=0&pageSize=15' \
  | jq
```

---

# Pool API

## List Pools

```http
GET /api/pools
```

Optional:

```text
topMinersRange
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools' \
  | jq
```

---

## Pool Information

```http
GET /api/pools/{poolId}
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/alph-prop' \
  | jq
```

---

## Pool Performance

```http
GET /api/pools/{poolId}/performance
```

Query parameters:

```text
r = sample range
i = sample interval
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/alph-prop/performance?r=Day&i=Hour' \
  | jq
```

---

## Pool Miners

```http
GET /api/pools/{poolId}/miners
```

Parameters:

```text
page
pageSize
topMinersRange
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/alph-prop/miners?page=0&pageSize=15' \
  | jq
```

---

## Pool Blocks

```http
GET /api/pools/{poolId}/blocks
```

Parameters:

```text
page
pageSize
state
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/alph-prop/blocks?page=0&pageSize=15' \
  | jq
```

---

## Pool Blocks V2

```http
GET /api/v2/pools/{poolId}/blocks
```

The V2 endpoint includes pagination metadata.

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/v2/pools/alph-prop/blocks?page=0&pageSize=15' \
  | jq
```

---

## Pool Payments

```http
GET /api/pools/{poolId}/payments
```

Parameters:

```text
page
pageSize
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/alph-prop/payments?page=0&pageSize=15' \
  | jq
```

---

## Pool Payments V2

```http
GET /api/v2/pools/{poolId}/payments
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/v2/pools/alph-prop/payments?page=0&pageSize=15' \
  | jq
```

---

# Miner API

Replace:

```text
POOL_ID
WALLET_ADDRESS
```

with real values.

Always URL-encode wallet addresses when required by the client.

---

## Miner Information

```http
GET /api/pools/{poolId}/miners/{address}
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/POOL_ID/miners/WALLET_ADDRESS' \
  | jq
```

This returns the normal Miningcore miner statistics, including performance/payment information when available.

---

# Live Redis Share Statistics API

This is an F4Pool fork extension.

```http
GET /api/pools/{poolId}/miners/{address}/shares
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/POOL_ID/miners/WALLET_ADDRESS/shares' \
  | jq
```

Example response:

```json
{
  "address": "WALLET_ADDRESS",
  "accepted": 1521,
  "rejected": 2,
  "stale": 1,
  "retargets": 8,
  "lastShare": "2026-08-18T14:20:00Z",
  "lastReject": null,
  "lastStale": null,
  "lastRetarget": "2026-08-18T14:18:00Z",
  "workers": [
    {
      "minerWorker": "WALLET_ADDRESS.worker1",
      "worker": "worker1",
      "accepted": 421,
      "rejected": 0,
      "stale": 0,
      "retargets": 2,
      "currentDifficulty": 512,
      "lastShare": "2026-08-18T14:20:00Z",
      "lastReject": null,
      "lastStale": null,
      "lastRetarget": "2026-08-18T14:18:00Z"
    }
  ]
}
```

Possible HTTP response:

```text
200 = statistics returned
404 = invalid/missing pool or wallet
503 = Redis statistics disabled or temporarily unavailable
```

---

# Live Dashboard Polling

The `/shares` endpoint returns a current snapshot.

It is not currently an SSE or WebSocket stream.

For a live frontend, poll the endpoint periodically.

Example JavaScript:

```javascript
const interval = setInterval(async () => {
    const response = await fetch(
        `/api/pools/${encodeURIComponent(poolId)}/miners/${encodeURIComponent(wallet)}/shares`
    );

    if (!response.ok)
        return;

    const stats = await response.json();

    console.log(stats);
}, 2000);
```

Clean up the polling interval when leaving the page:

```javascript
clearInterval(interval);
```

A two-to-five-second polling interval is normally sufficient for a live miner dashboard.

---

# Live Stats Privacy

The Redis implementation does not globally scan and return every wallet.

The API calculates the Redis wallet key from the wallet address requested and returns that wallet's worker statistics.

However:

```text
wallet address != authentication
```

If the API is publicly accessible and someone knows another miner's wallet address, they may be able to request that wallet's public miner statistics.

True ownership privacy requires authentication, a signed challenge, access token, or another wallet-ownership verification system.

---

## Miner Blocks

```http
GET /api/pools/{poolId}/miners/{address}/blocks
```

Parameters:

```text
page
pageSize
state
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/POOL_ID/miners/WALLET_ADDRESS/blocks?page=0&pageSize=15' \
  | jq
```

---

## Miner Blocks V2

```http
GET /api/v2/pools/{poolId}/miners/{address}/blocks
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/v2/pools/POOL_ID/miners/WALLET_ADDRESS/blocks?page=0&pageSize=15' \
  | jq
```

---

## Miner Payments

```http
GET /api/pools/{poolId}/miners/{address}/payments
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/POOL_ID/miners/WALLET_ADDRESS/payments?page=0&pageSize=15' \
  | jq
```

---

## Miner Payments V2

```http
GET /api/v2/pools/{poolId}/miners/{address}/payments
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/v2/pools/POOL_ID/miners/WALLET_ADDRESS/payments?page=0&pageSize=15' \
  | jq
```

---

## Miner Balance Changes

```http
GET /api/pools/{poolId}/miners/{address}/balancechanges
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/POOL_ID/miners/WALLET_ADDRESS/balancechanges?page=0&pageSize=15' \
  | jq
```

---

## Miner Balance Changes V2

```http
GET /api/v2/pools/{poolId}/miners/{address}/balancechanges
```

---

## Miner Daily Earnings

```http
GET /api/pools/{poolId}/miners/{address}/earnings/daily
```

Example:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/POOL_ID/miners/WALLET_ADDRESS/earnings/daily?page=0&pageSize=30' \
  | jq
```

---

## Miner Daily Earnings V2

```http
GET /api/v2/pools/{poolId}/miners/{address}/earnings/daily
```

---

## Miner Performance

```http
GET /api/pools/{poolId}/miners/{address}/performance
```

Example day:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/POOL_ID/miners/WALLET_ADDRESS/performance?mode=Day' \
  | jq
```

Supported ranges depend on the running build.

The current implementation supports hourly, daily, and monthly performance sampling modes.

---

## Miner Settings

Get settings:

```http
GET /api/pools/{poolId}/miners/{address}/settings
```

Update settings:

```http
POST /api/pools/{poolId}/miners/{address}/settings
```

The public update path performs additional checks including recently used mining IP validation.

---

# Admin API

Admin routes are under:

```text
/api/admin
```

These routes can modify runtime Miningcore behavior.

**Do not expose the Admin API unrestricted to the public internet.**

Use an IP whitelist, firewall rule, private interface, VPN, or Nginx restriction.

---

## Change Log Level

```http
GET /api/admin/logging/level/{level}
```

Example:

```text
Trace
Debug
Info
Warn
Error
Fatal
```

---

## Enable Payment Processing for All Pools

```http
GET /api/admin/payment/processing/enable
```

---

## Disable Payment Processing for All Pools

```http
GET /api/admin/payment/processing/disable
```

---

## Enable Payment Processing for One Pool

```http
GET /api/admin/payment/processing/{poolId}/enable
```

---

## Disable Payment Processing for One Pool

```http
GET /api/admin/payment/processing/{poolId}/disable
```

---

## Garbage Collection Statistics

```http
GET /api/admin/stats/gc
```

---

## Force Garbage Collection

```http
POST /api/admin/forcegc
```

---

## Miner Balance — Admin

```http
GET /api/admin/pools/{poolId}/miners/{address}/getbalance
```

---

## Miner Settings — Admin

Get:

```http
GET /api/admin/pools/{poolId}/miners/{address}/settings
```

Set:

```http
POST /api/admin/pools/{poolId}/miners/{address}/settings
```

Again, these endpoints should be protected.

---

# Protecting `/api/admin/` with Nginx

Example localhost-only restriction:

```nginx
location ^~ /api/admin/ {
    allow 127.0.0.1;
    deny all;

    proxy_pass http://127.0.0.1:4000;

    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

For remote administration, use a trusted management IP or VPN rather than exposing these endpoints to everyone.

---

# API Testing

Check API:

```bash
curl -s -o /dev/null \
  -w "HTTP %{http_code}\n" \
  http://127.0.0.1:4000/api/pools
```

Expected:

```text
HTTP 200
```

Health:

```bash
curl -s \
  http://127.0.0.1:4000/api/health-check
```

Routes:

```bash
curl -s \
  http://127.0.0.1:4000/api/help
```

---

# Redis Live Stats Testing

Check Redis:

```bash
redis-cli ping
```

Expected:

```text
PONG
```

List miner-stat keys:

```bash
redis-cli --scan \
  --pattern 'miningcore:minerstats:*' \
  | head -20
```

Inspect wallet totals:

```bash
redis-cli HGETALL \
  'miningcore:minerstats:POOL_ID:WALLET_HASH:totals'
```

Inspect workers:

```bash
redis-cli SMEMBERS \
  'miningcore:minerstats:POOL_ID:WALLET_HASH:workers'
```

Inspect worker:

```bash
redis-cli HGETALL \
  'miningcore:minerstats:POOL_ID:WALLET_HASH:worker:WORKER_HASH'
```

Check TTL:

```bash
redis-cli TTL \
  'miningcore:minerstats:POOL_ID:WALLET_HASH:worker:WORKER_HASH'
```

---

# Watch Live API Statistics

For testing:

```bash
watch -n 2 \
'curl -s "http://127.0.0.1:4000/api/pools/POOL_ID/miners/WALLET_ADDRESS/shares" | jq'
```

This makes it easy to watch:

```text
accepted
rejected
stale
retargets
currentDifficulty
lastShare
```

change while the miner is running.

---

# Test Worker Session Reset

To test the configured five-minute session reset:

1. Start a miner as:

```text
WALLET.testworker
```

2. Confirm accepted shares are increasing.

3. Stop only `testworker`.

4. Leave it disconnected longer than:

```text
sessionResetMinutes
```

5. Start `WALLET.testworker` again.

6. Query:

```bash
curl -s \
  'http://127.0.0.1:4000/api/pools/POOL_ID/miners/WALLET/shares' \
  | jq
```

The reconnecting worker should begin a new worker session.

Other workers on the same wallet should remain unchanged.

Wallet totals should remain cumulative.

---

# Pool Configuration

Each configured pool normally contains:

```text
id
enabled
coin
address
ports
daemons
paymentProcessing
rewardRecipients
banning
```

Example skeleton:

```json
{
  "id": "example-prop",
  "enabled": true,
  "coin": "example",
  "address": "POOL_WALLET",

  "ports": {
    "3333": {
      "listenAddress": "0.0.0.0",
      "difficulty": 100,
      "varDiff": {
        "minDiff": 10,
        "maxDiff": null,
        "targetTime": 15,
        "retargetTime": 90,
        "variancePercent": 30
      }
    }
  },

  "daemons": [
    {
      "host": "127.0.0.1",
      "port": 1234
    }
  ],

  "paymentProcessing": {
    "enabled": false,
    "minimumPayment": 0.1,
    "payoutScheme": "PROP",
    "payoutSchemeConfig": {}
  }
}
```

Actual daemon configuration varies by coin.

---

# Payout Schemes

Miningcore supports payout scheme configuration including:

```text
PPLNS
PPLNSBF
PROP
SOLO
PPS
FPPS
PPBS
```

Not every scheme is necessarily appropriate or fully tested for every coin.

Always test payout processing before enabling automatic payments on a production pool.

---

# F4Pool Coin Support

This fork includes the broader Miningcore coin-family infrastructure plus F4Pool-specific work.

F4Pool deployments currently include work for coins such as:

```text
Alephium
SatoshiCash / SCASH
```

The repository's actual supported coin definitions are stored in:

```text
src/Miningcore/coins.json
```

Always check `coins.json` and the corresponding blockchain implementation before assuming a coin is production-ready.

---

# SCASH

The fork contains SatoshiCash/SCASH Miningcore integration.

SCASH deployments may use:

```text
RPC
ZMQ raw block notifications
ZMQ raw transaction notifications
TCP Stratum
TLS Stratum
PROP
SOLO
```

Exact ports and daemon credentials belong in the production `config.json`, not in this README.

---

# Alephium

The fork contains Alephium pool support.

F4Pool deployment configurations may include separate:

```text
PROP
SOLO
```

pool IDs.

Use the actual production configuration for current Stratum ports and payout settings.

---

# Production Security

Never commit:

```text
wallet passwords
private keys
seed phrases
PostgreSQL passwords
Redis passwords
daemon RPC passwords
API keys
TLS private keys
exchange API secrets
```

Recommended:

```text
Miningcore API -> localhost
PostgreSQL     -> localhost/private network
Redis          -> localhost
Daemon RPC     -> localhost/private network
Stratum        -> public required ports only
Nginx          -> public HTTPS frontend/API proxy
```

Use a firewall.

Example UFW status:

```bash
sudo ufw status numbered
```

Only expose the services miners and web users actually need.

---

# Updating the Server

Example workflow:

```bash
cd /path/to/mc1

git fetch origin
git status
git pull --ff-only origin main

dotnet restore src/Miningcore/Miningcore.csproj

dotnet publish src/Miningcore/Miningcore.csproj \
  -c Release \
  -f net8.0 \
  -o ./build
```

Verify the production configuration before restarting.

Then restart the relevant Miningcore service.

---

# Troubleshooting

## API not responding

Check listener:

```bash
ss -ltnp | grep 4000
```

Test local API:

```bash
curl -v \
  http://127.0.0.1:4000/api/health-check
```

Check logs:

```bash
journalctl -u miningcore -n 200 --no-pager
```

---

## Redis not working

```bash
redis-cli ping
```

Check service:

```bash
systemctl status redis-server --no-pager -l
```

Check keys:

```bash
redis-cli DBSIZE
```

Check miner keys:

```bash
redis-cli --scan \
  --pattern 'miningcore:minerstats:*' \
  | head
```

---

## Redis `/shares` API returns 503

Verify Redis is enabled:

```json
"redis": {
  "enabled": true
}
```

Verify:

```bash
redis-cli ping
```

Then inspect Miningcore logs for:

```text
Redis miner stats online
```

---

## Pool API returns 500

Check:

```bash
curl -v \
  http://127.0.0.1:4000/api/pools
```

Then inspect:

```bash
tail -100 logs/api.log
```

or:

```bash
journalctl -u miningcore -n 200 --no-pager
```

---

## Check Miningcore Process

```bash
ps aux | grep -i Miningcore
```

---

## Check API Routes

```bash
curl -s \
  http://127.0.0.1:4000/api/help
```

---

# Development Workflow

Before committing:

```bash
git status --short
git diff --check
git diff
```

Build:

```bash
dotnet build src/Miningcore/Miningcore.csproj
```

Only commit after:

```text
0 Error(s)
```

Then:

```bash
git add .
git commit -m "Describe the change"
git push origin main
```

For larger production changes, using a separate branch and pull request is recommended.

---

# Repository Structure

Important locations:

```text
src/Miningcore/
├── Api/
│   └── Controllers/
├── Blockchain/
├── Configuration/
│   └── ClusterConfig.cs
├── Mining/
│   ├── PoolBase.cs
│   └── MinerStatsRedisService.cs
├── Persistence/
│   └── Postgres/
├── coins.json
├── config.schema.json
└── Miningcore.csproj
```

Redis miner-stat implementation:

```text
src/Miningcore/Mining/MinerStatsRedisService.cs
```

API:

```text
src/Miningcore/Api/Controllers/
```

Configuration classes:

```text
src/Miningcore/Configuration/ClusterConfig.cs
```

---

# Upstream

This repository is a customized fork of Miningcore.

Original/upstream Miningcore development has been maintained by the Miningcore community and upstream maintainers.

When taking upstream changes, review them carefully before merging because F4Pool contains custom code that may differ from upstream.

---

# License

This fork retains the licensing requirements of the original Miningcore project and the license included with this repository.

See the repository license file for details.

---

# F4Pool

Production mining-pool software used by:

```text
F4Pool
https://f4pool.com
```

This repository contains backend Miningcore infrastructure.

Frontend applications should consume the REST API rather than connecting directly to PostgreSQL or Redis.

In particular:

```text
Browser
   ↓
F4Pool frontend/backend
   ↓
Miningcore REST API
   ↓
Redis / PostgreSQL
```

Do not expose Redis directly to web browsers.
