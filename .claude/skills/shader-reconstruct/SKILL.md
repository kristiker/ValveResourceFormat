---
name: shader-reconstruct
description: Reconstruct a Source 2 shader from its compiled .vcs variants into one readable .slang/.glsl/.hlsl file with the combos restored as #if blocks. Use when asked to decompile, reconstruct, or merge the variants of a .vcs shader, or to work out what a specific S_/D_ combo does.
---

# Reconstructing a shader from its compiled variants

A `.vcs` holds one compiled program per legal combination of its static (`S_`) and dynamic (`D_`) combos.
The goal is to recover a single source file covering all of them.

Read [docs/guides/reconstructing-shaders.md](../../../docs/guides/reconstructing-shaders.md) for the full
walkthrough, the caveats, and a worked example. The short version:

## Steps

Build the CLI first (`dotnet build CLI/CLI.csproj -c Debug`), it lands in `CLI/bin/Debug/Source2Viewer-CLI.exe`.

1. **Survey.** `--shader_list_combos` lists every variant with its combo values and bytecode hash. Rows
   sharing a hash compiled to the same program.

2. **Dump.** `--shader_dump_all --shader_backend glsl --shader_clean --output <dir>`.
   `--shader_clean` is mandatory for this workflow: without it SPIR-V id derived names differ between
   variants that are otherwise identical, and nothing dedupes or diffs. Pick `glsl` for this repo's
   renderer, `hlsl` to read something closer to Valve's original `.vfx`.

3. **Triage.** `python .claude/skills/shader-reconstruct/verify_combos.py <dumpdir>` reports which combos
   change the code, which are inert, and the shortest reading order.

4. **Read one, diff the rest.** Read the all-zero base variant in full. For every other combo read
   `diff -u base variant` instead of the whole file, then diff combinations against each other to
   establish ordering between stages.

5. **Write** the reconstruction with every combo `#define`d to 0 at the top and each delta behind an `#if`.

6. **Verify.** Write a marker map (text in the dumped variant -> text in your reconstruction, keyed on
   *use* not declaration) and run
   `verify_combos.py <dumpdir> --reconstruction <file> --markers <json>`.
   It preprocesses your file once per combo configuration and checks every marker lands in exactly the
   variants the real bytecode has it in. Do not call the job done before this reports no mismatches.

## Traps

- **VCS 71 has no constant buffer member names,** so members come out as `_m0`. Get the real ones from the
  DirectX build, whose reflection chunk keeps them:
  `--shader_cbuffers` on the `_pc_` copy in `shaders_pc_dir.vpk` prints every buffer's members at their
  `packoffset`, plus the real texture and sampler names. Both builds compile the same source so the offsets
  agree; only bind points differ. Reflection is stripped per program, so if the one you want is missing, try
  the sibling program — a vertex program often declares the pixel program's buffers too. Failing that, fall
  back to `--shader_backend hlsl` for the offsets and name members from use.
- **Vertex input names are the real semantic, but a semantic is not a meaning.** They come from the
  shader's attribute map, looked up per SPIR-V location, so `input_TEXCOORD7` really is TEXCOORD7 even
  when a dozen streams are active. What the semantic does *not* tell you is what the data is: on a
  spritecard trail, TEXCOORD3 through TEXCOORD6 are spline control points and TEXCOORD7 holds three
  unrelated per-particle scalars. Read the code for that. A shader with no attribute map falls back to a
  positional `input_<location>` with no semantic at all.
- **Some combos only move vertex input locations** and leave the code identical.
- **Transform buffer slots carry packed metadata**, not only matrices.
- A combo being "inert" in the bytecode does not prove it does nothing; check the per-combo render state
  before saying so.

## Reporting

State the numbers: total variants, unique bytecodes, distinct bodies, and the verification result. Say
plainly which names are inferred rather than read out of the file, and whether the result was put through
a shader compiler or only checked structurally.
