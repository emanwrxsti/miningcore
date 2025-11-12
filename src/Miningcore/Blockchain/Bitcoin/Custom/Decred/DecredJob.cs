using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using Miningcore.Rpc;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using Contract = Miningcore.Contracts.Contract;
using Transaction = NBitcoin.Transaction;

namespace Miningcore.Blockchain.Bitcoin.Custom.Decred
{
    public class DecredJob : BitcoinJob
    {
    public override Task InitLegacy(BlockTemplate bt, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig,
        ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network,
        bool isPoS, double shareMultiplier, IHashAlgorithm coinbaseHasher,
        IHashAlgorithm headerHasher, IHashAlgorithm blockHasher, RpcClient rpc)
    {
            // Call base implementation to initialize core values but skip jobParams setup
            base.InitLegacy(bt, jobId, pc, extraPoolConfig, cc, clock, poolAddressDestination, network,
                isPoS, shareMultiplier, coinbaseHasher, headerHasher, blockHasher, rpc);

            var blockTemplate = BlockTemplate.Hex;
            
            // Calculate parameters based on dcrpool's format:
            // - prevBlock is bytes 8-72 of block header
            // - genTx1 is bytes 72-360 of the block 
            // - blockVersion is first 8 bytes
            // - nBits is bytes 232-240
            // - nTime is bytes 272-280
            var prevBlock = blockTemplate.Substring(8, 64);
            var genTx1 = blockTemplate.Substring(72, 288);    // 360 - 72 = 288 bytes
            var blockVersion = BlockTemplate.Version.ToStringHex8();
            var nBits = blockTemplate.Substring(232, 8);
            var nTime = blockTemplate.Substring(272, 8);

            // Format job parameters according to dcrpool's WorkNotification format:
            // [jobID, prevBlock, genTx1, "", [], blockVersion, nBits, nTime, cleanJob]
            jobParams = new object[]
            {
                JobId,           // jobID
                prevBlock,       // prevBlock (previous block hash)
                genTx1,         // genTx1 (generation tx part 1)
                "",             // genTx2 (empty for Decred)
                null, // merkle branches (empty array for Decred)
                blockVersion,    // blockVersion 
                nBits,          // nBits (target bits)
                true            // cleanJob
            };

            return Task.CompletedTask;
        }
        
    }
}
