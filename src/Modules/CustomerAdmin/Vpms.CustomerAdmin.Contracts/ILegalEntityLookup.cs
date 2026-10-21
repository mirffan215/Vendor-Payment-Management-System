namespace Vpms.CustomerAdmin.Contracts;

/// <summary>Lets other modules validate legal-entity ids of the current tenant.</summary>
public interface ILegalEntityLookup
{
    Task<bool> ExistsAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IEnumerable<Guid> legalEntityIds, CancellationToken ct = default);
}
