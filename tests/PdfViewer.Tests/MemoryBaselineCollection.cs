using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The memory baselines measure the whole test process. Run in parallel with other tests, they
/// count what those tests allocate as growth, so they run alone, after the parallel tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryBaselineCollection
{
    public const string Name = "Memory baselines";
}
