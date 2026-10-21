using Vpms.Migrator;

var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
         ?? throw new InvalidOperationException("ConnectionStrings__Migrator is required.");
await DatabaseMigrator.MigrateAsync(cs);
Console.WriteLine("Migrations applied.");
