# Quantus deployment and recovery

This source package repairs consensus hashing, QUIC mining, canonical block
identification, reward deduplication and payout recovery. Treat it as a release
candidate until the live acceptance gates below pass. No production server,
wallet or funds were accessed. Read `QUANTUS-VALIDATION.md` for actual test results.

## Reviewed compatibility

| Component | Exact reviewed commit |
| --- | --- |
| [Quantus chain](https://github.com/Quantus-Network/chain/tree/482c5b9e02bec0adc70797eade0a67f60baf2619) | `482c5b9e02bec0adc70797eade0a67f60baf2619` |
| [Poseidon](https://github.com/Quantus-Network/qp-poseidon/tree/0117e2ca7f46a07d86a43385b5079ac6aaadaadc) | `0117e2ca7f46a07d86a43385b5079ac6aaadaadc` |
| [Quantus CLI](https://github.com/Quantus-Network/quantus-cli/tree/b29fb45b1463f0e850570ab9d573174a8cd418c6) | `b29fb45b1463f0e850570ab9d573174a8cd418c6` |

Use Linux x64, .NET 8 SDK/runtime, Rust 1.90.0, a C linker and Microsoft's libmsquic.
Validation used .NET SDK 8.0.425/runtime 8.0.31 and libmsquic 2.4.20. The supplied
CI integration gate uses PostgreSQL 16. Other platforms and other coin algorithms
have not been validated in this pass. Node/runtime upgrades require review.

Build and install the CLI from the reviewed commit using its own instructions.
Set `cliSha256` to the SHA-256 of **your installed executable**, not its Git commit:
`sha256sum /opt/quantus/bin/quantus`. Each invocation verifies this pin. Install it
under an administrator-controlled directory. CLI event/receipt text is part of
this adapter's contract, so review before changing its binary or dependencies.

## Build and migration

Extract into a fresh directory. Merge your configuration with
`examples/quantus_pool.json`; replace every `REPLACE_...` placeholder.

```sh
rustup toolchain install 1.90.0 --profile minimal
rustup override set 1.90.0
cargo test --manifest-path src/Native/libquantuspow/Cargo.toml --locked --offline
dotnet publish src/Miningcore/Miningcore.csproj -c Release -o build-quantus \
  -p:SkipNativeLibs=true -p:DisableGitVersionTask=true
test -s build-quantus/libquantuspow.so
```

`SkipNativeLibs=true` skips other coin algorithms; the real Quantus library still
builds from its vendored source. Do not set `SkipQuantusPow=true`. For a multi-coin
pool, build and verify all its other native libraries too. Do not replace an
existing multi-coin service with output missing those libraries.

Stop **all** payment processors using the database and back it up before migration.
Run this additive migration as the database owner:

```sh
psql "$MININGCORE_DATABASE_URL" -v ON_ERROR_STOP=1 \
  -f src/Miningcore/Persistence/Postgres/Scripts/quantus.sql
```

The ordinary Miningcore schema must already exist. The application checks for
this migration but does not execute DDL. Do not run the old Quantus sender beside
this version. Review historical blocks/balances before resuming accounting: this
migration cannot repair past incorrect credits or reconstruct missing nonces.
Legacy candidates without evidence remain pending. Preserve the backup, candidate
JSON, reward claims, payout journal and payment history. Rolling back to the old
sender is unsafe after this sender has run.

## Configuration

Use one configured Quantus daemon. Set `genesisHash` from an independently trusted
source for the intended network; startup compares it with `chain_getBlockHash(0)`.
`nodeUrl` must be the WebSocket endpoint on the same host, port and path as the
HTTP daemon. Keep node RPC and its private miner endpoint restricted to the pool.

`pool.address` is the public payout wallet. `wormholeAddress` is the public mining
reward wormhole address configured on the node, not its inner hash or secret.
Both are network-189 SS58 addresses. Only the exact canonical candidate seal and
an event naming that wormhole authorize a block reward.

The service account must have its CLI wallet store and read its password file.
Use existing absolute paths and owner-only permissions (normally `chmod 600`) for
password/secret files. Under that account, `quantus wallet view --name pool-payouts`
must print the same public address as `pool.address`. Credential contents are not
put on command lines or logged by this adapter.

Use a dedicated payout wallet and the **same database for all processes sharing
it**. External wallet software, other databases and manual sends do not participate
in the locks. Keep a spendable reserve for fees and account minimums. CLI batch
payments use `transfer_allow_death`; reserve management is an operational duty.
Confirm current chain batch limits before choosing `batchSize` and minimum payment.

With `collectRewards=false`, fund the wallet using your audited collection process.
Automatic collection needs `collectRewards=true` and either the configured wallet
plus `wormholeIndex`, or an owner-only `wormholeSecretFile`. Its dry run must derive
the configured wormhole before collection to the payout address at finalized
height. Test the configured Subsquid service and CLI proof dependencies separately.
The chain's nullifiers prevent duplicate reward collection.

Mining uses QUIC/UDP, ALPN `quantus-miner/2`. Set `enableInternalStratum=false`,
configure exactly one public mining port and allow its UDP traffic. TCP Stratum
miners are incompatible. The pool PFX currently needs an empty password; restrict
its file permissions. Give miners the pool certificate's public SHA-256 fingerprint
and use `address.worker` as the Ready token. Worker suffixes are optional.

`authTokenPath` is the private node miner token; `tlsFingerprintPath` contains that
node's certificate SHA-256 hex, not the pool certificate. The default private node
miner port is 9833. Never publish the private token to pool miners.

There are limits of 512 connections, 1024-byte frames and 50 messages per second per
miner. Upstream loss, invalid/stale jobs or an unhealthy node invalidate work and
disconnect miners while reconnecting. `maxJobAgeSeconds` defaults to 180: a quiet
network can cause reconnects even if the connection itself is healthy. Load-test
your intended concurrency and set OS/firewall capacity accordingly.

## Acceptance gates before enabling sends

Keep global and per-pool `paymentProcessing.enabled=true` for accounting, while
Quantus `enablePayouts=false` prevents sends during acceptance.

1. Run the supplied Quantus CI workflow with PostgreSQL 16 and real libmsquic.
   For a local run set `QUANTUS_TEST_POSTGRES` to an isolated test database and
   `QUANTUS_REQUIRE_INTEGRATION=1`, then run:

   ```sh
   dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj -c Release \
     -p:SkipNativeLibs=true -p:DisableGitVersionTask=true \
     --filter FullyQualifiedName~Miningcore.Tests.Blockchain.Quantus
   ```

   Do not set `QUANTUS_TEST_SINGLE_SESSION` for this gate. Tests create/drop their
   own schemas; never point them at production.
2. Connect a real compatible miner to the intended network. Check accepted shares,
   difficulty changes, invalid-share rejection, and node-restart recovery. Verify
   miners stop receiving usable work while the upstream is unavailable.
3. Find a real block in an isolated deployment. Match persisted pre-seal mining
   hash/nonce to the finalized canonical header, actual height and exact reward
   event. Verify balance allocation, then repeat classification and restart;
   the reward must be credited once.
4. Verify the service account's CLI wallet address and spendable balance. Test
   collection separately if enabled. Complete a small payout in staging; verify
   every recipient amount, successful dispatch, finality, payment rows and exact
   balance debits.
5. Exercise interrupted-send and post-receipt restarts. An uncertain send must stop
   further sends; a stored finalized receipt must recover database entries without
   broadcasting again. Reconcile every open batch and historical accounting first.

Only after these gates should production `enablePayouts` become true. No live
mining, collection or payout was performed for you by this validation.

## Recovery

The journal intent commits before CLI execution. States are `submitting`,
`submitted`, `credited` and operator-reconciled `cancelled`. A failure even before
broadcast can leave an intent; ambiguous sends are never automatically retried.

With payment processors stopped, inspect:

```sql
SELECT id, poolid, walletkey, state, transactionhash, recipients, created, updated
FROM quantus_payout_batches
WHERE state IN ('submitting', 'submitted')
ORDER BY created;
```

`submitted` has a unique finalized-success receipt. Restarting with the same
pool/wallet records payments, subtracts exact recorded amounts and marks it
`credited` atomically. New balance accrual remains. The batch is not sent again.

`submitting` means unknown outcome. A timeout or missing receipt does not prove
failure. Match network, sender, exact recipients/amounts and successful finalized
atomic batch using trusted chain evidence. Only then may a reviewed operator
transaction set its verified transaction hash and state to `submitted` for normal
recovery. Set `cancelled` only with definitive proof of no broadcast or finalized
failed dispatch. A missing transaction search result is insufficient: the send may
still be pending. Never delete journal rows, erase balances, remove unique indexes
or change wallet/pool names to bypass a blocked batch. Preserve the evidence.

Reward claims are unique by genesis and canonical hash, in the same transaction as
fees, miner credits and block status. Duplicate candidates are orphaned without
rerunning the payment scheme. Unmatched pre-seal headers stay pending: the bounded
search around observed height (default 32, max 128) cannot prove an unmatched
candidate is confirmed or orphaned. Resolve from canonical evidence, never height
or wormhole address alone. Unsupported header/runtime layouts fail closed.

## Monitoring and dependency limits

Alert on old pending candidates, unresolved payout batches, pin/self-test errors,
RPC/QUIC reconnects, low fee reserves and DB persistence failures.

MailKit is upgraded to 4.16.0. AutoMapper remains 12.0.1 with the advisory's explicit
64-level depth mitigation and a regression test. NU1903 remains because the package
itself is not upgraded. Plan a separately reviewed major-version/license migration.
The warning is not suppressed. This is not an audit of every inherited dependency.
CodeRabbit could not authenticate in this environment; no CodeRabbit review is claimed.
