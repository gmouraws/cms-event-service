using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CmsEventService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace CmsEventService.Tests;

public sealed class DatabaseFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private string? external;
    private readonly string database = "cms_test_" + Guid.NewGuid().ToString("N");
    public string Connection { get; private set; } = "";
    public async Task InitializeAsync()
    {
        external = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (external is null)
        {
            container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
            await container.StartAsync();
            Connection = container.GetConnectionString();
        }
        else
        {
            await using var admin = new NpgsqlConnection(external);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await command.ExecuteNonQueryAsync();
            Connection = new NpgsqlConnectionStringBuilder(external) { Database = database }.ConnectionString;
        }
        await using var db = new WriterContext(new DbContextOptionsBuilder<WriterContext>().UseNpgsql(Connection).Options);
        await db.Database.MigrateAsync();
    }
    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (container is not null) await container.DisposeAsync();
        if (external is not null)
        {
            await using var admin = new NpgsqlConnection(external);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }
}

public sealed class ApiFactory(string connection) : WebApplicationFactory<Program>
{
    public readonly Dictionary<string, string?> Settings = new()
    {
        ["ConnectionStrings:Writer"] = connection,
        ["Auth:Cms:Username"] = "test-cms-user", ["Auth:Cms:Password"] = Guid.NewGuid().ToString(),
        ["Auth:Consumer:Username"] = "test-consumer", ["Auth:Consumer:Password"] = Guid.NewGuid().ToString(),
        ["Auth:Admin:Username"] = "test-admin", ["Auth:Admin:Password"] = Guid.NewGuid().ToString()
    };
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseEnvironment("Testing").ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(Settings));
    public HttpClient Client(string? role = null)
    {
        var client = CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Settings[$"Auth:{role}:Username"]}:{Settings[$"Auth:{role}:Password"]}")));
        return client;
    }
}

public class ApiTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static string Id() => Guid.NewGuid().ToString("N");
    private static object E(string id, long version = 1, string type = "publish", int time = 1, string title = "Example")
        => type == "delete" ? new { id, type, timestamp = $"2024-01-01T00:00:{time:D2}Z" }
            : new { id, type, version, timestamp = $"2024-01-01T00:00:{time:D2}Z", payload = new { title } };
    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private static async Task Send(HttpClient cms, params object[] events)
        => Assert.Equal(HttpStatusCode.OK, (await cms.PostAsJsonAsync("/cms/events", events)).StatusCode);

    [Fact]
    public async Task Revisions_unpublish_corner_case_and_visibility()
    {
        using var app = new ApiFactory(fixture.Connection);
        using var cms = app.Client("Cms"); using var consumer = app.Client("Consumer"); using var admin = app.Client("Admin");
        var id = Id();
        await Send(cms, E(id, 4));
        await Send(cms, E(id, 7, time: 2, title: "Revision"));
        await Send(cms, E(id, 8, "unPublish", 3, "Never published"));
        Assert.Equal(HttpStatusCode.NotFound, (await consumer.GetAsync($"/entities/{id}")).StatusCode);
        var row = await Json(await admin.GetAsync($"/entities/{id}"));
        Assert.Equal(8, row.GetProperty("version").GetInt64());
        Assert.Equal("Never published", row.GetProperty("payload").GetProperty("title").GetString());
        Assert.False(row.GetProperty("published").GetBoolean());
        var unknown = Id();
        await Send(cms, E(unknown, 5, "unPublish"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/entities/{unknown}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await consumer.GetAsync($"/entities/{unknown}")).StatusCode);
        await Send(cms, E(id, 8, "publish", 4, "Never published"));
        Assert.Equal(HttpStatusCode.OK, (await consumer.GetAsync($"/entities/{id}")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_stale_conflict_and_batch_rollback()
    {
        using var app = new ApiFactory(fixture.Connection); using var cms = app.Client("Cms"); using var admin = app.Client("Admin");
        var id = Id(); var other = Id();
        await Send(cms, E(id, 2, time: 2));
        var outcomes = await Json(await cms.PostAsJsonAsync("/cms/events", new[] { E(id, 2, time: 2), E(id, 1) }));
        Assert.Equal("duplicate", outcomes.GetProperty("results")[0].GetProperty("outcome").GetString());
        Assert.Equal("stale", outcomes.GetProperty("results")[1].GetProperty("outcome").GetString());
        var response = await cms.PostAsJsonAsync("/cms/events", new[] { E(other), E(id, 2, "unPublish", 2) });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var result = await Json(response);
        Assert.False(result.GetProperty("committed").GetBoolean());
        Assert.Equal("rolledBack", result.GetProperty("results")[0].GetProperty("outcome").GetString());
        Assert.Equal(1, result.GetProperty("results")[1].GetProperty("index").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/entities/{other}")).StatusCode);
        Assert.True((await Json(await admin.GetAsync($"/entities/{id}"))).GetProperty("published").GetBoolean());
    }

    [Fact]
    public async Task Hard_delete_unknown_delete_delayed_events_and_id_reuse()
    {
        using var app = new ApiFactory(fixture.Connection); using var cms = app.Client("Cms"); using var admin = app.Client("Admin");
        var id = Id(); var unknown = Id();
        await Send(cms, E(id));
        await Send(cms, E(id, type: "delete", time: 3), E(unknown, type: "delete", time: 3));
        await Send(cms, E(id, 20, time: 2), E(unknown, 20, time: 2));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/entities/{id}")).StatusCode);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WriterContext>();
            Assert.False(await db.Entities.AnyAsync(x => x.Id == id));
            Assert.True(await db.Deletions.AnyAsync(x => x.Id == id));
        }
        await Send(cms, E(id, 1, time: 4));
        await Send(cms, E(id, 99, time: 2));
        Assert.Equal(1, (await Json(await admin.GetAsync($"/entities/{id}"))).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Disable_survives_publish_and_list_filters_in_database()
    {
        using var app = new ApiFactory(fixture.Connection); using var cms = app.Client("Cms");
        using var admin = app.Client("Admin"); using var consumer = app.Client("Consumer");
        var id = Id(); var hidden = Id();
        await Send(cms, E(id), E(hidden, type: "unPublish"));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/entities/{id}/disable", null)).StatusCode);
        await Send(cms, E(id, 2, time: 2));
        Assert.Equal(HttpStatusCode.NotFound, (await consumer.GetAsync($"/entities/{id}")).StatusCode);
        var all = (await Json(await admin.GetAsync("/entities?limit=100"))).EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
        var visible = (await Json(await consumer.GetAsync("/entities?limit=100"))).EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
        Assert.Contains(id, all); Assert.Contains(hidden, all); Assert.DoesNotContain(id, visible); Assert.DoesNotContain(hidden, visible);
        var first = await Json(await admin.GetAsync("/entities?limit=1"));
        Assert.Equal(all.Order(StringComparer.Ordinal).First(), first[0].GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await consumer.GetAsync("/entities?limit=101")).StatusCode);
    }

    [Theory]
    [InlineData(null)] [InlineData("Basic ???")] [InlineData("Bearer abc")]
    [InlineData("Basic dXNlcg==")] [InlineData("Basic Og==")] [InlineData("Basic /w==")]
    [InlineData("Basic dXNlcjpwYXNz")] [InlineData("Basic")]
    public async Task Malformed_authentication_is_401_with_challenge(string? header)
    {
        using var app = new ApiFactory(fixture.Connection); using var client = app.Client();
        if (header is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        var response = await client.GetAsync("/entities");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Basic", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Theory]
    [InlineData("Cms", 403, 200, 403)]
    [InlineData("Consumer", 200, 403, 403)]
    [InlineData("Admin", 200, 403, 404)]
    public async Task Role_boundaries(string role, int read, int ingest, int disable)
    {
        using var app = new ApiFactory(fixture.Connection); using var client = app.Client(role);
        Assert.Equal(read, (int)(await client.GetAsync("/entities")).StatusCode);
        Assert.Equal(ingest, (int)(await client.PostAsJsonAsync("/cms/events", new[] { E(Id()) })).StatusCode);
        Assert.Equal(disable, (int)(await client.PostAsync($"/entities/{Id()}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PutAsJsonAsync($"/entities/{Id()}", new { title = "edit" })).StatusCode);
    }

    [Fact]
    public async Task Mixed_identity_password_is_rejected()
    {
        using var app = new ApiFactory(fixture.Connection); using var client = app.Client();
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            app.Settings["Auth:Cms:Username"] + ":" + app.Settings["Auth:Consumer:Password"])));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/entities")).StatusCode);
    }

    [Theory]
    [InlineData("\"version\":\"2\",\"payload\":{}")]
    [InlineData("\"version\":0,\"payload\":{}")]
    [InlineData("\"version\":1,\"payload\":[]")]
    [InlineData("\"version\":1,\"payload\":{\"a\":1,\"a\":2}")]
    [InlineData("\"version\":1,\"payload\":{\"a\":\"\\u0000\"}")]
    public async Task Whole_batch_validation_precedes_mutation(string fields)
    {
        using var app = new ApiFactory(fixture.Connection); using var cms = app.Client("Cms"); using var admin = app.Client("Admin");
        var id = Id();
        var raw = "[" + JsonSerializer.Serialize(E(id)) + ", {\"id\":\"invalid\",\"type\":\"publish\",\"timestamp\":\"2024-01-01T00:00:00Z\"," + fields + "}]";
        var response = await cms.PostAsync("/cms/events", new StringContent(raw, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, (await Json(response)).GetProperty("results")[0].GetProperty("index").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/entities/{id}")).StatusCode);
    }

    [Fact]
    public async Task Request_limits_and_json_normalization()
    {
        using var app = new ApiFactory(fixture.Connection); using var cms = app.Client("Cms");
        Assert.Equal(HttpStatusCode.BadRequest, (await cms.PostAsJsonAsync("/cms/events", Enumerable.Range(0, 101).Select(_ => E(Id())))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cms.PostAsJsonAsync("/cms/events", new[] { E(Id(), title: new string('a', 66000)) })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await cms.PostAsync("/cms/events", new StringContent(new string('a', EventParser.MaxRequestBytes + 1), Encoding.UTF8, "application/json"))).StatusCode);
        var id = Id();
        var raw = $$$"""[{"id":" {{{id}}} ","type":"publish","version":1,"timestamp":"2024-01-01T00:00:00.0000001Z","payload":{"z":1e2,"a":"<b>Keep</b>"}}]""";
        Assert.Equal(HttpStatusCode.OK, (await cms.PostAsync("/cms/events", new StringContent(raw, Encoding.UTF8, "application/json"))).StatusCode);
        var retry = await Json(await cms.PostAsync("/cms/events", new StringContent(raw, Encoding.UTF8, "application/json")));
        Assert.Equal("duplicate", retry.GetProperty("results")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Concurrent_creation_updates_deletion_and_disable_are_serialized()
    {
        using var app = new ApiFactory(fixture.Connection); using var cms = app.Client("Cms"); using var admin = app.Client("Admin");
        var id = Id();
        var creates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cms.PostAsJsonAsync("/cms/events", new[] { E(id) })));
        Assert.All(creates, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var actions = Enumerable.Range(2, 12).Select(v => cms.PostAsJsonAsync("/cms/events", new[] { E(id, v, time: v) })).ToList();
        actions.Add(admin.PostAsync($"/entities/{id}/disable", null));
        var responses = await Task.WhenAll(actions);
        Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode));
        var row = await Json(await admin.GetAsync($"/entities/{id}"));
        Assert.Equal(13, row.GetProperty("version").GetInt64()); Assert.True(row.GetProperty("locallyDisabled").GetBoolean());
        var deletes = await Task.WhenAll(
            cms.PostAsJsonAsync("/cms/events", new[] { E(id, type: "delete", time: 30) }),
            cms.PostAsJsonAsync("/cms/events", new[] { E(id, 14, time: 14) }),
            admin.PostAsync($"/entities/{id}/disable", null));
        Assert.All(deletes, response => Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/entities/{id}")).StatusCode);
    }

    [Fact]
    public async Task Opposite_batch_id_order_does_not_deadlock()
    {
        using var app = new ApiFactory(fixture.Connection); using var cms = app.Client("Cms");
        var a = Id(); var b = Id();
        var responses = await Task.WhenAll(cms.PostAsJsonAsync("/cms/events", new[] { E(a), E(b) }),
            cms.PostAsJsonAsync("/cms/events", new[] { E(b), E(a) }));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task Openapi_is_available_to_consumers_and_describes_basic_auth()
    {
        using var app = new ApiFactory(fixture.Connection); using var consumer = app.Client("Consumer");
        var response = await consumer.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("basic", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reader_connection_rejects_raw_sql_writes()
    {
        using var app = new ApiFactory(fixture.Connection);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReadContext>();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM \"Entities\" WHERE false"));
        Assert.Equal("25006", error.SqlState);
    }

    [Fact]
    public async Task Repeated_id_in_batch_can_delete_and_recreate()
    {
        using var app = new ApiFactory(fixture.Connection); using var cms = app.Client("Cms"); using var admin = app.Client("Admin");
        var id = Id();
        await Send(cms, E(id), E(id, type: "delete", time: 2), E(id, 1, time: 3, title: "Reused"));
        var row = await Json(await admin.GetAsync($"/entities/{id}"));
        Assert.Equal("Reused", row.GetProperty("payload").GetProperty("title").GetString());
    }
}
