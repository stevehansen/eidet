using Xunit;

// Every class in this assembly boots its own embedded RavenDB. Run in parallel they compete for CPU and
// disk hard enough that timing-sensitive assertions fail intermittently — a different test each run, which
// is what made a genuinely broken vector arm look like "one flaky integration test". Embedded-server tests
// are not parallel-safe here, so the assembly runs one class at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
