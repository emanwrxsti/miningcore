namespace Miningcore.Blockchain.Quantus;

public class QuantusDaemonEndpointConfigExtra
{
    public int MaxJobAgeSeconds { get; set; } = 180;
    /// <summary>
    /// Private Quantus miner QUIC port.
    /// Defaults to 9833.
    /// </summary>
    public int? MinerPort { get; set; }

    /// <summary>
    /// Path to the node's private miner auth-token file.
    /// </summary>
    public string AuthTokenPath { get; set; }

    /// <summary>
    /// Path to the public SHA-256 fingerprint of
    /// the node's miner TLS certificate.
    /// </summary>
    public string TlsFingerprintPath { get; set; }

    /// <summary>
    /// Display network name.
    /// </summary>
    public string Network { get; set; }
}

public class QuantusPoolConfigExtra
{
    /// <summary>
    /// PFX certificate used by the public miner-facing QUIC server.
    /// Currently expected to have an empty password.
    /// </summary>
    public string TlsPfxPath { get; set; }
}

public class QuantusPaymentProcessingConfigExtra
{
    public string CliSha256 { get; set; }
    public string GenesisHash { get; set; }
    public bool EnablePayouts { get; set; }
    public int BatchSize { get; set; } = 50;
    public int CliTimeoutSeconds { get; set; } = 1800;
    public int CandidateSearchWindow { get; set; } = 32;
    public bool CollectRewards { get; set; }
    public string WormholeSecretFile { get; set; }
    public int WormholeIndex { get; set; }
    public string SubsquidUrl { get; set; } = "https://sub2.quantus.com/v1/graphql";
    /// <summary>
    /// Quantus CLI executable used for event queries and,
    /// reward collection and batch payouts.
    /// </summary>
    public string CliPath { get; set; } =
        "/root/.cargo/bin/quantus";

    /// <summary>
    /// Quantus wallet name controlled by the pool.
    /// </summary>
    public string WalletName { get; set; }

    /// <summary>
    /// chmod 600 file containing the wallet password.
    /// </summary>
    public string PasswordFile { get; set; }

    /// <summary>
    /// Public wormhole address receiving mining rewards.
    /// This is NOT the wormhole secret/inner hash.
    /// </summary>
    public string WormholeAddress { get; set; }

    /// <summary>
    /// WebSocket node endpoint used by quantus-cli.
    /// </summary>
    public string NodeUrl { get; set; }

    /// <summary>
    /// Pool safety confirmation depth before crediting a block.
    /// This is a Miningcore policy, not a Quantus protocol constant.
    /// </summary>
    public int? MinimumConfirmations { get; set; }
}
