using ICEHOTT.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ICEHOTT.Tests;

public sealed class IcehottApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public IcehottApiFactory()
    {
        _connection.Open();
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>().Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ICEHOTTDbContext>>();
            services.RemoveAll<DbContextOptions<ICEHOTTDbContext>>();
            services.RemoveAll<ICEHOTTDbContext>();
            services.AddDbContext<ICEHOTTDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
