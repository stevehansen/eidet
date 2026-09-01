using Eidet.Core.Configuration;
using Eidet.Service;

namespace Eidet.Service.Tests;

/// <summary>
/// The startup banner's nightly-AI line. Its job is to make an expensive setting legible: before it
/// existed, turning drift review off looked exactly like leaving it on, because the only enrichment
/// lines on the banner describe the backend and the enrich-on-store worker.
/// </summary>
public class NightlyModelWorkTests
{
    private static EnrichmentConfig Enabled(Action<EnrichmentConfig> configure)
    {
        var config = new EnrichmentConfig { Enabled = true };
        configure(config);
        return config;
    }

    [Fact]
    public void Enrichment_off_reports_off_whatever_the_stage_switches_say()
    {
        var config = new EnrichmentConfig { Enabled = false };
        config.DriftReview.Enabled = true;
        config.Reflection.Enabled = true;

        var (enabled, detail) = EidetHost.DescribeNightlyModelWork(config);

        Assert.False(enabled);
        Assert.Contains("enrichment disabled", detail);
    }

    [Fact]
    public void Both_stages_off_reports_off()
    {
        var config = Enabled(c => { c.DriftReview.Enabled = false; c.Reflection.Enabled = false; });

        var (enabled, detail) = EidetHost.DescribeNightlyModelWork(config);

        Assert.False(enabled);
        Assert.Contains("off", detail);
    }

    [Fact]
    public void Drift_review_reports_the_per_repo_batch_and_the_re_review_interval()
    {
        var config = Enabled(c =>
        {
            c.DriftReview.Enabled = true;
            c.DriftReview.NightlyBatch = 25;
            c.DriftReview.ReviewIntervalDays = 90;
            c.Reflection.Enabled = false;
        });

        var (enabled, detail) = EidetHost.DescribeNightlyModelWork(config);

        Assert.True(enabled);
        Assert.Contains("25/repo", detail);
        Assert.Contains("90d", detail);
    }

    [Fact]
    public void A_zero_interval_is_named_as_the_nightly_sweep_it_is()
    {
        // The load profile that ran for hours: every night, forever, on a corpus nobody had touched.
        var config = Enabled(c =>
        {
            c.DriftReview.Enabled = true;
            c.DriftReview.ReviewIntervalDays = 0;
            c.Reflection.Enabled = false;
        });

        var (enabled, detail) = EidetHost.DescribeNightlyModelWork(config);

        Assert.True(enabled);
        Assert.Contains("every night", detail);
    }

    [Fact]
    public void Both_stages_on_reports_both()
    {
        var config = Enabled(c => { c.DriftReview.Enabled = true; c.Reflection.Enabled = true; });

        var (enabled, detail) = EidetHost.DescribeNightlyModelWork(config);

        Assert.True(enabled);
        Assert.Contains("drift review", detail);
        Assert.Contains("reflection", detail);
    }
}
