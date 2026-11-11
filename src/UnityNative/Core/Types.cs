// 型別查找：腳本（assembly + namespace + class）、原生元件（Unity 的類別名，例如 "Camera" → UnityEngine.Camera）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityNative
{
    public static class Types
    {
        static readonly Dictionary<string, Assembly> asms = new Dictionary<string, Assembly>();
        static readonly Dictionary<(string, string), Type> cache = new Dictionary<(string, string), Type>();
        static Dictionary<string, Type> engineByName;

        public static Assembly Asm(string name)
        {
            if (asms.TryGetValue(name, out var a)) return a;
            try { a = Assembly.Load(new AssemblyName(name)); }
            catch { a = null; }
            asms[name] = a;
            return a;
        }

        public static Type Find(string assembly, string fullName)
        {
            var key = (assembly ?? "", fullName);
            if (cache.TryGetValue(key, out var t)) return t;
            if (!string.IsNullOrEmpty(assembly)) t = Asm(assembly)?.GetType(fullName);
            if (t == null)
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = a.GetType(fullName);
                    if (t != null) break;
                }
            cache[key] = t;
            return t;
        }

        /// <summary>Unity 的原生類別名 → managed 型別（只找 UnityEngine.* 組件裡的 UnityEngine.Object 子類別）</summary>
        public static Type Engine(string unityClass)
        {
            if (engineByName == null)
            {
                engineByName = new Dictionary<string, Type>();
                foreach (var name in new[] { "UnityEngine.CoreModule", "UnityEngine.UIModule", "UnityEngine.AnimationModule", "UnityEngine.AudioModule",
                    "UnityEngine.ParticleSystemModule", "UnityEngine.PhysicsModule", "UnityEngine.Physics2DModule", "UnityEngine.DirectorModule",
                    "UnityEngine.TextRenderingModule", "UnityEngine.SpriteMaskModule", "UnityEngine.VideoModule", "UnityEngine.AIModule",
                    "UnityEngine.TilemapModule", "UnityEngine.SpriteShapeModule", "UnityEngine.GridModule", "UnityEngine.TerrainModule", "UnityEngine.ClothModule",
                    "UnityEngine.VFXModule", "UnityEngine.WindModule", "UnityEngine.VehiclesModule", "UnityEngine.UIElementsModule" })
                {
                    var a = Asm(name);
                    if (a == null) continue;
                    foreach (var t in SafeGetTypes(a))
                        if (typeof(UnityEngine.Object).IsAssignableFrom(t) && !engineByName.ContainsKey(t.Name)) engineByName[t.Name] = t;
                }
            }
            return engineByName.TryGetValue(unityClass, out var r) ? r : null;
        }

        /// <summary>組件裡載得起來的型別。CoreCLR 遇到相依缺檔時丟 ReflectionTypeLoadException、附上載得起來的那些；
        /// Mono 直接丟 FileNotFoundException，什麼都不給：改成從 metadata 列出型別名，一個一個拿，拿不到的跳過</summary>
        public static Type[] SafeGetTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null).ToArray(); }
            catch (Exception) when (!string.IsNullOrEmpty(a.Location))
            {
                var list = new List<Type>();
                using var pe = new System.Reflection.PortableExecutable.PEReader(System.IO.File.OpenRead(a.Location));
                var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                string NameOf(System.Reflection.Metadata.TypeDefinition td)
                {
                    var n = md.GetString(td.Name);
                    var outer = td.GetDeclaringType();
                    if (!outer.IsNil) return NameOf(md.GetTypeDefinition(outer)) + "+" + n;
                    var ns = md.GetString(td.Namespace);
                    return ns.Length > 0 ? ns + "." + n : n;
                }
                foreach (var h in md.TypeDefinitions)
                {
                    try
                    {
                        var t = a.GetType(NameOf(md.GetTypeDefinition(h)), false);
                        if (t != null) { _ = t.BaseType; list.Add(t); }
                    }
                    catch (Exception) { }
                }
                return list.ToArray();
            }
        }
    }
}
