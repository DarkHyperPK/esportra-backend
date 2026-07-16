using Esportra.Infrastructure.Email;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class PartnerInvitationEmailTemplateTests
{
    [Fact]
    public void PartnerInvite_UsesNewAccountCopy_WhenAccountDoesNotExist()
    {
        var (_, html) = EmailTemplates.PartnerInvite(
            "Sponsor",
            "https://partner.esportra.com/invite/accept",
            accountExists: false,
            requiresPasswordSetup: true);

        Assert.Contains("create your Esportra account", html);
    }

    [Fact]
    public void PartnerInvite_UsesSharedPasswordSetupCopy_WhenExistingAccountHasNoPassword()
    {
        var (_, html) = EmailTemplates.PartnerInvite(
            "Sponsor",
            "https://partner.esportra.com/invite/accept",
            accountExists: true,
            requiresPasswordSetup: true);

        Assert.Contains("account already exists", html);
        Assert.Contains("add a shared Esportra password", html);
        Assert.DoesNotContain("Google", html);
        Assert.DoesNotContain("Discord", html);
        Assert.DoesNotContain("create your Esportra account", html);
    }

    [Fact]
    public void PartnerInvite_UsesSignInCopy_WhenExistingAccountHasPassword()
    {
        var (_, html) = EmailTemplates.PartnerInvite(
            "Sponsor",
            "https://partner.esportra.com/invite/accept",
            accountExists: true,
            requiresPasswordSetup: false);

        Assert.Contains("sign in to your existing Esportra account", html);
        Assert.DoesNotContain("create your Esportra account", html);
    }
}
