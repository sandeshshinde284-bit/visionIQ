namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// A database integration test that is reported as SKIPPED (not passed) unless its environment
/// flag is "1". Mirrors VisionIQ.Api.Tests.DatabaseFactAttribute for the Ingestor test project.
/// </summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute(string flag)
    {
        if (Environment.GetEnvironmentVariable(flag) != "1")
        {
            Skip = $"Set {flag}=1 with a disposable PostgreSQL database to run this test.";
        }
    }
}
