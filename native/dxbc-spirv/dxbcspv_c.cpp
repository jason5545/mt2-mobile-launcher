// 步驟改寫自 dxbc-spirv c7f0697（https://github.com/doitsujin/dxbc-spirv）的 tools/dxbc_compiler.cpp、tools/dxbc_disasm.cpp，MIT，Copyright (c) 2025 Philip Rebohle；授權全文見 THIRD_PARTY_NOTICES.md。
// dxbcspv_c.h 的實作。步驟逐條照 dxbc-spirv（c7f0697 加 dxbc-spirv-local.patch）的
// tools/dxbc_compiler.cpp 與 tools/dxbc_disasm.cpp，只把讀檔、寫檔、stdout 換成記憶體。
#include "dxbcspv_c.h"

#include <cstdlib>
#include <cstring>
#include <sstream>
#include <string>
#include <vector>

#include "ir/ir.h"
#include "ir/ir_builder.h"
#include "ir/ir_legalize.h"
#include "ir/passes/ir_pass_lower_io.h"

#include "util/util_byte_stream.h"
#include "util/util_log.h"

#include "dxbc/dxbc_container.h"
#include "dxbc/dxbc_converter.h"
#include "dxbc/dxbc_disasm.h"
#include "dxbc/dxbc_interface.h"
#include "dxbc/dxbc_parser.h"
#include "dxbc/dxbc_signature.h"

#include "sm3/sm3_converter.h"
#include "sm3/sm3_disasm.h"
#include "sm3/sm3_parser.h"

#include "spirv/spirv_builder.h"
#include "spirv/spirv_mapping.h"

using namespace dxbc_spv;

namespace {

// 命令列版的 LocalLogger 把訊息寫到 stderr；這裡收集起來，失敗時回傳
class CollectLogger : public util::Logger {
public:
  util::LogLevel getMinimumSeverity() override { return util::LogLevel::eInfo; }
  void message(util::LogLevel severity, const char* msg) override {
    std::stringstream line;
    line << severity << msg << '\n';
    m_text += line.str();
  }
  std::string m_text;
};

char* dup(const std::string& s) {
  char* p = static_cast<char*>(std::malloc(s.size() + 1));
  if (p) std::memcpy(p, s.c_str(), s.size() + 1);
  return p;
}

int fail(char** error, const std::string& prefix, const CollectLogger& logger) {
  if (error) *error = dup(prefix + logger.m_text);
  return 1;
}

// tools/dxbc_compiler.cpp 的 writeSpirvBinary（傳值，跟原版一樣複製一份 builder）
std::vector<char> lowerToSpirv(ir::Builder builder) {
  { ir::LowerIoPass pass(builder);

    pass.lowerSampleCountToSpecConstant(0u);

    uint32_t cbv = 0u;
    pass.lowerLegacyBuiltInToCbv(ir::BuiltIn::eLegacyConstFloat, 0u, cbv++);
    pass.lowerLegacyBuiltInToCbv(ir::BuiltIn::eLegacyConstInt, 0u, cbv++);
    pass.lowerLegacyBuiltInToCbv(ir::BuiltIn::eLegacyConstBool, 0u, cbv++);
    pass.lowerLegacyBuiltInToCbv(ir::BuiltIn::eLegacyAlphaTest, 0u, cbv++);
    pass.lowerLegacyBuiltInToCbv(ir::BuiltIn::eLegacyFog, 0u, cbv++);
    pass.lowerLegacyBuiltInToCbv(ir::BuiltIn::eLegacyClipPlanes, 0u, cbv++);
    pass.lowerLegacyBuiltInToCbv(ir::BuiltIn::eLegacyPointArgs, 0u, cbv++);
    pass.lowerLegacyBuiltInToCbv(ir::BuiltIn::eLegacySamplerState, 0u, cbv++);
    pass.lowerLegacyBuiltInToCbv(ir::BuiltIn::eLegacyTextureStage, 0u, cbv++);
  }

  spirv::BasicResourceMapping mapping = { };

  spirv::SpirvBuilder::Options spirvOptions = { };
  spirvOptions.includeDebugNames = true;  // 命令列沒加 --no-debug
  spirvOptions.floatControls2 = false;

  spirvOptions.supportedRoundModesF16 =
  spirvOptions.supportedRoundModesF32 =
  spirvOptions.supportedRoundModesF64 = ir::RoundMode::eNearestEven | ir::RoundMode::eZero;

  spirvOptions.supportedDenormModesF16 =
  spirvOptions.supportedDenormModesF32 =
  spirvOptions.supportedDenormModesF64 = ir::DenormMode::eFlush | ir::DenormMode::ePreserve;

  spirv::SpirvBuilder spirvBuilder(builder, mapping, spirvOptions);
  spirvBuilder.buildSpirvBinary();

  size_t size = 0u;
  spirvBuilder.getSpirvBinary(size, nullptr);

  std::vector<char> data(size);
  spirvBuilder.getSpirvBinary(size, data.data());
  return data;
}

// tools/dxbc_disasm.cpp：反組譯寫進記憶體的串流（原版寫 stdout）
bool checkSignature(util::ByteReader reader, std::ostream& out) {
  if (!reader)
    return true;
  dxbc::Signature sig(reader);
  if (!sig) {
    util::Logger::err("Failed to parse signature");
    return false;
  }
  out << sig << std::endl;
  return true;
}

bool checkInterface(util::ByteReader reader, std::ostream& out) {
  if (!reader)
    return true;
  dxbc::InterfaceChunk iface(reader);
  if (!iface) {
    util::Logger::err("Failed to parse input signature chunk");
    return false;
  }
  out << iface << std::endl;
  return true;
}

bool checkCode(util::ByteReader reader, std::ostream& out) {
  if (!reader) {
    out << "(no code)" << std::endl;
    return true;
  }
  dxbc::Parser parser(reader);
  dxbc::Disassembler::Options options = { };
  options.lineNumbers = true;
  options.indent = true;
  dxbc::Disassembler disasm(options, parser.getShaderInfo());
  out << parser.getShaderInfo() << ":" << std::endl;
  while (parser) {
    auto op = parser.parseInstruction();
    if (!op) {
      util::Logger::err("Failed to parse instruction");
      return false;
    }
    disasm.disassembleOp(out, op);
    out << std::endl;
  }
  return true;
}

bool checkCodeSM3(util::ByteReader reader, std::ostream& out) {
  if (!reader) {
    out << "(no code)" << std::endl;
    return true;
  }
  sm3::Parser parser(reader);
  sm3::Disassembler::Options options = { };
  options.lineNumbers = true;
  options.indent = true;
  sm3::ConstantTable ctab;
  sm3::Disassembler disasm(options, parser.getShaderInfo());
  out << parser.getShaderInfo() << ":" << std::endl;
  while (parser) {
    auto op = parser.parseInstruction();
    if (op.getOpCode() == sm3::OpCode::eComment)
      continue;  // 命令列沒加 --debug-names
    if (!op) {
      util::Logger::err("Failed to parse instruction");
      return false;
    }
    disasm.disassembleOp(out, op, ctab);
    out << std::endl;
  }
  return true;
}

}  // namespace

extern "C" int dxbcspv_compile(const void* data, size_t size, uint32_t flags,
  void** spirv, size_t* spirv_size, char** error) {
  CollectLogger logger;
  if (spirv) *spirv = nullptr;
  if (spirv_size) *spirv_size = 0u;
  if (error) *error = nullptr;

  util::ByteReader reader(data, size);
  ir::Builder builder;
  bool status = false;

  if (!dxbc::Container::checkFourCC(reader)) {
    sm3::Converter::Options sm3Options = { };
    sm3Options.includeDebugNames = true;
    sm3::Converter converter(reader, sm3Options);
    status = converter.convertShader(builder);
  }

  if (!status) {
    dxbc::Container container(reader);
    if (!container)
      return fail(error, "Error: not a valid dxbc file.\n", logger);

    // 照原版：用檔案的雜湊當 shader 名稱
    auto name = [&] {
      std::stringstream stream;
      stream << container.getHash();
      return stream.str();
    } ();

    dxbc::Converter::Options dxbcOptions = { };
    dxbcOptions.includeDebugNames = true;
    dxbcOptions.name = name.c_str();

    dxbc::Converter converter(std::move(container), dxbcOptions);
    status = converter.convertShader(builder);
  }

  if (!status)
    return fail(error, "Error: Failed to convert shader.\n", logger);

  ir::CompileOptions compileOptions = { };
  compileOptions.syncOptions.insertLdsBarriers = false;
  compileOptions.syncOptions.insertUavBarriers = false;
  compileOptions.resourceOptions.structuredCbv = !(flags & DXBCSPV_CBV_ARRAY);
  compileOptions.cseOptions.relocateDescriptorLoad = true;
  compileOptions.descriptorIndexing.optimizeDescriptorIndexing = true;
  ir::legalizeIr(builder, compileOptions);

  std::vector<char> out = lowerToSpirv(builder);
  void* buf = std::malloc(out.size() ? out.size() : 1u);
  if (!buf)
    return fail(error, "Error: out of memory.\n", logger);
  std::memcpy(buf, out.data(), out.size());
  if (spirv) *spirv = buf; else std::free(buf);
  if (spirv_size) *spirv_size = out.size();
  return 0;
}

extern "C" int dxbcspv_disasm(const void* data, size_t size, char** text, char** error) {
  CollectLogger logger;
  if (text) *text = nullptr;
  if (error) *error = nullptr;

  util::ByteReader reader(data, size);
  std::ostringstream sink;
  bool ok;

  if (dxbc::Container::checkFourCC(reader)) {
    dxbc::Container container(reader);
    ok = checkSignature(container.getInputSignatureChunk(), sink) &&
         checkSignature(container.getOutputSignatureChunk(), sink) &&
         checkSignature(container.getPatchConstantSignatureChunk(), sink) &&
         checkInterface(container.getInterfaceChunk(), sink) &&
         checkCode(container.getCodeChunk(), sink);
  } else {
    ok = checkCodeSM3(reader, sink);
  }

  if (!ok)
    return fail(error, "", logger);
  if (text) *text = dup(sink.str());
  return 0;
}

extern "C" void dxbcspv_free(void* ptr) {
  std::free(ptr);
}
