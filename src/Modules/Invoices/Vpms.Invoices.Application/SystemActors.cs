namespace Vpms.Invoices.Application;

/// <summary>Fixed identities for work the system does by itself, so audit records always name an actor.</summary>
public static class SystemActors
{
    public static readonly Guid Intake = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
}
