using Npgsql;
using Xunit;

public sealed class HostPoolCleanupRegressionTests
{
    [Fact]
    public async Task CleanupRetainsEffectiveAuthPoolsAlongsideFixturePools()
    {
        await using var fixture = await RecoveryJoinFixture.CreateAsync();
        var identities = fixture.EffectivePoolsForRegression();
        Assert.Equal(3, identities.Length);
        foreach (var identity in identities)
        {
            var original = new NpgsqlConnectionStringBuilder(identity.FixtureConnection);
            var effective = new NpgsqlConnectionStringBuilder(identity.HostConnection);
            Assert.Equal(0, original.MinPoolSize);
            Assert.Equal(300, original.ConnectionIdleLifetime);
            Assert.Equal(2, effective.MinPoolSize);
            Assert.Equal(60, effective.ConnectionIdleLifetime);
            // Boolean assertions avoid retaining runtime connection strings or passwords in TRX.
            Assert.True(identity.FixtureConnection != identity.HostConnection);
            Assert.True(fixture.IsPoolRegistered(identity.FixtureConnection));
            Assert.True(fixture.IsPoolRegistered(identity.HostConnection));
        }
        await fixture.DisposeAsync();
        foreach (var identity in identities)
        {
            Assert.True(fixture.WasPoolCleared(identity.FixtureConnection));
            Assert.True(fixture.WasPoolCleared(identity.HostConnection));
        }
        Assert.True(fixture.AllRegisteredPoolsCleared);
        // ClearPool bookkeeping is not proof of backend connection/process absence.
        // The hosted supervisor must separately qualify native group/container absence.
    }
}
