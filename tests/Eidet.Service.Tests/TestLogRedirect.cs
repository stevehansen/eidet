using System.Runtime.CompilerServices;
using Eidet.Core;

namespace Eidet.Service.Tests;

/// <summary>Keeps this test run out of the user's real eidet.log (#97).</summary>
internal static class TestLogRedirect
{
    [ModuleInitializer]
    internal static void Redirect() =>
        EidetLog.RedirectTo(Path.Combine(Path.GetTempPath(), "eidet-tests", $"{typeof(TestLogRedirect).Assembly.GetName().Name}-{Environment.ProcessId}.log"));
}
