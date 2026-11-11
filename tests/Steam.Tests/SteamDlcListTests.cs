using SteamKit2;
using Xunit;

namespace Mt2Mobile.Steam.Tests;

/// <summary>Terra 第 3 輪第 4 條：沒拿到 MT2 的產品資訊，不能當成「沒有任何 DLC」。</summary>
public sealed class SteamDlcListTests
{
    [Fact]
    public void Reads_dlc_from_listofdlc_and_depot_dlcappid()
    {
        var app = new KeyValue("appinfo");
        var extended = new KeyValue("extended");
        extended.Children.Add(new KeyValue("listofdlc", "3993450, 4100000"));
        app.Children.Add(extended);
        var depots = new KeyValue("depots");
        depots.Children.Add(new KeyValue("2742831"));
        var dlcDepot = new KeyValue("3993450");
        dlcDepot.Children.Add(new KeyValue("dlcappid", "3993450"));
        depots.Children.Add(dlcDepot);
        app.Children.Add(depots);

        Assert.Equal([3993450u, 4100000u], SteamSession.DlcFromAppInfo(app));
    }

    [Fact]
    public void Missing_app_info_is_a_query_failure_not_an_empty_dlc_list() =>
        Assert.Throws<SteamException>(() => SteamSession.DlcFromAppInfo(null));

    [Fact]
    public void App_info_without_depots_is_incomplete_and_a_query_failure() =>
        Assert.Throws<SteamException>(() => SteamSession.DlcFromAppInfo(new KeyValue("appinfo")));
}
