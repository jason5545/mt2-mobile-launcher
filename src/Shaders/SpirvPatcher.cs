using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Mt2Mobile.Shaders;

/// <summary>
/// convert.py 的 SpirvPatcher：把 dxbc-spirv 的 Vulkan 導向 SPIR-V 改成 spirv-cross 能轉 ESSL 300 的形式
///（Vulkan 記憶體模型、demote→kill、Component 0、BaseVertex／BaseInstance、導數 Fine／Coarse、bitfieldInsert、名稱）。
/// </summary>
public sealed class SpirvPatcher
{
    private const int OpMemoryModel = 14, OpEntryPoint = 15, OpCapability = 17, OpDecorate = 71, OpConstant = 43, OpVariable = 59,
        OpLoad = 61, OpAccessChain = 65, OpInBoundsAccessChain = 66, OpCopyObject = 83, OpFunction = 54, OpFunctionEnd = 56,
        OpLabel = 248, OpBranch = 249, OpKill = 252, OpDemote = 5380, OpTypePointer = 32, OpDPdx = 207, OpDPdy = 208, OpFwidth = 209,
        OpBitFieldInsert = 201, OpNot = 200, OpShiftLeftLogical = 196, OpBitwiseAnd = 199, OpBitwiseOr = 197, OpName = 5;

    private static readonly Dictionary<uint, int> DerivativeMap = new() { [210] = OpDPdx, [211] = OpDPdy, [212] = OpFwidth, [213] = OpDPdx, [214] = OpDPdy, [215] = OpFwidth };
    private static readonly HashSet<uint> DropCaps = [5347, 5345, 5346, 5379, 51];  // PhysicalStorageBuffer、VulkanMemoryModel、…DeviceScope、Demote、DerivativeControl
    private const int DecorationComponent = 31, DecorationBuiltIn = 11, DecorationLocation = 30;
    private const uint BuiltinBaseVertex = 4424, BuiltinBaseInstance = 4425;
    private static readonly Dictionary<uint, string> StageSuffix = new() { [0] = "vs", [4] = "fs" };
    private static readonly Regex CbName = new(@"^cb(\d+)(_buf)?$", RegexOptions.CultureInvariant);

    private readonly uint[] _words;
    public List<string> Notes { get; } = [];
    private readonly Dictionary<(long storage, long location, long component), string> _ioRenames;
    private List<uint[]> _newConsts = [];

    public SpirvPatcher(byte[] data, Dictionary<(long, long, long), string>? ioRenames = null)
    {
        _words = new uint[data.Length / 4];
        for (int i = 0; i < _words.Length; i++) _words[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
        if (_words[0] != 0x07230203) throw new InvalidOperationException("不是 SPIR-V");
        _ioRenames = ioRenames ?? [];
    }

    private IEnumerable<uint[]> Instructions()
    {
        int i = 5;
        while (i < _words.Length)
        {
            int ln = (int)(_words[i] >> 16);
            yield return _words[i..Math.Min(i + ln, _words.Length)];
            i += ln;
            if (ln == 0) yield break;  // Python 版這裡會無限迴圈；正常的 SPIR-V 不會出現
        }
    }

    public static uint[] EncodeString(string s)
    {
        var data = Encoding.UTF8.GetBytes(s + "\0");
        int padded = (data.Length + 3) / 4 * 4;
        Array.Resize(ref data, padded);
        var w = new uint[padded / 4];
        for (int i = 0; i < w.Length; i++) w[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
        return w;
    }

    /// <summary>_decode_string(words, pos)：回傳（字串, 佔用的 word 數）</summary>
    public static (string s, int n) DecodeString(uint[] words, int pos)
    {
        var raw = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(i * 4), words[i]);
        int start = pos * 4;
        int end = Array.IndexOf(raw, (byte)0, start);
        if (end < 0) throw new InvalidOperationException("subsection not found");
        string s = new UTF8Encoding(false, true).GetString(raw, start, end - start);
        return (s, end / 4 + 1 - pos);
    }

    private static uint? ConstValue(List<uint[]> insts, uint resultId)
    {
        foreach (var inst in insts)
            if ((inst[0] & 0xFFFF) == OpConstant && inst.Length == 4 && inst[2] == resultId) return inst[3];
        return null;
    }

    public byte[] Patch()
    {
        var insts = Instructions().ToList();

        var baseVars = new HashSet<uint>();
        var varPointee = new Dictionary<uint, uint>();
        var consts = new Dictionary<(uint, uint), uint>();
        uint maxId = _words[3];
        var varStorage = new Dictionary<uint, uint>();
        var varLocation = new Dictionary<uint, uint>();
        var varComponent = new Dictionary<uint, uint>();
        var names = new Dictionary<uint, string>();
        string? stageSuffix = null;
        foreach (var inst in insts)
        {
            uint op = inst[0] & 0xFFFF;
            if (op == OpDecorate && inst.Length >= 4)
            {
                if (inst[2] == DecorationBuiltIn && inst[3] is BuiltinBaseVertex or BuiltinBaseInstance) baseVars.Add(inst[1]);
                else if (inst[2] == DecorationLocation) varLocation[inst[1]] = inst[3];
                else if (inst[2] == DecorationComponent) varComponent[inst[1]] = inst[3];
            }
            else if (op == OpVariable)
            {
                varPointee[inst[2]] = inst[1];
                varStorage[inst[2]] = inst[3];
            }
            else if (op == OpConstant && inst.Length == 4) consts[(inst[1], inst[3])] = inst[2];
            else if (op == OpName && inst.Length >= 3) names[inst[1]] = DecodeString(inst, 2).s;
            else if (op == OpEntryPoint) stageSuffix = StageSuffix.GetValueOrDefault(inst[1]);
        }
        var ptrPointee = new Dictionary<uint, uint>();
        foreach (var inst in insts)
            if ((inst[0] & 0xFFFF) == OpTypePointer && inst.Length == 4) ptrPointee[inst[1]] = inst[3];
        var varElemType = varPointee.ToDictionary(kv => kv.Key, kv => ptrPointee.TryGetValue(kv.Value, out var t) ? (uint?)t : null);

        uint GetConst(uint? typeId, uint value)
        {
            if (typeId is null) throw new InvalidOperationException("常數型別是 None");
            var key = (typeId.Value, value);
            if (consts.TryGetValue(key, out var id)) return id;
            uint newId = maxId++;
            consts[key] = newId;
            _newConsts.Add([(4u << 16) | OpConstant, typeId.Value, newId, value]);
            return newId;
        }

        uint AllocId() => maxId++;

        _newConsts = [];
        bool renamedIo = false, renamedCb = false;
        var output = new List<uint[]>();
        int i = 0;
        while (i < insts.Count)
        {
            var inst = insts[i];
            uint op = inst[0] & 0xFFFF;
            uint ln = inst[0] >> 16;

            if (op == OpCapability && DropCaps.Contains(inst[1]))
            {
                i++;
                continue;
            }
            if (op == OpMemoryModel) inst = [inst[0], 0, 1];  // Logical, GLSL450
            if (op == OpDecorate && inst.Length >= 4)
            {
                if (inst[2] == DecorationComponent && inst[3] == 0)
                {
                    i++;
                    continue;
                }
                if (inst[2] == DecorationBuiltIn && inst[3] is BuiltinBaseVertex or BuiltinBaseInstance)
                {
                    i++;
                    continue;
                }
            }
            if (op == OpName && inst.Length >= 3)
            {
                uint target = inst[1];
                string? old = names.GetValueOrDefault(target);
                string? nw = null;
                if (!string.IsNullOrEmpty(old))
                {
                    // cbuffer：每個 stage 自己的 block 名
                    var m = CbName.Match(old);
                    if (m.Success && stageSuffix is not null)
                    {
                        nw = $"cb{m.Groups[1].Value}_{stageSuffix}{(m.Groups[2].Success ? m.Groups[2].Value : "")}";
                        renamedCb = true;
                    }
                    // varying：換成語意名（VS 輸出／FS 輸入用同一套）
                    long storage = varStorage.TryGetValue(target, out var st) ? st : long.MinValue;
                    long location = varLocation.TryGetValue(target, out var lc) ? lc : long.MinValue;
                    long component = varComponent.TryGetValue(target, out var cp) ? cp : 0;
                    if (_ioRenames.TryGetValue((storage, location, component), out var rn))
                    {
                        nw = rn;
                        renamedIo = true;
                    }
                }
                if (nw is not null && nw != old)
                {
                    var enc = EncodeString(nw);
                    inst = [(uint)((2 + enc.Length) << 16) | OpName, target, .. enc];
                }
            }
            if (op == OpEntryPoint && baseVars.Count > 0)
            {
                var (name, nw) = DecodeString(inst, 3);
                var ids = inst[(3 + nw)..].Where(x => !baseVars.Contains(x)).ToArray();
                var enc = EncodeString(name);
                uint wc = (uint)(3 + nw + ids.Length);
                inst = [(wc << 16) | OpEntryPoint, inst[1], inst[2], .. enc, .. ids];
            }
            if ((op == OpLoad || op == OpAccessChain || op == OpInBoundsAccessChain) && inst.Length >= 4 && baseVars.Contains(inst[3]))
            {
                if (op != OpLoad) throw new Native.ToolError("BaseVertex/BaseInstance 透過 access chain 存取，未支援");
                var elem = varElemType.GetValueOrDefault(inst[3]);
                inst = [(4u << 16) | OpCopyObject, inst[1], inst[2], GetConst(elem, 0)];
                Notes.Add("baseVertex/Instance=0");
            }
            if (DerivativeMap.TryGetValue(op, out var plain))
            {
                inst = [(ln << 16) | (uint)plain, .. inst[1..]];
                Notes.Add("derivative fine/coarse→plain");
            }
            if (op == OpBitFieldInsert)
            {
                // [type, id, base, insert, offset, count] → 位元運算（ESSL 300 沒有 bitfieldInsert）
                uint rtype = inst[1], rid = inst[2], bas = inst[3], insert = inst[4], offId = inst[5], cntId = inst[6];
                var offVal = ConstValue(insts, offId);
                var cntVal = ConstValue(insts, cntId);
                if (offVal is null || cntVal is null) throw new Native.ToolError("bitfieldInsert 的 offset/count 不是常數，未支援");
                if (offVal >= 32)
                {
                    output.Add([(4u << 16) | OpCopyObject, rtype, rid, bas]);
                    Notes.Add("bitfieldInsert→位元運算");
                    i++;
                    continue;
                }
                // D3D bfi：offset+count 超過 32 的位元丟棄；mask 截到 32 bit
                ulong mask = ((((ulong)1 << (int)cntVal.Value) - 1) << (int)offVal.Value) & 0xFFFFFFFF;
                if (cntVal.Value >= 64) mask = (ulong.MaxValue << (int)offVal.Value) & 0xFFFFFFFF;
                uint maskId = GetConst(rtype, (uint)mask);
                uint notMask = AllocId();
                output.Add([(4u << 16) | OpNot, rtype, notMask, maskId]);
                uint t1 = AllocId();
                output.Add([(5u << 16) | OpBitwiseAnd, rtype, t1, bas, notMask]);
                uint t2a = AllocId();
                output.Add([(5u << 16) | OpShiftLeftLogical, rtype, t2a, insert, offId]);
                uint t2 = AllocId();
                output.Add([(5u << 16) | OpBitwiseAnd, rtype, t2, t2a, maskId]);
                output.Add([(5u << 16) | OpBitwiseOr, rtype, rid, t1, t2]);
                Notes.Add("bitfieldInsert→位元運算");
                i++;
                continue;
            }
            if (op == OpDemote)
            {
                output.Add([(1u << 16) | OpKill]);
                i++;
                // 吃掉同區塊的後續死碼（到 OpBranch / OpLabel / OpFunctionEnd 為止）
                while (i < insts.Count)
                {
                    uint nop = insts[i][0] & 0xFFFF;
                    if (nop == OpBranch)
                    {
                        i++;
                        break;
                    }
                    if (nop is OpLabel or OpFunctionEnd) break;
                    i++;
                }
                Notes.Add("demote→kill");
                continue;
            }
            output.Add(inst);
            i++;
        }

        if (renamedCb) Notes.Add("cbuffer block 名加 stage 後綴");
        if (renamedIo) Notes.Add("varying 用語意名對接");

        // 新常數插到第一個 OpFunction 之前
        if (_newConsts.Count > 0)
        {
            var merged = new List<uint[]>();
            bool inserted = false;
            foreach (var inst in output)
            {
                if (!inserted && (inst[0] & 0xFFFF) == OpFunction)
                {
                    merged.AddRange(_newConsts);
                    inserted = true;
                }
                merged.Add(inst);
            }
            if (!inserted) merged.AddRange(_newConsts);
            output = merged;
        }

        var words = new List<uint>(_words.Length + 64);
        words.AddRange(_words[..5]);
        foreach (var inst in output) words.AddRange(inst);
        words[3] = maxId;
        var bytes = new byte[words.Count * 4];
        for (int k = 0; k < words.Count; k++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(k * 4), words[k]);
        return bytes;
    }
}
