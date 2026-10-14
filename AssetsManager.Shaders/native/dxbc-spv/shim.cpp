// DXBC to SPIR-V through dxbc-spirv, with the lowering options a GLSL ES 3.00 backend needs.
//
// The options are the ones the translation was measured with: every cbuffer as a vec4 array
// (`structuredCbv = false`), raw and structured buffers as typed buffers so one texel-buffer
// rewrite covers both, no 16-bit types, and `lowerDot` left on because the SPIR-V backend has
// no `FDot`.
#include <cstdint>
#include <cstdlib>
#include <cstring>
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
#include "spirv/spirv_builder.h"
#include "spirv/spirv_mapping.h"

using namespace dxbc_spv;

namespace {

// dxbc-spirv logs through one process-wide logger. Warnings are dropped: a blob that converts
// is a blob, and one that does not reports through the return value.
class SilentLogger : public util::Logger {
public:
  util::LogLevel getMinimumSeverity() override { return util::LogLevel::eError; }
  void message(util::LogLevel, const char*) override { }
};

SilentLogger g_logger;

char* duplicate(const std::string& text) {
  char* out = static_cast<char*>(std::malloc(text.size() + 1));
  if (out != nullptr) {
    std::memcpy(out, text.c_str(), text.size() + 1);
  }
  return out;
}

}

extern "C" __declspec(dllexport) int dxbc_spv_lol_compile(
    const void* data, size_t size,
    uint32_t** out_words, size_t* out_count, char** out_error) {
  *out_words = nullptr;
  *out_count = 0;
  *out_error = nullptr;

  util::ByteReader reader(data, size);
  dxbc::Container container(reader);
  if (!container) {
    *out_error = duplicate("not a DXBC container");
    return 1;
  }

  ir::Builder builder;
  dxbc::Converter::Options dxbcOptions = { };
  dxbcOptions.includeDebugNames = true;
  dxbcOptions.name = "main";
  dxbc::Converter converter(std::move(container), dxbcOptions);
  if (!converter.convertShader(builder)) {
    *out_error = duplicate("convertShader failed");
    return 1;
  }

  ir::CompileOptions co = { };
  co.min16Options.enableFloat16 = false;
  co.min16Options.enableInt16 = false;
  co.resourceOptions.structuredCbv = false;
  co.resourceOptions.structuredSrvUav = false;
  co.bufferOptions.useTypedForRaw = true;
  co.bufferOptions.useTypedForStructured = true;
  co.bufferOptions.minStructureAlignment = 0x10000u;
  co.cseOptions.relocateDescriptorLoad = true;
  co.descriptorIndexing.optimizeDescriptorIndexing = true;
  ir::legalizeIr(builder, co);

  {
    ir::LowerIoPass pass(builder);
    pass.lowerSampleCountToSpecConstant(0u);
  }

  spirv::BasicResourceMapping mapping;
  spirv::SpirvBuilder::Options so = { };
  so.includeDebugNames = true;
  so.floatControls2 = false;
  spirv::SpirvBuilder sb(builder, mapping, so);
  sb.buildSpirvBinary();

  size_t bytes = 0u;
  sb.getSpirvBinary(bytes, nullptr);
  if (bytes == 0u || bytes % sizeof(uint32_t) != 0u) {
    *out_error = duplicate("SPIR-V builder produced no module");
    return 1;
  }
  uint32_t* words = static_cast<uint32_t*>(std::malloc(bytes));
  if (words == nullptr) {
    *out_error = duplicate("out of memory");
    return 1;
  }
  sb.getSpirvBinary(bytes, words);
  *out_words = words;
  *out_count = bytes / sizeof(uint32_t);
  return 0;
}

extern "C" __declspec(dllexport) void dxbc_spv_lol_free(void* pointer) {
  std::free(pointer);
}

