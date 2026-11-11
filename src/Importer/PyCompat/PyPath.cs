namespace Mt2Mobile.Importer.PyCompat;

/// <summary>pathlib 的規則（POSIX）：name／stem／suffix、with_suffix、relative_to、Path 物件的排序。路徑一律用 '/'。</summary>
public static class PyPath
{
    public static string Name(string p)
    {
        int i = p.LastIndexOf('/');
        return i < 0 ? p : p[(i + 1)..];
    }

    public static string Parent(string p)
    {
        int i = p.LastIndexOf('/');
        return i < 0 ? "." : (i == 0 ? "/" : p[..i]);
    }

    /// <summary>PurePath.suffix：最後一個點之後（點不能在開頭或結尾）</summary>
    public static string Suffix(string p)
    {
        string name = Name(p);
        int i = name.LastIndexOf('.');
        return 0 < i && i < name.Length - 1 ? name[i..] : "";
    }

    public static string Stem(string p)
    {
        string name = Name(p);
        int i = name.LastIndexOf('.');
        return 0 < i && i < name.Length - 1 ? name[..i] : name;
    }

    /// <summary>PurePath.with_suffix(s)</summary>
    public static string WithSuffix(string p, string suffix)
    {
        string name = Name(p);
        string stem = Stem(p);
        return p[..^name.Length] + stem + suffix;
    }

    public static string WithName(string p, string name) => p[..^Name(p).Length] + name;

    /// <summary>a / b：b 是絕對路徑時就是 b（同 pathlib）</summary>
    public static string Join(string a, string b) => b.StartsWith('/') ? b : a.EndsWith('/') ? a + b : a + "/" + b;

    /// <summary>str(p.relative_to(root))；不在底下就丟例外（同 Python 的 ValueError）</summary>
    public static string RelativeTo(string p, string root)
    {
        if (p == root) return ".";
        string prefix = root.EndsWith('/') ? root : root + "/";
        if (!p.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException($"'{p}' is not in the subpath of '{root}'");
        return p[prefix.Length..];
    }

    public static bool IsRelativeTo(string p, string root)
    {
        string prefix = root.EndsWith('/') ? root : root + "/";
        return p == root || p.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Path 物件的 &lt;：逐段（以 '/' 分開）比字串，不是整串比</summary>
    public static int Compare(string a, string b)
    {
        var pa = a.Split('/');
        var pb = b.Split('/');
        int n = Math.Min(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            int c = PyStr.Compare(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

    /// <summary>Path.resolve()：絕對路徑，逐段解開符號連結（同 os.path.realpath）</summary>
    public static string Resolve(string p)
    {
        string full = Full(p);
        var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string cur = "/";
        int guard = 0;
        var queue = new Queue<string>(parts);
        while (queue.Count > 0)
        {
            string part = queue.Dequeue();
            string next = cur == "/" ? "/" + part : cur + "/" + part;
            var info = new FileInfo(next);
            string? target = info.Exists || Directory.Exists(next) ? (info.LinkTarget ?? new DirectoryInfo(next).LinkTarget) : null;
            if (target is not null && guard++ < 64)
            {
                string t = target.StartsWith('/') ? target : (cur == "/" ? "/" : cur + "/") + target;
                var rest = queue.ToArray();
                queue.Clear();
                foreach (var x in Full(t).Split('/', StringSplitOptions.RemoveEmptyEntries).Concat(rest)) queue.Enqueue(x);
                cur = "/";
                continue;
            }
            cur = next;
        }
        return cur;
    }

    /// <summary>絕對路徑、去掉 . 與 ..（不追連結）</summary>
    public static string Full(string p) => System.IO.Path.GetFullPath(p).Replace('\\', '/').TrimEnd('/') is { Length: > 0 } s ? s : "/";

    /// <summary>Path.rglob(pattern) 的檔案與資料夾（pattern 只支援 "*" 與 "*.ext"）；順序不保證，呼叫端要排序</summary>
    public static IEnumerable<string> RGlob(string root, string pattern)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var e in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false, MatchType = MatchType.Simple }))
        {
            string name = Name(e);
            if (Matches(name, pattern)) yield return e.Replace('\\', '/');
        }
    }

    /// <summary>Path.glob(pattern)（單層）</summary>
    public static IEnumerable<string> Glob(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var e in Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { AttributesToSkip = 0, MatchType = MatchType.Simple }))
        {
            if (Matches(Name(e), pattern)) yield return e.Replace('\\', '/');
        }
    }

    /// <summary>fnmatch 的子集：*、?、其餘照字面（大小寫有別）。pathlib 的 glob 不讓 * 對到開頭的點以外的限制，這裡不需要</summary>
    public static bool Matches(string name, string pattern) => Match(name, 0, pattern, 0);

    private static bool Match(string s, int i, string p, int j)
    {
        while (j < p.Length)
        {
            char c = p[j];
            if (c == '*')
            {
                for (int k = i; k <= s.Length; k++)
                    if (Match(s, k, p, j + 1)) return true;
                return false;
            }
            if (i >= s.Length) return false;
            if (c != '?' && c != s[i]) return false;
            i++;
            j++;
        }
        return i == s.Length;
    }
}
