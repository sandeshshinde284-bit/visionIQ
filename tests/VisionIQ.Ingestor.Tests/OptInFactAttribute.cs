namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// A test that needs a real external resource (PostgreSQL, Cloud Storage). It is reported as
/// SKIPPED, never PASSED or FAILED, unless its environment flag is "1", so an ordinary
/// <c>dotnet test</c> is green without pretending the external check ran.
/// </summary>
public sealed class OptInFactAttribute : FactAttribute
{
    public OptInFactAttribute(string flag)
    {
        if (Environment.GetEnvironmentVariable(flag) != "1")
        {
            Skip = $"Opt-in external-resource test. Set {flag}=1 (and its documented settings) to run it.";
        }
    }
}
