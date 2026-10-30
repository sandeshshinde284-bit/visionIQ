using System.Data.Common;
using System.Globalization;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Configuration;

public enum CheckpointStoreKind
{
    Postgres,
    File,
}

/// <summary>Database connection for checkpoints, using the API role. The password never appears in <see cref="ToString"/>.</summary>
public sealed class DatabaseSettings
{
    public required string Host { get; init; }

    public int Port { get; init; } = 5432;

    public required string Name { get; init; }

    public required string User { get; init; }

    public string? Password { get; init; }

    public string SslMode { get; init; } = "disable";

    /// <summary>An Npgsql connection string, with every value quoted safely.</summary>
    public string ConnectionString
    {
        get
        {
            var builder = new DbConnectionStringBuilder
            {
                ["Host"] = Host,
                ["Port"] = Port.ToString(CultureInfo.InvariantCulture),
                ["Database"] = Name,
                ["Username"] = User,
                ["SSL Mode"] = SslMode.ToLowerInvariant() switch
                {
                    "allow" => "Allow",
                    "prefer" => "Prefer",
                    "require" => "Require",
                    "verify-ca" => "VerifyCA",
                    "verify-full" => "VerifyFull",
                    _ => "Disable",
                },
            };
            if (Password is not null)
            {
                builder["Password"] = Password;
            }

            return builder.ConnectionString;
        }
    }

    public override string ToString() => $"{User}@{Host}:{Port.ToString(CultureInfo.InvariantCulture)}/{Name}";
}

/// <summary>Everything the ingestor needs, read from the .env keys. Problems are reported together, never with values.</summary>
public sealed class IngestorSettings
{
    public required AdoOptions Ado { get; init; }

    public DateTimeOffset? Since { get; init; }

    public required string OutputDirectory { get; init; }

    public CheckpointStoreKind CheckpointStore { get; init; }

    public DatabaseSettings? Database { get; init; }

    /// <summary>Null when Confluence is not configured.</summary>
    public ConfluenceOptions? Confluence { get; init; }

    public static IngestorSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var problems = new List<string>();

        string? Get(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        Uri? organization = null;
        if (Get("ADO_ORG_URL") is not { } organizationText)
        {
            problems.Add("ADO_ORG_URL is empty (for example https://dev.azure.com/your-organization).");
        }
        else if (!Uri.TryCreate(organizationText, UriKind.Absolute, out organization) || organization.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add("ADO_ORG_URL must be an https:// address, for example https://dev.azure.com/your-organization.");
        }

        var projects = SplitList(Get("ADO_PROJECTS"));
        if (projects.Count == 0)
        {
            problems.Add("ADO_PROJECTS is empty (comma-separated project names).");
        }

        var token = Get("ADO_READ_PAT");
        if (token is null)
        {
            problems.Add("ADO_READ_PAT is empty (a read-only personal access token).");
        }

        var pipelineIds = new List<int>();
        foreach (var item in SplitList(Get("ADO_PIPELINE_IDS")))
        {
            if (int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                pipelineIds.Add(id);
            }
            else
            {
                problems.Add("ADO_PIPELINE_IDS must contain pipeline numbers separated by commas.");
                break;
            }
        }

        DateTimeOffset? since = null;
        if (Get("INGESTION_SINCE") is { } sinceText)
        {
            if (DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                since = parsed;
            }
            else
            {
                problems.Add("INGESTION_SINCE must be a date such as 2024-01-01.");
            }
        }

        var store = CheckpointStoreKind.Postgres;
        switch ((Get("CHECKPOINT_STORE") ?? "postgres").ToLowerInvariant())
        {
            case "postgres":
                break;
            case "file":
                store = CheckpointStoreKind.File;
                break;
            default:
                problems.Add("CHECKPOINT_STORE must be postgres or file.");
                break;
        }

        DatabaseSettings? database = null;
        if (store == CheckpointStoreKind.Postgres)
        {
            var user = Get("DB_API_USER");
            var password = Get("DB_API_PASSWORD");
            if (user is null)
            {
                problems.Add("DB_API_USER is empty (needed for CHECKPOINT_STORE=postgres).");
            }

            if (password is null)
            {
                problems.Add("DB_API_PASSWORD is empty (needed for CHECKPOINT_STORE=postgres).");
            }

            var port = 5432;
            if (Get("DB_PORT") is { } portText
                && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            {
                problems.Add("DB_PORT must be a number between 1 and 65535.");
                port = 5432;
            }

            database = new DatabaseSettings
            {
                Host = Get("DB_HOST") ?? "localhost",
                Port = port,
                Name = Get("DB_NAME") ?? "visioniq",
                User = user ?? string.Empty,
                Password = password,
                SslMode = Get("DB_SSL_MODE") ?? "disable",
            };
        }

        var confluence = ReadConfluence(Get, problems);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }

        return new IngestorSettings
        {
            Ado = new AdoOptions
            {
                OrganizationUrl = organization!,
                Projects = projects,
                Repositories = SplitList(Get("ADO_REPOS")),
                PipelineIds = pipelineIds,
                PersonalAccessToken = token!,
            },
            Since = since,
            OutputDirectory = Get("INGESTION_OUTPUT_DIR") ?? Path.Combine("artifacts", "ingestion"),
            CheckpointStore = store,
            Database = database,
            Confluence = confluence,
        };
    }

    private static ConfluenceOptions? ReadConfluence(Func<string, string?> get, List<string> problems)
    {
        var baseText = get("CONFLUENCE_BASE_URL");
        var spaces = SplitList(get("CONFLUENCE_SPACE_KEYS"));
        var token = get("CONFLUENCE_API_TOKEN");
        if (baseText is null && spaces.Count == 0 && token is null)
        {
            return null;
        }

        var before = problems.Count;
        Uri? baseUrl = null;
        if (baseText is null || !Uri.TryCreate(baseText, UriKind.Absolute, out baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add("CONFLUENCE_BASE_URL must be an https:// address, for example https://your-site.atlassian.net/wiki.");
        }

        if (spaces.Count == 0)
        {
            problems.Add("CONFLUENCE_SPACE_KEYS is empty (comma-separated keys of the approved spaces).");
        }

        if (token is null)
        {
            problems.Add("CONFLUENCE_API_TOKEN is empty.");
        }

        var deployment = ConfluenceDeployment.DataCenter;
        switch (get("CONFLUENCE_DEPLOYMENT")?.ToLowerInvariant())
        {
            case null:
                if (baseUrl is not null)
                {
                    deployment = ConfluenceOptions.Infer(baseUrl);
                }

                break;
            case "cloud":
                deployment = ConfluenceDeployment.Cloud;
                break;
            case "datacenter":
            case "server":
                deployment = ConfluenceDeployment.DataCenter;
                break;
            default:
                problems.Add("CONFLUENCE_DEPLOYMENT must be cloud or datacenter.");
                break;
        }

        var email = get("CONFLUENCE_USER_EMAIL");
        if (deployment == ConfluenceDeployment.Cloud && email is null)
        {
            problems.Add("CONFLUENCE_USER_EMAIL is empty (Confluence Cloud signs in with an email address and an API token).");
        }

        return problems.Count > before
            ? null
            : new ConfluenceOptions
            {
                BaseUrl = baseUrl!,
                SpaceKeys = spaces,
                Deployment = deployment,
                UserEmail = email,
                ApiToken = token!,
            };
    }

    private static List<string> SplitList(string? text) =>
        (text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
