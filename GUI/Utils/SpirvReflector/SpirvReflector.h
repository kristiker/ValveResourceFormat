#pragma once
#include <iostream>
#include <vector>

using namespace std;

class SpirvReflector
{
	vector<uint32_t> spirv_binary;
	string result;
	size_t result_len;

public:
	SpirvReflector();
	void PushUInt32(uint32_t val);
	void Parse();
	int GetDataLength();
	char GetChar(int i);
};

extern "C" __declspec(dllexport) void *CreateSpirvReflector()
{
	return (void *)new SpirvReflector();
}
extern "C" __declspec(dllexport) void PushUInt32(SpirvReflector *a, uint32_t y)
{
	a->PushUInt32(y);
}
extern "C" __declspec(dllexport) void Parse(SpirvReflector *a)
{
	return a->Parse();
}
extern "C" __declspec(dllexport) int GetDataLength(SpirvReflector *a)
{
	return a->GetDataLength();
}
extern "C" __declspec(dllexport) char GetChar(SpirvReflector *a, int i)
{
	return a->GetChar(i);
}
