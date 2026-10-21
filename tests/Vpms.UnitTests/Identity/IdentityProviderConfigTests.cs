using Vpms.Identity.Application;
using Vpms.Identity.Domain;

namespace Vpms.UnitTests.Identity;

public class IdentityProviderConfigTests
{
    [Theory]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://login.microsoftonline.com/organizations/v2.0")]
    [InlineData("https://login.microsoftonline.com/consumers/v2.0")]
    [InlineData("https://login.microsoftonline.com/")]
    [InlineData("not a url")]
    public void Entra_authority_must_be_tenant_specific(string authority) =>
        Assert.False(IdentityProviderConfig.IsAuthorityAcceptable(IdentityProviderKind.EntraId, authority));

    [Fact]
    public void Entra_authority_with_a_directory_id_is_accepted() =>
        Assert.True(IdentityProviderConfig.IsAuthorityAcceptable(IdentityProviderKind.EntraId,
            "https://login.microsoftonline.com/6f1f8c1e-0000-0000-0000-000000000000/v2.0"));

    [Theory]
    [InlineData("a@acme.com", true)]
    [InlineData("a@ACME.com", true)]
    [InlineData("a@sub.acme.com", false)]
    [InlineData("a@evilacme.com", false)]
    [InlineData("acme.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Email_domains_are_matched_exactly(string? email, bool allowed) =>
        Assert.Equal(allowed, UserProvisioner.EmailAllowed(
            new IdentityProviderConfig { AllowedEmailDomains = "acme.com, other.org" }, email));

    [Fact]
    public void Empty_allow_list_allows_nobody() =>
        Assert.False(UserProvisioner.EmailAllowed(new IdentityProviderConfig(), "a@acme.com"));
}
