using Eidet.Core;

namespace Eidet.Service.Tests.Api;

public class EidetApiServerTests
{
    [Fact]
    public void Version_IsSet()
    {
        Assert.False(string.IsNullOrEmpty(EidetVersion.Current));
        Assert.Matches(@"^\d+\.\d+\.\d+", EidetVersion.Current);
    }
}
