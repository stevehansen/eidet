using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

public class UpdateCommandTests
{
    [Fact]
    public async Task GetLatestNuGetVersion_ReturnsNullOrValid()
    {
        // This test is non-deterministic (depends on NuGet API availability)
        // but it should never throw
        var result = await UpdateCommand.GetLatestNuGetVersionAsync(CancellationToken.None);

        if (result != null)
        {
            // Should be a valid semver-like string
            Assert.Matches(@"^\d+\.\d+\.\d+", result);
        }
        // null is also acceptable (network issues, package not yet published)
    }
}
