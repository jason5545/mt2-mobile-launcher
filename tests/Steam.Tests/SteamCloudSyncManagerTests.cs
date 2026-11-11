using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mt2Mobile.Steam.SteamCloud;
using Xunit;

namespace Mt2Mobile.Steam.Tests;

public sealed class SteamCloudSyncManagerTests
{
    private const uint App = 2742830;
    private const string AccountA = "account-a";
    private const string AccountB = "account-b";

    private static readonly string[] DeviceFiles =
    [
        "saves/save-singlePlayer.json",
        "saves/save-regionRun.json",
        "saves/save-malickaChallenge.json",
        "saves/save-dailyChallenge.json",
        "preferences.json",
        "metagameSave.json",
    ];

    private static readonly string[] CloudOnlyFiles =
    [
        "run-history/RunHistory.json",
        "metagameSave.json.backup",
    ];

    [Fact]
    public async Task First_sync_returns_one_conflict_group_then_use_cloud_backups_and_downloads_all_eight()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "files", "saves", "persistent")).FullName;
        var cloudRoot = Path.Combine(temp.Path, "files", "cloud");
        var remote = new FakeCloudTransport();

        foreach (var path in DeviceFiles)
        {
            await WriteLocalAsync(persistent, path, Bytes("phone-test:" + path));
            remote.Set(path, Bytes("mac-cloud:" + path));
        }
        foreach (var path in CloudOnlyFiles)
            remote.Set(path, Bytes("mac-cloud:" + path));

        var manager = new SteamCloudSyncManager(persistent, cloudRoot);
        var conflict = await manager.SynchronizeAsync(App, AccountA, remote);

        Assert.Equal(SteamCloudSyncResultKind.ConflictRequired, conflict.Kind);
        Assert.Equal(6, conflict.Conflicts.Count);
        Assert.True(conflict.FirstSyncForAccount);
        Assert.Equal(0, conflict.DeviceOnlyCount);
        Assert.Equal(0, remote.DownloadCount);
        Assert.Equal(0, remote.UploadCount);
        Assert.False(File.Exists(manager.StatePath));
        Assert.False(Directory.Exists(manager.BackupRoot));

        var result = await manager.SynchronizeAsync(App, AccountA, remote, SteamCloudConflictApproval.For(conflict, SteamCloudConflictChoice.UseCloud));

        Assert.Equal(SteamCloudSyncResultKind.Synced, result.Kind);
        Assert.Equal(8, result.Downloaded);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(8, remote.Files.Count);
        foreach (var path in remote.Files.Keys)
            Assert.Equal(Sha1(remote.Files[path]), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));

        var backup = Assert.Single(Directory.GetDirectories(manager.ImportantBackupRoot));
        Assert.Equal(6, Directory.GetFiles(Path.Combine(backup, "device"), "*", SearchOption.AllDirectories).Length);
        Assert.True(File.Exists(Path.Combine(backup, "manifest.json")));
        Assert.Equal(AccountA, ReadAccountKey(manager));
    }

    [Fact]
    public async Task Choosing_device_backs_up_cloud_versions_and_never_declares_cloud_deletes()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var remote = new FakeCloudTransport();
        foreach (var path in DeviceFiles)
        {
            await WriteLocalAsync(persistent, path, Bytes("phone:" + path));
            remote.Set(path, Bytes("cloud:" + path));
        }
        foreach (var path in CloudOnlyFiles)
            remote.Set(path, Bytes("cloud:" + path));

        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        var shown = await manager.SynchronizeAsync(App, AccountA, remote);
        var result = await manager.SynchronizeAsync(App, AccountA, remote, SteamCloudConflictApproval.For(shown, SteamCloudConflictChoice.UseDevice));

        Assert.Equal(SteamCloudSyncResultKind.Synced, result.Kind);
        Assert.Equal(2, result.Downloaded);
        Assert.Equal(6, result.Uploaded);
        Assert.Equal(0, remote.DeleteCount);
        Assert.Equal(8, remote.Files.Count);
        foreach (var path in DeviceFiles)
            Assert.Equal(Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))), Sha1(remote.Files[path]));

        var backup = Assert.Single(Directory.GetDirectories(manager.ImportantBackupRoot));
        Assert.Equal(6, Directory.GetFiles(Path.Combine(backup, "cloud"), "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Cloud_deletion_backs_up_and_keeps_the_unchanged_device_file_without_uploading()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        const string path = "preferences.json";
        var bytes = Bytes("same-before-delete");
        await WriteLocalAsync(persistent, path, bytes);
        var remote = new FakeCloudTransport();
        remote.Set(path, bytes);
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));

        Assert.Equal(SteamCloudSyncResultKind.Synced, (await manager.SynchronizeAsync(App, AccountA, remote)).Kind);
        remote.Remove(path);

        var afterDelete = await manager.SynchronizeAsync(App, AccountA, remote);

        Assert.Equal(SteamCloudSyncResultKind.Synced, afterDelete.Kind);
        Assert.Equal(0, afterDelete.Uploaded);
        Assert.True(File.Exists(LocalPath(persistent, path)));
        var backup = Assert.Single(Directory.GetDirectories(manager.GeneralBackupRoot));
        Assert.True(File.Exists(Path.Combine(backup, "device", path)));
        using (var state = JsonDocument.Parse(await File.ReadAllTextAsync(manager.StatePath)))
            Assert.False(state.RootElement.GetProperty("files").GetProperty(path).GetProperty("cloudPresent").GetBoolean());

        remote.ResetCounters();
        var next = await manager.SynchronizeAsync(App, AccountA, remote);
        Assert.Equal(SteamCloudSyncResultKind.Synced, next.Kind);
        Assert.Equal(0, remote.UploadCount);
        Assert.Single(Directory.GetDirectories(manager.GeneralBackupRoot));
    }

    [Fact]
    public async Task Backup_retention_keeps_ten_completed_sets_per_category_by_creation_order_and_leaves_other_directories_alone()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        const string path = "preferences.json";
        var deviceBytes = Bytes("device-before-cloud-change");
        await WriteLocalAsync(persistent, path, deviceBytes);
        var remote = new FakeCloudTransport();
        remote.Set(path, deviceBytes);
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        await manager.SynchronizeAsync(App, AccountA, remote);

        Directory.CreateDirectory(manager.GeneralBackupRoot);
        Directory.CreateDirectory(manager.ImportantBackupRoot);
        static void Completed(string root, int sequence)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, $"{sequence:D10}-20261001T000000.000Z-{sequence:x8}")).FullName;
            File.WriteAllText(Path.Combine(directory, "manifest.json"), "[]");
        }
        for (int i = 1; i <= 11; i++)
            Completed(manager.GeneralBackupRoot, i);
        for (int i = 12; i <= 14; i++)
            Completed(manager.ImportantBackupRoot, i);
        // 舊版留下的未完成備份（符合命名、沒有 manifest.json）：不佔名額，也不刪
        Directory.CreateDirectory(Path.Combine(manager.GeneralBackupRoot, "0000000015-20261001T000000.000Z-0000000f"));
        Directory.CreateDirectory(Path.Combine(manager.BackupRoot, "20261001T000000.000Z-0000000a"));
        Directory.CreateDirectory(Path.Combine(manager.GeneralBackupRoot, "keep-manual"));
        remote.Set(path, Bytes("new-cloud-version"));

        var result = await manager.SynchronizeAsync(App, AccountA, remote);

        Assert.Equal(SteamCloudSyncResultKind.Synced, result.Kind);
        var general = Directory.GetDirectories(manager.GeneralBackupRoot)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
            .Select(Path.GetFileName).Order().ToList();
        Assert.Equal(10, general.Count);
        Assert.StartsWith("0000000003-", general[0]);
        Assert.StartsWith("0000000016-", general[^1]);
        Assert.True(Directory.Exists(Path.Combine(manager.GeneralBackupRoot, "0000000015-20261001T000000.000Z-0000000f")));
        Assert.Empty(Directory.GetDirectories(manager.GeneralBackupRoot, ".partial-*"));
        Assert.Equal(3, Directory.GetDirectories(manager.ImportantBackupRoot).Length);
        Assert.True(Directory.Exists(Path.Combine(manager.BackupRoot, "20261001T000000.000Z-0000000a")));
        Assert.True(Directory.Exists(Path.Combine(manager.GeneralBackupRoot, "keep-manual")));
    }

    [Fact]
    public async Task Applies_a_change_list_delta_to_the_previous_cloud_manifest()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        var remote = new FakeCloudTransport();
        const string changedPath = "preferences.json";
        const string unchangedPath = "metagameSave.json";
        var oldChanged = Bytes("old preference");
        var newChanged = Bytes("new preference from cloud");
        var unchanged = Bytes("unchanged metagame");
        await WriteLocalAsync(persistent, changedPath, oldChanged);
        await WriteLocalAsync(persistent, unchangedPath, unchanged);
        remote.Set(changedPath, oldChanged);
        remote.Set(unchangedPath, unchanged);
        Assert.Equal(SteamCloudSyncResultKind.Synced, (await manager.SynchronizeAsync(App, AccountA, remote)).Kind);

        remote.SetDelta(changedPath, newChanged);
        var result = await manager.SynchronizeAsync(App, AccountA, remote);

        Assert.Equal(SteamCloudSyncResultKind.Synced, result.Kind);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(10UL, remote.LastRequestedChangeNumber);
        Assert.Equal(2, manager.GetCloudFileCount());
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(manager.StatePath));
        Assert.Equal(11UL, state.RootElement.GetProperty("changeNumber").GetUInt64());
        Assert.Equal(Sha1(newChanged), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, changedPath))));
        Assert.Equal(Sha1(unchanged), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, unchangedPath))));
    }

    // ── 第 1 條：帳號切換 ───────────────────────────────────

    [Fact]
    public async Task Review1_switching_account_does_not_treat_the_other_accounts_cloud_as_a_newer_version()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json";
        var accountA = Bytes("account A progress");
        await WriteLocalAsync(persistent, path, accountA);
        var cloudA = new FakeCloudTransport(changeNumber: 84);
        cloudA.Set(path, accountA);
        Assert.Equal(SteamCloudSyncResultKind.Synced, (await manager.SynchronizeAsync(App, AccountA, cloudA)).Kind);
        var stateBefore = await File.ReadAllBytesAsync(manager.StatePath);

        // 登出 A、登入 B：B 的 Cloud 有同名但內容不同的檔
        var cloudB = new FakeCloudTransport(changeNumber: 3);
        cloudB.Set(path, Bytes("account B progress"));
        var result = await manager.SynchronizeAsync(App, AccountB, cloudB);

        Assert.Equal(0UL, cloudB.LastRequestedChangeNumber);
        Assert.Equal(SteamCloudSyncResultKind.ConflictRequired, result.Kind);
        Assert.True(result.FirstSyncForAccount);
        Assert.Equal(AccountB, result.AccountKey);
        Assert.Equal(Sha1(accountA), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(manager.StatePath));
        Assert.Equal(0, cloudB.DownloadCount);
    }

    [Fact]
    public async Task Round2_account_switch_asks_about_device_only_saves_and_use_cloud_moves_them_to_backup()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string shared = "metagameSave.json", deviceOnly = "run-history/RunHistory.json";
        var cloudA = new FakeCloudTransport(changeNumber: 84);
        foreach (var path in new[] { shared, deviceOnly })
        {
            await WriteLocalAsync(persistent, path, Bytes("A " + path));
            cloudA.Set(path, Bytes("A " + path));
        }
        await manager.SynchronizeAsync(App, AccountA, cloudA);

        var cloudB = new FakeCloudTransport(changeNumber: 3);
        cloudB.Set(shared, Bytes("B " + shared));
        var shown = await manager.SynchronizeAsync(App, AccountB, cloudB);

        Assert.Equal(SteamCloudSyncResultKind.ConflictRequired, shown.Kind);
        var only = Assert.Single(shown.Conflicts, c => c.LocalRelativePath == deviceOnly);
        Assert.NotNull(only.DeviceSha1);
        Assert.Null(only.CloudSha1);
        Assert.False(only.CloudDeleted);
        Assert.Equal(1, shown.DeviceOnlyCount);

        var result = await manager.SynchronizeAsync(App, AccountB, cloudB, SteamCloudConflictApproval.For(shown, SteamCloudConflictChoice.UseCloud));

        Assert.Equal(SteamCloudSyncResultKind.Synced, result.Kind);
        Assert.Equal(0, cloudB.UploadCount);
        Assert.False(cloudB.Files.ContainsKey(deviceOnly));
        Assert.False(File.Exists(LocalPath(persistent, deviceOnly)));
        Assert.Equal(Sha1(Bytes("B " + shared)), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, shared))));
        var backup = Assert.Single(Directory.GetDirectories(manager.ImportantBackupRoot));
        Assert.Equal(Sha1(Bytes("A " + deviceOnly)), Sha1(await File.ReadAllBytesAsync(Path.Combine(backup, "device", deviceOnly))));
        Assert.Equal(Sha1(Bytes("A " + shared)), Sha1(await File.ReadAllBytesAsync(Path.Combine(backup, "device", shared))));
        Assert.Equal(AccountB, ReadAccountKey(manager));
    }

    [Fact]
    public async Task Round2_account_switch_delta_does_not_mix_in_the_previous_accounts_cloud_files()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string shared = "metagameSave.json", accountAOnly = "run-history/RunHistory.json";
        var cloudA = new FakeCloudTransport(changeNumber: 84);
        foreach (var path in new[] { shared, accountAOnly })
        {
            await WriteLocalAsync(persistent, path, Bytes("A " + path));
            cloudA.Set(path, Bytes("A " + path));
        }
        await manager.SynchronizeAsync(App, AccountA, cloudA);

        var cloudB = new FakeCloudTransport(changeNumber: 90);
        cloudB.SetDelta(shared, Bytes("B " + shared));
        var result = await manager.SynchronizeAsync(App, AccountB, cloudB);

        Assert.Equal(0UL, cloudB.LastRequestedChangeNumber);
        Assert.Equal(1, result.CloudFileCount);
        Assert.Contains(result.Conflicts, c => c.LocalRelativePath == accountAOnly && c.CloudSha1 is null);
    }

    [Fact]
    public async Task Round2_state_without_an_account_key_is_treated_as_an_unknown_account()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var cloudRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "cloud")).FullName;
        const string path = "metagameSave.json";
        var old = Bytes("phone progress from the old launcher");
        await WriteLocalAsync(persistent, path, old);
        // 舊版寫的 state：沒有 accountKey
        await File.WriteAllTextAsync(Path.Combine(cloudRoot, "state.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            changeNumber = 84,
            files = new Dictionary<string, object> { [path] = new { sha1 = Sha1(old), cloudPresent = true } },
            cloudFiles = new Dictionary<string, object>(),
        }));
        var remote = new FakeCloudTransport(changeNumber: 90);
        remote.Set(path, Bytes("cloud version"));
        var manager = new SteamCloudSyncManager(persistent, cloudRoot);

        var result = await manager.SynchronizeAsync(App, AccountA, remote);

        Assert.Equal(SteamCloudSyncResultKind.ConflictRequired, result.Kind);
        Assert.Equal(0UL, remote.LastRequestedChangeNumber);
        Assert.Equal(Sha1(old), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));
    }

    [Fact]
    public async Task Round2_choice_for_another_account_is_rejected_before_anything_is_written()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json";
        await WriteLocalAsync(persistent, path, Bytes("phone"));
        var remote = new FakeCloudTransport();
        remote.Set(path, Bytes("cloud"));
        var shownForA = await manager.SynchronizeAsync(App, AccountA, remote);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.SynchronizeAsync(App, AccountB, remote, SteamCloudConflictApproval.For(shownForA, SteamCloudConflictChoice.UseCloud)));

        Assert.False(File.Exists(manager.StatePath));
        Assert.Equal(Sha1(Bytes("phone")), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));
        Assert.Equal(0, remote.DownloadCount);
    }

    // ── 第 2 條：上傳 ────────────────────────────────────────

    [Fact]
    public async Task Review2a_normal_upload_backs_up_the_cloud_version_it_replaces()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json";
        var synced = Bytes("synced version");
        await WriteLocalAsync(persistent, path, synced);
        var remote = new FakeCloudTransport();
        remote.Set(path, synced);
        await manager.SynchronizeAsync(App, AccountA, remote);

        await WriteLocalAsync(persistent, path, Bytes("played on phone"));
        var result = await manager.SynchronizeAsync(App, AccountA, remote);

        Assert.Equal(1, result.Uploaded);
        var backup = Assert.Single(Directory.GetDirectories(manager.GeneralBackupRoot));
        Assert.Equal(Sha1(synced), Sha1(await File.ReadAllBytesAsync(Path.Combine(backup, "cloud", path))));
        Assert.False(Directory.Exists(manager.ImportantBackupRoot));
    }

    [Fact]
    public async Task Review2b_upload_stops_when_the_cloud_file_changed_after_the_listing()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json", other = "preferences.json";
        var synced = Bytes("synced version");
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, path, synced);
        await WriteLocalAsync(persistent, other, Bytes("other synced"));
        remote.Set(path, synced);
        remote.Set(other, Bytes("other synced"));
        await manager.SynchronizeAsync(App, AccountA, remote);
        var stateBefore = await File.ReadAllBytesAsync(manager.StatePath);

        var phone = Bytes("played on phone");
        await WriteLocalAsync(persistent, path, phone);
        var otherDevice = Bytes("played on another device after the phone listed Cloud");
        remote.AfterNextManifest = () => remote.Set(path, otherDevice);
        remote.ResetCounters();

        await Assert.ThrowsAsync<SteamCloudProtocolException>(() => manager.SynchronizeAsync(App, AccountA, remote));

        Assert.Equal(0, remote.UploadCount);
        Assert.Equal(Sha1(otherDevice), Sha1(remote.Files[path]));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(manager.StatePath));
        Assert.Equal(Sha1(phone), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));
        Assert.Equal(Sha1(Bytes("other synced")), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, other))));
    }

    [Fact]
    public async Task Round2_upload_stops_when_another_device_creates_the_same_file_after_listing()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string existing = "metagameSave.json", created = "saves/save-dailyChallenge.json";
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, existing, Bytes("synced"));
        remote.Set(existing, Bytes("synced"));
        await manager.SynchronizeAsync(App, AccountA, remote);
        var stateBefore = await File.ReadAllBytesAsync(manager.StatePath);

        var phone = Bytes("phone daily");
        await WriteLocalAsync(persistent, created, phone);
        var otherDevice = Bytes("other device daily");
        remote.AfterNextManifest = () => remote.Set(created, otherDevice);
        remote.ResetCounters();

        await Assert.ThrowsAsync<SteamCloudRemoteChangedException>(() => manager.SynchronizeAsync(App, AccountA, remote));

        Assert.Equal(0, remote.UploadCount);
        Assert.Equal(Sha1(otherDevice), Sha1(remote.Files[created]));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(manager.StatePath));
        Assert.Equal(Sha1(phone), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, created))));
        Assert.Equal(Sha1(Bytes("synced")), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, existing))));
    }

    // ── 第 3 條：同步途中手機檔被改 ─────────────────────────

    [Fact]
    public async Task Review3_a_save_the_game_creates_during_sync_stops_the_sync_before_anything_is_replaced()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "saves/save-singlePlayer.json", other = "run-history/RunHistory.json";
        var remote = new FakeCloudTransport();
        remote.Set(path, Bytes("cloud run"));
        remote.Set(other, Bytes("cloud history"));
        var gameWrote = Bytes("game wrote this run while the launcher was syncing");
        remote.BeforeDownload = relative =>
        {
            if (relative == path) File.WriteAllBytes(LocalPath(persistent, path), gameWrote);
        };
        Directory.CreateDirectory(Path.GetDirectoryName(LocalPath(persistent, path))!);

        await Assert.ThrowsAsync<SteamCloudLocalChangedException>(() => manager.SynchronizeAsync(App, AccountA, remote));

        Assert.Equal(Sha1(gameWrote), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));
        Assert.False(File.Exists(LocalPath(persistent, other)));
        Assert.False(File.Exists(manager.StatePath));
        Assert.Equal(0, remote.UploadCount);
    }

    [Fact]
    public async Task Round2_a_save_changed_right_before_its_rename_is_not_replaced()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json";
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, path, Bytes("synced"));
        remote.Set(path, Bytes("synced"));
        await manager.SynchronizeAsync(App, AccountA, remote);
        var stateBefore = await File.ReadAllBytesAsync(manager.StatePath);
        remote.Set(path, Bytes("other device"));

        // checkpoint 的第 1 次在備份前、第 2 次在 rename 前：在第 2 次模擬遊戲寫入
        var gameWrote = Bytes("game wrote after the backup");
        int calls = 0;
        void Checkpoint()
        {
            if (++calls == 2) File.WriteAllBytes(LocalPath(persistent, path), gameWrote);
        }

        await Assert.ThrowsAsync<SteamCloudLocalChangedException>(() =>
            manager.SynchronizeAsync(App, AccountA, remote, checkpoint: Checkpoint));

        Assert.Equal(Sha1(gameWrote), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(manager.StatePath));
    }

    // ── 第 4 條：時鐘往回調 ─────────────────────────────────

    [Fact]
    public async Task Review4_backups_after_the_clock_moves_back_are_kept_by_creation_order()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var clock = new ManualClock(new DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"), clock);
        const string path = "metagameSave.json";
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, path, Bytes("v0"));
        remote.Set(path, Bytes("v0"));
        await manager.SynchronizeAsync(App, AccountA, remote);

        // 時鐘快的時候 10 次上傳，再往回調之後 2 次；每次上傳都備份被蓋掉的 Cloud 版本
        for (int i = 1; i <= 12; i++)
        {
            if (i == 11) clock.Now = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
            await WriteLocalAsync(persistent, path, Bytes("v" + i));
            Assert.Equal(1, (await manager.SynchronizeAsync(App, AccountA, remote)).Uploaded);
        }

        var kept = Directory.GetDirectories(manager.GeneralBackupRoot)
            .Select(d => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(d, "cloud", path))))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(Enumerable.Range(2, 10).Select(i => "v" + i).Order(StringComparer.Ordinal), kept);
    }

    // ── 第 6 條：衝突的選擇 ─────────────────────────────────

    [Fact]
    public async Task Review6_conflict_choice_is_not_applied_to_a_conflict_the_user_did_not_see()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string shown = "preferences.json";
        const string unseen = "metagameSave.json";
        var remote = new FakeCloudTransport();
        foreach (var path in new[] { shown, unseen })
        {
            await WriteLocalAsync(persistent, path, Bytes("synced " + path));
            remote.Set(path, Bytes("synced " + path));
        }
        await manager.SynchronizeAsync(App, AccountA, remote);
        var stateBefore = await File.ReadAllBytesAsync(manager.StatePath);

        await WriteLocalAsync(persistent, shown, Bytes("phone " + shown));
        remote.Set(shown, Bytes("other device " + shown));
        var first = await manager.SynchronizeAsync(App, AccountA, remote);
        Assert.Equal(shown, Assert.Single(first.Conflicts).LocalRelativePath);

        // 對話框開著的時候，另一個檔也變成衝突
        var phoneUnseen = Bytes("phone " + unseen);
        await WriteLocalAsync(persistent, unseen, phoneUnseen);
        remote.Set(unseen, Bytes("other device " + unseen));
        remote.ResetCounters();
        var second = await manager.SynchronizeAsync(App, AccountA, remote, SteamCloudConflictApproval.For(first, SteamCloudConflictChoice.UseCloud));

        Assert.Equal(SteamCloudSyncResultKind.ConflictRequired, second.Kind);
        Assert.Equal([unseen, shown], second.Conflicts.Select(c => c.LocalRelativePath));
        Assert.Equal(Sha1(phoneUnseen), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, unseen))));
        Assert.Equal(Sha1(Bytes("phone " + shown)), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, shown))));
        Assert.Equal(0, remote.DownloadCount);
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(manager.StatePath));
    }

    [Fact]
    public async Task Round2_shown_conflict_that_became_an_automatic_download_is_asked_again()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "preferences.json";
        var synced = Bytes("synced");
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, path, synced);
        remote.Set(path, synced);
        await manager.SynchronizeAsync(App, AccountA, remote);
        var stateBefore = await File.ReadAllBytesAsync(manager.StatePath);

        await WriteLocalAsync(persistent, path, Bytes("phone"));
        remote.Set(path, Bytes("other device"));
        var first = await manager.SynchronizeAsync(App, AccountA, remote);
        Assert.Single(first.Conflicts);

        // 對話框開著的時候，手機的檔變回同步時的版本：現在算「只有 Cloud 變了」，會自動下載
        await WriteLocalAsync(persistent, path, synced);
        remote.ResetCounters();
        var second = await manager.SynchronizeAsync(App, AccountA, remote, SteamCloudConflictApproval.For(first, SteamCloudConflictChoice.UseDevice));

        Assert.Equal(SteamCloudSyncResultKind.ConflictRequired, second.Kind);
        var again = Assert.Single(second.Conflicts);
        Assert.Equal(Sha1(synced), again.DeviceSha1);
        Assert.Equal(Sha1(synced), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));
        Assert.Equal(0, remote.DownloadCount + remote.UploadCount);
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(manager.StatePath));

        // 再選一次（這次看過新的清單）才套用
        var third = await manager.SynchronizeAsync(App, AccountA, remote, SteamCloudConflictApproval.For(second, SteamCloudConflictChoice.UseDevice));
        Assert.Equal(SteamCloudSyncResultKind.Synced, third.Kind);
        Assert.Equal(Sha1(synced), Sha1(remote.Files[path]));
    }

    [Fact]
    public async Task Round2_shown_conflict_that_became_identical_is_dropped_and_the_rest_is_applied()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string settled = "preferences.json", still = "metagameSave.json";
        var remote = new FakeCloudTransport();
        foreach (var path in new[] { settled, still })
        {
            await WriteLocalAsync(persistent, path, Bytes("synced " + path));
            remote.Set(path, Bytes("synced " + path));
        }
        await manager.SynchronizeAsync(App, AccountA, remote);
        foreach (var path in new[] { settled, still })
        {
            await WriteLocalAsync(persistent, path, Bytes("phone " + path));
            remote.Set(path, Bytes("other device " + path));
        }
        var first = await manager.SynchronizeAsync(App, AccountA, remote);
        Assert.Equal(2, first.Conflicts.Count);

        // 其中一個檔，別台裝置傳上來的剛好跟手機一樣
        remote.Set(settled, Bytes("phone " + settled));
        var result = await manager.SynchronizeAsync(App, AccountA, remote, SteamCloudConflictApproval.For(first, SteamCloudConflictChoice.UseCloud));

        Assert.Equal(SteamCloudSyncResultKind.Synced, result.Kind);
        Assert.Equal(Sha1(Bytes("other device " + still)), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, still))));
        Assert.Equal(Sha1(Bytes("phone " + settled)), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, settled))));
    }

    // ── 第 5 條：世代（checkpoint）─────────────────────────

    [Fact]
    public async Task Round2_revoked_checkpoint_stops_before_upload_and_before_writing_state()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string download = "preferences.json", upload = "metagameSave.json";
        var remote = new FakeCloudTransport();
        foreach (var path in new[] { download, upload })
        {
            await WriteLocalAsync(persistent, path, Bytes("synced " + path));
            remote.Set(path, Bytes("synced " + path));
        }
        await manager.SynchronizeAsync(App, AccountA, remote);
        var stateBefore = await File.ReadAllBytesAsync(manager.StatePath);
        remote.Set(download, Bytes("cloud " + download));
        await WriteLocalAsync(persistent, upload, Bytes("phone " + upload));
        remote.ResetCounters();

        // 第 1 次：備份前；第 2 次：rename 前；第 3 次：上傳前（這時撤回）
        int calls = 0;
        void Checkpoint()
        {
            if (++calls == 3) throw new OperationCanceledException("access revoked");
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.SynchronizeAsync(App, AccountA, remote, checkpoint: Checkpoint));

        Assert.Equal(0, remote.UploadCount);
        Assert.Equal(Sha1(Bytes("synced " + upload)), Sha1(remote.Files[upload]));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(manager.StatePath));
    }

    // ── Terra 第 13 輪第 1 條：檢查世代之後、本機寫入之前換了登入 ──

    [Fact]
    public async Task Round13_a_new_login_after_the_checkpoint_does_not_replace_the_save()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json";
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, path, Bytes("synced"));
        remote.Set(path, Bytes("synced"));
        await manager.SynchronizeAsync(App, AccountA, remote);
        remote.Set(path, Bytes("A cloud"));             // A 的 Cloud 有新版本：要下載、替換手機檔

        // 第 1 次在備份前、第 2 次在 rename 前：第 2 次通過之後換登入
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SyncWhileANewLoginIsSavedAsync(manager, remote, Path.Combine(temp.Path, "steam"), renewAt: 2));

        Assert.Equal(Sha1(Bytes("synced")), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));
    }

    [Fact]
    public async Task Round13_a_new_login_after_the_checkpoint_does_not_remove_a_device_save()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string deviceOnly = "preferences.json", cloudSave = "metagameSave.json";
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, deviceOnly, Bytes("phone only"));
        await WriteLocalAsync(persistent, cloudSave, Bytes("same"));
        remote.Set(cloudSave, Bytes("same"));
        var shown = await manager.SynchronizeAsync(App, AccountA, remote);   // 帳號的第一次同步：手機獨有的檔要問
        Assert.Equal(SteamCloudSyncResultKind.ConflictRequired, shown.Kind);

        // 選 Cloud：手機獨有的檔先備份再移除。第 1 次在備份前、第 2 次在移除前：第 2 次通過之後換登入
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SyncWhileANewLoginIsSavedAsync(manager, remote, Path.Combine(temp.Path, "steam"), renewAt: 2,
                SteamCloudConflictApproval.For(shown, SteamCloudConflictChoice.UseCloud)));

        Assert.True(File.Exists(LocalPath(persistent, deviceOnly)));
    }

    [Fact]
    public async Task Round13_a_new_login_after_the_checkpoint_does_not_write_the_old_accounts_state()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json";
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, path, Bytes("synced"));
        remote.Set(path, Bytes("synced"));
        await manager.SynchronizeAsync(App, AccountA, remote);
        var stateBefore = await File.ReadAllBytesAsync(manager.StatePath);

        // 沒有要替換、移除、上傳的（state 照樣會寫，記更新時間）：第 1 次在複核手機檔前、第 2 次在寫 state 前
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SyncWhileANewLoginIsSavedAsync(manager, remote, Path.Combine(temp.Path, "steam"), renewAt: 2));

        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(manager.StatePath));
    }

    // ── Terra 第 14 輪：本機寫入拿著鎖進行時出現撤回標記（記錄行為，不是修正）──

    [Fact]
    public async Task Round14_a_revoke_marker_written_during_a_local_write_stops_the_sync_after_that_step()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json";
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, path, Bytes("synced"));
        remote.Set(path, Bytes("synced"));
        await manager.SynchronizeAsync(App, AccountA, remote);
        var stateBefore = await File.ReadAllBytesAsync(manager.StatePath);
        remote.Set(path, Bytes("A cloud"));

        var steam = Directory.CreateDirectory(Path.Combine(temp.Path, "steam")).FullName;
        var installView = Path.Combine(temp.Path, "install-view");
        File.CreateSymbolicLink(installView, steam);
        var ui = new SteamAccessGeneration(steam);
        var install = new SteamAccessGeneration(installView);   // :install 程序
        using var syncA = ui.Begin();
        using var downloadA = install.Begin();
        SteamAccessRevoke? marked = null;
        void WriteWhileInstallRevokes(Action write) => ui.WriteIfCurrent(syncA, () =>
        {
            // 同步拿著鎖、通過第一次檢查之後卡住：:install 發現 A 的 token 失效，拿鎖逾時（5 秒），改寫 A 這一代的撤回標記
            marked ??= install.RevokeIfCurrent(downloadA, () => throw new InvalidOperationException("cleared without the lock"));
            write();
        }, rollback: () => { });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.SynchronizeAsync(App, AccountA, remote, checkpoint: syncA.ThrowIfStale, localWrite: WriteWhileInstallRevokes));

        Assert.Equal(SteamAccessRevoke.Revoked, marked);
        Assert.False(syncA.IsCurrent());
        // 卡住的那一步照樣寫完，跟「這一步寫完才撤回」的結果一樣；寫完再確認時發現撤回就停下，後面的步驟（寫 state）不做
        Assert.Equal(Sha1(Bytes("A cloud")), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(manager.StatePath));
        // 被換掉的手機檔在替換之前已經備份、驗證過
        var backup = Assert.Single(Directory.GetDirectories(manager.GeneralBackupRoot));
        Assert.Equal(Sha1(Bytes("synced")), Sha1(await File.ReadAllBytesAsync(LocalPath(Path.Combine(backup, "device"), path))));
    }

    [Fact]
    public async Task Round14_a_new_login_in_the_same_process_waits_for_the_local_write_in_progress()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json";
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, path, Bytes("synced"));
        remote.Set(path, Bytes("synced"));
        await manager.SynchronizeAsync(App, AccountA, remote);
        var stateBefore = await File.ReadAllBytesAsync(manager.StatePath);
        remote.Set(path, Bytes("A cloud"));

        var ui = new SteamAccessGeneration(Path.Combine(temp.Path, "steam"));
        using var syncA = ui.Begin();
        using var pageB = ui.Begin();
        Task? renewB = null;
        bool savedDuringWrite = true;
        void WriteWhileBLogsIn(Action write) => ui.WriteIfCurrent(syncA, () =>
        {
            if (renewB is null)
            {
                // 同步拿著鎖寫入時，另一個登入頁（同一個程序）要存入 B
                renewB = Task.Run(() => ui.RenewForLogin(pageB, () => { }, () => { }));
                Thread.Sleep(500);
                savedDuringWrite = renewB.IsCompleted;
            }
            write();
        }, rollback: () => { });
        void Checkpoint()
        {
            renewB?.Wait();                             // 這一步寫完、放開鎖之後，B 存好了才往下
            syncA.ThrowIfStale();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.SynchronizeAsync(App, AccountA, remote, checkpoint: Checkpoint, localWrite: WriteWhileBLogsIn));

        Assert.False(savedDuringWrite);                 // B 要等 A 這一步寫完才存得進去：新的登入一定在這一步之後
        Assert.True(pageB.IsCurrent());
        Assert.Equal(Sha1(Bytes("A cloud")), Sha1(await File.ReadAllBytesAsync(LocalPath(persistent, path))));
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(manager.StatePath));   // B 存好之後，A 後面的步驟停下
    }

    /// <summary>
    /// 照 MainActivity 的接法同步 A：checkpoint 是 A 的 scope 的 ThrowIfStale，本機寫入用 WriteIfCurrent 包起來。
    /// 第 renewAt 次 checkpoint 通過之後，另一個登入頁存入 B（RenewForLogin 換代），模擬「檢查之後、寫入之前」換登入。
    /// </summary>
    private static async Task<SteamCloudSyncResult> SyncWhileANewLoginIsSavedAsync(
        SteamCloudSyncManager manager, FakeCloudTransport remote, string steamFolder, int renewAt, SteamCloudConflictApproval? approval = null)
    {
        var access = new SteamAccessGeneration(steamFolder);
        using var syncA = access.Begin();
        using var pageB = access.Begin();
        int calls = 0;
        void Checkpoint()
        {
            syncA.ThrowIfStale();
            if (++calls == renewAt)
                access.RenewForLogin(pageB, () => { }, () => { });
        }
        return await manager.SynchronizeAsync(App, AccountA, remote, approval, checkpoint: Checkpoint,
            localWrite: write => access.WriteIfCurrent(syncA, write, rollback: () => { }));
    }

    // ── 第 3 輪第 1 條：失敗的備份不能佔保留名額 ─────────────

    [Fact]
    public async Task Round3_failed_important_backups_do_not_push_out_a_completed_one()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json";
        await WriteLocalAsync(persistent, path, Bytes("phone before A"));
        var cloudA = new FakeCloudTransport();
        cloudA.Set(path, Bytes("A cloud"));
        var shownA = await manager.SynchronizeAsync(App, AccountA, cloudA);
        await manager.SynchronizeAsync(App, AccountA, cloudA, SteamCloudConflictApproval.For(shownA, SteamCloudConflictChoice.UseCloud));

        var cloudB = new FakeCloudTransport();
        cloudB.Set(path, Bytes("B cloud"));
        var shownB = await manager.SynchronizeAsync(App, AccountB, cloudB);
        // 接著 10 次重要備份都在寫到一半時中止（空間不足、撤回都是這樣）
        for (int i = 0; i < 10; i++)
        {
            using var cts = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SynchronizeAsync(App, AccountB, cloudB,
                SteamCloudConflictApproval.For(shownB, SteamCloudConflictChoice.UseCloud), checkpoint: cts.Cancel, cancellationToken: cts.Token));
        }
        Assert.Equal(SteamCloudSyncResultKind.Synced, (await manager.SynchronizeAsync(App, AccountB, cloudB,
            SteamCloudConflictApproval.For(shownB, SteamCloudConflictChoice.UseCloud))).Kind);

        var kept = Directory.GetFiles(manager.ImportantBackupRoot, path, SearchOption.AllDirectories)
            .Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))).ToList();
        Assert.Contains("phone before A", kept);
        Assert.Contains("A cloud", kept);
        Assert.Equal(2, Directory.GetDirectories(manager.ImportantBackupRoot).Length);
        Assert.Empty(Directory.GetDirectories(manager.ImportantBackupRoot, ".partial-*"));
    }

    [Fact]
    public async Task Round3_failed_general_backups_do_not_push_out_a_completed_one()
    {
        using var temp = new TemporaryDirectory();
        var persistent = Directory.CreateDirectory(Path.Combine(temp.Path, "persistent")).FullName;
        var manager = new SteamCloudSyncManager(persistent, Path.Combine(temp.Path, "cloud"));
        const string path = "metagameSave.json";
        var remote = new FakeCloudTransport();
        await WriteLocalAsync(persistent, path, Bytes("v0"));
        remote.Set(path, Bytes("v0"));
        await manager.SynchronizeAsync(App, AccountA, remote);
        await WriteLocalAsync(persistent, path, Bytes("v1"));
        Assert.Equal(1, (await manager.SynchronizeAsync(App, AccountA, remote)).Uploaded);

        await WriteLocalAsync(persistent, path, Bytes("v2"));
        for (int i = 0; i < 10; i++)
        {
            using var cts = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                manager.SynchronizeAsync(App, AccountA, remote, checkpoint: cts.Cancel, cancellationToken: cts.Token));
        }
        Assert.Equal(1, (await manager.SynchronizeAsync(App, AccountA, remote)).Uploaded);

        var kept = Directory.GetFiles(manager.GeneralBackupRoot, path, SearchOption.AllDirectories)
            .Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))).ToList();
        Assert.Contains("v0", kept);
        Assert.Contains("v1", kept);
        Assert.Equal(2, Directory.GetDirectories(manager.GeneralBackupRoot).Length);
        Assert.Empty(Directory.GetDirectories(manager.GeneralBackupRoot, ".partial-*"));
    }

    private static string? ReadAccountKey(SteamCloudSyncManager manager)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(manager.StatePath));
        return state.RootElement.TryGetProperty("accountKey", out var key) ? key.GetString() : null;
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task WriteLocalAsync(string root, string relative, byte[] bytes)
    {
        var path = LocalPath(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static string LocalPath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private sealed class FakeCloudTransport : ISteamCloudTransport
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SteamCloudFileRecord> Records { get; } = new(StringComparer.OrdinalIgnoreCase);
        private List<SteamCloudFileRecord>? _nextDelta;
        public int DownloadCount { get; private set; }
        public int UploadCount { get; private set; }
        public int DeleteCount { get; private set; }
        private ulong _changeNumber = 10;
        public ulong LastRequestedChangeNumber { get; private set; }
        /// <summary>回傳清單之後跑一次（模擬別台裝置在讀清單之後才上傳）</summary>
        public Action? AfterNextManifest { get; set; }
        /// <summary>每次下載前跑（模擬遊戲在同步途中寫存檔）</summary>
        public Action<string>? BeforeDownload { get; set; }

        public FakeCloudTransport(ulong changeNumber = 10) => _changeNumber = changeNumber;

        public void Set(string relative, byte[] bytes)
        {
            var remote = Record(relative, bytes);
            Records[relative] = remote;
            Files[relative] = bytes;
        }

        public Task<SteamCloudManifest> GetManifestAsync(uint appId, ulong syncedChangeNumber, CancellationToken cancellationToken = default)
        {
            LastRequestedChangeNumber = syncedChangeNumber;
            SteamCloudManifest manifest;
            if (_nextDelta is { } delta)
            {
                _nextDelta = null;
                manifest = new SteamCloudManifest(_changeNumber, IsOnlyDelta: true, delta);
            }
            else
            {
                manifest = new SteamCloudManifest(_changeNumber, IsOnlyDelta: false, Records.Values.ToList());
            }
            var after = AfterNextManifest;
            AfterNextManifest = null;
            after?.Invoke();
            return Task.FromResult(manifest);
        }

        public Task<byte[]> DownloadFileAsync(uint appId, string remoteFileName, long expectedRawSize, string expectedSha1,
            Action<string>? onHostObserved = null, CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            var found = Records.Values.FirstOrDefault(f => f.RemoteFileName == remoteFileName)
                ?? throw new InvalidOperationException("Missing fake remote file.");
            BeforeDownload?.Invoke(found.LocalRelativePath);
            return Task.FromResult(Files[found.LocalRelativePath]);
        }

        public Task UploadBatchAsync(uint appId, string machineName, IReadOnlyList<SteamCloudUpload> files,
            Action<string>? onHostObserved = null, CancellationToken cancellationToken = default)
        {
            UploadCount += files.Count;
            foreach (var file in files)
            {
                var record = Record(file.LocalRelativePath, file.Bytes, file.ModifiedAtUtc);
                Records[file.LocalRelativePath] = record;
                Files[file.LocalRelativePath] = file.Bytes;
            }
            _changeNumber++;
            return Task.CompletedTask;
        }

        public void ResetCounters() => (DownloadCount, UploadCount, DeleteCount) = (0, 0, 0);
        public void Remove(string relative)
        {
            Records.Remove(relative);
            Files.Remove(relative);
        }

        public void SetDelta(string relative, byte[] bytes)
        {
            Set(relative, bytes);
            _changeNumber++;
            _nextDelta = [Records[relative]];
        }

        private static SteamCloudFileRecord Record(string relative, byte[] bytes, DateTimeOffset? timestamp = null)
        {
            var fileName = SteamCloudPathMapper.GamePathPrefix + relative;
            var remoteName = SteamCloudPathMapper.ExpectedRootPrefix + "/" + fileName;
            return new SteamCloudFileRecord(remoteName, relative, SteamCloudPathMapper.ExpectedRootPrefix, fileName,
                "fake device", 0, 0, bytes.Length, timestamp ?? DateTimeOffset.UtcNow, Sha1(bytes));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mt2-cloud-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
