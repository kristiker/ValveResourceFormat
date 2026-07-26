# Reconstructing a shader from its compiled variants

A Source 2 `.vcs` holds one compiled program per legal combination of its static (`S_`) and dynamic (`D_`)
combos. Decompiling one of them gives you one branch of the original shader. This guide walks through
recovering the whole thing: a single readable source file with the combos back in as `#if` blocks.

Worked example throughout: `shaders/vfx/depth_only_vulkan_50_vs.vcs` from Counter-Strike 2, which ships
432 variants.

## 1. See what is in there

```powershell
./Source2Viewer-CLI.exe -i "<game>/shaders_vulkan_dir.vpk" -f "shaders/vfx/depth_only_vulkan_50_vs.vcs" --shader_list_combos
```

Each row is one variant: its static and dynamic combo ids, the combo values that produce it, and the MD5
of the bytecode it selects. Rows sharing a hash compiled to the same program. The footer sums it up:

```
--- 432 variants across 24 static combos, 192 of them unique
```

Combos are not independent, the shader's rules delete combinations. `depth_only` declares
`Allow1(S_TRANSLUCENT, S_ALPHA_TEST)`, so 8 of its 32 static combinations were never compiled and are
simply absent from the list.

## 2. Dump every unique variant

```powershell
./Source2Viewer-CLI.exe -i "<game>/shaders_vulkan_dir.vpk" -f "shaders/vfx/depth_only_vulkan_50_vs.vcs" --shader_dump_all --shader_backend glsl --shader_clean --output "depth_only"
```

`--shader_clean` is what makes this tractable. SPIRV-Cross names temporaries and structs after SPIR-V ids
(`_25207`, `_1ident`, `struct _730`), and those ids differ between variants even when the emitted code is
identical. Without the flag every variant looks unrelated to every other one. With it, equivalent variants
become byte-identical, so they collapse into one file and the ones that do differ produce small diffs.

`--shader_backend glsl` matters if the result is meant for this repo's renderer, whose `.slang` files are
GLSL. Leave it off to get HLSL, which reads closer to the `.vfx` Valve wrote.

The dump writes one file per unique source plus `manifest.tsv` mapping every combo combination to its file.

## 3. Find the combos that actually matter

```powershell
python .claude/skills/shader-reconstruct/verify_combos.py depth_only/depth_only_vulkan_50_vs
```

This reports which combos change the emitted code, which are inert, and the shortest reading order: the
all-zero base variant, then one variant per combo raised on its own.

Expect surprises here. In `depth_only`'s vertex shader `S_MORPH_SUPPORTED` never changes the code by
itself, it only gates `D_MORPH`. In the pixel shader `D_WRITE_DEPTH_TO_COLOR` and `D_SHADOW_PASS` select
identical bytecode *and* identical render state in all 32 variants, so neither reaches the program at all.

## 4. Read the base, then diff

Read the base variant in full. After that, diff rather than re-read:

```bash
diff -u depth_only_vulkan_50_vs_00000000_0000.glsl depth_only_vulkan_50_vs_00000001_0000.glsl
```

Each single-combo diff is usually a handful of lines and tells you exactly what that combo adds. Once the
individual combos are understood, diff a few combinations against each other to pin down ordering: which
stage runs first when two are on together.

## 5. Write the reconstruction

Declare every combo as a `#define` at the top defaulting to 0, then guard each delta:

```glsl
#define S_MODE_DEPTH 0
#define D_MORPH 0

#if D_MORPH
    vPositionOs += GetMorphOffset(nTransformIdx);
#endif
```

Things worth knowing before you start:

- **Constant buffer member names are gone in VCS 71.** Older versions carry an external constant buffer
  block with member names; version 71 does not, so members come out as `_m0`, `_m1`. Decompile the same
  combo with `--shader_backend hlsl` to read the `packoffset` of each member, and name them from how the
  code uses them. Record the offsets in a comment so the guess is checkable.
- **Vertex input names can be wrong.** They are assigned from a semantic priority list rather than by
  SPIR-V location, so once several vertex streams are active the names rotate. Trust what the code does
  with an input, not its name: an input that indexes `g_instanceBuffer` is the instance index whatever it
  is called.
- **A combo may only change the vertex layout.** `D_COMPRESSED_NORMALS_AND_TANGENTS` produces identical
  code in every `depth_only` vertex variant that does not read the normal, and only shifts input locations.
- **Transform buffer slots carry more than matrices.** Source 2 packs metadata into unused matrix slots,
  so `transforms[i][0].z` may be a scale and `transforms[i + 1]` may be morph atlas placement rather than
  a transform.

## 6. Verify against every variant

Write a marker map: text that appears in a dumped variant, and the text that should appear in your
reconstruction for the same combos.

```json
{
  "g_tCompositeMorphTextureAtlas": "g_tCompositeMorphTextureAtlas",
  "0.00195503421127796173":        "1023.0",
  "out vec3 output_0":             "out vec3 vPositionWsOut"
}
```

Prefer markers that key on *use* rather than declaration, otherwise a guard that is too loose passes.

```powershell
python .claude/skills/shader-reconstruct/verify_combos.py depth_only/depth_only_vulkan_50_vs --reconstruction depth_only.vert.slang --markers markers.json
```

This preprocesses the reconstruction once per combo configuration and checks every marker appears in
exactly the variants the real bytecode has it in:

```
432 combo configurations preprocessed, 12 markers each
mismatches: none
```

This is the step that catches guards wired to the wrong combo, which is the most likely mistake and the
hardest to spot by eye.

## Decompiling a single variant

Useful while investigating, no dump needed:

```powershell
./Source2Viewer-CLI.exe -i "<game>/shaders_vulkan_dir.vpk" -f "shaders/vfx/depth_only_vulkan_50_vs.vcs" --shader_combo "S_ALPHA_TEST=1,D_BLEND_WEIGHT_COUNT=4" --shader_backend glsl --shader_clean
```

Static and dynamic combos mix freely, a bare name means `=1`, and anything omitted stays at its minimum.
Asking for a combination the shader's rules forbid reports which combination was used instead.

## Caveats

The `.vcs` is the compiled output, so some things are simply not in it: uniform names in newer versions,
the original control flow (loops arrive unrolled or rewritten), and anything the optimiser folded away. A
reconstruction is a faithful account of what the GPU runs, not a recovery of Valve's source file.
