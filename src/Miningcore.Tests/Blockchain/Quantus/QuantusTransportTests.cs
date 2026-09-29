using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.Internal;
using Miningcore.Blockchain.Quantus;
using Miningcore.Persistence.Model;
using Xunit;
namespace Miningcore.Tests.Blockchain.Quantus;

public sealed class QuicFactAttribute : FactAttribute
{
    public QuicFactAttribute()
    {
        if(!QuicConnection.IsSupported && Environment.GetEnvironmentVariable("QUANTUS_REQUIRE_INTEGRATION")!="1")
            Skip="Install libmsquic to run real QUIC integration tests";
    }
}
public class QuantusTransportTests
{
    [QuicFact] public async Task RealQuicHandlesStaleCancellationsClampsDifficultyAndInvalidatesWork()
    {
        Assert.True(QuicConnection.IsSupported);
        using var files=new Certificates();
        await using var server=new QuantusPoolQuicServer(1000,files.Pfx);
        var shares=Channel.CreateUnbounded<QuantusPoolShare>();server.ShareReceived+=s=>{shares.Writer.TryWrite(s);return Task.CompletedTask;};
        await server.SetCurrentJobAsync(QuantusConsensusTests.Job());await server.StartAsync(IPAddress.Loopback,0);
        await using var client=new QuantusQuicClient("127.0.0.1",server.LocalEndPoint.Port,files.Token,files.Pin);
        var jobs=Channel.CreateUnbounded<QuantusMiningRequest>();client.JobReceived+=j=>{jobs.Writer.TryWrite(j);return Task.CompletedTask;};
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(timeout.Token);var first=await jobs.Reader.ReadAsync(timeout.Token);Assert.Equal("1",first.Difficulty);
        await server.SetCurrentJobAsync(QuantusConsensusTests.Job());var current=await jobs.Reader.ReadAsync(timeout.Token);
        await client.SubmitResultAsync(new QuantusMiningResult{JobId=first.JobId,Status="cancelled"},timeout.Token);
        await Task.Delay(150,timeout.Token);Assert.False(jobs.Reader.TryRead(out _));
        await client.SubmitResultAsync(new QuantusMiningResult{JobId=current.JobId,Status="completed",Work=QuantusConsensusTests.Work},timeout.Token);
        var share=await shares.Reader.ReadAsync(timeout.Token);Assert.True(share.Validation.Accepted);Assert.True(share.Validation.BlockCandidate);
        Assert.NotEqual(current.JobId,(await jobs.Reader.ReadAsync(timeout.Token)).JobId);
        await server.SetCurrentJobAsync(null);
        try { await client.Completion.WaitAsync(timeout.Token); } catch(Exception ex) when(ex is not OperationCanceledException) { }
        Assert.False(client.IsConnected);
    }
    [QuicFact] public async Task RealQuicRejectsInvalidAddress()
    {
        Assert.True(QuicConnection.IsSupported);using var files=new Certificates();File.WriteAllText(files.Token,"invalid-address");
        await using var server=new QuantusPoolQuicServer(1,files.Pfx);await server.StartAsync(IPAddress.Loopback,0);
        await using var client=new QuantusQuicClient("127.0.0.1",server.LocalEndPoint.Port,files.Token,files.Pin);
        var received=false;client.JobReceived+=_=>{received=true;return Task.CompletedTask;};
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await client.ConnectAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(()=>client.Completion.WaitAsync(timeout.Token));Assert.False(received);Assert.False(client.IsConnected);
    }
    [QuicFact] public async Task RealQuicRequiresPinnedCertificate()
    {
        Assert.True(QuicConnection.IsSupported);using var files=new Certificates();File.WriteAllText(files.Pin,new string('0',64));
        await using var server=new QuantusPoolQuicServer(1,files.Pfx);await server.StartAsync(IPAddress.Loopback,0);
        await using var client=new QuantusQuicClient("127.0.0.1",server.LocalEndPoint.Port,files.Token,files.Pin);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await Assert.ThrowsAnyAsync<Exception>(()=>client.ConnectAsync(timeout.Token));
        Assert.False(client.IsConnected);
    }
    [Fact] public void RecursiveApplicationMappingsAreDepthBounded()
    {
        var config=new MapperConfiguration(c=>c.AddProfile<DepthProfile>());
        foreach(var map in config.Internal().GetAllTypeMaps().Where(x=>x.SourceType!=typeof(Newtonsoft.Json.Linq.JToken)&&x.SourceType!=typeof(BlockStatus)))
            Assert.Equal(64,map.MaxDepth);
        var root=new DepthSource();var cursor=root;for(var i=0;i<1000;i++){cursor.Next=new();cursor=cursor.Next;}
        var result=config.CreateMapper().Map<DepthTarget>(root);var count=0;while(result!=null){count++;result=result.Next;}
        Assert.InRange(count,1,64);
    }
    public class DepthSource { public DepthSource Next {get;set;} }
    public class DepthTarget { public DepthTarget Next {get;set;} }
    public class DepthProfile:AutoMapperProfile { public DepthProfile(){CreateMap<DepthSource,DepthTarget>();} }
    private sealed class Certificates:IDisposable
    {
        private readonly string path=Path.Combine(Path.GetTempPath(),"quantus-test-"+Guid.NewGuid().ToString("N"));
        public string Pfx=>Path.Combine(path,"test.pfx");public string Token=>Path.Combine(path,"token");public string Pin=>Path.Combine(path,"fingerprint");
        public Certificates()
        {
            Directory.CreateDirectory(path);using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
            File.WriteAllBytes(Pfx,cert.Export(X509ContentType.Pfx,""));File.WriteAllText(Pin,Convert.ToHexString(SHA256.HashData(cert.RawData)));
            File.WriteAllText(Token,QuantusConsensusTests.Address+".test-worker");
        }
        public void Dispose()=>Directory.Delete(path,true);
    }
}
