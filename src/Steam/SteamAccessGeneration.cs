using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Mt2Mobile.Steam;

/// <summary>
/// Steam 存取世代（Terra 第 2 輪）：登出、撤回同意、token 失效，不論在哪個程序發生，都換一個新的世代。
/// 每段 Steam 工作開始時用 <see cref="Begin"/> 記下當時的世代，之後：
/// - 同一個程序裡換代，立刻取消它的 Token；別的程序換代，由每秒一次的檢查發現，或在階段邊界呼叫 ThrowIfStale 時發現。
/// - 寫入 token、身分、授權快取，以及失效時清掉登入，都要確認還是同一個世代（<see cref="WriteIfCurrent"/>、<see cref="RevokeIfCurrent"/>）。
///
/// 世代存在 &lt;folder&gt;/access-generation：一開始是 "0"，每次換代寫一個新的 128 位元亂數（先寫暫存檔再 rename，讀的人不會讀到一半）。
/// 只比相等、不比大小，所以程序重啟後不會倒退，也不會跟以前的世代重複。讀不到就當成已經換代，工作停下來。
/// 換代與受保護的寫入在 &lt;folder&gt;/access.lock 的檔案鎖裡做（.NET 在 Unix 上用 flock 實作 FileShare.None），跨程序互斥。
///
/// 例外路徑（Terra 第 3、4 輪）：
/// - 寫不進新世代（例如空間不足）：把世代檔截成空的（不需要新空間）。空的代表「已撤回、還沒有新世代」，所有舊的 scope 都過期；
///   要開始新的工作時（Begin），在跨程序的鎖裡重讀，還是空的才補寫一個新的亂數世代（不是 "0"），不會蓋掉別的程序剛補好的世代。
/// - 連截斷都寫不了（儲存空間整個不能寫）：別的程序看不到這次撤回，**跨程序的撤回不保證立刻生效**。這個程序 fail closed：
///   舊的 scope 一律過期（IsCurrent 也看這個程序的取消狀態），新的工作開不起來，直到換代補寫成功；回傳 OnlyInThisProcess，
///   呼叫端要盡力停掉別的程序，並告訴使用者撤回沒有完成。
/// - 撤回拿不到鎖（別的程序拿著超過 5 秒、鎖檔開不了、儲存空間唯讀；Terra 第 5 輪）：不丟例外，同程序的工作照樣取消。
///   - 使用者撤回（Revoke）照樣盡力換代、清除，回傳 OnlyInThisProcess，這個程序 fail closed，新的工作開始前要先在鎖裡補換代。
///     拿著鎖的寫入在寫完之後再確認一次世代，換過代就自己執行 rollback，不會把清掉的資料寫回去；
///     但沒有鎖寫的世代可能被拿著鎖補寫的程序（空世代的補寫、待補的換代）蓋掉，所以不算 Revoked。
///   - 舊工作發現 token 失效（RevokeIfCurrent、RevokeIfGeneration）不換代、不清（Terra 第 6 輪第 2 條）：沒有鎖，世代的檢查和換代、清除
///     就沒有跨程序互斥，檢查之後別的程序可能已經登出又登入，這時換代或清除會清掉新的登入。改寫「這一代已撤回」的標記
///     &lt;folder&gt;/access-revoked-&lt;世代&gt;（Terra 第 7 輪）：Peek 讀到有標記的世代就當成空的（已撤回），所有程序立刻看得到，
///     程序結束也還在；標記只影響那一代，之後換了代、存了新登入都不受影響，所以不用鎖也沒有檢查之後才寫的競爭。回傳 Revoked。
///     標記也寫不進去，才回傳 OnlyInThisProcess，在這個程序記下這一代，之後在鎖裡世代還是這一代才補換代；這份紀錄只在這個程序的記憶體裡，
///     程序結束就沒了，所以 :install 會把那一代傳給介面程序，由介面用 RevokeIfGeneration 再做一次；
///     介面一收到就先 NoteRevokeReported：這個程序的 Peek 不再回那一代，工作、登入判斷、Begin 一起生效，RevokePending 為真（Terra 第 8、9 輪）。
///   - 標記在下一次補寫新世代時清掉。Peek 看完標記會再讀一次世代，一樣才算數：讀到舊世代之後標記才被清掉時，
///     補寫那邊已經先寫了新世代，第二次讀會讀到新的，不會讓舊世代通過（Terra 第 8 輪第 1 條）。
/// - clear 丟例外時，同程序的工作也先取消，例外才往外丟。記錄（log）丟的例外一律吞掉，不會打斷撤回（Terra 第 6 輪第 1 條）。
///
/// 存新登入也換代（<see cref="RenewForLogin"/>，Terra 第 12 輪）：換了登入等於登出舊的、登入新的，舊登入的工作和別的程序手上的設定快取
/// 都要看得出來。登入流程自己的 scope 改記新世代，繼續寫身分與授權。一個世代裡最多只有一次登入。
///
/// 限制：已經送到 Steam 的請求（上傳、commit、下載），取消之後仍可能在伺服器端完成；撤回只能讓之後的步驟不再進行，不能撤銷已經完成的動作。
/// </summary>
public sealed class SteamAccessGeneration(string folder, Action<string>? log = null)
{
    /// <summary>還沒換過代的世代。舊版存的登入沒有記世代，只在世代還是這個值的時候算數。</summary>
    public const string Initial = "0";
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);
    // 同一個程序裡，同一個資料夾共用一把 Monitor 與一個「同程序換代就取消」的 CTS
    private static readonly ConcurrentDictionary<string, InProcess> Shared = new(StringComparer.Ordinal);
    private readonly string _folder = Path.GetFullPath(folder);
    private readonly Action<string> _log = SafeLog(log);

    private string GenerationPath => Path.Combine(_folder, "access-generation");
    private string LockPath => Path.Combine(_folder, "access.lock");
    private const string RevokedPrefix = "access-revoked-";
    private string RevokedPath(string generation) => Path.Combine(_folder, RevokedPrefix + generation);
    private InProcess State => Shared.GetOrAdd(_folder, _ => new InProcess());

    /// <summary>測試用：Peek 讀完世代、還沒看撤回標記時呼叫，讓測試把別的程序的動作插在中間。</summary>
    internal Action? BetweenReadAndMarkerCheck { get; set; }

    /// <summary>
    /// 讀目前的世代，不補寫：空的（撤回時寫不進新世代、截斷了）、這一代有撤回標記、或別的程序回報過這一代撤回沒完成
    /// （<see cref="NoteRevokeReported"/>），回 null。登入判斷、IsCurrent、Begin（Current 會在鎖裡補寫新世代）都經過這裡，
    /// 回報過的那一代在這個程序就不會再被當成有效（Terra 第 9 輪第 2 條）。
    /// 檔案還沒有就建成 "0"（條件式建立，兩個程序同時建只會有一個成功）。讀不到、內容不是英數字（不能拿來當標記的檔名）丟例外。
    /// </summary>
    public string? Peek() => Read(honorReported: true);

    /// <summary>
    /// 跟 <see cref="Peek"/> 一樣，但別的程序回報過的那一代照實回：RevokeIfGeneration 要確認是不是還在那一代，
    /// RevokePending 要確認開遊戲時要不要擋。
    /// </summary>
    private string? PeekIgnoringReported() => Read(honorReported: false);

    private string? Read(bool honorReported)
    {
        if (!File.Exists(GenerationPath))
        {
            Directory.CreateDirectory(_folder);
            var temporary = GenerationPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, Initial);
                File.Move(temporary, GenerationPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(GenerationPath)) { }
            finally { TryDelete(temporary); }
        }
        // 看完標記再讀一次世代，一樣才算數（Terra 第 8 輪）：讀到舊世代之後，別的程序可能補寫了新世代、清掉舊世代的標記。
        // 補寫那邊是先寫新世代、再清標記，所以標記已經被清掉時，第二次一定讀到新世代，重來一次就好
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var value = ReadGeneration();
            if (value is null)
                return null;
            BetweenReadAndMarkerCheck?.Invoke();
            if (File.Exists(RevokedPath(value)))
                return null;
            if (ReadGeneration() == value)
                return honorReported && State.Reported.ContainsKey(value) ? null : value;
        }
        throw new IOException("Steam access generation keeps changing.");
    }

    /// <summary>世代檔的內容，空的回 null。不是英數字（不能拿來組標記的檔名）丟例外。</summary>
    private string? ReadGeneration()
    {
        var value = File.ReadAllText(GenerationPath).Trim();
        if (value.Length == 0)
            return null;
        if (!value.All(char.IsAsciiLetterOrDigit))
            throw new IOException("Steam access generation is malformed.");
        return value;
    }

    /// <summary>
    /// 目前的世代。空的就在跨程序的鎖裡重讀，還是空的才補寫新的亂數世代：別的程序剛補好的不會被蓋掉。
    /// 補不了或讀不到丟例外。
    /// </summary>
    public string Current()
    {
        var value = Peek();
        if (value is not null)
            return value;
        lock (State.Gate)
        {
            using var fileLock = AcquireFileLock();
            value = Peek();
            if (value is not null)
                return value;
            WriteNewGeneration();
            value = Peek() ?? throw new IOException("Steam access generation is empty.");
            DeleteRevokedMarkersExcept(value);
            return value;
        }
    }

    /// <summary>
    /// 這個程序撤回過，但還沒在鎖裡補換代，別的程序不保證看得到。這個程序開的別的程序（遊戲）不要再用 Steam 的身分與授權快取：
    /// 它從磁碟讀，讀到的可能是撤回前的（儲存空間寫不了時，清除只清得掉這個程序記憶體裡的設定）。
    /// 也包括別的程序回報、還在處理中的那一代（<see cref="NoteRevokeReported"/>），只要世代還是那一代；
    /// 有回報紀錄、世代卻一時讀不到（或一直在變）時也算，fail closed（Terra 第 9 輪第 1 條）。沒有任何紀錄時讀不到不算：
    /// 遊戲自己讀身分時也讀不到世代，本來就不給 Steam 身分；多擋只會讓那一局沒有 DLC。
    /// 不拿 Gate：背景撤回拿著 Gate 等跨程序的鎖（最多 5 秒）時，開遊戲的檢查不能被卡住（Terra 第 8 輪第 2 條）。
    /// </summary>
    public bool RevokePending
    {
        get
        {
            var state = State;
            if (Volatile.Read(ref state.RevokePending))
                return true;
            if (state.Reported.IsEmpty)
                return false;
            try { return PeekIgnoringReported() is { } current && state.Reported.ContainsKey(current); }
            catch (Exception) { return true; }
        }
    }

    /// <summary>
    /// 別的程序（:install）回報這一代撤回沒有完成：收到就同步記下（不拿 Gate）。這個程序裡 Peek 從此不再回這一代：
    /// 這一代的工作立刻不算數（IsCurrent）、不算登入、新的工作不會在這一代開始（Begin 在鎖裡補寫新世代，寫不了就丟例外），
    /// 開遊戲也不給 Steam 身分（RevokePending），不用等之後的 <see cref="RevokeIfGeneration"/> 拿到鎖（Terra 第 8、9 輪）。
    /// 世代不會重複，記下的那一代以後也不會再是有效的世代，所以不用清。
    /// </summary>
    public void NoteRevokeReported(string generation) => State.Reported.TryAdd(generation, 0);

    /// <summary>
    /// 開始一段 Steam 工作。讀不到世代就丟例外，呼叫端當成不能用 Steam。
    /// 這個程序撤回過、但世代沒寫成（別的程序看不到）：先在鎖裡補換代，補不了就丟例外，不准開始新的工作（fail closed）。
    /// </summary>
    public SteamAccessScope Begin()
    {
        var state = State;
        lock (state.Gate)
        {
            if (state.RevokePending)
            {
                using (AcquireFileLock())
                {
                    // 舊工作拿不到鎖時記下的撤回，只在世代還是那一代時才補：已經換過代就可能有新的登入，不能把它判成失效
                    if (state.PendingOnlyIf is not null && Peek() != state.PendingOnlyIf)
                        _log("pending steam access revoke dropped: the generation already changed");
                    else if (!ChangeGeneration())
                        throw new IOException("A Steam access revoke is still pending: the generation cannot be written.");
                    else
                        _log("pending steam access revoke written");
                }
                state.ClearPending();
            }
            return new SteamAccessScope(this, Current(), state.Cts.Token);
        }
    }

    /// <summary>
    /// 使用者登出、撤回同意：一定執行 clear，同程序的工作一定取消。拿不到跨程序的鎖也照樣換代、清除（拿著鎖的寫入會自己 rollback）。
    /// 回傳 Revoked：在鎖裡寫進新世代或把世代檔截成空的，別的程序看得到。OnlyInThisProcess：換代寫不了，或拿不到鎖
    /// （沒鎖寫的世代可能被拿著鎖補寫的程序蓋掉），這個程序 fail closed，別的程序不保證知道，呼叫端要盡力停掉別的程序並告訴使用者。
    /// 不因為拿不到鎖丟例外；clear 丟的例外照樣往外丟，但同程序的工作先取消。
    /// 同程序的工作在離開鎖之後才取消，取消的回呼不會在鎖裡跑。
    /// </summary>
    public SteamAccessRevoke Revoke(Action clear) => RevokeCore(generation: null, isCurrent: null, clear);

    /// <summary>
    /// 工作自己發現 token 失效時用：只有 scope 還是目前的世代才換代並執行 clear。
    /// 已經換過代（使用者登出後又登入了別的帳號）就什麼都不做、回傳 NotCurrent，舊的工作不會清掉新的登入；拿不到鎖時也先這樣確認。
    /// 換代寫不了時跟 <see cref="Revoke"/> 一樣：這個程序 fail closed，回傳 OnlyInThisProcess。
    /// 拿不到鎖時不換代也不執行 clear，改寫這一代的撤回標記（回傳 Revoked）；標記也寫不進去才回傳 OnlyInThisProcess（見類別註解）。
    /// </summary>
    public SteamAccessRevoke RevokeIfCurrent(SteamAccessScope scope, Action clear) =>
        RevokeCore(scope.Generation, scope.IsCurrent, clear);

    /// <summary>
    /// 別的程序（:install）發現 token 失效、撤回沒有完成時，介面程序替它再做一次（Terra 第 7 輪第 2 條）：
    /// 世代還是傳過來的那一代才撤回，其餘跟 <see cref="RevokeIfCurrent"/> 一樣。讀不到世代算不是那一代（這時這個程序的工作也都會停）。
    /// </summary>
    public SteamAccessRevoke RevokeIfGeneration(string generation, Action clear) =>
        RevokeCore(generation, () => TryPeekIgnoringReported() == generation, clear);

    /// <summary>
    /// 兩種撤回共用（Terra 第 5 輪第 1 條：原本兩邊各自拿鎖，鎖檔開不了、鎖被佔住時在取消之前就丟例外）。
    /// generation／isCurrent 是 null 代表使用者撤回（一定做）；有值代表舊工作發現 token 失效，拿不到鎖時不換代、不清，改寫撤回標記。
    /// </summary>
    private SteamAccessRevoke RevokeCore(string? generation, Func<bool>? isCurrent, Action clear)
    {
        var state = State;
        CancellationTokenSource? old = null;
        bool visible = false;
        try
        {
            lock (state.Gate)
            {
                using var fileLock = TryAcquireFileLock();
                if (isCurrent is not null && !isCurrent())
                    return SteamAccessRevoke.NotCurrent;
                // 舊工作拿不到鎖：檢查之後別的程序可能已經登出又登入，換代、清除都可能清掉新的登入（Terra 第 6 輪第 2 條）
                bool shared = generation is null || fileLock is not null;
                if (shared)
                {
                    visible = ChangeGeneration() && fileLock is not null;
                    if (visible) state.ClearPending();
                    else state.MarkPending(onlyIf: null);
                }
                else if (TryMarkRevoked(generation!))
                    visible = true;
                else
                    state.MarkPending(onlyIf: generation);
                old = state.Swap();
                if (shared)
                    clear();
            }
        }
        finally
        {
            if (old is not null)
                Cancel(old);
        }
        return visible ? SteamAccessRevoke.Revoked : SteamAccessRevoke.OnlyInThisProcess;
    }

    /// <summary>
    /// 存新登入（Terra 第 12 輪）：在跨程序的鎖裡確認 scope 還是目前的世代（這個程序也沒有待補的撤回），寫新的世代，
    /// scope 改記新世代（登入流程接著用同一個 scope 寫身分與授權，Token 不取消），再執行 write 存登入（記 scope 的新世代）。
    /// 換代之前的工作都過期：舊登入的 Cloud 同步、:install 的下載、另一個登入頁，在階段邊界（ThrowIfStale、受保護的寫入）立刻發現，
    /// Token 由每秒一次的檢查取消，跟別的程序換代一樣；同程序的也是，不像 <see cref="Revoke"/> 立刻取消程序內的 CTS，因為這個 scope 也連在上面。
    /// 別的程序（:game）手上的設定快取記的是舊世代，登入判斷不會過。
    /// 寫不進新世代就丟例外、不存登入：什麼都還沒改，不用截斷世代檔。write 寫完再確認一次，被沒有鎖的撤回換過代就 rollback。
    /// </summary>
    public void RenewForLogin(SteamAccessScope scope, Action write, Action rollback)
    {
        var state = State;
        lock (state.Gate)
        {
            using var fileLock = AcquireFileLock();
            if (state.RevokePending || !scope.IsCurrent())
                throw new OperationCanceledException("Steam access was revoked.");
            string generation;
            // 寫新世代和 scope 改記新世代一起做：scope 每秒一次的檢查不會夾在中間，把自己判成過期
            lock (scope.MoveGate)
            {
                generation = WriteNewGeneration();
                scope.MoveTo(generation);
            }
            DeleteRevokedMarkersExcept(generation);
            write();
            if (!scope.IsCurrent())
            {
                _log("steam access revoked while saving a login; rolled back");
                rollback();
                throw new OperationCanceledException("Steam access was revoked while saving the login.");
            }
        }
    }

    /// <summary>
    /// 受保護的寫入：拿跨程序的鎖，確認 scope 還是目前的世代才執行 write；不是就丟 OperationCanceledException。
    /// 寫完再確認一次：寫的途中被撤回（撤回拿不到鎖時會直接換代、清除），就執行 rollback 把剛寫的清掉，再丟 OperationCanceledException。
    /// </summary>
    public void WriteIfCurrent(SteamAccessScope scope, Action write, Action rollback)
    {
        lock (State.Gate)
        {
            using var fileLock = AcquireFileLock();
            if (!scope.IsCurrent())
                throw new OperationCanceledException("Steam access was revoked.");
            write();
            if (!scope.IsCurrent())
            {
                _log("steam access revoked during a protected write; rolled back");
                rollback();
                throw new OperationCanceledException("Steam access was revoked while writing.");
            }
        }
    }

    /// <summary>換代：寫新的亂數世代；寫不進去就把世代檔截成空的（讀的人當成已撤回）。兩個都失敗回傳 false。</summary>
    private bool ChangeGeneration()
    {
        try
        {
            WriteNewGeneration();
            return true;
        }
        catch (Exception e)
        {
            _log($"steam access generation write failed: {e.GetType().Name}; truncating it instead");
        }
        try
        {
            using (new FileStream(GenerationPath, FileMode.Truncate, FileAccess.Write)) { }
            return true;
        }
        catch (Exception e)
        {
            _log($"steam access generation could not be invalidated: {e.GetType().Name}");
            return false;
        }
    }

    /// <summary>記錄失敗（例如啟動器的日誌檔沒有寫入權限）不能打斷撤回與換代（Terra 第 6 輪第 1 條）：記錄丟的例外一律吞掉。</summary>
    private static Action<string> SafeLog(Action<string>? log) => message =>
    {
        try { log?.Invoke(message); }
        catch (Exception) { }
    };

    /// <summary>
    /// 寫「這一代已撤回」的標記（空檔，存在就算數）。不需要鎖：只影響這一代，不會被別人蓋掉。
    /// 寫不進去回 false（儲存空間整個寫不了）。
    /// </summary>
    private bool TryMarkRevoked(string generation)
    {
        try
        {
            using (new FileStream(RevokedPath(generation), FileMode.OpenOrCreate, FileAccess.Write)) { }
            return true;
        }
        catch (Exception e)
        {
            _log($"steam access revoked marker could not be written: {e.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// 補寫新世代之後（拿著鎖）清掉舊世代的撤回標記：那些世代不會再出現。目前這一代的不清，可能是剛寫的。
    /// 一定要先寫新世代、再清標記：Peek 靠這個順序判斷讀到的舊世代是不是剛被換掉。
    /// </summary>
    private void DeleteRevokedMarkersExcept(string current)
    {
        try
        {
            foreach (var marker in Directory.EnumerateFiles(_folder, RevokedPrefix + "*"))
                if (Path.GetFileName(marker) != RevokedPrefix + current)
                    TryDelete(marker);
        }
        catch (Exception e) { _log($"steam access revoked markers not cleaned up: {e.GetType().Name}"); }
    }

    private string? TryPeekIgnoringReported()
    {
        try { return PeekIgnoringReported(); }
        catch (Exception) { return null; }
    }

    /// <summary>撤回用：拿不到鎖（別的程序拿著超過 5 秒、鎖檔沒有權限、儲存空間唯讀）回 null，什麼例外都不往外丟。</summary>
    private FileStream? TryAcquireFileLock()
    {
        try { return AcquireFileLock(); }
        catch (Exception e)
        {
            _log($"steam access lock unavailable: {e.GetType().Name}");
            return null;
        }
    }

    private FileStream AcquireFileLock()
    {
        Directory.CreateDirectory(_folder);
        var deadline = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private string WriteNewGeneration()
    {
        Directory.CreateDirectory(_folder);
        var temporary = GenerationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var generation = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try
        {
            File.WriteAllText(temporary, generation);
            File.Move(temporary, GenerationPath, overwrite: true);
        }
        finally { TryDelete(temporary); }
        return generation;
    }

    private void Cancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (AggregateException e) { _log($"steam access cancel callback failed: {e.InnerException?.GetType().Name}"); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private sealed class InProcess
    {
        public readonly object Gate = new();
        public CancellationTokenSource Cts = new();
        /// <summary>這個程序撤回過，但世代沒在鎖裡寫成：新的工作開不起來，直到在鎖裡補換代</summary>
        public bool RevokePending;
        /// <summary>別的程序回報撤回沒有完成的世代（NoteRevokeReported），不拿 Gate 就能讀寫</summary>
        public readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.Ordinal);
        /// <summary>補換代的條件：null 是一定要補（使用者撤回、換代寫不了）；有值是世代還是這一代才補（舊工作拿不到鎖時發現 token 失效）</summary>
        public string? PendingOnlyIf;

        public void MarkPending(string? onlyIf)
        {
            // 已經有一定要補的，就維持一定要補
            PendingOnlyIf = RevokePending && PendingOnlyIf is null ? null : onlyIf;
            RevokePending = true;
        }

        public void ClearPending()
        {
            RevokePending = false;
            PendingOnlyIf = null;
        }

        public CancellationTokenSource Swap()
        {
            var old = Cts;
            Cts = new CancellationTokenSource();
            return old;
        }
    }
}

/// <summary>
/// 一段 Steam 工作看到的世代。Token 在換代時取消（同程序撤回立刻，別的程序換代、存新登入最慢約 1 秒）。用完要 Dispose。
/// 存新登入的那段工作（RenewForLogin）會改記新世代，其餘的世代不會變。
/// </summary>
public sealed class SteamAccessScope : IDisposable
{
    private readonly SteamAccessGeneration _owner;
    private readonly CancellationTokenSource _cts;
    private readonly Timer _poll;
    private volatile string _generation;

    internal SteamAccessScope(SteamAccessGeneration owner, string generation, CancellationToken inProcess)
    {
        _owner = owner;
        _generation = generation;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(inProcess);
        _poll = new Timer(_ =>
        {
            if (!IsCurrent())
                CancelSelf();
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public string Generation => _generation;
    public CancellationToken Token => _cts.Token;

    /// <summary>RenewForLogin 寫新世代、改記新世代時拿著；IsCurrent 也拿，讀到的檔案和 Generation 一定是同一邊的</summary>
    internal object MoveGate { get; } = new();

    internal void MoveTo(string generation) => _generation = generation;

    /// <summary>
    /// 還是這段工作的世代：這個程序沒撤回過（Token 沒取消）、別的程序沒回報這一代撤回沒完成，而且檔案裡的世代沒變（沒有撤回標記）。
    /// 只讀，不補寫；讀不到、空的都算不是。
    /// </summary>
    public bool IsCurrent()
    {
        try
        {
            lock (MoveGate)
                return !_cts.IsCancellationRequested && _owner.Peek() == Generation;
        }
        catch (Exception) { return false; }
    }

    /// <summary>階段邊界用：已經換代就取消 Token 並丟 OperationCanceledException。</summary>
    public void ThrowIfStale()
    {
        if (!IsCurrent())
            CancelSelf();
        _cts.Token.ThrowIfCancellationRequested();
    }

    private void CancelSelf()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _poll.Dispose();
        _cts.Dispose();
    }
}

/// <summary>撤回的結果（<see cref="SteamAccessGeneration.Revoke"/>、<see cref="SteamAccessGeneration.RevokeIfCurrent"/>）</summary>
public enum SteamAccessRevoke
{
    /// <summary>已經不是目前的世代（別人撤回過），什麼都沒做</summary>
    NotCurrent,
    /// <summary>換代成功，別的程序看得到</summary>
    Revoked,
    /// <summary>世代寫不了：只有這個程序知道（fail closed），別的程序不保證立刻停</summary>
    OnlyInThisProcess,
}
