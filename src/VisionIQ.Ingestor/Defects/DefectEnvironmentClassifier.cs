using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisionIQ.Ingestor.Defects;

/// <summary>
/// Provider-neutral view of a defect used for environment classification. Azure DevOps work items and
/// Jira issues both map into this shape; nothing here is specific to one tracker.
/// </summary>
public sealed record DefectEvidence(
    string Provider,
    string Project,
    string? Title,
    string? Description,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> PathSegments);

/// <summary>One source's contribution to a classification, kept as provenance.</summary>
public sealed record EnvironmentFinding(string Source, string Trust, string Raw, string Normalized);

/// <summary>
/// Optional enrichment: where a defect was found. <see cref="Normalized"/> is PROD, UAT, TEST, UNKNOWN or
/// CONFLICT. A PROD defect is NOT a confirmed production incident; this records environment only.
/// </summary>
public sealed record EnvironmentClassification(
    string Normalized,
    string Status,
    string? Raw,
    string Source,
    string Reason,
    string? RulesVersion,
    IReadOnlyList<EnvironmentFinding> Findings)
{
    public const string Prod = "PROD", Uat = "UAT", Test = "TEST", Unknown = "UNKNOWN", Conflict = "CONFLICT";

    /// <summary>Status values: verified, candidate, unknown, conflict.</summary>
    public const string Verified = "verified", Candidate = "candidate", Unresolved = "unknown", Conflicting = "conflict";

    public static EnvironmentClassification NotConfigured { get; } =
        new(Unknown, Unresolved, null, "none", "not_configured", null, []);

    internal static EnvironmentClassification UnknownBecause(string reason, string? version, IReadOnlyList<EnvironmentFinding> findings, string source = "none", string? raw = null) =>
        new(Unknown, Unresolved, raw, source, reason, version, findings);
}

/// <summary>One configured source, evaluated in the project's configured order.</summary>
public sealed class EnvironmentSourceRule
{
    /// <summary>field, labels, path, title or description.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>For type "field": the provider's field reference, e.g. "Custom.FoundInEnvironment" or "environment".</summary>
    public string? Name { get; init; }

    /// <summary>For type "labels": only labels/tags starting with this prefix (e.g. "env:") are read.</summary>
    public string? Prefix { get; init; }

    /// <summary>For "title"/"description": a regular expression with a named group "env" holding the raw value.</summary>
    public string? Pattern { get; init; }

    /// <summary>verified or candidate. Defaults: field/labels/path verified, title candidate. Description is always candidate.</summary>
    public string? Trust { get; init; }
}

/// <summary>A project's convention: ordered sources plus the raw values that map to PROD/UAT/TEST.</summary>
public sealed class ProjectEnvironmentRules
{
    public List<EnvironmentSourceRule> Sources { get; init; } = [];

    public Dictionary<string, string[]> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Classifies defects into PROD/UAT/TEST/UNKNOWN/CONFLICT with provenance. It never guesses and never blocks
/// ingestion: missing, uninterpretable or contradictory evidence produces UNKNOWN or CONFLICT, and a project
/// whose rules are invalid is classified UNKNOWN (rules_invalid) without affecting any other project.
/// </summary>
public sealed class DefectEnvironmentClassifier
{
    public static readonly string[] Providers = ["azure_devops", "jira"];
    private static readonly string[] Environments = [EnvironmentClassification.Prod, EnvironmentClassification.Uat, EnvironmentClassification.Test];
    private static readonly string[] SourceTypes = ["field", "labels", "path", "title", "description"];
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex FieldName = new(@"^[A-Za-z][A-Za-z0-9_\-]*(\.[A-Za-z0-9_\-]+)*$", RegexOptions.CultureInvariant, RegexTimeout);

    private readonly Dictionary<(string Provider, string Project), CompiledRules> projects = [];
    private readonly HashSet<(string Provider, string Project)> invalid = [];

    private DefectEnvironmentClassifier(IReadOnlyList<string> problems) => Problems = problems;

    /// <summary>Configuration problems found while loading; each affects only the project it names.</summary>
    public IReadOnlyList<string> Problems { get; }

    public static DefectEnvironmentClassifier None { get; } = new([]);

    /// <summary>
    /// Loads {"providers": {"azure_devops": {"projects": {"&lt;project&gt;": {"sources": [...], "values": {...}}}},
    /// "jira": {...}}}. Never throws for rule problems: an invalid project is reported in <see cref="Problems"/>
    /// and classified UNKNOWN (rules_invalid); malformed JSON disables classification with one problem.
    /// </summary>
    public static DefectEnvironmentClassifier FromJson(string json, IReadOnlyDictionary<string, IReadOnlyCollection<string>> approvedProjects)
    {
        ArgumentNullException.ThrowIfNull(approvedProjects);
        RulesDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<RulesDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException error)
        {
            return new DefectEnvironmentClassifier([$"Environment rules are not valid JSON (line {error.LineNumber + 1}); classification is disabled."]);
        }

        var problems = new List<string>();
        var classifier = new DefectEnvironmentClassifier(problems);
        foreach (var (provider, section) in document?.Providers ?? [])
        {
            var providerKey = provider.ToLowerInvariant();
            if (!Providers.Contains(providerKey))
            {
                problems.Add($"Environment rules name unknown provider '{provider}'; it is ignored.");
                continue;
            }

            approvedProjects.TryGetValue(providerKey, out var approved);
            foreach (var (project, rules) in section?.Projects ?? [])
            {
                var key = (providerKey, project);
                if (approved is null || !approved.Contains(project, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"Environment rules for {providerKey}/{project}: project is not an approved source project; ignored.");
                    continue;
                }

                try
                {
                    classifier.projects[key] = CompiledRules.Compile(rules ?? new ProjectEnvironmentRules());
                }
                catch (InvalidOperationException error)
                {
                    // Isolation: this project becomes UNKNOWN (rules_invalid); every other project still classifies.
                    classifier.invalid.Add(key);
                    problems.Add($"Environment rules for {providerKey}/{project} are invalid ({error.Message}); its defects are UNKNOWN until fixed.");
                }
            }
        }

        return classifier;
    }

    public EnvironmentClassification Classify(DefectEvidence defect)
    {
        ArgumentNullException.ThrowIfNull(defect);
        var key = (defect.Provider.ToLowerInvariant(), defect.Project);
        var match = projects.FirstOrDefault(pair => pair.Key.Provider == key.Item1 && string.Equals(pair.Key.Project, key.Project, StringComparison.OrdinalIgnoreCase));
        if (match.Value is not { } rules)
        {
            return invalid.Any(pair => pair.Provider == key.Item1 && string.Equals(pair.Project, key.Project, StringComparison.OrdinalIgnoreCase))
                ? EnvironmentClassification.UnknownBecause("rules_invalid", null, [])
                : EnvironmentClassification.NotConfigured;
        }

        return rules.Classify(defect);
    }

    private sealed class CompiledRules
    {
        private readonly List<(EnvironmentSourceRule Rule, string Trust, string Label, Regex? Pattern)> sources = [];
        private readonly Dictionary<string, string> lookup = new(StringComparer.OrdinalIgnoreCase);
        private string version = string.Empty;

        public static CompiledRules Compile(ProjectEnvironmentRules rules)
        {
            var compiled = new CompiledRules();
            if (rules.Sources.Count == 0)
            {
                throw new InvalidOperationException("no sources configured");
            }

            foreach (var source in rules.Sources)
            {
                var type = source.Type.Trim().ToLowerInvariant();
                if (!SourceTypes.Contains(type))
                {
                    throw new InvalidOperationException($"unknown source type '{source.Type}'");
                }

                var trust = (source.Trust ?? (type is "title" or "description" ? "candidate" : "verified")).Trim().ToLowerInvariant();
                if (trust is not ("verified" or "candidate"))
                {
                    throw new InvalidOperationException($"trust must be 'verified' or 'candidate', not '{source.Trust}'");
                }

                if (type == "description" && trust == "verified")
                {
                    throw new InvalidOperationException("description-based classification is always a candidate, never verified");
                }

                Regex? pattern = null;
                var label = type;
                switch (type)
                {
                    case "field":
                        if (string.IsNullOrWhiteSpace(source.Name) || !FieldName.IsMatch(source.Name))
                        {
                            throw new InvalidOperationException($"field source needs a valid field name, not '{source.Name}'");
                        }

                        label = "field:" + source.Name;
                        break;
                    case "labels" when source.Prefix is { Length: 0 }:
                        throw new InvalidOperationException("labels prefix is empty");
                    case "title" or "description":
                        if (string.IsNullOrWhiteSpace(source.Pattern))
                        {
                            throw new InvalidOperationException($"{type} source needs a pattern");
                        }

                        try
                        {
                            pattern = new Regex(source.Pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);
                        }
                        catch (ArgumentException)
                        {
                            throw new InvalidOperationException($"{type} pattern is not a valid regular expression");
                        }

                        if (!pattern.GetGroupNames().Contains("env"))
                        {
                            throw new InvalidOperationException($"{type} pattern needs a named group 'env'");
                        }

                        break;
                }

                compiled.sources.Add((source, trust, label, pattern));
            }

            foreach (var (key, values) in rules.Values)
            {
                var normalized = key.Trim().ToUpperInvariant();
                if (!Environments.Contains(normalized))
                {
                    throw new InvalidOperationException($"environment '{key}' must be PROD, UAT or TEST (UNKNOWN and CONFLICT are reserved)");
                }

                foreach (var raw in (values ?? []).Select(value => value?.Trim()))
                {
                    if (string.IsNullOrEmpty(raw))
                    {
                        throw new InvalidOperationException($"environment '{normalized}' has an empty value");
                    }

                    if (compiled.lookup.TryGetValue(raw, out var existing) && existing != normalized)
                    {
                        throw new InvalidOperationException($"value '{raw}' maps to both {existing} and {normalized}");
                    }

                    compiled.lookup[raw] = normalized;
                }
            }

            if (compiled.lookup.Count == 0)
            {
                throw new InvalidOperationException("no values configured");
            }

            compiled.version = Version(rules);
            return compiled;
        }

        public EnvironmentClassification Classify(DefectEvidence defect)
        {
            var findings = new List<EnvironmentFinding>();
            foreach (var (rule, trust, label, pattern) in sources)
            {
                // A field, a prefixed label or a pattern capture CLAIMS to be an environment, so an
                // unrecognised value is "unmapped". Plain labels and path segments are mostly unrelated
                // to environment: only those that match a configured value are findings.
                var claimsEnvironment = label.StartsWith("field:", StringComparison.Ordinal)
                    || label is "title" or "description"
                    || (label == "labels" && rule.Prefix is not null);
                foreach (var raw in RawValues(defect, rule, pattern))
                {
                    if (lookup.TryGetValue(raw, out var normalized))
                    {
                        findings.Add(new(label, trust, raw, normalized));
                    }
                    else if (claimsEnvironment)
                    {
                        findings.Add(new(label, trust, raw, "unmapped"));
                    }
                }
            }

            // Highest trust tier with any value decides; candidates never override verified evidence.
            foreach (var tier in new[] { "verified", "candidate" })
            {
                var tierFindings = findings.Where(finding => finding.Trust == tier).ToList();
                if (tierFindings.Count == 0)
                {
                    continue;
                }

                var unmapped = tierFindings.FirstOrDefault(finding => finding.Normalized == "unmapped");
                if (unmapped is not null)
                {
                    // A source we trust says something we cannot interpret: unknown, never a fallback guess.
                    return EnvironmentClassification.UnknownBecause("unmapped_value", version, findings, unmapped.Source, unmapped.Raw);
                }

                var distinct = tierFindings.Select(finding => finding.Normalized).Distinct(StringComparer.Ordinal).ToList();
                if (distinct.Count > 1)
                {
                    return new(EnvironmentClassification.Conflict, EnvironmentClassification.Conflicting,
                        string.Join("; ", tierFindings.Select(finding => $"{finding.Source}={finding.Raw}")),
                        string.Join(",", tierFindings.Select(finding => finding.Source).Distinct(StringComparer.Ordinal)),
                        tier == "verified" ? "sources_disagree" : "candidates_disagree", version, findings);
                }

                var primary = tierFindings[0];
                return new(distinct[0], tier == "verified" ? EnvironmentClassification.Verified : EnvironmentClassification.Candidate,
                    primary.Raw, primary.Source, "matched", version, findings);
            }

            return EnvironmentClassification.UnknownBecause("no_value", version, findings);
        }

        private static IEnumerable<string> RawValues(DefectEvidence defect, EnvironmentSourceRule rule, Regex? pattern)
        {
            switch (rule.Type.Trim().ToLowerInvariant())
            {
                case "field":
                    var field = defect.Fields.FirstOrDefault(pair => string.Equals(pair.Key, rule.Name, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
                    return string.IsNullOrEmpty(field) ? [] : [field];
                case "labels":
                    return defect.Labels
                        .Select(label => label.Trim())
                        .Where(label => rule.Prefix is null || label.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(label => rule.Prefix is null ? label : label[rule.Prefix.Length..].Trim())
                        .Where(label => label.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                case "path":
                    return defect.PathSegments.Select(segment => segment.Trim()).Where(segment => segment.Length > 0).ToList();
                default:
                    var text = rule.Type.Trim().Equals("title", StringComparison.OrdinalIgnoreCase) ? defect.Title : defect.Description;
                    if (pattern is null || string.IsNullOrEmpty(text))
                    {
                        return [];
                    }

                    try
                    {
                        return pattern.Match(text) is { Success: true } match && match.Groups["env"].Value.Trim() is { Length: > 0 } value
                            ? [value]
                            : [];
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return []; // pathological text: no finding rather than a stalled sync
                    }
            }
        }

        private static string Version(ProjectEnvironmentRules rules)
        {
            var canonical = JsonSerializer.Serialize(new
            {
                Sources = rules.Sources.Select(source => new { source.Type, source.Name, source.Prefix, source.Pattern, source.Trust }),
                Values = rules.Values
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new { Key = pair.Key.ToUpperInvariant(), Values = (pair.Value ?? []).Select(v => v.Trim()).OrderBy(v => v, StringComparer.OrdinalIgnoreCase) }),
            });
            return "env-rules-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];
        }
    }

    private sealed class RulesDocument
    {
        public Dictionary<string, ProviderSection?> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProviderSection
    {
        public Dictionary<string, ProjectEnvironmentRules?> Projects { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
