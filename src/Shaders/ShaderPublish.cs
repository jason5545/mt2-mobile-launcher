using System.Buffers.Binary;
using System.Text;
using Mt2Mobile.Importer;
using Mt2Mobile.Importer.PyCompat;

namespace Mt2Mobile.Shaders;

/// <summary>
/// importer/shaders.py：轉換結果發布到 &lt;out&gt;/shaders（每個 shader 的 index.json 與 .glsl；vertex 變體補 vertexInputs；
/// 最上層的 index.json 是 shader 名稱 → 資料夾）。
/// </summary>
public static class ShaderPublish
{
    /// <summary>DXBC 的 ISGN（或 ISG1）：[{semantic, index, register, mask, systemValue}]</summary>
    public static List<PyDict> DxbcInputSignature(byte[] dxbc)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(28));
        for (int k = 0; k < count; k++)
        {
            int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(32 + k * 4));
            string tag = Encoding.ASCII.GetString(dxbc, off, 4);
            if (tag is not ("ISGN" or "ISG1")) continue;
            int b = off + 8;
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(b));
            var (size, skip) = tag == "ISGN" ? (24, 0) : (32, 4);
            var o = new List<PyDict>();
            for (int i = 0; i < n; i++)
            {
                int e = b + 8 + i * size + skip;
                int nameOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(e));
                uint semIndex = BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(e + 4));
                uint sysval = BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(e + 8));
                uint reg = BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(e + 16));
                int end = Array.IndexOf(dxbc, (byte)0, b + nameOff);
                if (end < 0) throw new InvalidOperationException("subsection not found");
                o.Add(new PyDict
                {
                    ["semantic"] = new UTF8Encoding(false, true).GetString(dxbc, b + nameOff, end - b - nameOff).ToUpperInvariant(),
                    ["index"] = (long)semIndex,
                    ["register"] = (long)reg,
                    ["mask"] = (long)dxbc[e + 20],
                    ["systemValue"] = (long)sysval,
                });
            }
            return o;
        }
        return [];
    }

    public static PyDict Publish(string src, string outDir)
    {
        long source = 0, exported = 0, glsl = 0;
        var failed = new List<object?>();
        var rep = new PyDict { ["source"] = 0L, ["exported"] = 0L, ["failed"] = failed, ["glsl"] = 0L };
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        if (!File.Exists(PyPath.Join(src, "report.json")))
        {
            failed.Add(new PyDict { ["src"] = src, ["error"] = "沒有轉換結果，先跑 shaders/convert.py" });
            return rep;
        }
        Directory.CreateDirectory(outDir);
        var names = new PyDict();
        foreach (var d in Directory.EnumerateDirectories(src).Select(p => p.Replace('\\', '/')).Order(PyPath.Comparer))
        {
            source++;
            string dname = PyPath.Name(d);
            try
            {
                var idx = PyJson.LoadDict(PyPath.Join(d, "index.json"));
                Directory.CreateDirectory(PyPath.Join(outDir, dname));
                foreach (PyDict ss in Assets.AsList(idx.Get("subShaders", new List<object?>())).Cast<PyDict>())
                    foreach (PyDict p in Assets.AsList(ss.Get("passes", new List<object?>())).Cast<PyDict>())
                        foreach (var (stage, sdObj) in (PyDict)p.Get("stages", new PyDict())!)
                            foreach (PyDict v in Assets.AsList(((PyDict)sdObj!).Get("variants", new List<object?>())).Cast<PyDict>())
                            {
                                var files = Assets.Dict(v.Get("files"));
                                if (files.Get("glsl") is string g && Py.Truthy(g) && File.Exists(PyPath.Join(d, g)))
                                {
                                    File.Copy(PyPath.Join(d, g), PyPath.Join(PyPath.Join(outDir, dname), g), overwrite: true);
                                    glsl++;
                                }
                                if (Py.Eq(stage, "vertex") && Py.Truthy(files.Get("dxbc")))
                                {
                                    var sig = new Dictionary<long, PyDict>();
                                    foreach (var s in DxbcInputSignature(File.ReadAllBytes(PyPath.Join(d, (string)files["dxbc"]!)))) sig[Py.ToLong(s["register"])] = s;
                                    var inputs = Assets.ListOf(Assets.Dict(v.Get("reflection")).Get("inputs"));
                                    v["vertexInputs"] = inputs.Cast<PyDict>().Where(i => sig.ContainsKey(Py.ToLong(i["location"]))).Select(i =>
                                    {
                                        var s = sig[Py.ToLong(i["location"])];
                                        return (object?)new PyDict { ["name"] = i["name"], ["location"] = i["location"], ["type"] = i["type"], ["semantic"] = s["semantic"], ["index"] = s["index"] };
                                    }).ToList();
                                }
                            }
                PyStr.WriteText(PyPath.Join(PyPath.Join(outDir, dname), "index.json"), PyJson.Compact(idx));
                names[idx["name"]] = dname;
                exported++;
            }
            catch (Exception e)
            {
                failed.Add(new PyDict { ["src"] = dname, ["error"] = $"{Assets.ExceptionName(e)}: {e.Message}" });
            }
        }
        PyStr.WriteText(PyPath.Join(outDir, "index.json"), PyJson.Indented(Assets.Sorted(names), 1));
        rep["source"] = source;
        rep["exported"] = exported;
        rep["glsl"] = glsl;
        return rep;
    }
}
