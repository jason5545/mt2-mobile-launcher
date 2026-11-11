using Mt2Mobile.Steam.SteamCloud;
using Xunit;

namespace Mt2Mobile.Steam.Tests;

public sealed class SteamCloudPathMapperTests
{
    [Theory]
    [InlineData("saves/save-singlePlayer.json")]
    [InlineData("run-history/RunHistory.json")]
    [InlineData("preferences.json")]
    [InlineData("metagameSave.json")]
    [InlineData("metagameSave.json.backup")]
    public void Maps_supported_AutoCloud_files(string relative)
    {
        var ok = SteamCloudPathMapper.TryMap(
            SteamCloudPathMapper.ExpectedRootPrefix,
            SteamCloudPathMapper.GamePathPrefix + relative,
            out var mapped);

        Assert.True(ok);
        Assert.Equal(relative, mapped);
        Assert.Equal(
            $"{SteamCloudPathMapper.ExpectedRootPrefix}{SteamCloudPathMapper.GamePathPrefix}{relative}",
            SteamCloudPathMapper.ToRemoteFileName(relative));
    }

    [Theory]
    [InlineData("%WinAppDataLocalLow%Shiny Shoe/MonsterTrain2/", "metagameSave.json", "metagameSave.json")]
    [InlineData("%WinAppDataLocalLow%Shiny Shoe/MonsterTrain2/run-history/", "RunHistory.json", "run-history/RunHistory.json")]
    [InlineData("%WinAppDataLocalLow%Shiny Shoe/MonsterTrain2/saves/", "save-singlePlayer.json", "saves/save-singlePlayer.json")]
    public void Maps_observed_Steam_path_prefix_and_filename_splits(string prefix, string fileName, string relative)
    {
        Assert.True(SteamCloudPathMapper.TryMap(prefix, fileName, out var mapped));
        Assert.Equal(relative, mapped);
        Assert.Equal(
            "%WinAppDataLocalLow%Shiny Shoe/MonsterTrain2/" + relative,
            SteamCloudPathMapper.ToRemoteFileName(relative, prefix));
    }

    [Fact]
    public void Maps_full_AutoCloud_path_when_root_is_only_the_Windows_macro()
    {
        Assert.True(SteamCloudPathMapper.TryMap(
            "%WinAppDataLocalLow%",
            "Shiny Shoe/MonsterTrain2/preferences.json",
            out var mapped));

        Assert.Equal("preferences.json", mapped);
        Assert.Equal(
            "%WinAppDataLocalLow%Shiny Shoe/MonsterTrain2/preferences.json",
            SteamCloudPathMapper.ToRemoteFileName(mapped));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("saves/../../outside.json")]
    [InlineData("/saves/save-singlePlayer.json")]
    [InlineData("C:\\save-singlePlayer.json")]
    [InlineData("saves//save-singlePlayer.json")]
    [InlineData("saves/./save-singlePlayer.json")]
    public void Rejects_unsafe_paths(string path)
    {
        Assert.False(SteamCloudPathMapper.TryMap(
            SteamCloudPathMapper.ExpectedRootPrefix,
            SteamCloudPathMapper.GamePathPrefix + path,
            out _));
    }

    [Theory]
    [InlineData("Other Studio/MonsterTrain2/saves/save-singlePlayer.json")]
    [InlineData("Shiny Shoe/OtherGame/saves/save-singlePlayer.json")]
    [InlineData("Shiny Shoe/MonsterTrain2/preferences-local.json")]
    [InlineData("Shiny Shoe/MonsterTrain2/metagameSave.json.1")]
    [InlineData("Shiny Shoe/MonsterTrain2/logfile.01.log")]
    [InlineData("Shiny Shoe/MonsterTrain2/cache/cache.json")]
    [InlineData("Shiny Shoe/MonsterTrain2/playerprefs.json")]
    public void Rejects_paths_outside_the_AutoCloud_allowlist(string fileName)
    {
        Assert.False(SteamCloudPathMapper.TryMap(
            SteamCloudPathMapper.ExpectedRootPrefix,
            fileName,
            out _));
    }

    [Fact]
    public void Rejects_an_unexpected_root_prefix()
    {
        Assert.False(SteamCloudPathMapper.TryMap(
            "%WinAppData%",
            SteamCloudPathMapper.GamePathPrefix + "preferences.json",
            out _));
    }
}
