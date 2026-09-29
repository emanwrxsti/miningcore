-- Run as the Miningcore DB owner with payment processors stopped. Additive migration.
BEGIN;
CREATE TABLE IF NOT EXISTS quantus_reward_credits (
    genesishash TEXT NOT NULL CHECK (genesishash ~ '^0x[0-9a-f]{64}$'),
    blockhash TEXT NOT NULL CHECK (blockhash ~ '^0x[0-9a-f]{64}$'),
    poolid TEXT NOT NULL,
    blockid BIGINT NOT NULL UNIQUE,
    created TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (genesishash, blockhash)
);
CREATE TABLE IF NOT EXISTS quantus_payout_batches (
    id UUID PRIMARY KEY, poolid TEXT NOT NULL, walletkey TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('submitting', 'submitted', 'credited', 'cancelled')),
    recipients JSONB NOT NULL CHECK (jsonb_typeof(recipients) = 'array' AND jsonb_array_length(recipients) > 0),
    transactionhash TEXT CHECK (transactionhash ~ '^0x[0-9a-f]{64}$'),
    created TIMESTAMPTZ NOT NULL DEFAULT now(), updated TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (state NOT IN ('submitted', 'credited') OR transactionhash IS NOT NULL)
);
CREATE UNIQUE INDEX IF NOT EXISTS quantus_payout_one_open_wallet ON quantus_payout_batches(walletkey) WHERE state IN ('submitting', 'submitted');
CREATE UNIQUE INDEX IF NOT EXISTS quantus_payout_one_open_pool ON quantus_payout_batches(poolid) WHERE state IN ('submitting', 'submitted');
CREATE UNIQUE INDEX IF NOT EXISTS quantus_payout_unique_receipt ON quantus_payout_batches(walletkey, transactionhash) WHERE transactionhash IS NOT NULL;
COMMIT;
