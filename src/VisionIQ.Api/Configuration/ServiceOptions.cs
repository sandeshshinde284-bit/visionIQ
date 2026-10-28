using System.ComponentModel.DataAnnotations;

namespace VisionIQ.Api.Configuration;

/// <summary>General service settings, bound from the "Service" section of the configuration.</summary>
public sealed class ServiceOptions
{
    public const string SectionName = "Service";

    [Required]
    [MinLength(1)]
    public string Name { get; set; } = "VisionIQ.Api";
}
