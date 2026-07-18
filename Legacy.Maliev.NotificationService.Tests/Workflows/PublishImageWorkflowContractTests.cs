namespace Legacy.Maliev.NotificationService.Tests.Workflows;

public sealed class PublishImageWorkflowContractTests
{
    [Fact]
    public void PublishImage_IsManualOwnerGatedAndUsesDedicatedWif()
    {
        var source = File.ReadAllText(FindRepositoryFile(".github", "workflows", "publish-image.yml"));

        Assert.Contains("workflow_dispatch:", source, StringComparison.Ordinal);
        Assert.Contains("type: boolean", source, StringComparison.Ordinal);
        Assert.Contains("default: false", source, StringComparison.Ordinal);
        Assert.Contains("inputs.publish && github.ref == 'refs/heads/main'", source, StringComparison.Ordinal);
        Assert.Contains("id-token: write", source, StringComparison.Ordinal);
        Assert.Contains("providers/legacy-maliev-notification", source, StringComparison.Ordinal);
        Assert.Contains("github-actions-artifact-writer@maliev-website.iam.gserviceaccount.com", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-notification-service:${{ github.sha }}", source, StringComparison.Ordinal);
        Assert.Contains("actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0", source, StringComparison.Ordinal);
        Assert.Contains("google-github-actions/auth@7c6bc770dae815cd3e89ee6cdf493a5fab2cc093", source, StringComparison.Ordinal);
        Assert.Contains("google-github-actions/setup-gcloud@aa5489c8933f4cc7a4f7d45035b3b1440c9c10db", source, StringComparison.Ordinal);
        Assert.DoesNotContain("push:\n", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GITOPS_PAT", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kubectl", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("argocd", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{Path.Combine(segments)}'.");
    }
}
