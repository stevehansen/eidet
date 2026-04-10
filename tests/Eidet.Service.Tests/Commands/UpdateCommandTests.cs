using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

public class UpdateCommandTests
{
    [Fact]
    public async Task GetLatestRelease_ReturnsNullOrValid()
    {
        // This test is non-deterministic (depends on GitHub API availability)
        // but it should never throw
        var result = await UpdateCommand.GetLatestReleaseAsync(CancellationToken.None);

        if (result.HasValue)
        {
            Assert.False(string.IsNullOrEmpty(result.Value.Version));
        }
        // null is also acceptable (network issues, rate limiting)
    }
}
