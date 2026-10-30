using VisionIQ.Ingestor.Sources;

namespace VisionIQ.Ingestor.Tests;

public sealed class SourceCapabilityRegistryTests
{
    [Fact]
    public void Unregistered_capability_is_unknown_and_never_supported()
    {
        var registry = new SourceCapabilityRegistry();

        Assert.Equal(CapabilityStatus.Unknown, registry.Get(SourceKind.Confluence, "page-permissions"));
        Assert.False(registry.IsSupported(SourceKind.Confluence, "page-permissions"));
    }

    [Fact]
    public void Registered_capability_is_reported()
    {
        var registry = new SourceCapabilityRegistry();
        registry.Register(SourceKind.AzureDevOpsWorkItems, "wiql", CapabilityStatus.Supported);
        registry.Register(SourceKind.Jira, "wiql", CapabilityStatus.NotSupported);

        Assert.True(registry.IsSupported(SourceKind.AzureDevOpsWorkItems, "wiql"));
        Assert.Equal(CapabilityStatus.NotSupported, registry.Get(SourceKind.Jira, "wiql"));
    }

    [Fact]
    public void Blank_capability_name_is_rejected()
    {
        var registry = new SourceCapabilityRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(SourceKind.Jira, " ", CapabilityStatus.Supported));
    }
}
