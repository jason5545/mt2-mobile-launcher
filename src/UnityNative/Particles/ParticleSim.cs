// 一個 ParticleSystem 的模擬。Unity 的粒子系統是原生程式（看不到原始碼），照 TS 版（unity-cs2ts/runtime/src/particles/system.ts）：
// 依據 Unity 2022.3 手冊各模組的說明與 ParticleSystem 的腳本 API 文件；推定的地方標「未查證」。
//
// 時間：系統時間在 0～duration 之間；looping 時繞回 0（burst 次數重算），不循環的到 duration 停止發射，粒子死光後停下（isPlaying false）
//   並執行 stopAction。startDelay 在 Play 時取一次，延遲期間不發射、系統時間不走。prewarm（looping 才有效）：Play 時先模擬一整個 duration。
// 發射：rateOverTime 照系統時間比例取曲線值累積；同一格發的粒子平均分散在這一格的時間點（未查證細節）；rateOverDistance 照發射器的世界移動距離；
//   bursts：time、count、cycleCount（0 無限）、repeatInterval、probability。
// 每顆粒子每格：年齡 → 重力 → Force over Lifetime → Limit Velocity → Inherit Velocity → Velocity over Lifetime（附加、不累積；orbital、radial）
//   → Noise → 位置積分 → Rotation over Lifetime → 拖尾的點。顏色、大小、貼圖格在畫的時候照年齡比例算（ParticleMesh.cs）。
// 子發射器：Birth（跟著父粒子持續發）、Death（父粒子死掉的位置跑一個 duration）；繼承顏色、大小、旋轉、生命期。Collision、Trigger、Manual 沒做。
// 沒做：Collision、Triggers、External Forces、Lights、Color／Size／Rotation by Speed、Ring Buffer、Custom Simulation Space、Mesh 形狀。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public struct EmitterPose
    {
        public Matrix4x4 ToWorld, Inv;
        public Vector3 Position, SizeScale;
        public Quaternion Rotation;
        public Vector3? ShapeScale;
    }

    public sealed class ParticleSim
    {
        public struct TrailPoint { public Vector3 P; public float T; }
        public sealed class Orphan { public List<TrailPoint> Points; public Color Color; public float Size, Life; }
        sealed class Instance
        {
            public Vector3 Pos;
            public float Time, Acc;
            public List<int> BurstDone = new List<int>();
            public uint ParentId;
            public bool Following;
            public ParticleSim Parent;
            public Color? Color;
            public float Size, Rotation, Lifetime;
        }
        public sealed class Sub { public ParticleSim Sim; public int Type, Properties; public float Probability; }

        public readonly PMod Comp;
        public readonly NParticleSystem Owner;
        static uint nextId = 1;

        // ── 粒子（SoA） ──
        public int Cap, N;
        public uint[] Id = Array.Empty<uint>(), Seed = Array.Empty<uint>();
        public float[] Px = Array.Empty<float>(), Py = Array.Empty<float>(), Pz = Array.Empty<float>();
        public float[] Vx = Array.Empty<float>(), Vy = Array.Empty<float>(), Vz = Array.Empty<float>();
        public float[] Avx = Array.Empty<float>(), Avy = Array.Empty<float>(), Avz = Array.Empty<float>();
        public float[] Age = Array.Empty<float>(), Life = Array.Empty<float>();
        public float[] Sx = Array.Empty<float>(), Sy = Array.Empty<float>(), Sz = Array.Empty<float>();
        public float[] Rx = Array.Empty<float>(), Ry = Array.Empty<float>(), Rz = Array.Empty<float>();
        public float[] Nsz = Array.Empty<float>();
        public Color[] Col = Array.Empty<Color>();
        public List<TrailPoint>[] Trails = Array.Empty<List<TrailPoint>>();
        public readonly List<Orphan> Orphans = new List<Orphan>();

        // ── 狀態 ──
        public bool Playing, Paused, Emitting, StopNotified = true;
        public float Time, Clock, Delay, EmitAcc, DistAcc;
        Vector3? lastEmitterPos;
        List<int> burstDone = new List<int>();
        public readonly XorShift Rng = new XorShift(1);
        public ParticleSim SubEmitterOf;
        List<Instance> instances = new List<Instance>();
        public EmitterPose? Pose;
        public Vector3 EmitterVel;
        public readonly List<Sub> Subs = new List<Sub>();

        public ParticleSim(NParticleSystem owner, PMod comp)
        {
            Owner = owner;
            Comp = comp;
            Ensure(Math.Min(64, MaxParticles));
        }

        public PMod Initial => Comp.M("InitialModule");
        public PMod Mod(string name) => Comp.M(name);
        public float Duration => Math.Max(0.05f, Comp.F("lengthInSec", 5));
        public bool Looping => Comp.F("looping") == 1;
        public bool WorldSpace => Comp.F("moveWithTransform") == 1;
        public int MaxParticles => Math.Max(0, (int)Initial.F("maxNumParticles", 1000));
        public float SystemT => Math.Min(1, Time / Duration);

        void Ensure(int need)
        {
            if (need <= Cap) return;
            int cap = Math.Max(Math.Max(need, Cap * 2), 16);
            void G<T>(ref T[] a) { var b = new T[cap]; Array.Copy(a, b, Math.Min(a.Length, N)); a = b; }
            G(ref Id); G(ref Seed); G(ref Px); G(ref Py); G(ref Pz); G(ref Vx); G(ref Vy); G(ref Vz); G(ref Avx); G(ref Avy); G(ref Avz);
            G(ref Age); G(ref Life); G(ref Sx); G(ref Sy); G(ref Sz); G(ref Rx); G(ref Ry); G(ref Rz); G(ref Nsz); G(ref Col); G(ref Trails);
            Cap = cap;
        }

        // ── 腳本 API ──

        public bool IsPlaying => Playing && !Paused;
        public bool IsEmitting => Playing && !Paused && Emitting;
        public bool IsStopped => !Playing && !Paused && N == 0;
        public bool IsAlive => N > 0 || Orphans.Count > 0 || instances.Count > 0 || (Playing && Emitting);

        /// <summary>Play：停止中從頭開始（延遲、prewarm、burst 重算）；暫停中接著播；播放中不動</summary>
        public void Play(XorShift global)
        {
            if (Paused) { Paused = false; return; }
            if (Playing) return;
            Playing = Emitting = true;
            Time = 0; EmitAcc = 0; DistAcc = 0;
            lastEmitterPos = null;
            burstDone = new List<int>();
            StopNotified = false;
            uint seed = Comp.F("autoRandomSeed", 1) == 0 ? (uint)Comp.F("randomSeed") : global.UInt();
            Rng.Reseed(seed);
            Delay = Math.Max(0, Comp.C("startDelay").Eval(0, Rng.Next()));
            if (Looping && Comp.F("prewarm") == 1 && SubEmitterOf == null && Pose != null) Prewarm();
        }

        public void Stop(bool clear)
        {
            Playing = Paused = Emitting = false;
            if (clear) Clear();
        }

        public void Pause() { if (Playing) Paused = true; }

        public void Clear()
        {
            for (int i = 0; i < N; i++) Trails[i] = null;
            N = 0;
            Orphans.Clear();
            instances.Clear();
        }

        void Prewarm()
        {
            int steps = Math.Min(60, Math.Max(1, (int)MathF.Ceiling(Duration * 30)));
            float dt = Duration / steps, delay = Delay;
            Delay = 0;
            for (int i = 0; i < steps; i++) Step(dt);
            Time = 0;
            burstDone = new List<int>();
            Delay = delay;
        }

        // ── 模擬 ──

        public void Step(float dt)
        {
            if (Pose == null || dt <= 0 || Paused) return;
            Clock += dt;
            AdvanceAll(dt);
            if (instances.Count > 0) StepInstances(dt);
            if (Playing && Emitting && SubEmitterOf == null)
            {
                float edt = dt;
                if (Delay > 0) { float used = Math.Min(Delay, edt); Delay -= used; edt -= used; }
                if (edt > 0) EmitOverTime(edt, dt);
            }
            if (Orphans.Count > 0) AgeOrphans();
            if (Playing && !Emitting && N == 0 && instances.Count == 0 && Orphans.Count == 0) Playing = false;
        }

        static int Done(List<int> l, int b) => b < l.Count ? l[b] : 0;
        static void SetDone(List<int> l, int b, int v) { while (l.Count <= b) l.Add(0); l[b] = v; }

        void EmitOverTime(float edt, float dt)
        {
            var em = Mod("EmissionModule");
            var pose = Pose.Value;
            float moved = lastEmitterPos.HasValue ? Vector3.Distance(lastEmitterPos.Value, pose.Position) : 0;
            var prevPos = lastEmitterPos ?? pose.Position;
            lastEmitterPos = pose.Position;
            float remaining = edt, consumed = dt - edt;
            while (remaining > 1e-9f)
            {
                float seg = Math.Min(Duration - Time, remaining);
                if (em.On)
                {
                    float t0 = Time, t1 = Time + seg;
                    float rate = em.C("rateOverTime").Eval(Math.Min(1, t0 / Duration), Rng.Next());
                    EmitAcc += rate * seg;
                    // 每格加 rate×dt 的浮點累積誤差：取整時給一點容差
                    int count = (int)MathF.Floor(EmitAcc + 1e-6f);
                    if (count > 0)
                    {
                        EmitAcc -= count;
                        for (int k = 0; k < count; k++)
                        {
                            float at = consumed + seg * ((k + 1f) / count);
                            EmitOne(dt - at, t0 + seg * ((k + 1f) / count), k, count, null, null);
                        }
                    }
                    var bursts = em.L("m_Bursts");
                    for (int b = 0; b < bursts.Count; b++)
                    {
                        var burst = bursts[b];
                        if (burst == null) continue;
                        float bt = burst.F("time");
                        int cycles = (int)burst.F("cycleCount", 1);
                        float interval = Math.Max(0.0001f, burst.F("repeatInterval", 0.01f));
                        int done = Done(burstDone, b);
                        while ((cycles <= 0 || done < cycles) && bt + done * interval < t1)
                        {
                            float when = bt + done * interval;
                            done++;
                            if (Rng.Next() > burst.F("probability", 1)) continue;
                            int n = (int)MathF.Round(burst.C("countCurve").Eval(Math.Min(1, when / Duration), Rng.Next()));
                            float at = consumed + Math.Max(0, when - t0);
                            for (int k = 0; k < n; k++) EmitOne(dt - at, when, k, n, null, null);
                        }
                        SetDone(burstDone, b, done);
                    }
                }
                Time += seg;
                remaining -= seg;
                consumed += seg;
                if (Time >= Duration - 1e-9f)
                {
                    if (Looping) { Time = 0; burstDone = new List<int>(); }
                    else { Time = Duration; Emitting = false; break; }
                }
            }
            if (em.On && moved > 0)
            {
                float rd = em.C("rateOverDistance").Eval(SystemT, Rng.Next());
                if (rd > 0)
                {
                    DistAcc += rd * moved;
                    int count = (int)MathF.Floor(DistAcc + 1e-6f);
                    DistAcc -= count;
                    for (int k = 0; k < count; k++)
                    {
                        float f = (k + 1f) / count;
                        EmitOne(dt * (1 - f), Time, k, count, Vector3.Lerp(prevPos, pose.Position, f), null);
                    }
                }
            }
        }

        public void Emit(int count) { for (int k = 0; k < count; k++) EmitOne(0, Time, k, count, null, null); }

        Vector3 SimVector(Vector3 v) => WorldSpace || Pose == null ? v : MathUtil.MulVector(Pose.Value.Inv, v);
        Vector3 LocalToSim(Vector3 v) => WorldSpace && Pose != null ? Pose.Value.Rotation * v : v;

        int EmitOne(float remainingDt, float at, int index, int count, Vector3? worldAt, Instance inst)
        {
            if (N >= MaxParticles) return -1;
            Ensure(N + 1);
            var pose = Pose.Value;
            int i = N++;
            var ini = Initial;
            uint seed = Rng.UInt();
            Seed[i] = seed;
            Id[i] = nextId++;
            float sysT = Math.Min(1, at / Duration);
            PShape.Emit(Mod("ShapeModule"), Rng, Clock, sysT, index, count, out var pos, out var dir);
            if (pose.ShapeScale.HasValue) pos = Vector3.Scale(pos, pose.ShapeScale.Value);
            float speed = ini.C("startSpeed").Eval(sysT, PRand.Hash01(seed, PRand.Speed));
            Vector3? origin = inst != null ? inst.Pos : worldAt;
            if (WorldSpace)
            {
                dir = pose.Rotation * dir;
                pos = origin.HasValue ? pose.Rotation * pos + origin.Value : MathUtil.MulPoint(pose.ToWorld, pos);
            }
            else if (origin.HasValue) pos += MathUtil.MulPoint(pose.Inv, origin.Value);
            Px[i] = pos.x; Py[i] = pos.y; Pz[i] = pos.z;
            var v = dir * speed;
            var inh = Mod("InheritVelocityModule");
            if (inh.On && (int)inh.F("m_Mode") != 1)
            {
                float k = inh.C("m_Curve").Eval(0, PRand.Hash01(seed, PRand.Inherit));
                v += SimVector(EmitterVel) * k;
            }
            Vx[i] = v.x; Vy[i] = v.y; Vz[i] = v.z;
            Avx[i] = Avy[i] = Avz[i] = 0;
            float life = ini.C("startLifetime").Eval(sysT, PRand.Hash01(seed, PRand.Lifetime));
            if (inst != null && inst.Lifetime > 0) life *= inst.Lifetime;
            Life[i] = Math.Max(1e-4f, life);
            Age[i] = 0;
            bool size3D = ini.F("size3D") == 1;
            float s = ini.C("startSize").Eval(sysT, PRand.Hash01(seed, PRand.Size));
            Sx[i] = s;
            Sy[i] = size3D ? ini.C("startSizeY").Eval(sysT, PRand.Hash01(seed, PRand.SizeY)) : s;
            Sz[i] = size3D ? ini.C("startSizeZ").Eval(sysT, PRand.Hash01(seed, PRand.SizeZ)) : s;
            if (inst != null && inst.Size > 0) { Sx[i] *= inst.Size; Sy[i] *= inst.Size; Sz[i] *= inst.Size; }
            bool rot3D = ini.F("rotation3D") == 1;
            float rz = ini.C("startRotation").Eval(sysT, PRand.Hash01(seed, PRand.Rotation));
            // randomizeRotationDirection：這個比例的粒子旋轉方向相反（手冊 Main module「Flip Rotation」）
            if (PRand.Hash01(seed, PRand.Flip) < ini.F("randomizeRotationDirection")) rz = -rz;
            Rz[i] = rz + (inst?.Rotation ?? 0);
            Rx[i] = rot3D ? ini.C("startRotationX").Eval(sysT, PRand.Hash01(seed, PRand.RotationY)) : 0;
            Ry[i] = rot3D ? ini.C("startRotationY").Eval(sysT, PRand.Hash01(seed, PRand.RotationZ)) : 0;
            var sc = ini.G("startColor");
            Col[i] = sc != null ? sc.Eval(sysT, PRand.Hash01(seed, PRand.Color)) : Color.white;
            if (inst?.Color is Color ic) Col[i] *= ic;
            Nsz[i] = 0;
            Trails[i] = null;
            foreach (var sub in Subs) if (sub.Type == 0 && Rng.Next() <= sub.Probability) sub.Sim.Trigger(this, i, sub.Properties);
            if (remainingDt > 0 && !Advance(i, remainingDt)) { Kill(i); return -1; }
            return i;
        }

        void AdvanceAll(float dt)
        {
            int i = 0;
            while (i < N)
            {
                if (Advance(i, dt)) i++;
                else Kill(i);
            }
        }

        bool Advance(int i, float dt)
        {
            float age = Age[i] + dt;
            Age[i] = age;
            float life = Life[i];
            if (age >= life) return false;
            float t = age / life;
            uint seed = Seed[i];
            var ini = Initial;
            float vx = Vx[i], vy = Vy[i], vz = Vz[i];
            float gm = ini.C("gravityModifier").Eval(SystemT, PRand.Hash01(seed, PRand.Gravity));
            if (gm != 0)
            {
                var g = SimVector(new Vector3(0, -9.81f, 0) * gm);
                vx += g.x * dt; vy += g.y * dt; vz += g.z * dt;
            }
            var force = Mod("ForceModule");
            if (force.On)
            {
                bool rnd = force.F("randomizePerFrame") == 1;
                var f = new Vector3(
                    force.C("x").Eval(t, rnd ? Rng.Next() : PRand.Hash01(seed, PRand.ForceX)),
                    force.C("y").Eval(t, rnd ? Rng.Next() : PRand.Hash01(seed, PRand.ForceY)),
                    force.C("z").Eval(t, rnd ? Rng.Next() : PRand.Hash01(seed, PRand.ForceZ)));
                f = force.F("inWorldSpace") == 1 ? SimVector(f) : LocalToSim(f);
                vx += f.x * dt; vy += f.y * dt; vz += f.z * dt;
            }
            // Limit Velocity over Lifetime：超過上限時照 dampen 往上限拉（每 1/30 秒拉 dampen 的比例，未查證）；drag 照比例減速
            var clamp = Mod("ClampVelocityModule");
            if (clamp.On)
            {
                float damp = 1 - MathF.Pow(1 - Math.Clamp(clamp.F("dampen"), 0, 1), dt * 30);
                if (clamp.F("separateAxis") == 1)
                {
                    float lx = Math.Abs(clamp.C("x").Eval(t, PRand.Hash01(seed, PRand.Limit))), ly = Math.Abs(clamp.C("y").Eval(t, PRand.Hash01(seed, PRand.Limit + 100))), lz = Math.Abs(clamp.C("z").Eval(t, PRand.Hash01(seed, PRand.Limit + 200)));
                    static float Pull(float v, float l, float d) => Math.Abs(v) > l ? v - (v - Math.Sign(v) * l) * d : v;
                    vx = Pull(vx, lx, damp); vy = Pull(vy, ly, damp); vz = Pull(vz, lz, damp);
                }
                else
                {
                    float lim = Math.Abs(clamp.C("magnitude").Eval(t, PRand.Hash01(seed, PRand.Limit)));
                    float sp = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                    if (sp > lim && sp > 0) { float k = (sp - (sp - lim) * damp) / sp; vx *= k; vy *= k; vz *= k; }
                }
                float drag = clamp.C("drag").Eval(t, PRand.Hash01(seed, PRand.Drag));
                if (drag > 0)
                {
                    float d = drag;
                    if (clamp.F("multiplyDragByParticleSize") == 1) d *= Math.Max(Sx[i], Sy[i]);
                    if (clamp.F("multiplyDragByParticleVelocity") == 1) d *= MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                    float k = Math.Max(0, 1 - d * dt);
                    vx *= k; vy *= k; vz *= k;
                }
            }
            var inh = Mod("InheritVelocityModule");
            Vector3 iv = default;
            if (inh.On && (int)inh.F("m_Mode") == 1) iv = SimVector(EmitterVel) * inh.C("m_Curve").Eval(t, PRand.Hash01(seed, PRand.Inherit));
            Vx[i] = vx; Vy[i] = vy; Vz[i] = vz;
            Vector3 a = default;
            float speedMod = 1;
            var vol = Mod("VelocityModule");
            if (vol.On)
            {
                a = new Vector3(vol.C("x").Eval(t, PRand.Hash01(seed, PRand.VelocityX)), vol.C("y").Eval(t, PRand.Hash01(seed, PRand.VelocityY)), vol.C("z").Eval(t, PRand.Hash01(seed, PRand.VelocityZ)));
                a = vol.F("inWorldSpace") == 1 ? SimVector(a) : LocalToSim(a);
                float ox = vol.C("orbitalX").Eval(t, PRand.Hash01(seed, PRand.Orbital)), oy = vol.C("orbitalY").Eval(t, PRand.Hash01(seed, PRand.Orbital + 1)), oz = vol.C("orbitalZ").Eval(t, PRand.Hash01(seed, PRand.Orbital + 2));
                float radial = vol.C("radial").Eval(t, PRand.Hash01(seed, PRand.Radial));
                if (ox != 0 || oy != 0 || oz != 0 || radial != 0)
                {
                    var center = new Vector3(vol.C("orbitalOffsetX").Eval(t, 0.5f), vol.C("orbitalOffsetY").Eval(t, 0.5f), vol.C("orbitalOffsetZ").Eval(t, 0.5f));
                    if (WorldSpace && Pose != null) center = MathUtil.MulPoint(Pose.Value.ToWorld, center);
                    var rel = new Vector3(Px[i], Py[i], Pz[i]) - center;
                    if (ox != 0 || oy != 0 || oz != 0)
                    {
                        var q = MathUtil.AngleAxis(ox * dt * Mathf.Rad2Deg, Vector3.right) * MathUtil.AngleAxis(oy * dt * Mathf.Rad2Deg, Vector3.up) * MathUtil.AngleAxis(oz * dt * Mathf.Rad2Deg, Vector3.forward);
                        var after = q * rel;
                        Px[i] += after.x - rel.x; Py[i] += after.y - rel.y; Pz[i] += after.z - rel.z;
                        rel = after;
                    }
                    if (radial != 0 && rel.sqrMagnitude > 1e-12f) a += rel.normalized * radial;
                }
                speedMod = vol.Has("speedModifier") ? vol.C("speedModifier").Eval(t, PRand.Hash01(seed, PRand.SpeedModifier)) : 1;
            }
            // Noise：取樣點是位置 × 頻率加上捲動（scrollSpeed × 時間）；三個軸各取一份。damping 開著時強度除以頻率（未查證）
            var noise = Mod("NoiseModule");
            Vector3 nv = default;
            if (noise.On)
            {
                float freq = Math.Max(1e-4f, noise.F("frequency", 0.5f));
                float scroll = noise.C("scrollSpeed").Eval(t, 0.5f) * Clock;
                int oct = (int)noise.F("octaves", 1);
                float om = noise.F("octaveMultiplier", 0.5f), os = noise.F("octaveScale", 2);
                bool sep = noise.F("separateAxes") == 1;
                float r = PRand.Hash01(seed, PRand.Noise);
                float sxN = noise.C("strength").Eval(t, r), syN = sep ? noise.C("strengthY").Eval(t, r) : sxN, szN = sep ? noise.C("strengthZ").Eval(t, r) : sxN;
                if (noise.F("damping") == 1) { sxN /= freq; syN /= freq; szN /= freq; }
                float X = Px[i] * freq + scroll, Y = Py[i] * freq + scroll, Z = Pz[i] * freq + scroll;
                nv = new Vector3(PNoise.Fractal3(X, Y, Z, oct, om, os) * sxN, PNoise.Fractal3(X + 31.4f, Y + 17.7f, Z + 9.2f, oct, om, os) * syN, PNoise.Fractal3(X + 61.3f, Y + 47.1f, Z + 23.9f, oct, om, os) * szN);
                float pAmt = noise.COrNull("positionAmount")?.Eval(t, r) ?? noise.F("positionAmount", 1);
                Nsz[i] = nv.x;
                nv *= pAmt;
            }
            a += nv;
            Avx[i] = a.x + iv.x; Avy[i] = a.y + iv.y; Avz[i] = a.z + iv.z;
            Px[i] += (vx + a.x + iv.x) * speedMod * dt;
            Py[i] += (vy + a.y + iv.y) * speedMod * dt;
            Pz[i] += (vz + a.z + iv.z) * speedMod * dt;
            var rol = Mod("RotationModule");
            if (rol.On)
            {
                Rz[i] += rol.C("curve").Eval(t, PRand.Hash01(seed, PRand.RotationOverLifetime)) * dt;
                if (rol.F("separateAxes") == 1)
                {
                    Rx[i] += rol.C("x").Eval(t, PRand.Hash01(seed, PRand.RotationOverLifetime + 1)) * dt;
                    Ry[i] += rol.C("y").Eval(t, PRand.Hash01(seed, PRand.RotationOverLifetime + 2)) * dt;
                }
            }
            var trail = Mod("TrailModule");
            if (trail.On && (int)trail.F("mode") != 1) RecordTrail(i, trail);
            return true;
        }

        void RecordTrail(int i, PMod trail)
        {
            var pts = Trails[i];
            if (pts == null)
            {
                if (PRand.Hash01(Seed[i], PRand.TrailLifetime + 7) >= trail.F("ratio", 1)) return;
                pts = Trails[i] = new List<TrailPoint>();
            }
            var p = TrailPos(i, trail);
            float minD = trail.F("minVertexDistance", 0.2f);
            if (pts.Count == 0 || Vector3.Distance(p, pts[^1].P) >= minD) pts.Add(new TrailPoint { P = p, T = Clock });
            float pointLife = trail.C("lifetime").Eval(Age[i] / Life[i], PRand.Hash01(Seed[i], PRand.TrailLifetime)) * Life[i];
            while (pts.Count > 1 && Clock - pts[0].T > pointLife) pts.RemoveAt(0);
        }

        /// <summary>拖尾點：trail.worldSpace 而且是 Local 模擬時存世界座標（之後系統移動，拖尾留在原地）</summary>
        public Vector3 TrailPos(int i, PMod trail)
        {
            var p = new Vector3(Px[i], Py[i], Pz[i]);
            if (trail.F("worldSpace") == 1 && !WorldSpace && Pose != null) p = MathUtil.MulPoint(Pose.Value.ToWorld, p);
            return p;
        }

        void AgeOrphans()
        {
            Orphans.RemoveAll(o =>
            {
                while (o.Points.Count > 0 && Clock - o.Points[0].T > o.Life) o.Points.RemoveAt(0);
                return o.Points.Count <= 1;
            });
        }

        void Kill(int i)
        {
            var trail = Mod("TrailModule");
            var pts = Trails[i];
            if (pts != null && pts.Count > 1 && trail.On && trail.F("dieWithParticles", 1) == 0)
                Orphans.Add(new Orphan { Points = pts, Color = Col[i], Size = Sx[i], Life = trail.C("lifetime").Eval(1, PRand.Hash01(Seed[i], PRand.TrailLifetime)) * Life[i] });
            if (Subs.Count > 0)
            {
                var wp = WorldPos(i);
                foreach (var sub in Subs) if (sub.Type == 2 && Rng.Next() <= sub.Probability) sub.Sim.TriggerAt(wp, this, i, sub.Properties);
            }
            int last = --N;
            if (i != last)
            {
                Px[i] = Px[last]; Py[i] = Py[last]; Pz[i] = Pz[last]; Vx[i] = Vx[last]; Vy[i] = Vy[last]; Vz[i] = Vz[last];
                Avx[i] = Avx[last]; Avy[i] = Avy[last]; Avz[i] = Avz[last]; Age[i] = Age[last]; Life[i] = Life[last];
                Sx[i] = Sx[last]; Sy[i] = Sy[last]; Sz[i] = Sz[last]; Rx[i] = Rx[last]; Ry[i] = Ry[last]; Rz[i] = Rz[last]; Nsz[i] = Nsz[last];
                Seed[i] = Seed[last]; Id[i] = Id[last]; Col[i] = Col[last]; Trails[i] = Trails[last];
            }
            Trails[last] = null;
        }

        public Vector3 WorldPos(int i)
        {
            var p = new Vector3(Px[i], Py[i], Pz[i]);
            return !WorldSpace && Pose != null ? MathUtil.MulPoint(Pose.Value.ToWorld, p) : p;
        }

        // ── 子發射器 ──

        Instance Inherit(ParticleSim parent, int i, int properties) => new Instance
        {
            Color = (properties & 1) != 0 ? parent.Col[i] : null,
            Size = (properties & 2) != 0 ? parent.Sx[i] : 0,
            Rotation = (properties & 4) != 0 ? parent.Rz[i] : 0,
            Lifetime = (properties & 8) != 0 ? Math.Max(0, 1 - parent.Age[i] / parent.Life[i]) : 0,
        };

        public void Trigger(ParticleSim parent, int i, int properties)
        {
            var inst = Inherit(parent, i, properties);
            inst.Pos = parent.WorldPos(i);
            inst.ParentId = parent.Id[i];
            inst.Following = true;
            inst.Parent = parent;
            instances.Add(inst);
        }

        public void TriggerAt(Vector3 wp, ParticleSim parent, int i, int properties)
        {
            var inst = Inherit(parent, i, properties);
            inst.Pos = wp;
            instances.Add(inst);
        }

        void StepInstances(float dt)
        {
            var em = Mod("EmissionModule");
            var keep = new List<Instance>();
            foreach (var inst in instances)
            {
                if (inst.Following && inst.Parent != null)
                {
                    var p = inst.Parent;
                    int j = -1;
                    for (int k = 0; k < p.N; k++) if (p.Id[k] == inst.ParentId) { j = k; break; }
                    if (j < 0) continue;
                    inst.Pos = p.WorldPos(j);
                }
                float t0 = inst.Time, t1 = Math.Min(Duration, t0 + dt);
                if (em.On)
                {
                    float rate = em.C("rateOverTime").Eval(Math.Min(1, t0 / Duration), Rng.Next());
                    inst.Acc += rate * (t1 - t0);
                    int count = (int)MathF.Floor(inst.Acc + 1e-6f);
                    inst.Acc -= count;
                    for (int k = 0; k < count; k++) EmitOne(dt * (1 - (k + 1f) / count), t0, k, count, null, inst);
                    var bursts = em.L("m_Bursts");
                    for (int b = 0; b < bursts.Count; b++)
                    {
                        var burst = bursts[b];
                        if (burst == null) continue;
                        int cycles = (int)burst.F("cycleCount", 1);
                        float interval = Math.Max(0.0001f, burst.F("repeatInterval", 0.01f)), bt = burst.F("time");
                        int done = Done(inst.BurstDone, b);
                        while ((cycles <= 0 || done < cycles) && bt + done * interval < t1)
                        {
                            float when = bt + done * interval;
                            done++;
                            if (Rng.Next() > burst.F("probability", 1)) continue;
                            int n = (int)MathF.Round(burst.C("countCurve").Eval(Math.Min(1, when / Duration), Rng.Next()));
                            for (int k = 0; k < n; k++) EmitOne(0, when, k, n, null, inst);
                        }
                        SetDone(inst.BurstDone, b, done);
                    }
                }
                inst.Time = t1;
                if (inst.Following)
                {
                    if (inst.Time >= Duration && Looping) { inst.Time = 0; inst.BurstDone = new List<int>(); }
                    keep.Add(inst);
                }
                else if (inst.Time < Duration) keep.Add(inst);
            }
            instances = keep;
        }
    }
}
