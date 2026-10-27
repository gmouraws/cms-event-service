using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CmsEventService;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<WriterContext>
{
    public WriterContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<WriterContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Writer")
            ?? "Host=localhost;Database=cms;Username=placeholder;Password=placeholder")
        .Options);
}
