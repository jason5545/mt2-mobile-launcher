using Xunit;

namespace Mt2Mobile.Steam.Tests;

public sealed class OfficialSigningRuleTests
{
    private const string Official = "ea3c5c9dd9c64a811497a42118d71d2e41557c78fae45bf0b9fb4a293548f2f8";
    private const string OldOfficial = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Other = "2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly string[] OfficialList = [Official, OldOfficial];

    [Fact]
    public void Single_signer_with_the_official_certificate_is_official() =>
        Assert.True(OfficialSigningRule.IsOfficial(false, [Official], [Official], OfficialList));

    [Fact]
    public void Single_signer_after_a_legitimate_rotation_is_judged_by_the_current_certificate() =>
        Assert.True(OfficialSigningRule.IsOfficial(false, [Official], [OldOfficial, Official], OfficialList));

    [Fact]
    public void Single_signer_rotated_away_from_the_official_key_is_not_official() =>
        Assert.False(OfficialSigningRule.IsOfficial(false, [Other], [Official, Other], OfficialList));

    [Fact]
    public void Single_signer_with_another_certificate_is_not_official() =>
        Assert.False(OfficialSigningRule.IsOfficial(false, [Other], [Other], OfficialList));

    [Theory]
    [InlineData(Official, Other)]
    [InlineData(Other, Official)]
    [InlineData(Official, OldOfficial)]
    public void Multiple_signers_are_never_official(string first, string second) =>
        Assert.False(OfficialSigningRule.IsOfficial(true, [first, second], [first], OfficialList));
}
