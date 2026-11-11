using Xunit;

namespace Mt2Mobile.Steam.Tests;

/// <summary>
/// 存取世代的檔案邏輯（Terra 第 2 輪第 5 條）。真的跨程序（介面、:install、:game 三個程序）在桌面測不到；
/// 這裡用「直接改世代檔」模擬別的程序換代、用新的實例模擬程序重啟。
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class SteamAccessGenerationTests
{
    [Fact]
    public void Starts_at_the_initial_generation_and_survives_a_process_restart()
    {
        using var temp = new TemporaryDirectory();
        Assert.Equal(SteamAccessGeneration.Initial, new SteamAccessGeneration(temp.Path).Current());
        Assert.Equal(SteamAccessGeneration.Initial, new SteamAccessGeneration(temp.Path).Current());
    }

    [Fact]
    public void Every_revoke_writes_a_new_generation_that_never_repeats_or_goes_back()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        var seen = new HashSet<string> { access.Current() };
        for (int i = 0; i < 5; i++)
        {
            access.Revoke(() => { });
            var restarted = new SteamAccessGeneration(temp.Path).Current();
            Assert.NotEqual(SteamAccessGeneration.Initial, restarted);
            Assert.True(seen.Add(restarted), "generation repeated");
        }
    }

    [Fact]
    public void Revoke_in_the_same_process_cancels_running_work_immediately()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var work = access.Begin();
        bool cleared = false;

        access.Revoke(() => cleared = true);

        Assert.True(cleared);
        Assert.True(work.Token.IsCancellationRequested);
        Assert.False(work.IsCurrent());
        Assert.Throws<OperationCanceledException>(work.ThrowIfStale);
    }

    [Fact]
    public async Task A_revoke_in_another_process_cancels_the_token_within_a_few_seconds()
    {
        using var temp = new TemporaryDirectory();
        using var work = new SteamAccessGeneration(temp.Path).Begin();

        // 別的程序換代：只有檔案變了，這個程序的 CTS 沒被取消
        File.WriteAllText(Path.Combine(temp.Path, "access-generation"), "ffffffffffffffffffffffffffffffff");

        Assert.False(work.IsCurrent());
        await Task.Delay(TimeSpan.FromSeconds(5), work.Token).ContinueWith(_ => { });
        Assert.True(work.Token.IsCancellationRequested);
    }

    [Fact]
    public void Stale_work_cannot_clear_or_overwrite_a_newer_login()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var oldWork = access.Begin();
        access.Revoke(() => { });               // 使用者登出
        using var newLogin = access.Begin();    // 接著登入另一個帳號
        var generation = access.Current();

        bool cleared = false, oldWrote = false, newWrote = false;
        Assert.Equal(SteamAccessRevoke.NotCurrent, access.RevokeIfCurrent(oldWork, () => cleared = true));
        Assert.Throws<OperationCanceledException>(() => access.WriteIfCurrent(oldWork, () => oldWrote = true, () => { }));
        access.WriteIfCurrent(newLogin, () => newWrote = true, () => { });

        Assert.False(cleared);
        Assert.False(oldWrote);
        Assert.True(newWrote);
        Assert.Equal(generation, access.Current());
        Assert.True(newLogin.IsCurrent());
    }

    [Fact]
    public void Current_work_that_finds_its_token_invalid_revokes_once()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var work = access.Begin();
        int cleared = 0;

        Assert.Equal(SteamAccessRevoke.Revoked, access.RevokeIfCurrent(work, () => cleared++));
        Assert.Equal(SteamAccessRevoke.NotCurrent, access.RevokeIfCurrent(work, () => cleared++));

        Assert.Equal(1, cleared);
        Assert.True(work.Token.IsCancellationRequested);
    }

    [Fact]
    public void An_empty_generation_means_revoked_and_the_next_reader_starts_a_new_one()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var work = access.Begin();

        File.WriteAllText(Path.Combine(temp.Path, "access-generation"), "");

        Assert.False(work.IsCurrent());
        Assert.Throws<OperationCanceledException>(work.ThrowIfStale);
        Assert.Throws<OperationCanceledException>(() => access.WriteIfCurrent(work, () => { }, () => { }));
        using var next = access.Begin();
        Assert.NotEqual(SteamAccessGeneration.Initial, next.Generation);
        Assert.NotEqual(work.Generation, next.Generation);
        Assert.True(next.IsCurrent());
    }

    [Fact]
    public void An_unreadable_generation_stops_work_instead_of_allowing_it()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var work = access.Begin();
        var generation = Path.Combine(temp.Path, "access-generation");

        File.SetUnixFileMode(generation, UnixFileMode.None);
        try
        {
            Assert.False(work.IsCurrent());
            Assert.Throws<OperationCanceledException>(work.ThrowIfStale);
            Assert.ThrowsAny<Exception>(() => access.Begin());
        }
        finally { File.SetUnixFileMode(generation, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public void User_revoke_still_clears_when_the_cross_process_lock_is_held()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var work = access.Begin();
        bool cleared = false;

        // 別的程序拿著鎖不放：使用者的登出等最多 5 秒，之後照樣換代、照樣清
        var waited = System.Diagnostics.Stopwatch.StartNew();
        using (new FileStream(Path.Combine(temp.Path, "access.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            access.Revoke(() => cleared = true);
        waited.Stop();

        Assert.True(waited.Elapsed >= TimeSpan.FromSeconds(4), $"lock was not contended ({waited.Elapsed})");
        Assert.True(cleared);
        Assert.False(work.IsCurrent());
    }

    // ── 第 3 輪第 2 條：撤回的兩條例外路徑 ───────────────────
    // 用指到同一個資料夾的符號連結開第二個實例：檔案（世代檔、flock）共用，同程序的 Monitor 與 CTS 不共用，等於另一個程序。

    private static (string Folder, string OtherProcessView) TwoProcessViews(TemporaryDirectory temp)
    {
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "steam")).FullName;
        var view = Path.Combine(temp.Path, "other-process-view");
        File.CreateSymbolicLink(view, folder);
        return (folder, view);
    }

    [Fact]
    public void Round3_revoke_that_cannot_write_a_new_generation_still_stops_other_processes()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        using var download = new SteamAccessGeneration(view).Begin();
        File.WriteAllText(Path.Combine(folder, "access.lock"), "");
        bool cleared = false;

        // 空間不足：資料夾寫不進新的檔（暫存檔建不起來）
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try { ui.Revoke(() => cleared = true); }
        finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

        Assert.True(cleared);
        Assert.False(download.IsCurrent());
        Assert.Throws<OperationCanceledException>(download.ThrowIfStale);
        // 空間回來之後，下一段工作拿到新的世代（不是 "0"，舊版沒記世代的登入也不會復活）
        using var next = ui.Begin();
        Assert.NotEqual(SteamAccessGeneration.Initial, next.Generation);
    }

    [Fact]
    public void Round3_revoke_that_cannot_touch_the_generation_at_all_reports_it()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        using var download = new SteamAccessGeneration(view).Begin();
        using var sameProcess = ui.Begin();
        var generation = Path.Combine(folder, "access-generation");
        File.WriteAllText(Path.Combine(folder, "access.lock"), "");
        bool cleared = false;
        SteamAccessRevoke visible;

        File.SetUnixFileMode(generation, UnixFileMode.UserRead);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try { visible = ui.Revoke(() => cleared = true); }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(generation, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // 別的程序看不到這次撤回：回報 false，呼叫端要自己停掉別的程序；同程序的工作照樣取消、資料照樣清
        Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, visible);
        Assert.True(cleared);
        Assert.True(sameProcess.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Round3_a_writer_holding_the_lock_cannot_write_back_after_a_revoke()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        var other = new SteamAccessGeneration(view);
        var prefs = new Dictionary<string, string> { ["token"] = "logged in" };   // 兩個程序共用的設定檔
        using var work = other.Begin();
        using var inside = new ManualResetEventSlim();
        using var revoked = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            try
            {
                other.WriteIfCurrent(work, () =>
                {
                    inside.Set();
                    revoked.Wait(TimeSpan.FromSeconds(30));
                    lock (prefs) prefs["owned_dlc"] = "written by stale work";
                }, rollback: () => { lock (prefs) prefs.Clear(); });
            }
            catch (OperationCanceledException) { }
        });
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));

        // 鎖被拿著超過 5 秒：撤回照樣換代、清掉；之後舊的寫入才完成
        ui.Revoke(() => { lock (prefs) prefs.Clear(); });
        revoked.Set();
        await writer;

        lock (prefs) Assert.Empty(prefs);
    }

    // ── 第 4 輪：寫不了世代時這個程序要 fail closed；空世代的補寫不能蓋掉別的程序 ──

    /// <summary>儲存空間整個寫不了：世代檔與資料夾都不能寫（新檔建不起來、舊檔也不能截斷）</summary>
    private static void MakeUnwritable(string folder) =>
        SetModes(folder, UnixFileMode.UserRead, UnixFileMode.UserRead | UnixFileMode.UserExecute);

    private static void MakeWritable(string folder) =>
        SetModes(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    private static void SetModes(string folder, UnixFileMode file, UnixFileMode directory)
    {
        if (directory.HasFlag(UnixFileMode.UserWrite)) File.SetUnixFileMode(folder, directory);
        File.SetUnixFileMode(Path.Combine(folder, "access-generation"), file);
        if (!directory.HasFlag(UnixFileMode.UserWrite)) File.SetUnixFileMode(folder, directory);
    }

    [Fact]
    public void Round4_token_invalid_revoke_that_cannot_write_fails_closed_in_this_process()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var sync = access.Begin();
        File.WriteAllText(Path.Combine(temp.Path, "access.lock"), "");
        bool cleared = false;

        MakeUnwritable(temp.Path);
        try
        {
            var result = access.RevokeIfCurrent(sync, () => cleared = true);

            // 磁碟上的世代沒換成，這個程序還是要當成已撤回：舊的工作不算數，也不准開始新的；結果要回報出去，不能吞掉
            Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, result);
            Assert.True(cleared);
            Assert.False(sync.IsCurrent());
            Assert.ThrowsAny<Exception>(() => access.Begin());
        }
        finally { MakeWritable(temp.Path); }
    }

    [Fact]
    public void Round4_user_revoke_that_cannot_write_fails_closed_in_this_process()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var sync = access.Begin();
        File.WriteAllText(Path.Combine(temp.Path, "access.lock"), "");

        MakeUnwritable(temp.Path);
        try
        {
            Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, access.Revoke(() => { }));

            Assert.False(sync.IsCurrent());
            Assert.ThrowsAny<Exception>(() => access.Begin());
        }
        finally { MakeWritable(temp.Path); }
    }

    [Fact]
    public void Round4_a_pending_revoke_is_written_before_new_work_once_storage_is_writable_again()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        using var download = new SteamAccessGeneration(view).Begin();   // 別的程序的舊工作
        File.WriteAllText(Path.Combine(folder, "access.lock"), "");

        MakeUnwritable(folder);
        try { Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, ui.Revoke(() => { })); }
        finally { MakeWritable(folder); }
        Assert.True(download.IsCurrent());   // 寫不了的這段期間，別的程序確實看不到（不保證，所以要提示使用者）

        using var next = ui.Begin();         // 空間回來：開始新工作前先補換代
        Assert.False(download.IsCurrent());
        Assert.NotEqual(download.Generation, next.Generation);
        Assert.True(next.IsCurrent());
    }

    [Fact]
    public async Task Round4_repairing_an_empty_generation_does_not_overwrite_another_processes_repair()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        ui.Begin().Dispose();
        var generation = Path.Combine(folder, "access-generation");
        File.WriteAllText(generation, "");
        const string repairedByOther = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // 另一個程序正在補寫（拿著鎖），這個程序同時也讀到空的
        Task<SteamAccessScope> begin;
        using (new FileStream(Path.Combine(view, "access.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            begin = Task.Run(ui.Begin);
            Thread.Sleep(500);
            File.WriteAllText(Path.Combine(view, "access-generation"), repairedByOther);
        }
        using var work = await begin;

        Assert.Equal(repairedByOther, File.ReadAllText(generation));
        Assert.Equal(repairedByOther, work.Generation);
        Assert.True(work.IsCurrent());
    }

    // ── 第 5 輪第 1 條：拿不到鎖的撤回也要 fail closed，不能在取消之前丟例外 ──
    // 沒有鎖寫的世代，可能被拿著鎖補寫的程序蓋掉，別的程序不保證看得到：一律回 OnlyInThisProcess。

    /// <summary>鎖檔開不了（沒有寫入權限，開檔丟 UnauthorizedAccessException，不是 IOException）</summary>
    private static string MakeLockUnopenable(string folder)
    {
        var lockPath = Path.Combine(folder, "access.lock");
        File.WriteAllText(lockPath, "");
        File.SetUnixFileMode(lockPath, UnixFileMode.None);
        return lockPath;
    }

    private static void MakeLockOpenable(string lockPath) =>
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    [Fact]
    public void Round5_user_revoke_with_an_unopenable_lock_fails_closed_without_throwing()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        using var download = new SteamAccessGeneration(view).Begin();
        using var sameProcess = ui.Begin();
        bool cleared = false;

        var lockPath = MakeLockUnopenable(folder);
        try
        {
            Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, ui.Revoke(() => cleared = true));

            Assert.True(cleared);
            Assert.True(sameProcess.Token.IsCancellationRequested);
            Assert.False(sameProcess.IsCurrent());
            Assert.False(download.IsCurrent());               // 世代照樣盡力換，別的程序多半看得到
            Assert.ThrowsAny<Exception>(() => ui.Begin());    // 鎖還是開不了：新的工作開不起來
        }
        finally { MakeLockOpenable(lockPath); }

        var revoked = File.ReadAllText(Path.Combine(folder, "access-generation"));
        using var next = ui.Begin();                          // 鎖回來：先在鎖裡補換代，才開始新的工作
        Assert.NotEqual(revoked, next.Generation);
        Assert.True(next.IsCurrent());
    }

    [Fact]
    public void Round5_token_invalid_revoke_with_an_unopenable_lock_fails_closed_without_throwing()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var sync = access.Begin();
        bool cleared = false;

        var lockPath = MakeLockUnopenable(temp.Path);
        try
        {
            // 第 7 輪：拿不到鎖但寫得進檔案，就寫「這一代已撤回」的標記，別的程序看得到
            Assert.Equal(SteamAccessRevoke.Revoked, access.RevokeIfCurrent(sync, () => cleared = true));

            Assert.False(cleared);                            // 第 6 輪：舊工作拿不到鎖時不清、不換代
            Assert.True(sync.Token.IsCancellationRequested);
            Assert.False(sync.IsCurrent());
            Assert.ThrowsAny<Exception>(() => access.Begin());
        }
        finally { MakeLockOpenable(lockPath); }
    }

    [Fact]
    public void Round5_stale_work_with_an_unopenable_lock_still_cannot_clear_a_newer_login()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var oldWork = access.Begin();
        access.Revoke(() => { });               // 使用者登出
        using var newLogin = access.Begin();    // 接著登入另一個帳號
        var generation = access.Current();
        bool cleared = false;

        var lockPath = MakeLockUnopenable(temp.Path);
        try
        {
            Assert.Equal(SteamAccessRevoke.NotCurrent, access.RevokeIfCurrent(oldWork, () => cleared = true));
        }
        finally { MakeLockOpenable(lockPath); }

        Assert.False(cleared);
        Assert.Equal(generation, access.Current());
        Assert.True(newLogin.IsCurrent());
        Assert.False(newLogin.Token.IsCancellationRequested);
    }

    [Fact]
    public void Round5_user_revoke_with_the_lock_held_too_long_reports_only_in_this_process()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        using var sameProcess = ui.Begin();
        bool cleared = false;
        SteamAccessRevoke result;

        // 別的程序拿著鎖超過 5 秒
        using (new FileStream(Path.Combine(view, "access.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            result = ui.Revoke(() => cleared = true);

        Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, result);
        Assert.True(cleared);
        Assert.True(sameProcess.Token.IsCancellationRequested);
        var revoked = File.ReadAllText(Path.Combine(folder, "access-generation"));
        using var next = ui.Begin();            // 鎖放掉之後：先在鎖裡補換代
        Assert.NotEqual(revoked, next.Generation);
    }

    [Fact]
    public void Round5_token_invalid_revoke_with_the_lock_held_too_long_fails_closed_without_throwing()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        using var sync = ui.Begin();
        bool cleared = false;
        SteamAccessRevoke result;

        using (new FileStream(Path.Combine(view, "access.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            result = ui.RevokeIfCurrent(sync, () => cleared = true);

        Assert.Equal(SteamAccessRevoke.Revoked, result);     // 第 7 輪：寫了這一代的撤回標記
        Assert.False(cleared);                  // 第 6 輪：舊工作拿不到鎖時不清、不換代
        Assert.True(sync.Token.IsCancellationRequested);
        Assert.False(sync.IsCurrent());
    }

    [Fact]
    public void Round5_a_revoke_whose_clear_throws_still_cancels_this_processes_work_first()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var user = access.Begin();

        Assert.Throws<InvalidOperationException>(() => access.Revoke(() => throw new InvalidOperationException()));
        Assert.True(user.Token.IsCancellationRequested);

        using var sync = access.Begin();
        Assert.Throws<InvalidOperationException>(() => access.RevokeIfCurrent(sync, () => throw new InvalidOperationException()));
        Assert.True(sync.Token.IsCancellationRequested);
    }

    // ── 第 6 輪 ─────────────────────────────────────────────

    /// <summary>啟動器的日誌檔也沒有寫入權限：記錄本身丟 UnauthorizedAccessException</summary>
    private static void ThrowingLog(string _) => throw new UnauthorizedAccessException("the launcher log is not writable");

    [Fact]
    public void Round6_a_log_that_throws_cannot_break_a_revoke_without_the_lock()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path, ThrowingLog);
        using var work = access.Begin();

        var lockPath = MakeLockUnopenable(temp.Path);
        try
        {
            Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, access.Revoke(() => { }));
            Assert.True(work.Token.IsCancellationRequested);
        }
        finally { MakeLockOpenable(lockPath); }
    }

    [Fact]
    public void Round6_a_log_that_throws_cannot_break_a_token_invalid_revoke_that_cannot_write()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path, ThrowingLog);
        using var sync = access.Begin();
        File.WriteAllText(Path.Combine(temp.Path, "access.lock"), "");

        MakeUnwritable(temp.Path);
        try
        {
            Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, access.RevokeIfCurrent(sync, () => { }));
            Assert.True(sync.Token.IsCancellationRequested);
            Assert.False(sync.IsCurrent());
        }
        finally { MakeWritable(temp.Path); }
    }

    /// <summary>拿不到鎖、檔案也寫不進去（標記寫不了）：撤回只能留在這個程序</summary>
    private static string MakeLockAndStorageUnwritable(string folder)
    {
        var lockPath = MakeLockUnopenable(folder);
        MakeUnwritable(folder);
        return lockPath;
    }

    private static void MakeLockAndStorageWritable(string folder, string lockPath)
    {
        MakeWritable(folder);
        MakeLockOpenable(lockPath);
    }

    [Fact]
    public void Round6_token_invalid_revoke_that_cannot_write_anything_stays_in_this_process()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var install = new SteamAccessGeneration(view);
        using var download = install.Begin();                      // :install 的舊工作
        using var uiWork = new SteamAccessGeneration(folder).Begin(); // 介面程序的工作
        var generation = Path.Combine(folder, "access-generation");
        var before = File.ReadAllText(generation);
        bool cleared = false;

        var lockPath = MakeLockAndStorageUnwritable(folder);
        try { Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, install.RevokeIfCurrent(download, () => cleared = true)); }
        finally { MakeLockAndStorageWritable(folder, lockPath); }

        // 沒有鎖就沒辦法跟別的程序互斥：不換代、不清；標記也寫不進去，只在這個程序 fail closed
        Assert.False(cleared);
        Assert.Equal(before, File.ReadAllText(generation));
        Assert.True(download.Token.IsCancellationRequested);
        Assert.False(download.IsCurrent());
        Assert.True(uiWork.IsCurrent());
    }

    [Fact]
    public void Round6_a_login_made_after_the_check_survives_the_deferred_token_invalid_revoke()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var install = new SteamAccessGeneration(view);
        var ui = new SteamAccessGeneration(folder);
        var prefs = new Dictionary<string, string> { ["token"] = "old login" };   // 兩個程序共用的設定檔
        using var download = install.Begin();

        // :install 的舊工作發現 token 失效、通過世代檢查，但拿不到鎖，標記也寫不進去
        var lockPath = MakeLockAndStorageUnwritable(folder);
        try { install.RevokeIfCurrent(download, () => prefs.Clear()); }
        finally { MakeLockAndStorageWritable(folder, lockPath); }
        Assert.Equal("old login", prefs["token"]);

        // 之後介面程序登出、登入新的帳號
        ui.Revoke(() => prefs.Clear());
        using var login = ui.Begin();
        ui.WriteIfCurrent(login, () => prefs["token"] = "new login", () => prefs.Clear());
        var generation = ui.Current();

        // :install 再開始工作時先補記下的撤回：世代已經不是當時那一代，不能把新登入判成失效
        using var next = install.Begin();
        Assert.Equal("new login", prefs["token"]);
        Assert.Equal(generation, ui.Current());
        Assert.True(login.IsCurrent());
        Assert.Equal(generation, next.Generation);
    }

    [Fact]
    public void Round6_the_deferred_token_invalid_revoke_is_written_once_the_lock_is_back()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        using var sync = ui.Begin();                                   // 介面發現 token 失效的工作
        using var download = new SteamAccessGeneration(view).Begin();  // 別的程序的舊工作

        var lockPath = MakeLockAndStorageUnwritable(folder);
        try
        {
            Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, ui.RevokeIfCurrent(sync, () => { }));
            Assert.ThrowsAny<Exception>(() => ui.Begin());             // 鎖還沒回來：新的工作開不起來
        }
        finally { MakeLockAndStorageWritable(folder, lockPath); }
        Assert.True(download.IsCurrent());     // 這段期間別的程序看不到（所以要告訴使用者撤回沒有完成）

        using var next = ui.Begin();           // 鎖回來、世代還是同一代：先補換代
        Assert.False(download.IsCurrent());
        Assert.NotEqual(download.Generation, next.Generation);
        Assert.True(next.IsCurrent());
    }

    // ── 第 7 輪：拿不到鎖的撤回不能只活在那個程序的記憶體裡 ──

    /// <summary>再開一個「程序」：另一個指到同一個資料夾的符號連結，跟之前的實例不共用記憶體</summary>
    private static string AnotherProcessView(TemporaryDirectory temp, string folder, string name)
    {
        var view = Path.Combine(temp.Path, name);
        File.CreateSymbolicLink(view, folder);
        return view;
    }

    [Fact]
    public void Round7_a_token_invalid_revoke_without_the_lock_outlives_the_install_process()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        using var cloud = new SteamAccessGeneration(folder).Begin();   // 介面程序正在用同一代（Cloud）
        var revoked = cloud.Generation;

        var install = new SteamAccessGeneration(view);
        using (var download = install.Begin())
        {
            var lockPath = MakeLockUnopenable(folder);
            try { Assert.Equal(SteamAccessRevoke.Revoked, install.RevokeIfCurrent(download, () => { })); }
            finally { MakeLockOpenable(lockPath); }
        }
        // :install 結束，它記憶體裡記的都不算；之後的程序（新的實例）一樣要看到這一代已經撤回
        var restarted = new SteamAccessGeneration(AnotherProcessView(temp, folder, "restarted-install"));

        Assert.Null(restarted.Peek());
        Assert.False(cloud.IsCurrent());
        using var next = restarted.Begin();
        Assert.NotEqual(revoked, next.Generation);
        Assert.True(next.IsCurrent());
    }

    [Fact]
    public void Round7_a_revoked_marker_only_revokes_its_own_generation()
    {
        using var temp = new TemporaryDirectory();
        var access = new SteamAccessGeneration(temp.Path);
        using var oldWork = access.Begin();
        access.Revoke(() => { });               // 使用者登出
        using var login = access.Begin();       // 接著登入另一個帳號

        // 舊工作通過檢查之後才寫下標記（拿不到鎖的那條路）：只影響當時那一代，不碰新的登入
        File.WriteAllText(Path.Combine(temp.Path, "access-revoked-" + oldWork.Generation), "");
        Assert.True(login.IsCurrent());
        Assert.Equal(login.Generation, access.Peek());

        File.WriteAllText(Path.Combine(temp.Path, "access-revoked-" + login.Generation), "");
        Assert.False(login.IsCurrent());
        Assert.Null(access.Peek());
    }

    [Fact]
    public void Round7_the_ui_revokes_the_generation_the_install_process_reported()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        using var cloud = ui.Begin();           // 介面程序正在用這一代（Cloud）
        var reported = cloud.Generation;        // :install 撤回沒完成時傳過來的那一代
        bool cleared = false;

        Assert.Equal(SteamAccessRevoke.Revoked, ui.RevokeIfGeneration(reported, () => cleared = true));
        Assert.True(cleared);
        Assert.True(cloud.Token.IsCancellationRequested);

        // 傳過來的時候已經換過代（使用者登出又登入）：不動新的登入
        using var login = ui.Begin();
        cleared = false;
        Assert.Equal(SteamAccessRevoke.NotCurrent, ui.RevokeIfGeneration(reported, () => cleared = true));
        Assert.False(cleared);
        Assert.True(login.IsCurrent());
        Assert.False(login.Token.IsCancellationRequested);
    }

    [Fact]
    public void Round7_the_ui_fails_closed_when_it_cannot_revoke_the_reported_generation_either()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        using var cloud = ui.Begin();
        File.WriteAllText(Path.Combine(temp.Path, "access.lock"), "");

        MakeUnwritable(temp.Path);
        try
        {
            Assert.Equal(SteamAccessRevoke.OnlyInThisProcess, ui.RevokeIfGeneration(cloud.Generation, () => { }));
            Assert.True(cloud.Token.IsCancellationRequested);   // 介面的 Cloud 工作停下來
            Assert.False(cloud.IsCurrent());
            Assert.True(ui.RevokePending);                       // 介面開遊戲時不給 Steam 身分
            Assert.ThrowsAny<Exception>(() => ui.Begin());       // 新的工作開不起來
        }
        finally { MakeWritable(temp.Path); }

        using var next = ui.Begin();                             // 空間回來：補換代之後就不算待補
        Assert.False(ui.RevokePending);
    }

    // ── 第 8 輪 ─────────────────────────────────────────────

    [Fact]
    public void Round8_a_marker_cleaned_up_while_reading_does_not_let_the_revoked_generation_pass()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var other = new SteamAccessGeneration(folder);   // 程序乙
        var reader = new SteamAccessGeneration(view);    // 程序甲
        using var work = reader.Begin();                 // 世代 A
        var revoked = work.Generation;
        // A 已經撤回：別的程序拿不到鎖時寫的標記
        File.WriteAllText(Path.Combine(folder, "access-revoked-" + revoked), "");

        // 甲讀到 A、還沒看標記時，乙在鎖裡補寫新世代 B，並清掉 A 的標記
        int interleaved = 0;
        reader.BetweenReadAndMarkerCheck = () =>
        {
            if (Interlocked.Exchange(ref interleaved, 1) == 0)
                other.Current();
        };

        Assert.False(work.IsCurrent());     // 這次檢查開始時 A 已經撤回，不能通過
        Assert.Equal(1, interleaved);
        Assert.NotEqual(revoked, reader.Peek());
    }

    [Fact]
    public async Task Round8_a_reported_revoke_counts_before_the_background_revoke_finishes()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        using var cloud = ui.Begin();
        var reported = cloud.Generation;

        // 別的程序拿著鎖：介面收到 broadcast 之後，背景撤回卡在等鎖
        var holder = new FileStream(Path.Combine(view, "access.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            ui.NoteRevokeReported(reported);   // receiver 一收到就同步做
            var relay = Task.Run(() => ui.RevokeIfGeneration(reported, () => { }));
            Thread.Sleep(500);

            // 這時開遊戲：要帶旗標，也不能被卡在等鎖的背景撤回擋住；同一代的工作不算數
            var waited = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(ui.RevokePending);
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(1), $"RevokePending waited {waited.Elapsed}");
            Assert.False(cloud.IsCurrent());

            holder.Dispose();
            Assert.Equal(SteamAccessRevoke.Revoked, await relay);
        }
        finally { holder.Dispose(); }

        Assert.False(ui.RevokePending);        // 背景撤回寫進去了：世代換了，不用再擋
        using var next = ui.Begin();
        Assert.NotEqual(reported, next.Generation);
    }

    [Fact]
    public void Round8_a_reported_generation_does_not_touch_newer_work()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        using var old = ui.Begin();
        ui.Revoke(() => { });                   // 已經換過代
        using var login = ui.Begin();

        ui.NoteRevokeReported(old.Generation);  // 晚到的 broadcast
        Assert.True(login.IsCurrent());
        Assert.False(ui.RevokePending);
    }

    // ── 第 9 輪：已回報的那一代，每個入口都要一起看 ──

    [Fact]
    public void Round9_a_reported_revoke_stays_pending_while_the_generation_is_unreadable()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        using var cloud = ui.Begin();
        ui.NoteRevokeReported(cloud.Generation);
        var generation = Path.Combine(temp.Path, "access-generation");

        // 背景撤回還沒做完，世代一時讀不到：開遊戲還是要帶旗標
        File.SetUnixFileMode(generation, UnixFileMode.None);
        try { Assert.True(ui.RevokePending); }
        finally { File.SetUnixFileMode(generation, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        Assert.True(ui.RevokePending);          // 讀得到了，還是那一代
    }

    [Fact]
    public void Round9_an_unreadable_generation_without_a_report_does_not_block_steam_identity()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        using var work = ui.Begin();
        var generation = Path.Combine(temp.Path, "access-generation");

        // 沒有撤回紀錄：讀不到時遊戲自己讀身分也會失敗（不給 Steam 身分），這裡不另外擋
        File.SetUnixFileMode(generation, UnixFileMode.None);
        try { Assert.False(ui.RevokePending); }
        finally { File.SetUnixFileMode(generation, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public void Round9_after_a_report_no_new_work_starts_on_the_reported_generation()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        string reported;
        using (var cloud = ui.Begin())
            reported = cloud.Generation;
        bool cleared = false;

        ui.NoteRevokeReported(reported);        // 收到 broadcast，背景撤回還沒拿到 Gate
        Assert.Null(ui.Peek());                 // 登入判斷看的就是這個：那一代不算登入
        using var next = ui.Begin();            // 帳號頁重新檢查：不能在那一代開始，在鎖裡補寫新世代
        Assert.NotEqual(reported, next.Generation);
        Assert.True(next.IsCurrent());
        Assert.NotEqual(reported, new SteamAccessGeneration(view).Peek());   // 別的程序也看到換代

        // 背景撤回之後才拿到 Gate：世代已經換過，不動新的
        Assert.Equal(SteamAccessRevoke.NotCurrent, ui.RevokeIfGeneration(reported, () => cleared = true));
        Assert.False(cleared);
        Assert.True(next.IsCurrent());
    }

    [Fact]
    public void Round9_after_a_report_begin_fails_when_it_cannot_move_off_the_reported_generation()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        var reported = ui.Current();
        File.WriteAllText(Path.Combine(temp.Path, "access.lock"), "");
        ui.NoteRevokeReported(reported);

        MakeUnwritable(temp.Path);
        try
        {
            Assert.ThrowsAny<Exception>(() => ui.Begin());   // 寫不了新世代：不准在那一代開始新的工作
            Assert.Null(ui.Peek());
            Assert.True(ui.RevokePending);
        }
        finally { MakeWritable(temp.Path); }
    }

    // ── 第 10 輪：換代後存了新登入，舊帳號的遊戲身分與授權不能跟著變回有效 ──

    private const ulong AccountA = 1001, AccountB = 1002;   // 假的 Steam ID，只比相等

    [Fact]
    public void Round10_a_new_login_after_a_reported_revoke_does_not_revive_the_old_accounts_game_identity()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        var game = new SteamAccessGeneration(view);    // 遊戲程序：從磁碟讀，沒有介面程序的回報紀錄
        var stored = new StoredAccount(ui);
        string a;
        using (var login = ui.Begin())                  // 帳號 A 登入，查完身分與授權
        {
            var loginA = stored.SaveLogin(login);
            a = login.Generation;
            stored.SaveGameIdentity(AccountA, login, loginA);
            stored.SaveOwnership(AccountA, login, loginA);
        }
        Assert.True(stored.GameAuthorization(game));

        ui.NoteRevokeReported(a);                       // :install 回報 A 撤回沒有完成
        using var relogin = ui.Begin();                 // 登入頁開始新的工作：在鎖裡換成 B
        Assert.NotEqual(a, relogin.Generation);
        bool cleared = false;
        Assert.Equal(SteamAccessRevoke.NotCurrent, ui.RevokeIfGeneration(a, () => cleared = true));
        Assert.False(cleared);                          // 背景撤回晚到：不清，A 的身分與授權還在

        // 另一個帳號的新登入存好了（戳記是 B），登入、查身分、查授權還沒完成或失敗了。
        // Save 會順手清掉舊的身分與授權，但那只是保險（別的程序拿著舊的 SharedPreferences 再 commit 會寫回來），這裡不清
        var loginB = stored.SaveLogin(relogin, clearGameCache: false);
        Assert.False(stored.GameAuthorization(game));
        Assert.False(stored.GameAuthorization(ui));

        stored.SaveGameIdentity(AccountB, relogin, loginB);   // 查到 B 的身分，授權還是 A 查的
        Assert.False(stored.GameAuthorization(game));
        stored.SaveOwnership(AccountB, relogin, loginB);
        Assert.True(stored.GameAuthorization(game));
    }

    [Fact]
    public void Round10_a_game_identity_saved_before_this_version_needs_a_sync_first()
    {
        using var temp = new TemporaryDirectory();
        var game = new SteamAccessGeneration(temp.Path);
        Assert.Equal(SteamAccessGeneration.Initial, game.Current());
        // 舊版存的：登入沒有記世代，身分沒有記是哪一次登入
        var stored = new StoredAccount(game) { TokenCiphertext = "legacy", IdentitySteamId = AccountA, OwnershipSteamId = AccountA };
        Assert.True(SteamAccountStamps.LoginIsCurrent(game.Peek(), stored.LoginGeneration));   // 登入照舊算數
        Assert.False(stored.GameAuthorization(game));

        using (var sync = game.Begin())
            Assert.True(stored.SaveGameIdentity(AccountA, sync, stored.CurrentLogin!));   // 同步時重寫身分
        Assert.True(stored.GameAuthorization(game));

        stored.OwnershipSteamId = 0;                    // 更舊的版本存的授權沒有記帳號：不給遊戲用
        Assert.False(stored.GameAuthorization(game));
    }

    [Fact]
    public void Round10_the_ownership_counts_for_the_account_it_was_checked_for()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        var stored = new StoredAccount(ui);
        using (var login = ui.Begin())
        {
            var loginA = stored.SaveLogin(login);
            stored.SaveGameIdentity(AccountA, login, loginA);
            stored.SaveOwnership(AccountA, login, loginA);
        }
        ui.Revoke(() => { });                           // 登出（假設別的程序把舊的身分與授權寫回來了）

        using var relogin = ui.Begin();
        var again = stored.SaveLogin(relogin, clearGameCache: false);
        stored.SaveGameIdentity(AccountA, relogin, again);   // 同一個帳號重新登入，授權查詢失敗：用這個帳號查的舊結果
        Assert.True(stored.GameAuthorization(ui));
        stored.SaveGameIdentity(AccountB, relogin, again);   // 換了帳號：A 查的授權不給 B
        Assert.False(stored.GameAuthorization(ui));
    }

    // ── 第 11 輪：存了別的登入之後，晚到的寫入不能把另一個帳號的身分接到目前的登入上 ──

    [Fact]
    public void Round11_a_late_write_from_the_previous_login_cannot_attach_its_identity_to_the_newer_login()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        var game = new SteamAccessGeneration(view);
        var stored = new StoredAccount(ui);
        using var pageA = ui.Begin();
        var loginA = stored.SaveLogin(pageA);           // A 先存登入、連上 Steam
        using var pageB = ui.Begin();                   // 另一個登入頁
        var loginB = stored.SaveLogin(pageB);           // B 接著存登入（清掉身分與授權），成為目前的登入
        bool wroteIdentity = stored.SaveGameIdentity(AccountA, pageA, loginA);   // A 的查詢晚到
        bool wroteOwnership = stored.SaveOwnership(AccountA, pageA, loginA);
        Assert.False(stored.GameAuthorization(ui));
        Assert.False(stored.GameAuthorization(game));
        Assert.False(wroteIdentity);                    // 存著的登入已經不是 A 這一次：不寫
        Assert.False(wroteOwnership);

        Assert.True(stored.SaveGameIdentity(AccountB, pageB, loginB));
        Assert.True(stored.SaveOwnership(AccountB, pageB, loginB));
        Assert.True(stored.GameAuthorization(ui));
        Assert.True(stored.GameAuthorization(game));
    }

    [Fact]
    public void Round11_an_identity_saved_for_another_login_does_not_count_even_if_it_lands_on_disk()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        var game = new SteamAccessGeneration(view);
        var stored = new StoredAccount(ui);
        using var pageA = ui.Begin();
        var loginA = stored.SaveLogin(pageA);
        Assert.True(stored.SaveGameIdentity(AccountA, pageA, loginA));
        Assert.True(stored.SaveOwnership(AccountA, pageA, loginA));
        Assert.True(stored.GameAuthorization(game));

        // 存了 B 的登入，A 的身分與授權卻還在（別的程序拿著舊的 SharedPreferences 再 commit、或 Save 的清除沒生效）
        using var pageB = ui.Begin();
        stored.SaveLogin(pageB, clearGameCache: false);
        Assert.False(stored.GameAuthorization(ui));
        Assert.False(stored.GameAuthorization(game));
    }

    // ── 第 12 輪：存新登入是撤回邊界，舊登入的工作和別的程序的設定快取都要看得出登入換了 ──

    [Fact]
    public void Round12_the_game_process_cannot_apply_its_cached_prefs_after_a_new_login()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        var game = new SteamAccessGeneration(view);
        var stored = new StoredAccount(ui);
        using (var pageA = ui.Begin())                  // A 登入，身分與授權都齊了
        {
            var loginA = stored.SaveLogin(pageA);
            stored.SaveGameIdentity(AccountA, pageA, loginA);
            stored.SaveOwnership(AccountA, pageA, loginA);
        }
        var gameCache = stored.Snapshot();              // :game 載入 SharedPreferences（A 的）之後暫停，還沒套用身分
        Assert.True(gameCache.GameAuthorization(game));

        using var pageB = ui.Begin();                   // 介面程序存入 B（清掉快取），:game 的快取看不到
        stored.SaveLogin(pageB);
        Assert.False(gameCache.GameAuthorization(game));   // :game 恢復執行：快取裡全是 A，世代從檔案讀
        Assert.False(stored.GameAuthorization(game));
    }

    [Fact]
    public void Round12_an_old_logins_work_is_stale_once_a_new_login_is_saved()
    {
        using var temp = new TemporaryDirectory();
        var (folder, view) = TwoProcessViews(temp);
        var ui = new SteamAccessGeneration(folder);
        var install = new SteamAccessGeneration(view);
        var stored = new StoredAccount(ui);
        using (var pageA = ui.Begin())
        {
            var loginA = stored.SaveLogin(pageA);
            stored.SaveGameIdentity(AccountA, pageA, loginA);
            stored.SaveOwnership(AccountA, pageA, loginA);
        }
        using var cloudA = ui.Begin();                  // A 的 Cloud 同步：已經過了授權與身分，正在替換、上傳存檔
        using var downloadA = install.Begin();          // :install 用 A 的 token 下載

        using var pageB = ui.Begin();                   // 另一個登入頁存入 B
        var loginB = stored.SaveLogin(pageB);

        Assert.False(cloudA.IsCurrent());               // 每次替換、上傳之前的 ThrowIfStale，同步結束後 LaunchUnlessRevoked 都看這個
        Assert.Throws<OperationCanceledException>(cloudA.ThrowIfStale);
        Assert.False(downloadA.IsCurrent());

        // B 的登入流程接著用同一個 scope：還是目前的，寫得進身分與授權
        Assert.True(pageB.IsCurrent());
        Assert.False(pageB.Token.IsCancellationRequested);
        Assert.True(stored.SaveGameIdentity(AccountB, pageB, loginB));
        Assert.True(stored.SaveOwnership(AccountB, pageB, loginB));
        Assert.True(stored.GameAuthorization(install));
    }

    [Fact]
    public async Task Round12_a_new_login_cancels_the_old_logins_work_but_not_its_own_flow()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        var stored = new StoredAccount(ui);
        using (var pageA = ui.Begin())
            stored.SaveLogin(pageA);
        using var cloudA = ui.Begin();
        using var pageB = ui.Begin();
        stored.SaveLogin(pageB);

        // 同程序的舊工作由每秒一次的檢查取消；B 的登入流程那條不能跟著被取消
        await Task.Delay(TimeSpan.FromSeconds(5), cloudA.Token).ContinueWith(_ => { });
        Assert.True(cloudA.Token.IsCancellationRequested);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.False(pageB.Token.IsCancellationRequested);
        Assert.True(pageB.IsCurrent());
    }

    [Fact]
    public void Round12_of_two_login_pages_begun_together_only_the_first_save_counts()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        var stored = new StoredAccount(ui);
        using var pageA = ui.Begin();
        using var pageB = ui.Begin();
        Assert.Equal(pageA.Generation, pageB.Generation);

        var loginA = stored.SaveLogin(pageA);           // 先存的換代：另一頁的登入跟其他舊工作一樣停下來，要重新登入
        Assert.Throws<OperationCanceledException>(() => stored.SaveLogin(pageB));
        Assert.True(stored.SaveGameIdentity(AccountA, pageA, loginA));
        Assert.True(stored.SaveOwnership(AccountA, pageA, loginA));
        Assert.True(stored.GameAuthorization(ui));
    }

    // ── 第 13 輪：重新檢查讀登入和拿世代之間換了登入，不能清掉新的登入 ──

    [Fact]
    public void Round13_a_recheck_that_read_the_old_token_cannot_clear_a_login_saved_in_between()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        var stored = new StoredAccount(ui);
        using (var pageA = ui.Begin())
            stored.SaveLogin(pageA);
        string? loginB = null;

        // 重新檢查的兩步之間，另一個登入頁存入 B；之後 Steam 回 A 的 token 失效
        Recheck(ui, stored, tokenInvalid: true, between: () =>
        {
            using var pageB = ui.Begin();
            loginB = stored.SaveLogin(pageB);
        });

        Assert.Equal(loginB, stored.CurrentLogin);
        Assert.True(SteamAccountStamps.LoginIsCurrent(ui.Peek(), stored.LoginGeneration));
    }

    [Fact]
    public void Round13_a_recheck_that_finds_no_login_while_a_new_one_is_being_saved_does_not_clear_it()
    {
        using var temp = new TemporaryDirectory();
        var ui = new SteamAccessGeneration(temp.Path);
        var stored = new StoredAccount(ui);
        using (var pageA = ui.Begin())
            stored.SaveLogin(pageA);

        // 另一個登入頁的 RenewForLogin 已經寫了新世代、還沒存 B：重新檢查這時讀登入，讀到的是對不上的 A
        var b = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        File.WriteAllText(Path.Combine(temp.Path, "access-generation"), b);
        Recheck(ui, stored, tokenInvalid: false);
        stored.TokenCiphertext = "login-b";             // B 存完
        stored.LoginGeneration = b;

        Assert.True(SteamAccountStamps.LoginIsCurrent(ui.Peek(), stored.LoginGeneration));
        Assert.Equal(SteamAccountStamps.LoginKey("login-b"), stored.CurrentLogin);
    }

    /// <summary>
    /// SteamAccountActivity.RecheckAsync 的順序（在 Android，桌面測不到，這裡用同樣的 store 動作照順序走一遍）：
    /// 拿世代、讀這一代的登入（讀不到時只有這一代的登入解不開才 ClearIfCurrent）、用讀到的 token 登入 Steam；token 失效就 ClearIfCurrent。
    /// between 是兩步之間（別的登入頁在這時存入 B）。
    /// </summary>
    private static void Recheck(SteamAccessGeneration ui, StoredAccount stored, bool tokenInvalid, Action? between = null)
    {
        using var access = ui.Begin();
        between?.Invoke();
        var login = stored.Load(access);
        if (login is null)
        {
            if (stored.HasLoginOf(access))
                stored.ClearIfCurrent(access);
            return;
        }
        if (!access.IsCurrent())                        // LogOnAsync 之後的 ThrowIfStale
            return;
        if (tokenInvalid)
            stored.ClearIfCurrent(access);
    }

    /// <summary>
    /// SharedPreferences 裡跟這個判斷有關的欄位，寫法照 Android 的 SteamAccountStore：Save 存登入（回傳這一次登入的識別）、
    /// SaveGameIdentity、SaveOwnership 經過 WriteForLogin（寫不進去回 false，Android 是丟 OperationCanceledException）；
    /// GameAuthorization 照 HasCompleteGameAuthorization：登入在、身分與授權都在，世代讀一次交給 SteamAccountStamps。
    /// process 是存登入、寫身分的那個程序（介面）。Snapshot 是別的程序（:game）載入 SharedPreferences 時拿到的那一份，之後的寫入它看不到。
    /// </summary>
    private sealed class StoredAccount(SteamAccessGeneration process)
    {
        public string? TokenCiphertext;                 // token_ct，null 是沒有登入
        public string? LoginGeneration;                 // access_generation
        public ulong IdentitySteamId;                   // game_steam_id
        public string? IdentityLogin;                   // game_login
        public ulong OwnershipSteamId;                  // owned_for_steam_id

        /// <summary>每次存登入都換代、是新的密文（新的 IV）。clearGameCache：Save 順手清掉身分與授權。scope 已經不是目前的就丟 OperationCanceledException</summary>
        public string SaveLogin(SteamAccessScope scope, bool clearGameCache = true)
        {
            var ciphertext = Guid.NewGuid().ToString("N");
            process.RenewForLogin(scope, () =>
            {
                TokenCiphertext = ciphertext;
                LoginGeneration = scope.Generation;
                if (clearGameCache)
                {
                    IdentitySteamId = OwnershipSteamId = 0;
                    IdentityLogin = null;
                }
            }, rollback: () => { });
            return SteamAccountStamps.LoginKey(ciphertext);
        }

        /// <summary>Load 讀到的這一次登入的識別</summary>
        public string? CurrentLogin => TokenCiphertext is null ? null : SteamAccountStamps.LoginKey(TokenCiphertext);

        public StoredAccount Snapshot() => (StoredAccount)MemberwiseClone();

        /// <summary>Load(context, scope)：登入要是 scope 那一代存的，而且那一代還是目前的世代（這裡不模擬 token 解不開）</summary>
        public string? Load(SteamAccessScope scope) =>
            TokenCiphertext is not null && SteamAccountStamps.LoginIsCurrent(process.Peek(), LoginGeneration)
            && SteamAccountStamps.LoginIsCurrent(scope.Generation, LoginGeneration) ? CurrentLogin : null;

        /// <summary>HasLoginOf：存著的登入是 scope 那一代存的</summary>
        public bool HasLoginOf(SteamAccessScope scope) =>
            TokenCiphertext is not null && SteamAccountStamps.LoginIsCurrent(scope.Generation, LoginGeneration);

        /// <summary>ClearLocked</summary>
        public void Clear()
        {
            TokenCiphertext = LoginGeneration = IdentityLogin = null;
            IdentitySteamId = OwnershipSteamId = 0;
        }

        /// <summary>ClearIfCurrent：token 失效時，只有 scope 還是目前的世代才換代並清</summary>
        public SteamAccessRevoke ClearIfCurrent(SteamAccessScope scope) => process.RevokeIfCurrent(scope, Clear);

        private bool WriteForLogin(SteamAccessScope scope, string login, Action write)
        {
            try
            {
                process.WriteIfCurrent(scope, () =>
                {
                    if (!SteamAccountStamps.IsSameLogin(scope.Generation, LoginGeneration, CurrentLogin, login))
                        throw new OperationCanceledException("The Steam login changed while this work was running.");
                    write();
                }, rollback: () => { });
                return true;
            }
            catch (OperationCanceledException) { return false; }
        }

        public bool SaveGameIdentity(ulong steamId, SteamAccessScope scope, string login) =>
            WriteForLogin(scope, login, () =>
            {
                IdentitySteamId = steamId;
                IdentityLogin = login;
            });

        public bool SaveOwnership(ulong steamId, SteamAccessScope scope, string login) =>
            WriteForLogin(scope, login, () => OwnershipSteamId = steamId);

        public bool GameAuthorization(SteamAccessGeneration reader) =>
            TokenCiphertext is not null && IdentitySteamId != 0
            && SteamAccountStamps.GameAuthorizationIsCurrent(reader.Peek(), LoginGeneration, CurrentLogin, IdentityLogin, IdentitySteamId, OwnershipSteamId);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mt2-access-test-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
