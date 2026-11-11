using Mt2Mobile.Steam.SteamCloud;
using Xunit;

namespace Mt2Mobile.Steam.Tests;

public sealed class SteamCloudSyncDecisionTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static IEnumerable<object?[]> DecisionCases =>
    [
        ["both unchanged", new SteamCloudBaseline(A), A, A, SteamCloudSyncAction.None],
        ["cloud changed or new", new SteamCloudBaseline(A), A, B, SteamCloudSyncAction.Download],
        ["device changed or new", new SteamCloudBaseline(A), B, A, SteamCloudSyncAction.Upload],
        ["both changed to same", new SteamCloudBaseline(A), B, B, SteamCloudSyncAction.UpdateState],
        ["both changed differently", new SteamCloudBaseline(A), B, "cccccccccccccccccccccccccccccccccccccccc", SteamCloudSyncAction.Conflict],
        ["device file deleted while cloud exists", new SteamCloudBaseline(A), null, A, SteamCloudSyncAction.Download],
        ["device file deleted while cloud changed", new SteamCloudBaseline(A), null, B, SteamCloudSyncAction.Download],
        ["cloud deleted while device unchanged", new SteamCloudBaseline(A), A, null, SteamCloudSyncAction.KeepLocalAfterCloudDelete],
        ["both copies deleted", new SteamCloudBaseline(A), null, null, SteamCloudSyncAction.ForgetState],
        ["first sync cloud-only", null, null, A, SteamCloudSyncAction.Download],
        ["first sync identical", null, A, A, SteamCloudSyncAction.UpdateState],
        ["first sync different", null, A, B, SteamCloudSyncAction.Conflict],
        ["new device file in an account that already synced", null, A, null, SteamCloudSyncAction.Upload],
        ["first sync with neither copy", null, null, null, SteamCloudSyncAction.None],
    ];

    [Theory]
    [MemberData(nameof(DecisionCases))]
    public void Applies_each_sync_table_row(string _, SteamCloudBaseline? previous, string? local, string? cloud, SteamCloudSyncAction expected)
    {
        Assert.Equal(expected, SteamCloudSyncDecision.Decide(previous, local, cloud));
    }

    [Theory]
    [InlineData(SteamCloudConflictChoice.UseCloud, SteamCloudSyncAction.Download)]
    [InlineData(SteamCloudConflictChoice.UseDevice, SteamCloudSyncAction.Upload)]
    [InlineData(SteamCloudConflictChoice.Cancel, SteamCloudSyncAction.Cancelled)]
    public void Resolves_a_conflict_as_one_user_choice(SteamCloudConflictChoice choice, SteamCloudSyncAction expected)
    {
        Assert.Equal(expected, SteamCloudSyncDecision.ResolveConflict(choice, localExists: true, cloudExists: true));
    }

    [Theory]
    [InlineData(null, "a", SteamCloudSyncAction.Download)]
    [InlineData("a", "a", SteamCloudSyncAction.UpdateState)]
    [InlineData("a", "b", SteamCloudSyncAction.Conflict)]
    [InlineData("a", null, SteamCloudSyncAction.Conflict)]
    public void First_sync_for_an_account_asks_about_device_only_files(string? local, string? cloud, SteamCloudSyncAction expected)
    {
        static string? Sha(string? x) => x is null ? null : new string(x[0], 40);
        Assert.Equal(expected, SteamCloudSyncDecision.Decide(null, Sha(local), Sha(cloud), firstSyncForAccount: true));
    }

    [Fact]
    public void Use_cloud_on_a_first_sync_moves_device_only_files_to_backup()
    {
        Assert.Equal(
            SteamCloudSyncAction.MoveLocalToBackup,
            SteamCloudSyncDecision.ResolveConflict(SteamCloudConflictChoice.UseCloud, localExists: true, cloudExists: false, firstSyncForAccount: true));
    }

    [Fact]
    public void Does_not_turn_a_cloud_deletion_into_a_device_delete()
    {
        Assert.Equal(
            SteamCloudSyncAction.KeepLocalAfterCloudDelete,
            SteamCloudSyncDecision.ResolveConflict(SteamCloudConflictChoice.UseCloud, localExists: true, cloudExists: false));
    }
}
