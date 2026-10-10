using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Legacy.Maliev.AuthService.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Each case owns actual ordinary Programs and fresh disposable storage.
public sealed class JoinedRecoveryTests
{
    private const string Prefix = "/bff/employee-recovery/";

    [Theory]
    [InlineData("password-reset", false, 201)]
    [InlineData("password-reset", true, 201)]
    [InlineData("email-confirmation", false, 201)]
    [InlineData("email-confirmation", true, 201)]
    [InlineData("password-reset", false, 400)]
    [InlineData("password-reset", true, 400)]
    [InlineData("email-confirmation", false, 400)]
    [InlineData("email-confirmation", true, 400)]
    public async Task NormalJoin_PreservesMailbox_AndConsumesChallengeExactlyOnce(
        string purpose, bool quoted, int providerStatus)
    {
        await using var fixture = await RecoveryJoinFixture.CreateAsync();
        await using var employees = fixture.Employees();
        await using var state = fixture.State();
        using var browser = fixture.Browser();
        var key = Guid.NewGuid().ToString("N");
        var email = quoted ? $"\"{key}@office\"@example.com" : $"{key}@example.com";
        var hasher = new PasswordHasher<LegacyIdentityRow>();
        var admin = new EmployeeIdentityAdminService(employees, hasher);
        var seedEmail = key + "@example.com";
        var result = await admin.CreateAsync(Random.Shared.Next(1, int.MaxValue),
            new(seedEmail, seedEmail, "original-password", false, null), fixture.Token);
        Assert.NotNull(result);
        // Represent a historical persisted mailbox exactly as the reviewed Auth fixture does.
        // Recovery HTTP, identity service, cryptography, and database remain real.
        if (quoted)
            await employees.Users.Where(x => x.Email == seedEmail).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Email, email).SetProperty(x => x.NormalizedEmail, email.ToUpperInvariant()), fixture.Token);
        var user = await employees.Users.AsNoTracking().SingleAsync(x => x.Email == email, fixture.Token);
        var original = IdentitySnapshot(user);
        Assert.Empty(await state.IdentityActionTokens.Where(x => x.IdentityId == user.Id).ToListAsync(fixture.Token));
        fixture.Provider.Status = providerStatus;
        await Csrf(browser, fixture.Token);

        using var requested = await browser.PostAsJsonAsync(Prefix + purpose + "/request", new { email }, fixture.Token);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var acceptedBody = await requested.Content.ReadAsStringAsync(fixture.Token);
        Assert.DoesNotContain("token", acceptedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Provider.Payloads.Count);
        var captured = fixture.Provider.Payloads[0];
        Assert.Equal(email, captured.GetProperty("to")[0].GetProperty("email").GetString());
        var html = captured.GetProperty("htmlContent").GetString()!;
        var links = Regex.Matches(html, "href=\"([^\"]+)\"");
        Assert.Equal(1, links.Count);
        var link = links[0];
        var callback = new Uri(WebUtility.HtmlDecode(link.Groups[1].Value));
        Assert.Equal(RecoveryJoinFixture.PublicOrigin.Authority, callback.Authority);
        Assert.Equal("https", callback.Scheme);
        Assert.Equal(purpose == "password-reset" ? "/Employees/ResetPassword" : "/Employees/EmailConfirmation", callback.AbsolutePath);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal(email, query["email"].ToString());
        var token = query["token"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var challenge = Assert.Single(await state.IdentityActionTokens.AsNoTracking()
            .Where(x => x.IdentityId == user.Id).ToListAsync(fixture.Token));
        Assert.Null(challenge.ConsumedAt);
        Assert.Null(challenge.FinalizedAt);
        Assert.Equal("employee-" + purpose, challenge.Purpose);
        Assert.Equal("service:legacy-intranet", challenge.OwnerSubject);
        Assert.Equal(email.ToUpperInvariant(), challenge.BoundNormalizedEmail);
        Assert.True(original == IdentitySnapshot(await employees.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id, fixture.Token)));
        Assert.Empty(await employees.RecoveryEffects.Where(x => x.IdentityId == user.Id).ToListAsync(fixture.Token));

        object completion = purpose == "password-reset"
            ? new { email, token, password = "replacement-password", confirmPassword = "replacement-password" }
            : new { email, token };
        using var completed = await browser.PostAsJsonAsync(Prefix + purpose + "/complete", completion, fixture.Token);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        var after = await employees.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id, fixture.Token);
        if (purpose == "password-reset")
        {
            Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(after, after.PasswordHash!, "replacement-password"));
            Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(after, after.PasswordHash!, "original-password"));
        }
        else Assert.True(after.EmailConfirmed);
        var consumed = await state.IdentityActionTokens.AsNoTracking().SingleAsync(x => x.Id == challenge.Id, fixture.Token);
        Assert.NotNull(consumed.ConsumedAt);
        Assert.NotNull(consumed.FinalizedAt);
        Assert.Equal(challenge.Id, consumed.EffectActionId);
        var effect = Assert.Single(await employees.RecoveryEffects.AsNoTracking().Where(x => x.IdentityId == user.Id).ToListAsync(fixture.Token));
        Assert.Equal(challenge.Id, effect.ActionId);

        using var replay = await browser.PostAsJsonAsync(Prefix + purpose + "/complete", completion, fixture.Token);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.True(IdentitySnapshot(after) == IdentitySnapshot(await employees.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id, fixture.Token)));
        var replayToken = await state.IdentityActionTokens.AsNoTracking().SingleAsync(x => x.Id == challenge.Id, fixture.Token);
        Assert.Equal(consumed.ConsumedAt, replayToken.ConsumedAt);
        Assert.Equal(consumed.FinalizedAt, replayToken.FinalizedAt);
        Assert.Single(await employees.RecoveryEffects.Where(x => x.IdentityId == user.Id).ToListAsync(fixture.Token));
        fixture.AssertRealJoin(purpose);
    }

    private static (string? PasswordHash, string? SecurityStamp, string? ConcurrencyStamp, bool EmailConfirmed)
        IdentitySnapshot(LegacyIdentityRow row) => (row.PasswordHash, row.SecurityStamp, row.ConcurrencyStamp, row.EmailConfirmed);

    private static async Task Csrf(HttpClient browser, CancellationToken token)
    {
        using var response = await browser.GetAsync("/bff/session", token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var csrfToken = body.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));
        browser.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrfToken);
    }
}
