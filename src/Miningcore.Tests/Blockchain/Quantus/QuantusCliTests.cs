using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Quantus;
using Miningcore.Persistence.Model;
using Xunit;
namespace Miningcore.Tests.Blockchain.Quantus;

public sealed class UnixFactAttribute:FactAttribute
{
    public UnixFactAttribute(){if(OperatingSystem.IsWindows())Skip="Process fixtures require a POSIX shell";}
}
public class QuantusCliTests
{
    [UnixFact] public async Task PinnedCliReceivesExactBatchAndArgumentsAndCleansUp()
    {
        using var f=new Fixture("""
            printf '%s\n' "$@" > "$0.args"
            previous=''
            for arg do
              if [ "$previous" = '--batch-file' ]; then
                cp "$arg" "$0.batch"
                stat -c '%a' "$arg" > "$0.mode"
                printf '%s' "$arg" > "$0.path"
              fi
              previous="$arg"
            done
            printf 'Batch transaction finalized. Hash: 0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n'
            exit 2
            """);
        var hash=await f.Cli.SendBatchAsync(new[]{new Balance{Address=QuantusConsensusTests.Address,Amount=1.000000000001m}},default);
        Assert.Equal("0x"+new string('a',64),hash);var args=File.ReadAllLines(f.Path+".args");
        Assert.Contains("wallet name $(do-not-run)",args);Assert.Contains("--finalized-tx",args);Assert.Equal("600",File.ReadAllText(f.Path+".mode").Trim());
        Assert.False(File.Exists(File.ReadAllText(f.Path+".path")));
        using var json=JsonDocument.Parse(File.ReadAllText(f.Path+".batch"));Assert.Equal("1000000000001",json.RootElement[0].GetProperty("amount").GetString());
        Assert.Equal(QuantusConsensusTests.Address,json.RootElement[0].GetProperty("to").GetString());
    }
    [UnixFact] public async Task ChangedCliIsRejectedBeforeExecution()
    {
        using var f=new Fixture("touch \"$0.executed\"");File.AppendAllText(f.Path,"\n# changed\n");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Cli.QueryEventsAsync("0x"+new string('0',64),default));Assert.False(File.Exists(f.Path+".executed"));
    }
    [UnixFact] public async Task CancelledCliIsStoppedAndItsCapturedOutputIsNotExposed()
    {
        using var f=new Fixture("printf 'private-wallet-data\\n' >&2\nexec sleep 20");using var ct=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Cli.QueryEventsAsync("0x"+new string('0',64),ct.Token));
        Assert.DoesNotContain("private-wallet-data",error.ToString());
    }
    private sealed class Fixture:IDisposable
    {
        private readonly string directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"quantus-cli-test-"+Guid.NewGuid().ToString("N"));
        public string Path=>System.IO.Path.Combine(directory,"test-cli");public QuantusCli Cli{get;}
        public Fixture(string body)
        {
            Directory.CreateDirectory(directory);File.WriteAllText(Path,"#!/bin/sh\nset -eu\n"+body+"\n");File.SetUnixFileMode(Path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            Cli=new QuantusCli(new QuantusPaymentProcessingConfigExtra{CliPath=Path,CliSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path))),WalletName="wallet name $(do-not-run)",PasswordFile="/unused/password file",CliTimeoutSeconds=30},"ws://127.0.0.1:9944/");
        }
        public void Dispose()=>Directory.Delete(directory,true);
    }
}
