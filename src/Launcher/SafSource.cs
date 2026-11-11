using Android.Content;
using Android.Provider;
using Mt2Mobile.Launcher.Install;

namespace Mt2Mobile.Launcher;

/// <summary>
/// 使用者用 SAF 選的資料夾（ACTION_OPEN_DOCUMENT_TREE，docs/phase4.md「第一個里程碑的範圍」：選資料夾，不選 zip）。
/// 介面拿到 tree URI 後 TakePersistableUriPermission，權限跟著 app 的 uid，:install 程序直接用 ContentResolver 讀。
/// 跟 DirectorySource 一樣：可以選遊戲根目錄（底下剛好一個有 Managed 的 &lt;名稱&gt;_Data）或 _Data 本身。
/// </summary>
public sealed class SafSource : IGameSource
{
    readonly ContentResolver cr;
    readonly Android.Net.Uri tree;
    readonly string dataId;
    readonly Dictionary<string, string> docs = new(StringComparer.Ordinal);

    public SafSource(Context context, Android.Net.Uri tree)
    {
        cr = context.ContentResolver!;
        this.tree = tree;
        var rootId = DocumentsContract.GetTreeDocumentId(tree) ?? throw new InstallException(context.GetString(Resource.String.install_error_not_folder));
        var rootName = Name(rootId);
        if (rootName.EndsWith("_Data", StringComparison.Ordinal) && Children(rootId).Any(c => c.Dir && c.Name == "Managed"))
        {
            dataId = rootId;
            DataDirName = rootName;
        }
        else
        {
            var hits = Children(rootId).Where(c => c.Dir && c.Name.EndsWith("_Data", StringComparison.Ordinal))
                .Where(c => Children(c.Id).Any(x => x.Dir && x.Name == "Managed")).ToList();
            if (hits.Count != 1) throw new InstallException(context.GetString(Resource.String.install_error_data_not_found_format, hits.Count), "idle");
            dataId = hits[0].Id;
            DataDirName = hits[0].Name;
        }
    }

    public string Describe => "saf:" + tree;
    public string DataDirName { get; }

    public IEnumerable<string> Files()
    {
        docs.Clear();
        Walk(dataId, "");
        return docs.Keys.ToList();
    }

    public Stream Open(string relative) =>
        cr.OpenInputStream(DocumentsContract.BuildDocumentUriUsingTree(tree, docs[relative])!) ?? throw new IOException(Strings.Get(Resource.String.install_error_file_unreadable_format, relative));

    void Walk(string id, string prefix)
    {
        foreach (var c in Children(id))
        {
            var rel = prefix + c.Name;
            if (c.Dir) Walk(c.Id, rel + "/");
            else docs[rel] = c.Id;
        }
    }

    string Name(string id)
    {
        using var c = cr.Query(DocumentsContract.BuildDocumentUriUsingTree(tree, id)!, [DocumentsContract.Document.ColumnDisplayName], null, null, null)
            ?? throw new InstallException(Strings.Get(Resource.String.install_error_folder_unreadable), "idle");
        return c.MoveToFirst() ? c.GetString(0) ?? "" : "";
    }

    List<(string Id, string Name, bool Dir)> Children(string id)
    {
        var list = new List<(string, string, bool)>();
        string[] cols = [DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnMimeType];
        using var c = cr.Query(DocumentsContract.BuildChildDocumentsUriUsingTree(tree, id)!, cols, null, null, null);
        if (c is null) return list;
        while (c.MoveToNext())
            list.Add((c.GetString(0)!, c.GetString(1) ?? "", c.GetString(2) == DocumentsContract.Document.MimeTypeDir));
        return list;
    }
}
