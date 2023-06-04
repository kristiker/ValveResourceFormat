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
    string source;

    try
    {
        auto hlsl = make_unique<CompilerHLSL>(move(this->spirv_binary));
        ShaderResources resources = hlsl->get_shader_resources();

        CompilerHLSL::Options hlsl_options;
        hlsl_options.shader_model = 50;
        hlsl_options.use_entry_point_name = true;
        hlsl_options.preserve_structured_buffers = true;

        hlsl->set_hlsl_options(hlsl_options);
        //hlsl->rename_entry_point("main", "MainVs", spv::ExecutionModel::ExecutionModelVertex);
        //hlsl->rename_entry_point("main", "MainPs", spv::ExecutionModel::ExecutionModelFragment);

        hlsl->build_dummy_sampler_for_combined_images();
        hlsl->build_combined_image_samplers();

        try
        {
            source = hlsl->compile();
        }
        catch (spirv_cross::CompilerError& c)
        {
            source = hlsl->get_partial_source() + "\n" + c.what();
        }
    }
    catch (std::exception& ex)
    {
        source = ex.what();
    }

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
