using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Tests.Tools;

public class RestFormatterTests
{
    [Theory]
    [InlineData(ToolStatus.Ok, 200, 200)]
    [InlineData(ToolStatus.Ok, 201, 201)]
    [InlineData(ToolStatus.NotFound, 200, 404)]
    [InlineData(ToolStatus.BadRequest, 200, 400)]
    [InlineData(ToolStatus.Conflict, 200, 409)]
    [InlineData(ToolStatus.Rejected, 200, 422)]
    [InlineData(ToolStatus.Internal, 200, 500)]
    public void StatusCodeFor_MapsCorrectly(ToolStatus status, int success, int expected)
    {
        Assert.Equal(expected, RestFormatter.StatusCodeFor(status, success));
    }
}
