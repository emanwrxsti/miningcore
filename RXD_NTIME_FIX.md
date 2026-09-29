# Radiant RXD nTime / IceRiver Work-ID Fix

This source package includes a Radiant-only compatibility fix for IceRiver RXD miners that may update an internal `current_work_id` after a new `mining.notify`, then submit a solution from an older job under the newest job ID.

## Changes

- Parses the daemon's lowercase `mintime` field from `getblocktemplate`.
- Uses `mintime` as the lower nTime bound for Radiant RXD, falling back to `curtime` when `mintime` is unavailable.
- Retains exactly 50 active jobs per Radiant miner connection.
- Searches retained RXD jobs for the timestamp actually submitted by the miner.
- Retries a bounded set of likely prior jobs when the submitted job produces `ntime out of range` or `low difficulty share`.
- Registers the share only against the job that successfully validates it, preserving duplicate-share detection.
- Accepts either `maxActiveJobs` or the compatibility name `maxActiveJobsPerMiner` in Bitcoin-family pool configuration.

## Build

From the repository root:

```bash
cd src
dotnet build Miningcore/Miningcore.csproj -c Release
```

To place the compiled output in a separate directory:

```bash
cd src
dotnet build Miningcore/Miningcore.csproj -c Release -o /home/eman/Main-Stratum/build
```

Radiant is forced to 50 retained active jobs by code, so an RXD configuration cannot accidentally lower it.
