using Miningcore.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin.Configuration;

public class BitcoinPoolConfigExtra
{
    public BitcoinAddressType AddressType { get; set; } = BitcoinAddressType.Legacy;

    public string BechPrefix { get; set; } = "bc";

    /// <summary>
    /// CashAddr prefix (e.g. bitcoincash, ecash, bitcoincashii)
    /// </summary>
    public string CashAddrPrefix { get; set; } = "bitcoincash";

    /// <summary>
    /// Maximum number of tracked jobs.
    /// </summary>
    public int? MaxActiveJobs { get; set; }

    /// <summary>
    /// Compatibility alias used by some existing pool configurations.
    /// </summary>
    [JsonProperty("maxActiveJobsPerMiner")]
    public int? MaxActiveJobsPerMiner { get; set; }

    /// <summary>
    /// Set to true to limit RPC commands to old Bitcoin command set
    /// </summary>
    public bool? HasLegacyDaemon { get; set; }

    /// <summary>
    /// Set to true to fall back to multiple sendtoaddress RPC calls for payments
    /// </summary>
    public bool HasBrokenSendMany { get; set; } = false;

    /// <summary>
    /// Arbitrary string appended at end of coinbase tx
    /// Overrides property of same name from BitcoinTemplate
    /// </summary>
    public string CoinbaseTxComment { get; set; }

    /// <summary>
    /// Blocktemplate stream published via ZMQ
    /// </summary>
    public ZmqPubSubEndpointConfig BtStream { get; set; }

    /// <summary>
    /// Custom Arguments for getblocktemplate RPC
    /// </summary>
    public JToken GBTArgs { get; set; }
}
