#include "pch.h"
#include "SpirvReflector.h"
#include "SPIRV-Cross/spirv_hlsl.hpp"
#include "SPIRV-Cross/spirv_parser.hpp"
#include <vector>

using namespace std;
using namespace spirv_cross;

SpirvReflector::SpirvReflector()
{
}

void SpirvReflector::PushUInt32(uint32_t val)
{
	this->spirv_binary.push_back(val);
}

void SpirvReflector::Parse()
{
	auto hlsl = make_unique<CompilerHLSL>(move(this->spirv_binary));
	ShaderResources resources = hlsl->get_shader_resources();
	string source = hlsl->compile();
	this->result = source;
	this->result_len = source.length();
}

int SpirvReflector::GetDataLength()
{
	return this->result_len;
}

char SpirvReflector::GetChar(int i)
{
	return this->result[i];
}
