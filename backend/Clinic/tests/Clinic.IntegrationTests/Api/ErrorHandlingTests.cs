using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Clinic.IntegrationTests.Api;

public sealed class ErrorHandlingTests :
    IClassFixture<ErrorHandlingApiFactory>
{
    private readonly ErrorHandlingApiFactory _factory;

    public ErrorHandlingTests(ErrorHandlingApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnexpectedException_ReturnsSafeProblemDetails()
    {
        using var client = CreateClient();

        using var response =
            await client.GetAsync("/__tests/errors/unexpected");

        Assert.Equal(
            HttpStatusCode.InternalServerError,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(
            "INTERNAL_TEST_DETAIL_DO_NOT_EXPOSE",
            body);

        Assert.DoesNotContain("InvalidOperationException", body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(500, root.GetProperty("status").GetInt32());

        Assert.Equal(
            "unexpected_error",
            root.GetProperty("code").GetString());

        Assert.False(string.IsNullOrWhiteSpace(
            root.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task UnknownRoute_ReturnsNotFoundProblemDetails()
    {
        using var client = CreateClient();

        using var response =
            await client.GetAsync("/__tests/route-that-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(404, root.GetProperty("status").GetInt32());

        Assert.False(string.IsNullOrWhiteSpace(
            root.GetProperty("traceId").GetString()));
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient(
    new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

        client.DefaultRequestHeaders.Accept.ParseAdd(
            "application/problem+json");

        return client;
    }
}
