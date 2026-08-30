namespace Legacy.Maliev.NotificationService.Tests.Workflows;

public sealed class DependabotConfigurationContractTests
{
    [Fact]
    public void NuGetUpdater_ScansOnlyIndependentlyResolvableProjectDirectories()
    {
        var source = ReadNuGetBlock();

        Assert.DoesNotContain("    directory: /", source, StringComparison.Ordinal);
        foreach (var directory in new[]
                 {
            "/Legacy.Maliev.NotificationService.Application",
            "/Legacy.Maliev.NotificationService.Data",
            "/Legacy.Maliev.NotificationService.Domain",
                 })
        {
            Assert.Contains($"      - {directory}", source, StringComparison.Ordinal);
        }

        Assert.Equal(3, source.Split("\n      - /Legacy.Maliev.NotificationService.", StringSplitOptions.None).Length - 1);
    }

    private static string ReadNuGetBlock()
    {
        var source = File.ReadAllText(FindRepositoryFile(".github", "dependabot.yml"));
        var start = source.IndexOf("  - package-ecosystem: nuget", StringComparison.Ordinal);
        var end = source.IndexOf("  - package-ecosystem: docker", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        return source[start..end];
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
}
