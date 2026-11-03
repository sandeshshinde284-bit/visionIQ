namespace VisionIQ.Api.Tests;

/// <summary>
/// M-10: a database integration test that is reported as SKIPPED (not passed) unless its
/// environment flag is "1". CI sets the flags in the fresh-PostgreSQL job.
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
