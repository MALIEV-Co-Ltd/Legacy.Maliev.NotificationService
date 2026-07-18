using System.Text.RegularExpressions;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.NotificationService.Tests.Workflows;

public sealed class WorkflowContractTests
{
    private static readonly string Workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "_build-and-test.yml"));
    private static readonly string ApiProject = File.ReadAllText(
        FindRepositoryFile("Legacy.Maliev.NotificationService.Api", "Legacy.Maliev.NotificationService.Api.csproj"));

    [Fact]
    public void BuildAndTest_SatisfiesStructuralContract()
    {
        WorkflowContractValidator.Validate(Workflow);
    }

    [Fact]
    public void BuildAndTest_RejectsSharedActionMainWithPinnedShaComment()
    {
        AssertMutationRejected(
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@6017816fa67f369d785ed30794f002cfd6299af7",
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@main # 6017816fa67f369d785ed30794f002cfd6299af7");
    }

    [Fact]
    public void BuildAndTest_RejectsCommentedDependencySha()
    {
        AssertMutationRejected(
            "ref: bcab875a7f703d1d9c2d535479e93653720eb62d",
            "ref: main # bcab875a7f703d1d9c2d535479e93653720eb62d");
    }

    [Fact]
    public void ApiProject_UsesOnlyLegacyServiceDefaults()
    {
        Assert.Contains("Legacy.Maliev.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Maliev.Aspire\\Maliev.Aspire.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Include=\"Maliev.Aspire.ServiceDefaults\"", ApiProject, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAndTest_RejectsJobPermissionEscalation()
    {
        AssertMutationRejected(
            "  validate:\n    name: validate",
            "  validate:\n    permissions:\n      contents: write\n    name: validate");
    }

    [Fact]
    public void BuildAndTest_RejectsSecretReferenceAnywhere()
    {
        var mutated = $"{Workflow}\n# ${{{{ secrets.X }}}}\n";

        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(mutated));
    }

    [Theory]
    [InlineData("${{secrets.X}}")]
    [InlineData("${{ secrets['X'] }}")]
    public void BuildAndTest_RejectsSecretExpressionInJobEnvironment(string expression)
    {
        AssertMutationRejected(
            "    env:\n      MalievWorkspaceRoot: ${{ github.workspace }}/.dependencies",
            $"    env:\n      MalievWorkspaceRoot: ${{{{ github.workspace }}}}/.dependencies\n      REVIEW_TOKEN: {expression}");
    }

    [Fact]
    public void BuildAndTest_RejectsWhitespaceObfuscatedRestoreCommand()
    {
        AssertMutationRejected(
            "          solution: Legacy.Maliev.NotificationService.slnx",
            "          solution: Legacy.Maliev.NotificationService.slnx\n      - run: dotnet  restore Legacy.Maliev.NotificationService.slnx");
    }

    [Fact]
    public void BuildAndTest_RejectsMissingLocalDependencyOptIn()
    {
        AssertMutationRejected(
            "          use-local-maliev-dependencies: 'true'\n",
            string.Empty);
    }

    [Fact]
    public void BuildAndTest_RejectsReservedGitHubActionsOverride()
    {
        AssertMutationRejected(
            "          use-local-maliev-dependencies: 'true'\n",
            "          use-local-maliev-dependencies: 'true'\n        env:\n          GITHUB_ACTIONS: 'false'\n");
    }

    [Fact]
    public void ImagePublication_IsManualValidatedOidcOnlyAndDoesNotDeploy()
    {
        var source = File.ReadAllText(FindRepositoryFile(".github", "workflows", "publish-image.yml"));

        Assert.Contains("workflow_dispatch:", source, StringComparison.Ordinal);
        Assert.Contains("confirm-publication:", source, StringComparison.Ordinal);
        Assert.Contains("type: boolean", source, StringComparison.Ordinal);
        Assert.Contains("default: false", source, StringComparison.Ordinal);
        Assert.Contains("validate:\n    uses: ./.github/workflows/_build-and-test.yml", Normalize(source), StringComparison.Ordinal);
        Assert.Contains("needs: validate", source, StringComparison.Ordinal);
        Assert.Contains("inputs.confirm-publication == true", source, StringComparison.Ordinal);
        Assert.Contains("contents: read", source, StringComparison.Ordinal);
        Assert.Contains("id-token: write", source, StringComparison.Ordinal);
        Assert.Matches(
            @"MALIEV-Co-Ltd/Legacy\.Maliev\.Workflows/\.github/workflows/publish-image\.yml@[0-9a-f]{40}",
            source);
        Assert.Contains("image: asia-southeast1-docker.pkg.dev/maliev-website/maliev-website-artifact-prod/legacy-maliev-notification-service", source, StringComparison.Ordinal);
        Assert.Contains("dockerfile: Legacy.Maliev.NotificationService.Api/Dockerfile", source, StringComparison.Ordinal);
        Assert.Contains("context: .", source, StringComparison.Ordinal);
        Assert.Contains("environment: legacy-image-publication", source, StringComparison.Ordinal);
        Assert.Contains("legacy-service-defaults-ref: bcab875a7f703d1d9c2d535479e93653720eb62d", source, StringComparison.Ordinal);
        Assert.Contains("compatibility-contracts-ref: 95c62eb6209411f5aada443b315447a2f76ca0cd", source, StringComparison.Ordinal);
        Assert.Contains("workload-identity-provider: ${{ vars.LEGACY_GCP_WORKLOAD_IDENTITY_PROVIDER }}", source, StringComparison.Ordinal);
        Assert.Contains("service-account: ${{ vars.LEGACY_GCP_ARTIFACT_REGISTRY_SERVICE_ACCOUNT }}", source, StringComparison.Ordinal);

        Assert.DoesNotContain("push:", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pull_request", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schedule:", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secrets.", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credentials_json", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kubectl", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("argocd", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gitops", source, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertMutationRejected(string original, string replacement)
    {
        Assert.Contains(original, Workflow, StringComparison.Ordinal);
        var mutated = Workflow.Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(mutated));
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{Path.Combine(segments)}'.");
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}

internal static partial class WorkflowContractValidator
{
    private const string CheckoutAction = "actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0";
    private const string SharedValidationAction = "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@6017816fa67f369d785ed30794f002cfd6299af7";

    public static void Validate(string workflow)
    {
        if (SecretExpression().IsMatch(workflow))
        {
            throw new InvalidOperationException("Workflow must not reference secrets.");
        }

        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(workflow));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Workflow must be valid YAML.", exception);
        }

        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("Workflow must contain exactly one mapping document.");
        }

        var workflowPermissions = RequireExactReadOnlyPermissions(RequireMapping(root, "permissions"), "workflow");
        var jobs = RequireMapping(root, "jobs");
        if (jobs.Children.Count != 1)
        {
            throw new InvalidOperationException("Workflow must define only the validate job.");
        }

        var validateJob = RequireMapping(jobs, "validate");
        var jobPermissionsNode = GetOptional(validateJob, "permissions");
        var effectiveJobPermissions = jobPermissionsNode is null
            ? workflowPermissions
            : RequireExactReadOnlyPermissions(RequireMapping(jobPermissionsNode, "jobs.validate.permissions"), "validate job");
        if (!effectiveJobPermissions.SequenceEqual(workflowPermissions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Validate job permissions must not differ from workflow permissions.");
        }

        RequireScalarValue(validateJob, "name", "validate");
        RejectDuplicatedValidationActionsAndCommands(jobs);

        var steps = RequireSequence(validateJob, "steps");
        if (steps.Children.Count != 4)
        {
            throw new InvalidOperationException("Validate job must contain exactly four caller-owned steps.");
        }

        ValidateStep(
            steps.Children[0],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[1],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.ServiceDefaults",
                ["ref"] = "bcab875a7f703d1d9c2d535479e93653720eb62d",
                ["path"] = ".dependencies/Legacy.Maliev.ServiceDefaults",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[2],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.CompatibilityContracts",
                ["ref"] = "95c62eb6209411f5aada443b315447a2f76ca0cd",
                ["path"] = ".dependencies/Legacy.Maliev.CompatibilityContracts",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[3],
            SharedValidationAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["solution"] = "Legacy.Maliev.NotificationService.slnx",
                ["use-local-maliev-dependencies"] = "true",
            });
    }

    private static IReadOnlyList<string> RequireExactReadOnlyPermissions(YamlMappingNode permissions, string scope)
    {
        if (permissions.Children.Count != 1)
        {
            throw new InvalidOperationException($"{scope} permissions must contain only contents: read.");
        }

        RequireScalarValue(permissions, "contents", "read");
        return ["contents:read"];
    }

    private static void ValidateStep(
        YamlNode node,
        string expectedAction,
        IReadOnlyDictionary<string, string> expectedInputs)
    {
        var step = RequireMapping(node, "workflow step");
        var allowedKeys = new HashSet<string>(["name", "uses", "with"], StringComparer.Ordinal);

        var actualKeys = step.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal);
        if (!actualKeys.SetEquals(allowedKeys))
        {
            throw new InvalidOperationException("Each workflow step must contain exactly name, uses, and with.");
        }

        var name = RequireScalar(GetRequired(step, "name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Workflow step name must not be empty.");
        }

        RequireScalarValue(step, "uses", expectedAction);
        var inputs = RequireMapping(step, "with");
        if (inputs.Children.Count != expectedInputs.Count)
        {
            throw new InvalidOperationException($"Action {expectedAction} has an unexpected input count.");
        }

        foreach (var expectedInput in expectedInputs)
        {
            RequireScalarValue(inputs, expectedInput.Key, expectedInput.Value);
        }

        var environment = GetOptional(step, "env");
        if (environment is not null)
        {
            throw new InvalidOperationException("Workflow action steps must not override reserved environment variables.");
        }

        if (expectedInputs.ContainsKey("use-local-maliev-dependencies"))
        {
            var useLocalDependencies = GetRequired(inputs, "use-local-maliev-dependencies") as YamlScalarNode;
            if (useLocalDependencies?.Style != ScalarStyle.SingleQuoted)
            {
                throw new InvalidOperationException("use-local-maliev-dependencies must use the single-quoted string value 'true'.");
            }
        }
    }

    private static void RejectDuplicatedValidationActionsAndCommands(YamlMappingNode jobs)
    {
        foreach (var jobNode in jobs.Children.Values.OfType<YamlMappingNode>())
        {
            var stepsNode = GetOptional(jobNode, "steps");
            if (stepsNode is not YamlSequenceNode steps)
            {
                continue;
            }

            foreach (var stepNode in steps.Children.OfType<YamlMappingNode>())
            {
                if (GetOptional(stepNode, "uses") is YamlScalarNode usesNode)
                {
                    var action = usesNode.Value ?? string.Empty;
                    if (action.StartsWith("actions/setup-dotnet@", StringComparison.OrdinalIgnoreCase)
                        || action.StartsWith("actions/cache@", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Caller duplicates shared action {action}.");
                    }
                }

                if (GetOptional(stepNode, "run") is YamlScalarNode runNode)
                {
                    RejectDuplicatedDotNetCommand(runNode.Value ?? string.Empty);
                }
            }
        }
    }

    private static void RejectDuplicatedDotNetCommand(string command)
    {
        var tokens = command
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim('"', '\'', ';', '&', '|').ToLowerInvariant())
            .Where(token => token.Length > 0)
            .ToArray();

        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!string.Equals(tokens[index], "dotnet", StringComparison.Ordinal))
            {
                continue;
            }

            var verb = tokens[index + 1];
            if (verb is "restore" or "build" or "test" or "format" or "list"
                || tokens[(index + 1)..].Any(token => token is "audit" or "--vulnerable"))
            {
                throw new InvalidOperationException($"Caller duplicates shared dotnet validation command: {verb}.");
            }
        }
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode parent, string key) =>
        RequireMapping(GetRequired(parent, key), key);

    private static YamlMappingNode RequireMapping(YamlNode node, string description)
    {
        return node as YamlMappingNode
            ?? throw new InvalidOperationException($"{description} must be a mapping.");
    }

    private static YamlSequenceNode RequireSequence(YamlMappingNode parent, string key)
    {
        return GetRequired(parent, key) as YamlSequenceNode
            ?? throw new InvalidOperationException($"{key} must be a sequence.");
    }

    private static void RequireScalarValue(YamlMappingNode parent, string key, string expected)
    {
        var actual = RequireScalar(GetRequired(parent, key));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{key} must equal '{expected}', but was '{actual}'.");
        }
    }

    private static string RequireScalar(YamlNode node)
    {
        return (node as YamlScalarNode)?.Value
            ?? throw new InvalidOperationException("Expected a scalar YAML value.");
    }

    private static YamlNode GetRequired(YamlMappingNode parent, string key)
    {
        return GetOptional(parent, key)
            ?? throw new InvalidOperationException($"Missing required YAML key '{key}'.");
    }

    private static YamlNode? GetOptional(YamlMappingNode parent, string key)
    {
        foreach (var child in parent.Children)
        {
            if (child.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return child.Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\$\{\{\s*secrets\s*(?:\.|\[)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretExpression();
}
