using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XanhNow.Messaging.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
var database = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
    ?? new DatabaseOptions();
var connectionString = !string.IsNullOrWhiteSpace(database.ConnectionStringFile)
    ? SecretFile.ReadRequired(database.ConnectionStringFile, "XanhNowMessaging migrator database")
    : database.ConnectionString;
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("XanhNowMessaging migrator database connection is required.");
}
builder.Services.AddDbContext<XanhNowMessagingDbContext>(options => options.UseNpgsql(
    connectionString,
    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", XanhNowMessagingDbContext.Schema)));

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<XanhNowMessagingDbContext>();
if (args.Contains("--check", StringComparer.Ordinal))
{
    var applied = await db.Database.GetAppliedMigrationsAsync();
    if (!applied.Contains("20261006010000_InitialMessaging", StringComparer.Ordinal))
    {
        throw new InvalidOperationException("XanhNowMessaging initial migration is not applied.");
    }
    Console.WriteLine("XANHNOW_MESSAGING_SCHEMA_CHECK_PASS");
    return;
}

await db.Database.MigrateAsync();
Console.WriteLine("XANHNOW_MESSAGING_MIGRATION_PASS");
