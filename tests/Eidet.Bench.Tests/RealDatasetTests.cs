namespace Eidet.Bench.Tests;

/// <summary>
/// Real-run scaffold, excluded from CI (<c>--filter "Category!=RealRun"</c>) and skipped (early
/// return, the repo's idiom) when the dataset isn't present. Phase 0 only pins the Phase 1
/// adapter's on-disk contract: point <c>EIDET_SWEBENCH_DATA</c> at a local download of
/// https://huggingface.co/datasets/jiayuanz3/SWEContextBench — parquet files, with the Lite
/// head-to-head slice distinguished by filename.
/// </summary>
public class RealDatasetTests
{
    [Fact]
    [Trait("Category", "RealRun")]
    public void RealDatasetLayout_HasParquetFiles()
    {
        var root = Environment.GetEnvironmentVariable("EIDET_SWEBENCH_DATA");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return; // dataset not downloaded — skip (never fabricate)

        var parquet = Directory.GetFiles(root, "*.parquet", SearchOption.AllDirectories);
        Assert.NotEmpty(parquet);
    }
}
