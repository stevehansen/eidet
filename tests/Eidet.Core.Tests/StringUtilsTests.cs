using Eidet.Core;

namespace Eidet.Core.Tests;

public class StringUtilsTests
{
    [Fact]
    public void Truncate_ShortString_Unchanged()
    {
        Assert.Equal("hello", StringUtils.Truncate("hello", 10));
    }

    [Fact]
    public void Truncate_ExactLength_Unchanged()
    {
        Assert.Equal("hello", StringUtils.Truncate("hello", 5));
    }

    [Fact]
    public void Truncate_LongString_TruncatedWithEllipsis()
    {
        var result = StringUtils.Truncate("hello world", 8);
        Assert.Equal("hello...", result);
        Assert.Equal(8, result.Length);
    }

    [Fact]
    public void Truncate_MinLength_Works()
    {
        var result = StringUtils.Truncate("abcdef", 4);
        Assert.Equal("a...", result);
    }
}
