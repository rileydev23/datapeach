using DataPeach.Core;
using DataPeach.Core.Db;

namespace DataPeach.Tests;

public class ConnectionFlagsTests
{
    [Fact]
    public void FlagsTypedByHandShowUpCheckedAndLeaveTheRest()
    {
        var (on, rest) = ConnectionFlags.Split(DbKind.SqlServer, "Trust Server Certificate=true;Connect Timeout=5");
        Assert.Equal(new[] { "trustCert" }, on);
        Assert.Equal("Connect Timeout=5", rest, ignoreCase: true);
    }

    [Fact]
    public void AFlagWithAnotherValueStaysInTheText()
    {
        var (on, rest) = ConnectionFlags.Split(DbKind.Postgres, "SSL Mode=VerifyFull");
        Assert.Empty(on);
        Assert.Equal("SSL Mode=VerifyFull", rest, ignoreCase: true);
    }

    [Fact]
    public void MergeThenSplitRoundTrips()
    {
        var merged = ConnectionFlags.Merge(DbKind.MySql, new[] { "publicKey", "zeroDate" }, "Connection Timeout=7");
        var (on, rest) = ConnectionFlags.Split(DbKind.MySql, merged);
        Assert.Equal(new[] { "publicKey", "zeroDate" }, on.Order());
        Assert.Equal("Connection Timeout=7", rest, ignoreCase: true);
    }

    [Fact]
    public void ACheckedFlagReplacesTheSameKeyInTheText()
    {
        var merged = ConnectionFlags.Merge(DbKind.SqlServer, new[] { "trustCert" }, "TrustServerCertificate=False");
        Assert.Equal("TrustServerCertificate=True", merged, ignoreCase: true);
    }

    [Fact]
    public void NothingCheckedKeepsTheTextAsTyped()
    {
        Assert.Equal("a=1;b=2", ConnectionFlags.Merge(DbKind.Oracle, Array.Empty<string>(), " a=1;b=2 "));
        Assert.Equal("", ConnectionFlags.Merge(DbKind.Oracle, Array.Empty<string>(), ""));
    }

    [Fact]
    public void AMalformedStringIsLeftWhole()
    {
        var (on, rest) = ConnectionFlags.Split(DbKind.SqlServer, "Encrypt='oops");
        Assert.Empty(on);
        Assert.Equal("Encrypt='oops", rest);
    }

    [Fact]
    public void EveryFlagHasALabel()
    {
        foreach (var k in DbKinds.All)
            foreach (var f in ConnectionFlags.For(k))
                Assert.True(Strings.All.ContainsKey("conn.flag." + f.Id), $"{k} {f.Id}");
    }
}
