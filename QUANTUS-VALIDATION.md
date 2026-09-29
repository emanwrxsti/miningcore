# Quantus validation record

Validated 2026-09-29 UTC. This record covers the corrected source in this package.
Original input: `mc1-main(3).zip`, SHA-256
`37f924448456d34e2caf068687fd7be5724c291ea1d22e673e43612681683ff3`;
archive Git reference `70a0cae21587b0ca854e6f5aa754b284036e74ca`.

## Results

| Check | Result | Scope |
| --- | --- | --- |
| .NET Debug build | Passed | Miningcore and test projects; other coin native libraries skipped |
| Linux Release publish | Passed | Main project and actual `libquantuspow.so`; published ABI and pinned PoW vector verified |
| Rust native tests | 3 passed | 64 distinct FFI/core inputs; null pointers; independently specified 45-felt header layout including root reduction, digest terminator and size bound |
| Managed Quantus suite | 31 passed, 0 failed | Consensus, addresses, canonical rewards, real QUIC, CLI process adapter and database recovery |
| Independent PostgreSQL sessions | 1 skipped locally | Cannot be validated using the local single-session WASM backend; included in the PostgreSQL 16 CI gate |
| Vendored Poseidon | Exact byte match | Four Rust files match the reviewed upstream source |
| Configuration and patch | Passed | Example JSON parses and references an existing coin; Authored changes pass whitespace checks; patch applies to baseline |

The whitespace check excludes the unmodified upstream vendor files, whose original
trailing blank lines are preserved to retain exact source bytes.

Detailed test output is in `docs/validation/quantus-managed-tests.txt` and
`quantus-rust-tests.txt`; published native verification is in `publish-check.txt`.
The suite contains 18 consensus/classification/address/amount cases, three real
libmsquic cases, three process-based CLI adapter cases, six database accounting
cases and one AutoMapper recursion mitigation case. All were rerun after recovery
from a workspace reset, using the reconstructed test files included here.

Environment: Linux x64; .NET SDK 8.0.425/runtime 8.0.31; Rust 1.90.0; libmsquic
2.4.20. The host's normal .NET/VSTest launcher failed, so MSBuild was called through
`MSBuild.dll`. Compiled xUnit Fact/Theory methods ran through the included
`docs/validation/ReflectionRunner.cs`, with real assertions, inline data and skip
conditions. A shell wrapper was needed for Cargo's compiler launches on this host.
These are host workarounds, not production runtime dependencies. This does not claim
that standard VSTest or the GitHub Actions workflow successfully ran here.

Database tests used Npgsql/Dapper, the real Miningcore schema and new migration,
and actual repositories/journal methods against PGlite 0.3.15 and its socket adapter
0.0.20. PGlite runs PostgreSQL in WebAssembly but has only one backend session.
It validates the exercised SQL and transaction rollback, not independent-process
locking or production concurrency. The skipped test runs against PostgreSQL 16 in
the supplied CI workflow. That workflow was supplied, not executed remotely.

QUIC tests used real local listeners/clients and ephemeral certificates. They check
stale cancellation, difficulty clamping, share delivery, work invalidation, rejected
addresses and certificate pin failure. CLI tests use controlled shell executables
to check literal argument boundaries, exact raw-unit batch JSON, file mode/removal,
binary pin rejection, cancellation and finalized receipts despite a subsequent
process failure. They do not execute a real wallet or broadcast transactions.

## Fixes included

- Real pinned upstream Poseidon native implementation and automatic Linux build;
  ABI/known-answer startup checks and the chain's custom header hash layout.
- Strict nonce/hash/difficulty/address validation and atomic duplicate share checks.
  Network candidates survive a configured pool difficulty above network difficulty.
- Bounded QUIC sessions/messages/timeouts, certificate pinning, stale job handling,
  upstream supervision and invalidation of unusable work.
- Full mining-hash/nonce candidate evidence persisted in the required database field.
  Classification proves the exact finalized canonical seal and queries its exact
  reward event; height and wormhole alone cannot establish block ownership.
- Transactional unique canonical reward claims, stale-row checks and duplicate
  rejection before running the payout scheme, preventing repeated reward credits.
- Pinned CLI atomic batch payouts and optional wormhole collection, with durable
  intent before sending, explicit finalized receipt and atomic ledger recovery.
  Uncertain sends block new sends. Recovery never blindly resubmits a batch.
- MailKit 4.16.0, with its BouncyCastle types isolated from legacy NBitcoin types;
  explicit application-wide AutoMapper mapping-depth mitigation, tested at 64 levels.

`quantus-fixes.patch` in this repository root is relative to the uploaded source
baseline. The ZIP already contains the patched files; do not apply the patch again
to the supplied source. It is provided for reviewing or applying to a clean baseline.

## Remaining release gates and limits

No live node, real miner, production database, owned wallet or funds were available.
No deployment or chain transaction was performed. Before enabling production payouts,
complete the PostgreSQL integration run, actual block/reward verification, collection
(if used), small finalized payout and restart/reconciliation steps in
`QUANTUS-DEPLOYMENT.md`. Existing Quantus credits need historical review; a schema
migration cannot repair past accounting errors or recover missing candidate data.

AutoMapper 12.0.1 still emits NU1903 for
[GHSA-rvv3-g6hj-g44x](https://github.com/advisories/GHSA-rvv3-g6hj-g44x).
The advisory's explicit depth mitigation is applied and tested; the package itself
is not upgraded and the warning is not suppressed. MailKit 4.16.0 addresses
[GHSA-9j88-vvj5-vhgr](https://github.com/advisories/GHSA-9j88-vvj5-vhgr).
Inherited build/analyzer warnings remain. Other coin algorithms, existing container
recipes and the complete legacy test suite were outside this validation pass.

CodeRabbit authentication returned `environment_unsupported`; no CodeRabbit result
is claimed. This is direct source review and regression evidence, not certification
of every inherited repository component or live production behavior.
