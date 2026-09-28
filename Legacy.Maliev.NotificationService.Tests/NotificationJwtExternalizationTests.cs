using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.NotificationService.Tests;

public sealed class NotificationJwtExternalizationTests
{
    private const string Issuer = "https://iam.maliev.com";
    private const string Audience = "maliev-services";
    private const string LegacyTestKey = "test-only-legacy-key-of-at-least-32-bytes"; // gitleaks:allow

    [Fact]
    public void NotificationApi_RequiresExternalPublicKeyWithoutStoredSymmetricKey()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.NotificationService.Api", "Program.cs"));
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root, "Legacy.Maliev.NotificationService.Api", "appsettings.json")));
        var jwt = settings.RootElement.GetProperty("Jwt");

        Assert.Contains("builder.AddJwtAuthentication();", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddJwtAuthenticationSymmetric", program, StringComparison.Ordinal);
        Assert.Equal(string.Empty, jwt.GetProperty("PublicKey").GetString());
        Assert.False(jwt.TryGetProperty("SecurityKey", out _));
        Assert.Equal(Issuer, jwt.GetProperty("Issuer").GetString());
        Assert.Equal(Audience, jwt.GetProperty("Audience").GetString());
    }

    [Fact]
    public void ProductionNotificationValidator_AcceptsOnlyConfiguredRs256IssuerAndAudience()
    {
        using var rsa = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:SecurityKey"] = LegacyTestKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
        });
        builder.AddJwtAuthentication();

        using var services = builder.Services.BuildServiceProvider();
        var parameters = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        Assert.True(parameters.ValidateIssuer);
        Assert.True(parameters.ValidateAudience);
        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.ValidateIssuerSigningKey);
        Assert.IsType<RsaSecurityKey>(parameters.IssuerSigningKey);
        Assert.Null(parameters.IssuerSigningKeys);
        Assert.Equal([SecurityAlgorithms.RsaSha256], parameters.ValidAlgorithms);
        Assert.Equal("employee:42", handler.ValidateToken(Token(rsa, SecurityAlgorithms.RsaSha256),
            parameters, out _).FindFirst(JwtRegisteredClaimNames.Sub)?.Value);

        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            Token(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(LegacyTestKey)), SecurityAlgorithms.HmacSha256),
            parameters, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            Token(rsa, SecurityAlgorithms.RsaSha384), parameters, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            Token(other, SecurityAlgorithms.RsaSha256), parameters, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            Token(rsa, SecurityAlgorithms.RsaSha256, issuer: "https://other.example"), parameters, out _));
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(
            Token(rsa, SecurityAlgorithms.RsaSha256, audience: "other-services"), parameters, out _));
    }

    [Fact]
    public void ProductionNotificationValidator_DoesNotFallBackToLegacySymmetricKey()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Jwt:PublicKey"] = "",
            ["Jwt:SecurityKey"] = LegacyTestKey,
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
        });

        Assert.Throws<InvalidOperationException>(() => builder.AddJwtAuthentication());
    }

    private static WebApplicationBuilder CreateBuilder(Dictionary<string, string?> configuration)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(configuration);
        return builder;
    }

    private static string Token(RSA signingKey, string algorithm, string? issuer = null, string? audience = null) =>
        Token(new RsaSecurityKey(signingKey), algorithm, issuer, audience);

    private static string Token(SecurityKey signingKey, string algorithm, string? issuer = null, string? audience = null)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(issuer ?? Issuer, audience ?? Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "employee:42")], now.AddMinutes(-1), now.AddMinutes(5),
            new SigningCredentials(signingKey, algorithm));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,
            "Legacy.Maliev.NotificationService.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("NotificationService repository root not found.");
    }
}
