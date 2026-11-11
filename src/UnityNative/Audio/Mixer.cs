// AudioMixer、AudioMixerGroup、AudioMixerSnapshot（匯入器的 audiomixers.json，一個 .mixer 一筆）。規則照 TS 版 csport/unityAudio.ts：
//   參數值從起始快照開始；TransitionTo(t) 在 t 秒內照 dB 線性插到目標快照；SetFloat 設過的曝露參數不再跟快照走，ClearFloat 才回來。
//   曝露參數名稱照 CRC32 比對：build 只存名稱的 CRC32，AssetRipper 補的名稱（mposwoH）跟原名（MusicVolume）CRC32 相同。
//   主群組接 output（別的混音器的群組）或執行期設的 outputAudioMixerGroup。
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.Audio;

namespace UnityNative
{
    public sealed class NAudioMixer : NObject
    {
        public sealed class Group { public string Id, Name, Volume, Pitch; public bool Mute; public List<string> Children = new List<string>(); public AudioMixerGroup Managed; }
        public string Guid;
        public JsonObject Data;
        public readonly Dictionary<string, Group> Groups = new Dictionary<string, Group>();
        public readonly Dictionary<string, AudioMixerSnapshot> Snapshots = new Dictionary<string, AudioMixerSnapshot>();
        public readonly Dictionary<string, string> SnapshotNames = new Dictionary<string, string>();
        public readonly List<(string guid, string name, uint crc)> Exposed = new List<(string, string, uint)>();
        public readonly Dictionary<string, float> Values = new Dictionary<string, float>();
        public readonly Dictionary<string, float> Overrides = new Dictionary<string, float>();
        public string Master;
        public AudioMixerGroup Output;
        public bool OutputSet;
        public AudioMixerUpdateMode UpdateMode;
        (Dictionary<string, float> from, Dictionary<string, float> to, double start, float duration)? transition;
        public int Version;

        public Dictionary<string, float> SnapshotValues(string fileId)
        {
            var r = new Dictionary<string, float>();
            foreach (var g in Groups.Values) { if (g.Volume != null) r[g.Volume] = 0; if (g.Pitch != null) r[g.Pitch] = 1; }
            if (Data["snapshots"]?[fileId]?["values"] is JsonObject vals) foreach (var kv in vals) r[kv.Key] = (float)Serializer.D(kv.Value);
            return r;
        }

        public string ExposedGuid(string name)
        {
            uint h = Crc32.Of(name);
            foreach (var e in Exposed) if (e.name == name || e.crc == h) return e.guid;
            return null;
        }

        public float Value(string p) => Overrides.TryGetValue(p, out var o) ? o : Values.TryGetValue(p, out var v) ? v : 0f;

        double Clock => UpdateMode == AudioMixerUpdateMode.UnscaledTime ? Engine.Time.UnscaledTime : Engine.Time.Time;

        public void TransitionTo(Dictionary<string, float> target, float seconds)
        {
            if (!(seconds > 0)) { transition = null; foreach (var kv in target) Values[kv.Key] = kv.Value; Version++; return; }
            transition = (new Dictionary<string, float>(Values), target, Clock, seconds);
        }

        public void Tick()
        {
            if (transition is not var (from, to, start, duration) || to == null) return;
            float t = (float)Math.Min(1.0, (Clock - start) / duration);
            foreach (var kv in to) Values[kv.Key] = (from.TryGetValue(kv.Key, out var f) ? f : kv.Value) + (kv.Value - (from.TryGetValue(kv.Key, out var f2) ? f2 : kv.Value)) * t;
            if (t >= 1) transition = null;
            Version++;
        }

        /// <summary>群組的音量倍率（dB → 倍率，−80 dB 當 0），往上乘到主群組再接 output</summary>
        public float GroupGain(string groupId, int depth = 0)
        {
            if (depth > 16 || !Groups.TryGetValue(groupId, out var g)) return 1f;
            float gain = g.Mute ? 0f : DbToGain(Value(g.Volume));
            var parent = ParentOf(groupId);
            if (parent != null) return gain * GroupGain(parent, depth + 1);
            var outG = OutputSet ? Output : Data["output"] is JsonObject o && o["guid"] != null ? Assets.Load(o["guid"].ToString(), o["fileID"]?.ToString(), typeof(AudioMixerGroup)) as AudioMixerGroup : null;
            var og = Native.Of<NMixerGroup>(outG);
            return og == null ? gain : gain * og.Mixer.GroupGain(og.Id, depth + 1);
        }

        string ParentOf(string id)
        {
            foreach (var g in Groups.Values) if (g.Children.Contains(id)) return g.Id;
            return null;
        }

        public static float DbToGain(float db) => db <= -80f ? 0f : MathF.Pow(10f, db / 20f);
        public static readonly List<NAudioMixer> All = new List<NAudioMixer>();
    }

    public sealed class NMixerGroup : NObject { public NAudioMixer Mixer; public string Id; }
    public sealed class NMixerSnapshot : NObject { public NAudioMixer Mixer; public string Id; }

    public static class Mixers
    {
        static readonly Dictionary<string, NAudioMixer> byGuid = new Dictionary<string, NAudioMixer>();

        public static void Register()
        {
            Assets.Loaders["audiomixer"] = (g, f, r, w) => Load(g, f, r, w);
            Engine.PreLateUpdate.Add(() => { foreach (var m in NAudioMixer.All) m.Tick(); });
        }

        static UnityEngine.Object Load(string guid, string fileId, JsonObject r, Type want)
        {
            var m = Mixer(guid, r);
            if (m == null) return null;
            if (m.Groups.TryGetValue(fileId, out var g)) return g.Managed;
            if (m.Snapshots.TryGetValue(fileId, out var s)) return s;
            if (want == typeof(AudioMixerGroup)) return m.Groups.TryGetValue(m.Master ?? "", out var mg) ? mg.Managed : null;
            return m.Managed;
        }

        static NAudioMixer Mixer(string guid, JsonObject r)
        {
            if (byGuid.TryGetValue(guid, out var m)) return m;
            if (r == null) return null;
            var am = (AudioMixer)Native.NewShell(typeof(AudioMixer));
            m = Native.Bind(new NAudioMixer { Name = r["name"]?.ToString() ?? "", Guid = guid, Data = r, Master = r["master"]?.ToString(), UpdateMode = (AudioMixerUpdateMode)(int)Serializer.D(r["updateMode"]), Source = guid }, am, true);
            byGuid[guid] = m;
            NAudioMixer.All.Add(m);
            if (r["groups"] is JsonObject gs)
                foreach (var kv in gs)
                {
                    var gd = (JsonObject)kv.Value;
                    var grp = new NAudioMixer.Group { Id = kv.Key, Name = gd["name"]?.ToString() ?? "", Volume = gd["volume"]?.ToString(), Pitch = gd["pitch"]?.ToString(), Mute = Serializer.D(gd["mute"]) != 0 };
                    if (gd["children"] is JsonArray ch) foreach (var c in ch) grp.Children.Add(c.ToString());
                    var mg = (AudioMixerGroup)Native.NewShell(typeof(AudioMixerGroup));
                    Native.Bind(new NMixerGroup { Name = grp.Name, Mixer = m, Id = kv.Key, Source = guid }, mg, true);
                    grp.Managed = mg;
                    m.Groups[kv.Key] = grp;
                }
            if (r["snapshots"] is JsonObject ss)
                foreach (var kv in ss)
                {
                    var sn = (AudioMixerSnapshot)Native.NewShell(typeof(AudioMixerSnapshot));
                    var name = kv.Value?["name"]?.ToString() ?? "";
                    Native.Bind(new NMixerSnapshot { Name = name, Mixer = m, Id = kv.Key, Source = guid }, sn, true);
                    m.Snapshots[kv.Key] = sn;
                    m.SnapshotNames[kv.Key] = name;
                }
            if (r["exposed"] is JsonArray ex)
                foreach (var e in ex)
                {
                    var n = e?["name"]?.ToString() ?? "";
                    m.Exposed.Add((e?["guid"]?.ToString(), n, Crc32.Of(n)));
                }
            foreach (var kv in m.SnapshotValues(r["startSnapshot"]?.ToString() ?? "")) m.Values[kv.Key] = kv.Value;
            return m;
        }
    }

    public static class Crc32
    {
        static readonly uint[] table = Build();
        static uint[] Build()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1; t[n] = c; }
            return t;
        }
        public static uint Of(string s)
        {
            uint c = 0xffffffff;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(s ?? "")) c = table[(c ^ b) & 0xff] ^ (c >> 8);
            return c ^ 0xffffffff;
        }
    }
}

namespace UnityNative.Icalls
{
    public static unsafe partial class UnityEngine_Audio_AudioMixer
    {
        static NAudioMixer M(AudioMixer m) => Native.Self<NAudioMixer>(m);
        public static AudioMixerGroup get_outputAudioMixerGroup(AudioMixer self) => M(self).Output;
        public static void set_outputAudioMixerGroup(AudioMixer self, AudioMixerGroup value) { var m = M(self); m.Output = value; m.OutputSet = true; m.Version++; }
        public static AudioMixerSnapshot FindSnapshot(AudioMixer self, string name)
        {
            var m = M(self);
            foreach (var kv in m.SnapshotNames) if (kv.Value == name) return m.Snapshots[kv.Key];
            return null;
        }
        public static AudioMixerGroup[] FindMatchingGroups(AudioMixer self, string subPath)
        {
            var m = M(self);
            var want = (subPath ?? "").ToLowerInvariant();
            var r = new List<AudioMixerGroup>();
            void Walk(string id, string path)
            {
                if (!m.Groups.TryGetValue(id, out var g)) return;
                var p = path.Length > 0 ? path + "/" + g.Name : g.Name;
                if (p.ToLowerInvariant().Contains(want)) r.Add(g.Managed);
                foreach (var c in g.Children) Walk(c, p);
            }
            if (m.Master != null) Walk(m.Master, "");
            return r.ToArray();
        }
        public static void TransitionToSnapshotInternal(AudioMixer self, AudioMixerSnapshot snapshot, float timeToReach)
        {
            var s = Native.Of<NMixerSnapshot>(snapshot);
            if (s == null) return;
            M(self).TransitionTo(s.Mixer.SnapshotValues(s.Id), timeToReach);
        }
        public static void TransitionToSnapshots(AudioMixer self, AudioMixerSnapshot[] snapshots, float[] weights, float timeToReach)
        {
            var m = M(self);
            var sum = new Dictionary<string, float>();
            float tw = 0;
            for (int i = 0; i < snapshots.Length; i++)
            {
                var s = Native.Of<NMixerSnapshot>(snapshots[i]);
                if (s == null) continue;
                float w = i < weights.Length ? weights[i] : 0;
                tw += w;
                foreach (var kv in s.Mixer.SnapshotValues(s.Id)) sum[kv.Key] = (sum.TryGetValue(kv.Key, out var x) ? x : 0) + kv.Value * w;
            }
            if (tw > 0) foreach (var k in new List<string>(sum.Keys)) sum[k] /= tw;
            m.TransitionTo(sum, timeToReach);
        }
        public static AudioMixerUpdateMode get_updateMode(AudioMixer self) => M(self).UpdateMode;
        public static void set_updateMode(AudioMixer self, AudioMixerUpdateMode value) => M(self).UpdateMode = value;
        public static bool SetFloat(AudioMixer self, string name, float value)
        {
            var m = M(self); var p = m.ExposedGuid(name);
            if (p == null) return false;
            m.Overrides[p] = value; m.Version++;
            return true;
        }
        public static bool ClearFloat(AudioMixer self, string name)
        {
            var m = M(self); var p = m.ExposedGuid(name);
            if (p == null) return false;
            m.Overrides.Remove(p); m.Version++;
            return true;
        }
        public static bool GetFloat(AudioMixer self, string name, out float value)
        {
            var m = M(self); var p = m.ExposedGuid(name);
            value = p == null ? 0 : m.Value(p);
            return p != null;
        }
        public static float GetAbsoluteAudibilityFromGroup(AudioMixer self, AudioMixerGroup group)
        {
            var g = Native.Of<NMixerGroup>(group);
            return g == null ? 1f : g.Mixer.GroupGain(g.Id);
        }
    }

    public static unsafe partial class UnityEngine_Audio_AudioMixerGroup
    {
        public static AudioMixer get_audioMixer(AudioMixerGroup self) => (AudioMixer)Native.Self<NMixerGroup>(self).Mixer.Managed;
    }

    public static unsafe partial class UnityEngine_Audio_AudioMixerSnapshot
    {
        public static AudioMixer get_audioMixer(AudioMixerSnapshot self) => (AudioMixer)Native.Self<NMixerSnapshot>(self).Mixer.Managed;
    }
}
