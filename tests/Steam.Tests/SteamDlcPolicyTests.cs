using Mt2Mobile.Steam;
using Xunit;

namespace Mt2Mobile.Steam.Tests;

public sealed class SteamDlcPolicyTests
{
    private static readonly SteamDlcDepotTarget Supported = new(3993450, 3993450, 8605106753921413412);

    [Fact]
    public void Downloads_an_owned_dlc_when_the_target_has_a_pinned_manifest()
    {
        var result = SteamDlcPolicy.Resolve([3993450], [Supported]);

        Assert.Equal([Supported], result.DepotsToDownload);
        Assert.Equal(Mt2Steam.AppId, result.DepotsToDownload[0].ToDepotSpec().AppId);
        Assert.Equal([3993450u], result.SupportedOwnedDlcIds);
        Assert.Empty(result.UnsupportedOwnedDlcIds);
    }

    [Fact]
    public void Does_not_download_and_filters_an_owned_dlc_without_target_manifest()
    {
        var result = SteamDlcPolicy.Resolve([4739980], [Supported]);

        Assert.Empty(result.DepotsToDownload);
        Assert.Empty(result.SupportedOwnedDlcIds);
        Assert.Equal([4739980u], result.UnsupportedOwnedDlcIds);
        Assert.Empty(SteamDlcPolicy.ForGame(hasSteamAuthorization: true, [4739980], [Supported]));
    }

    [Fact]
    public void Does_not_download_a_dlc_the_account_does_not_own()
    {
        var result = SteamDlcPolicy.Resolve([], [Supported]);

        Assert.Empty(result.DepotsToDownload);
        Assert.Empty(result.SupportedOwnedDlcIds);
        Assert.Empty(result.UnsupportedOwnedDlcIds);
    }

    [Fact]
    public void A_SAF_install_enables_an_owned_supported_DLC()
    {
        // Importing the files does not grant DLC; the account and target intersection does.
        Assert.Equal([3993450u], SteamDlcPolicy.ForGame(hasSteamAuthorization: true, [3993450], [Supported]));
    }

    [Fact]
    public void A_SAF_install_without_a_login_does_not_use_cached_owned_DLC_ids()
    {
        Assert.Empty(SteamDlcPolicy.ForGame(hasSteamAuthorization: false, [3993450], [Supported]));
    }
}
