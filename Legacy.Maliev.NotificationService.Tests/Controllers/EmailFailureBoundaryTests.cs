using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace Legacy.Maliev.NotificationService.Tests.Controllers;

public sealed class EmailFailureBoundaryTests
{
    [Fact]
    public async Task UnhandledEmailFailure_EmitsIncidentWithoutRecipientOrExceptionDetails()
    {
        const string sensitive = "customer-secret@example.com";
        var logger = new CapturingLogger();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(value => value.EnvironmentName).Returns(Environments.Production);
        environment.SetupGet(value => value.ApplicationName).Returns("Legacy.Maliev.NotificationService.Api");
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/Emails/" + sensitive;
        context.Request.QueryString = new QueryString("?to=" + sensitive);
        context.TraceIdentifier = "email-incident-123";
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new Exception(sensitive), logger, environment.Object);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("email-incident-123", response.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(500, response.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal("An internal server error occurred", response.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("details").ValueKind);
        Assert.Contains("UnhandledRequestFailure", logger.Message, StringComparison.Ordinal);
        Assert.Contains("Path=/", logger.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, logger.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmailCorrelationScope_UsesFallbackRouteAndValidatedIdWithoutLiteralRecipient()
    {
        const string sensitive = "customer-secret@example.com";
        var logger = new CapturingScopeLogger();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/Emails/" + sensitive;
        context.Request.Headers["X-Correlation-ID"] = "email-incident-123";
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(context);

        Assert.Equal("email-incident-123", context.Response.Headers["X-Correlation-ID"].ToString());
        Assert.Equal("email-incident-123", logger.Scope["CorrelationId"]);
        Assert.Equal("/", logger.Scope["RouteTemplate"]);
        Assert.DoesNotContain("RequestPath", logger.Scope.Keys);
        Assert.DoesNotContain(sensitive, string.Join(' ', logger.Scope.Values), StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public string Message { get; private set; } = string.Empty;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
        }
    }

    private sealed class CapturingScopeLogger : ILogger<CorrelationIdMiddleware>
    {
        public IReadOnlyDictionary<string, object?> Scope { get; private set; } =
            new Dictionary<string, object?>();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            Scope = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(item => item.Key, item => item.Value)
                : throw new InvalidOperationException("The correlation scope is not structured.");
            return NoopScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
