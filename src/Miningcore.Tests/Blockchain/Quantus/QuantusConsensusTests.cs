using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Quantus;
using Miningcore.Persistence.Model;
using Xunit;

namespace Miningcore.Tests.Blockchain.Quantus;

public class QuantusConsensusTests
{
    public const string Address = "qzjTEwCDN6vQ6g4C5dq2NBJ2yxkZ9XtJzprerRgPpmj3ay16V";
    public static readonly string Work = string.Concat(Enumerable.Repeat("01", 64));
    public static QuantusJob Job(string difficulty = "1") => new(new QuantusMiningRequest { JobId = "test", MiningHash = new string('0',64), Difficulty = difficulty }, 100);

    [Fact] public void NativeHashMatchesPinnedVectorAndBigEndianTarget()
    {
        QuantusPow.EnsureAvailable();
        var hash = QuantusPow.ComputeHash(new string('0',64), Work);
        Assert.Equal("20F16094A3DD2D8A5CDD7D0B04932FF206752F5A5A9ECA790368C6A70977256AEF126A65838683A5BB5A7556157C783F283D163F98B752B587A462B35D839064", Convert.ToHexString(hash));
        Assert.True(QuantusPow.MeetsDifficulty(new string('0',64), Work, 7, out _, out var value));
        Assert.Equal(new BigInteger(hash, true, true), value);
        Assert.False(QuantusPow.MeetsDifficulty(new string('0',64), Work, 8, out _, out _));
    }
    [Fact] public void NetworkCandidateSurvivesPoolDifficultyAboveNetwork()
    {
        var accepted = Job().ProcessShare(Work, 1000);
        Assert.True(accepted.Accepted); Assert.True(accepted.BlockCandidate);
        Assert.False(Job("1000").ProcessShare(Work,1000).Accepted);
    }
    [Fact] public void ZeroNonceAndMalformedWorkAreRejected()
    {
        foreach(var work in new[]{new string('0',128), "xyz", "01", new string('g',128)})
            Assert.False(Job().ProcessShare(work,1).Accepted);
    }
    [Fact] public void NormalizedWorkCannotBeReplayed()
    {
        var job = Job(); Assert.True(job.ProcessShare("0x"+Work,1).Accepted);
        Assert.False(job.ProcessShare(Work.ToUpperInvariant(),1).Accepted);
    }
    [Fact] public async Task ConcurrentDuplicatesCreditOnlyOnce()
    {
        var job=Job();var results=await Task.WhenAll(Enumerable.Range(0,32).Select(_=>Task.Run(()=>job.ProcessShare(Work,1))));
        Assert.Equal(1,results.Count(x=>x.Accepted));
    }
    [Fact] public void InvalidDifficultyAndMiningHashesFailAtCreation()
    {
        foreach(var diff in new[]{"0","-1","n/a",(QuantusPow.MaxTarget+1).ToString()}) Assert.Throws<ArgumentException>(()=>Job(diff));
        Assert.Throws<FormatException>(()=>new QuantusJob(new QuantusMiningRequest{JobId="x",MiningHash=new string('g',64),Difficulty="1"}));
    }
    [Fact] public void AddressesRequireCorrectNetworkChecksumAndEncoding()
    {
        Assert.True(QuantusEncoding.IsAddress(Address));
        Assert.False(QuantusEncoding.IsAddress(Address[..^1]+"1"));
        Assert.False(QuantusEncoding.IsAddress("5GrwvaEF5zXb26Fz9rcQpDWS64cGWS6BqxENhCUYW1bqX8fs"));
        Assert.False(QuantusEncoding.IsAddress(""));
    }
    [Fact] public void CandidateIdentityKeepsFullEvidenceAndRejectsLegacyRows()
    {
        var candidate=Candidate(Header(100),100);var restored=QuantusCandidate.Parse(candidate.Serialize());
        Assert.Equal(candidate,restored);Assert.Equal(candidate.Id,restored.Id);
        Assert.Throws<FormatException>(()=>QuantusCandidate.Parse("null"));
        Assert.ThrowsAny<Exception>(()=>QuantusCandidate.Parse("legacy-nonce"));
    }
    [Fact] public void HeaderBindsHeightRootsDigestAndNonce()
    {
        var header=Header(100);var c=Candidate(header,100); Assert.True(header.Matches(c));
        Assert.False((header with{Number=101}).Matches(c));
        var changed=header.Fields.ToArray();changed[50]^=1;Assert.False((header with{Fields=changed}).Matches(c));
        var logs=header.Logs.Select(x=>x.ToArray()).ToArray();logs[^1][10]^=1;
        Assert.False((header with{Logs=logs}).Matches(c));Assert.True((header with{Logs=logs}).MatchesMiningHash(c));
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(new{number="0x64",parentHash=Hex(header.Fields[..32]),stateRoot=Hex(header.Fields[32..64]),extrinsicsRoot=Hex(header.Fields[64..96]),zkTreeRoot=Hex(header.Fields[96..]),digest=new{logs=header.Logs.Select(Hex)}}));
        Assert.Equal(header.Hash,QuantusHeader.Parse(json.RootElement).Hash);
    }
    [Fact] public void LegacyUpgradeDigestIsLimitedToHistoricalRange()
    {
        var h=Header(100);h=h with{Logs=new[]{h.Logs[0],new byte[]{8},h.Logs[1]}};
        Assert.True(h.Matches(Candidate(h,100)));
        var later=h with{Number=1_000_001};Assert.False(later.Matches(Candidate(later,1_000_001)));
    }
    [Fact] public async Task ClassificationProvesActualHeightAndQueriesExactHash()
    {
        var header=Header(102);var candidate=Candidate(header,100);var block=BlockFor(candidate);string queried=null;
        await QuantusBlockClassifier.ClassifyAsync(block,150,150,20,32,Address,(h,_)=>Task.FromResult(h==102?header:Header((uint)h)),(hash,_)=>{queried=hash;return Task.FromResult(Reward());},default);
        Assert.Equal(BlockStatus.Confirmed,block.Status);Assert.Equal(102UL,block.BlockHeight);Assert.Equal(5m,block.Reward);Assert.Equal(header.Hash,queried);Assert.Equal(candidate.Serialize(),block.TransactionConfirmationData);
    }
    [Fact] public async Task SameHeightAndWormholeCannotProveCandidateIdentity()
    {
        var block=BlockFor(Candidate(Header(100),100));var other=Header(100);var fields=other.Fields.ToArray();fields[0]^=1;other=other with{Fields=fields};
        await QuantusBlockClassifier.ClassifyAsync(block,150,150,20,1,Address,(h,_)=>Task.FromResult(h==100?other:Header((uint)h)),(_,_)=>throw new Exception("Must not query rewards without exact header"),default);
        Assert.Equal(BlockStatus.Pending,block.Status);Assert.Equal(0m,block.Reward);
    }
    [Theory][InlineData(105,100)][InlineData(150,99)]
    public async Task RewardsWaitForDepthAndFinality(int tip,int finalized)
    {
        var block=BlockFor(Candidate(Header(100),100));
        await QuantusBlockClassifier.ClassifyAsync(block,tip,(uint)finalized,20,32,Address,(h,_)=>Task.FromResult(Header((uint)h)),(_,_)=>throw new Exception("No reward before confirmation"),default);
        Assert.Equal(BlockStatus.Pending,block.Status);
    }
    [Fact] public async Task DifferentFinalizedSealProvesCandidateOrphaned()
    {
        var header=Header(100);var block=BlockFor(Candidate(header,100));var logs=header.Logs.Select(x=>x.ToArray()).ToArray();logs[^1][9]^=1;header=header with{Logs=logs};
        await QuantusBlockClassifier.ClassifyAsync(block,150,150,20,32,Address,(h,_)=>Task.FromResult(h==100?header:Header((uint)h)),(_,_)=>throw new Exception("No reward for different nonce"),default);
        Assert.Equal(BlockStatus.Orphaned,block.Status);Assert.Equal(0m,block.Reward);
    }
    [Fact] public void MissingAmbiguousAndForeignRewardsFailClosed()
    {
        Assert.Equal(5m,QuantusBlockClassifier.ParseReward(Reward(),Address));
        foreach(var events in new[]{"",Reward()+Reward(),Reward().Replace(Address,"wrong"),Reward().Replace("5000000000000","0")})
            Assert.Throws<InvalidDataException>(()=>QuantusBlockClassifier.ParseReward(events,Address));
    }
    [Fact] public void BatchAmountsUseExactSmallestUnitStrings()
    {
        var json=JsonSerializer.Serialize(QuantusCli.BatchTransfers(new[]{new Balance{Address=Address,Amount=1.000000000001m}}));
        using var document=JsonDocument.Parse(json);Assert.Equal("1000000000001",document.RootElement[0].GetProperty("amount").GetString());
        foreach(var amount in new[]{0m,-1m,0.0000000000001m})Assert.Throws<FormatException>(()=>QuantusCli.BatchTransfers(new[]{new Balance{Address=Address,Amount=amount}}));
    }
    [Fact] public void OnlyUniqueFinalizedReceiptPermitsPaymentDebit()
    {
        var hash="0x"+new string('a',64);var receipt="Batch transaction finalized. Hash: "+hash;
        Assert.Equal(hash,QuantusCli.ParseFinalizedHash(receipt));
        foreach(var text in new[]{"submitted "+hash,receipt+"\n"+receipt,"error"}) Assert.Throws<InvalidOperationException>(()=>QuantusCli.ParseFinalizedHash(text));
    }
    public static QuantusHeader Header(uint height)
    {
        var fields=Enumerable.Range(0,128).Select(x=>(byte)x).ToArray();
        var pre=new byte[]{6,112,111,119,95,128}.Concat(Enumerable.Repeat((byte)7,32)).ToArray();
        var seal=new byte[]{5,112,111,119,95,1,1}.Concat(Convert.FromHexString(Work)).ToArray();
        return new(height,fields,new[]{pre,seal});
    }
    public static QuantusCandidate Candidate(QuantusHeader h,long observed)
    {
        var logs=h.Logs[..^1];var digest=new[]{(byte)(logs.Length<<2)}.Concat(logs.SelectMany(x=>x)).ToArray();
        return new(1,Convert.ToHexString(QuantusPow.ComputeHeaderHash(h.Fields,h.Number,digest)).ToLowerInvariant(),Convert.ToHexString(h.Logs[^1][7..]).ToLowerInvariant(),observed);
    }
    public static Block BlockFor(QuantusCandidate c)=>new(){PoolId="quantus-tests",BlockHeight=(ulong)c.ObservedHeight,NetworkDifficulty=1,Status=BlockStatus.Pending,Hash=c.Id,TransactionConfirmationData=c.Serialize(),Miner=Address,Created=DateTime.UtcNow,Source="quantus-test"};
    private static string Hex(byte[] value)=>"0x"+Convert.ToHexString(value);
    private static string Reward()=>$"MiningRewards(MinerRewarded {{ miner: {Address}, reward: 5000000000000 }})";
}
