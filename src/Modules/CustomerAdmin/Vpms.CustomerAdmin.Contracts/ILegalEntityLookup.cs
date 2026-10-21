namespace Vpms.CustomerAdmin.Contracts;

/// <summary>Lets other modules validate legal-entity ids of the current tenant.</summary>
public interface ILegalEntityLookup
{
    Task<bool> ExistsAsync(Guid legalEntityId, CancellationToken ct = default);

    /// <summary>ISO 3166 alpha-2 country of the legal entity, or null when it does not exist.</summary>
    Task<string?> GetCountryAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IEnumerable<Guid> legalEntityIds, CancellationToken ct = default);
}
