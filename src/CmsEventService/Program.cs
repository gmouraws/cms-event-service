using System.Security.Claims;
using System.Text.Json;
using CmsEventService;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = EventParser.MaxRequestBytes);
builder.Services.AddDbContext<WriterContext>((services, o) => o.UseNpgsql(
    services.GetRequiredService<IConfiguration>().GetConnectionString("Writer")
    ?? throw new InvalidOperationException("ConnectionStrings:Writer is required.")));
builder.Services.AddDbContext<ReadContext>((services, o) =>
{
    var config = services.GetRequiredService<IConfiguration>();
    var readConnection = new NpgsqlConnectionStringBuilder(config.GetConnectionString("Reader") ?? config.GetConnectionString("Writer")
        ?? throw new InvalidOperationException("ConnectionStrings:Writer is required."));
    readConnection.Options = (readConnection.Options + " -c default_transaction_read_only=on").Trim();
    o.UseNpgsql(readConnection.ConnectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});
builder.Services.AddSingleton<Accounts>();
builder.Services.AddScoped<EventProcessor>();
builder.Services.AddAuthentication("Basic").AddScheme<AuthenticationSchemeOptions, BasicHandler>("Basic", _ => { });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Cms", p => p.RequireRole("Cms"));
    o.AddPolicy("Read", p => p.RequireRole("Consumer", "Admin"));
    o.AddPolicy("Admin", p => p.RequireRole("Admin"));
});
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    document.Components ??= new();
    document.Components.SecuritySchemes["Basic"] = new() { Type = SecuritySchemeType.Http, Scheme = "basic" };
    document.SecurityRequirements.Add(new() { [new OpenApiSecurityScheme { Reference = new() { Type = ReferenceType.SecurityScheme, Id = "Basic" } }] = [] });
    return Task.CompletedTask;
}));
// EF error messages can contain database values. Application logs record safe failure types instead.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Update", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Query", LogLevel.None);
var app = builder.Build();
if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<WriterContext>().Database.MigrateAsync();
    return;
}
_ = app.Services.GetRequiredService<Accounts>();
app.Use(async (context, next) =>
{
    using var scope = app.Logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = context.TraceIdentifier });
    context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        app.Logger.LogError("Request failed: {FailureType}", ex.GetType().Name);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "Request could not be completed. Retry safely.", correlationId = context.TraceIdentifier });
        }
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi().RequireAuthorization("Read");
app.MapGet("/health/ready", async (ReadContext db, CancellationToken ct) =>
{
    await db.Entities.Select(x => x.Id).Take(1).ToListAsync(ct);
    return Results.Ok(new { status = "ready" });
});
app.MapPost("/cms/events", async (HttpRequest request, EventProcessor processor, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!request.HasJsonContentType()) return Results.StatusCode(415);
    try
    {
        // Also enforce the cap under TestServer and proxies, including chunked bodies.
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, ct)) != 0)
        {
            if (body.Length + count > EventParser.MaxRequestBytes)
            {
                logger.LogWarning("CMS validation failed: request too large");
                return Results.StatusCode(413);
            }
            body.Write(buffer, 0, count);
        }
        using var json = JsonDocument.Parse(body.ToArray(), new() { MaxDepth = 32 });
        var events = EventParser.Parse(json.RootElement);
        var result = await processor.Process(events, ct);
        return Results.Json(result, statusCode: result.Committed ? 200 : 409);
    }
    catch (InvalidBatchException ex)
    {
        foreach (var error in ex.Errors) logger.LogWarning("CMS validation failed at {Index}: {Reason}", error.Index, error.Reason);
        return Results.BadRequest(new { committed = false, results = ex.Errors });
    }
    catch (JsonException)
    {
        logger.LogWarning("CMS validation failed: malformed JSON or depth exceeded");
        return Results.BadRequest(new { committed = false, results = new[] { new EventResult(-1, "invalid", "Malformed JSON or depth exceeds 32.") } });
    }
    catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
    {
        logger.LogWarning("CMS validation failed: request too large");
        return Results.StatusCode(413);
    }
}).RequireAuthorization("Cms").Accepts<JsonElement>("application/json").Produces<BatchResult>().Produces(400).Produces(409);

app.MapGet("/entities", async (int? offset, int? limit, ClaimsPrincipal user, ReadContext db, CancellationToken ct) =>
{
    var skip = offset ?? 0;
    var take = limit ?? 20;
    if (skip is < 0 or > 100_000 || take is < 1 or > 100) return Results.BadRequest(new { error = "offset must be 0-100000; limit must be 1-100." });
    var rows = await Visible(db, user).OrderBy(x => x.Id).Skip(skip).Take(take)
        .Select(x => new { x.Id, x.Version, x.Payload, x.Published, x.LocallyDisabled }).ToListAsync(ct);
    return Results.Ok(rows.Select(x => new { x.Id, x.Version, payload = JsonSerializer.Deserialize<JsonElement>(x.Payload), x.Published, x.LocallyDisabled }));
}).RequireAuthorization("Read");
app.MapGet("/entities/{id}", async (string id, ClaimsPrincipal user, ReadContext db, CancellationToken ct) =>
{
    var row = await Visible(db, user).Where(x => x.Id == id)
        .Select(x => new { x.Id, x.Version, x.Payload, x.Published, x.LocallyDisabled }).SingleOrDefaultAsync(ct);
    return row is null ? Results.NotFound() : Results.Ok(new { row.Id, row.Version, payload = JsonSerializer.Deserialize<JsonElement>(row.Payload), row.Published, row.LocallyDisabled });
}).RequireAuthorization("Read");
app.MapPost("/entities/{id}/disable", async (string id, WriterContext db, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!EventParser.ValidId(id)) return Results.BadRequest();
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await EntityLock.Acquire(db, [id], ct);
    var entity = await db.Entities.FindAsync([id], ct);
    if (entity is null) return Results.NotFound();
    entity.LocallyDisabled = true;
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    logger.LogInformation("Administrative disable committed for {EntityId}", id);
    return Results.NoContent();
}).RequireAuthorization("Admin");
app.Run();

static IQueryable<Entity> Visible(ReadContext db, ClaimsPrincipal user) => user.IsInRole("Admin")
    ? db.Entities : db.Entities.Where(x => x.Published && !x.LocallyDisabled);

public partial class Program;
