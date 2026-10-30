namespace VisionIQ.Ingestor.Sources;

public enum CapabilityStatus
{
    /// <summary>The source is known to support this capability.</summary>
    Supported,

    /// <summary>The source is known not to support this capability.</summary>
    NotSupported,

    /// <summary>Not yet checked. Never treated as supported.</summary>
    Unknown,
}

/// <summary>
/// Records what each source supports. Anything not explicitly registered is Unknown,
/// so no code path can assume a capability it has not verified.
/// </summary>
public sealed class SourceCapabilityRegistry
{
    private readonly Dictionary<(SourceKind Source, string Capability), CapabilityStatus> entries = new();

    public void Register(SourceKind source, string capability, CapabilityStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        entries[(source, capability)] = status;
    }

    public CapabilityStatus Get(SourceKind source, string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return entries.TryGetValue((source, capability), out var status) ? status : CapabilityStatus.Unknown;
    }

    public bool IsSupported(SourceKind source, string capability) =>
        Get(source, capability) == CapabilityStatus.Supported;
}
