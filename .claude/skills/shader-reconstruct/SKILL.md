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

- **VCS 71 has no constant buffer member names.** Members come out as `_m0`. Re-decompile the same combo
  with `--shader_backend hlsl` to read each member's `packoffset`, name them from use, and keep the
  offsets in a comment.
- **Vertex input names can be wrong.** They are assigned by semantic priority, not SPIR-V location, so
  they rotate once several streams are active. Trust what the code does with an input over its name.
- **Some combos only move vertex input locations** and leave the code identical.
- **Transform buffer slots carry packed metadata**, not only matrices.
- A combo being "inert" in the bytecode does not prove it does nothing; check the per-combo render state
  before saying so.

## Reporting

State the numbers: total variants, unique bytecodes, distinct bodies, and the verification result. Say
plainly which names are inferred rather than read out of the file, and whether the result was put through
a shader compiler or only checked structurally.
