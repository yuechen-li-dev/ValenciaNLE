using Valencia.Core;
using Xunit;

namespace Valencia.Core.Tests;

public sealed class ResultTests
{
    [Fact]
    public void CasesRemainDistinctWhenSuccessAndErrorHaveTheSameType()
    {
        Result<string, string> success = Result<string, string>.Success("value");
        Result<string, string> failure = Result<string, string>.Failure("reason");
        Assert.True(success is Ok<string> { Value: "value" });
        Assert.True(failure is Err<string> { Error: "reason" });
        Assert.False(success is Err<string>);
        Assert.False(failure is Ok<string>);
    }

    [Fact]
    public void ExactInt64ValuesDoNotPassThroughDouble()
    {
        Result<long, string> result = Result<long, string>.Success(9_007_199_254_740_993L);
        Assert.Equal(9_007_199_254_740_993L, result switch
        {
            Ok<long> value => value.Value,
            Err<string> => -1,
            null => -2
        });
    }

    [Fact]
    public void DefaultUnionIsExplicitlyUninitialized()
    {
        Result<int, string> result = default;
        Assert.True(result is null);
        Assert.False(result is Ok<int>);
        Assert.False(result is Err<string>);
    }
}
