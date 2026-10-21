using Vpms.Migrator;

var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
         ?? throw new InvalidOperationException("ConnectionStrings__Migrator is required.");
await DatabaseMigrator.MigrateAsync(cs);
Console.WriteLine("Migrations applied.");

// Optional: provision the restricted application login (local development and first deployment).
var appLogin = Environment.GetEnvironmentVariable("VPMS_APP_LOGIN");
var appPassword = Environment.GetEnvironmentVariable("VPMS_APP_LOGIN_PASSWORD");
if (!string.IsNullOrEmpty(appLogin) && !string.IsNullOrEmpty(appPassword))
{
    await DatabaseMigrator.EnsureAppLoginAsync(cs, appLogin, appPassword);
    Console.WriteLine($"Application login '{appLogin}' is ready.");
}
