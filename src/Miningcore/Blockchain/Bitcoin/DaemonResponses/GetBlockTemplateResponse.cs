using Newtonsoft.Json;

namespace Miningcore.Blockchain.Bitcoin.DaemonResponses;

public class BitcoinBlockTransaction
{
    /// <summary>
    /// transaction data encoded in hexadecimal (byte-for-byte)
    /// </summary>
    public string Data { get; set; }

    /// <summary>
    /// transaction id encoded in little-endian hexadecimal
    /// </summary>
    public string TxId { get; set; }

    /// <summary>
    /// hash encoded in little-endian hexadecimal (including witness data)
    /// </summary>
    public string Hash { get; set; }

    /// <summary>
    /// The amount of the fee in BTC
    /// </summary>
    public decimal Fee { get; set; }
}

public class CoinbaseAux
{
    public string Flags { get; set; }
}

public class CoinbaseRequiredOutput
{
    [JsonProperty("value")]
    public long Value { get; set; }

    [JsonProperty("scriptPubKey")]
    public string ScriptPubKey { get; set; }
}

public class BlockTemplate
{
    /// <summary>
    /// The preferred block version
    /// </summary>
    public uint Version { get; set; }

    /// <summary>
    /// The hash of current highest block
    /// </summary>
    public string PreviousBlockhash { get; set; }

    /// <summary>
    /// Maximum allowable input to coinbase transaction, including the generation award and transaction fees (in Satoshis)
    /// </summary>
    public long CoinbaseValue { get; set; }

    /// <summary>
    /// The hash target
    /// </summary>
    public string Target { get; set; }

    /// <summary>
    /// A range of valid nonces
    /// </summary>
    public string NonceRange { get; set; }

    /// <summary>
    /// Current timestamp in seconds since epoch (Jan 1 1970 GMT)
    /// </summary>
    public uint CurTime { get; set; }

    /// <summary>
    /// Minimum valid timestamp accepted by the daemon for this template.
    /// Bitcoin-family daemons expose this field as lowercase "mintime".
    /// </summary>
    [JsonProperty("mintime")]
    public uint MinTime { get; set; }

    /// <summary>
    /// Compressed target of next block
    /// </summary>
    public string Bits { get; set; }

    /// <summary>
    /// The height of the next block
    /// </summary>
    public uint Height { get; set; }

    /// <summary>
    /// Contents of non-coinbase transactions that should be included in the next block
    /// </summary>
    public BitcoinBlockTransaction[] Transactions { get; set; }

    /// <summary>
    /// Data that should be included in the coinbase's scriptSig content
    /// </summary>
    public CoinbaseAux CoinbaseAux { get; set; }

    /// <summary>
    /// SegWit
    /// </summary>
    [JsonProperty("default_witness_commitment")]
    public string DefaultWitnessCommitment { get; set; }

    /// <summary>
    /// Elektron Net: exact scriptSig supplied by getblocktemplate.
    /// Must not be modified or extended.
    /// </summary>
    [JsonProperty("coinbase_script_sig_prefix")]
    public string CoinbaseScriptSigPrefix { get; set; }

    /// <summary>
    /// Elektron Net: required outputs supplied by getblocktemplate.
    /// Must be appended verbatim and in the supplied order.
    /// </summary>
    [JsonProperty("coinbase_required_outputs")]
    public CoinbaseRequiredOutput[] CoinbaseRequiredOutputs { get; set; }

    /// <summary>
    /// CommunityAutonomous
    /// </summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public string CommunityAutonomousAddress { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public long CommunityAutonomousValue { get; set; }

    [JsonExtensionData]
    public IDictionary<string, object> Extra { get; set; }
}
