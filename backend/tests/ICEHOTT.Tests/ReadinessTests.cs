using System.Net;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Tests;

public sealed class ReadinessTests : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;

    public ReadinessTests(IcehottApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Ready_Reports_Active_Embedding_Profile()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal("ready", json.RootElement.GetProperty("status").GetString());
        Assert.True(json.RootElement.GetProperty("embeddingProfileReady").GetBoolean());
        Assert.Equal(
            EmbeddingProfileDefaults.LocalDeterministic64.Key,
            json.RootElement.GetProperty("embeddingProfile").GetString());
    }
}
