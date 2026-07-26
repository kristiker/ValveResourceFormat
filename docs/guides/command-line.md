# Command-line utility

While Source 2 Viewer is a GUI application for Windows, there is also a command-line utility available for all of Windows, Linux, and macOS.

The binary name is `Source2Viewer-CLI`.

## Command-line options

| Option                       | Description                                                                                                                                                     |
| ---------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Input**                    |                                                                                                                                                                 |
| `--input` (or `-i`)          | Input file to be processed. With no additional arguments, a summary of the input(s) will be displayed.                                                          |
| `--recursive`                | If specified and given input is a folder, all subdirectories will be scanned too.                                                                              |
| `--recursive_vpk`            | If specified along with `--recursive`, will also recurse into VPK archives.                                                                                     |
| `--vpk_extensions` (or `-e`) | File extension(s) filter, example: "vcss_c,vjs_c,vxml_c".                                                                                                       |
| `--vpk_filepath` (or `-f`)   | File path filter(s), supports comma-separated values. Example: "panorama/,sounds/" or "scripts/items/items_game.txt".                                           |
| `--vpk_cache`                | Use cached VPK manifest to keep track of updates. Only changed files will be written to disk.                                                                   |
| `--vpk_verify`               | Verify checksums and signatures.                                                                                                                                |
| **Output**                   |                                                                                                                                                                 |
| `--output` (or `-o`)         | Output path to write to. If input is a folder (or a VPK), this should be a folder.                                                                              |
| `--all` (or `-a`)            | Print the content of each resource block in the file.                                                                                                           |
| `--block` (or `-b`)          | Print the content of a specific block, example: DATA, RERL, REDI, NTRO.                                                                                         |
| `--vpk_decompile` (or `-d`)  | Decompile supported resource files.                                                                                                                             |
| `--texture_decode_flags`     | Decompile textures with specified decode flags. Options: "none", "auto", "ForceLDR". Default: "auto".                                                           |
| `--vpk_list` (or `-l`)       | Lists all resources in given VPK. File extension and path filters apply.                                                                                        |
| `--vpk_dir`                  | Print a list of files in given VPK and information about them.                                                                                                  |
| **Type specific export**     |                                                                                                                                                                 |
| `--gltf_export_format`       | Exports meshes/models in given glTF format. Must be either 'gltf' or 'glb'.                                                                                     |
| `--gltf_export_materials`    | Whether to export materials during glTF exports.                                                                                                                |
| `--gltf_export_animations`   | Whether to export animations during glTF exports.                                                                                                               |
| `--gltf_mesh_list`           | Comma-separated list of meshes to include in glTF export. By default includes all meshes in a model.                                                            |
| `--gltf_animation_list`      | Comma-separated list of animations to include in glTF export, example: "idle,dropped". Requires `--gltf_export_animations`. By default includes all animations. |
| `--gltf_textures_adapt`      | Whether to perform any glTF spec adaptations on textures (e.g. split metallic map).                                                                             |
| `--gltf_export_extras`       | Export additional Mesh properties into glTF extras                                                                                                              |
| `--tools_asset_info_short`   | Whether to print only file paths for tools_asset_info files.                                                                                                    |
| **Shaders**                  |                                                                                                                                                                 |
| `--shader_list_combos`       | List every compiled variant of a shader with its combo values and bytecode hash.                                                                                |
| `--shader_combo`             | Decompile the shader variant matching these combo values, example: "S_ALPHA_TEST=1,D_BLEND_WEIGHT_COUNT=4". A bare name means "=1", omitted combos stay at their minimum. |
| `--shader_dump_all`          | Write every unique compiled variant of a shader to the output folder, along with a manifest.                                                                    |
| `--shader_backend`           | Language to decompile shader bytecode to. Must be either 'glsl' or 'hlsl'. By default hlsl is attempted first, falling back to glsl.                            |
| `--shader_clean`             | Rename generated identifiers and strip constant buffer prefixes, so that variants of the same shader can be compared to each other.                              |
| `--shader_cbuffers`          | Print the constant buffer and resource names a DirectX shader's reflection chunk retains. Only the "_pc_" build of a shader has them.                            |
| **Other**                    |                                                                                                                                                                 |
| `--threads`                  | If higher than 1, files will be processed concurrently.                                                                                                         |
| `--version`                  | Show version information.                                                                                                                                       |
| `--help`                     | Show help information.                                                                                                                                          |

There are also `--stats` related options (for collecting statistics and testing exports) primarily intended for VRF developers. You can pass `--input "steam"` to automatically scan all Steam library folders for Source 2 files. See the `--help` output for details.

### Cached VPK Manifest

When using `--vpk_cache`, a `.manifest.txt` file is created alongside the VPK to track file versions. This allows incremental exports where only changed files are written. The cache is automatically invalidated if the decompiler version changes.

## Examples

### List all files in a VPK

Use `--vpk_dir` to also print file metadata.

```powershell
./Source2Viewer-CLI.exe -i "core/pak01_dir.vpk" --vpk_list
```

### Export the entire VPK as is

```powershell
./Source2Viewer-CLI.exe -i "core/pak01_dir.vpk" --output "pak01_exported"
```

### Export only specific folders from a VPK

Export only the "panorama/layout" folder:

```powershell
./Source2Viewer-CLI.exe -i "core/pak01_dir.vpk" --output "pak01_exported" --vpk_filepath "panorama/layout"
```

### Decompile and export Panorama files

Decompile and export all Panorama files to a folder named "exported":

```powershell
./Source2Viewer-CLI.exe -i "core/pak01_dir.vpk" -e "vjs_c,vxml_c,vcss_c" -o "exported" -d
```

### Print resource blocks

Print resource blocks for a specific file (similar to resourceinfo.exe in Source 2). Use `--block DATA` to only print a specific block:

```powershell
./Source2Viewer-CLI.exe -i "file.vtex_c" --all
```

### Decompile a specific file

```powershell
./Source2Viewer-CLI.exe -i "file.vtex_c" -o exported.png
```

### Export a model to glTF with specific animations

Export a model with only specific animations included:

```powershell
./Source2Viewer-CLI.exe -i "model.vmdl_c" -o "output.glb" -d --gltf_export_format glb --gltf_export_animations --gltf_animation_list "idle,walk,run"
```

### Scan Steam libraries for statistics

```powershell
./Source2Viewer-CLI.exe -i "steam" --stats
```

### Decompile all shaders

```powershell
./Source2Viewer-CLI.exe -i "<game>/shaders_vulkan_dir.vpk" --vpk_decompile --vpk_extensions "vcs" --output "."
```

### Decompile one variant of a shader

A shader is compiled once per legal combination of its static (`S_`) and dynamic (`D_`) combos. List them to
see which combinations exist and which of them share bytecode:

```powershell
./Source2Viewer-CLI.exe -i "<game>/shaders_vulkan_dir.vpk" -f "shaders/vfx/depth_only_vulkan_50_vs.vcs" --shader_list_combos
```

Then decompile the one you want. Static and dynamic combos can be mixed freely, anything you leave out stays
at its minimum value:

```powershell
./Source2Viewer-CLI.exe -i "<game>/shaders_vulkan_dir.vpk" -f "shaders/vfx/depth_only_vulkan_50_vs.vcs" --shader_combo "S_ALPHA_TEST=1,D_BLEND_WEIGHT_COUNT=4" --shader_backend glsl
```

Asking for a combination the shader's rules forbid reports which combination was used instead.

### Dump every variant of a shader

`--shader_clean` renames the SPIR-V id derived identifiers that SPIRV-Cross generates. Those ids differ
between variants even when the code is identical, so without it every variant looks different from every
other one. With it, variants that compiled to the same code produce the same text and collapse into one file:

```powershell
./Source2Viewer-CLI.exe -i "<game>/shaders_vulkan_dir.vpk" -f "shaders/vfx/depth_only_vulkan_50_vs.vcs" --shader_dump_all --shader_backend glsl --shader_clean --output "depth_only"
```

The `manifest.tsv` written alongside maps every combo combination to the file it produced.

### Recovering constant buffer member names

VCS 71 stores no constant buffer member names, so a decompiled Vulkan shader names them `_m0`, `_m1` and
so on. The DirectX build of the same shader keeps the original HLSL names in its DXBC reflection chunk, and
because both builds compile the same source their register offsets agree. So read the names off the
`shaders_pc_dir.vpk` copy and apply them to the `shaders_vulkan_dir.vpk` decompile:

```bash
./Source2Viewer-CLI.exe -i "<game>/shaders_pc_dir.vpk" -f "shaders/vfx/spritecard_pc_50_vs.vcs" --shader_cbuffers
```

Members are listed at their `packoffset`, in the same `cN.lane` form `--shader_backend hlsl` emits, so the
two line up directly. Members the shader never reads are marked `(--)`; the reflection chunk lists
everything the source declared, whereas a decompile only shows what a given variant actually touches.

Two caveats. Reflection is stripped per program rather than per shader, so a shader's vertex program may
keep it while its pixel program does not — but a program often declares buffers it barely uses, so the
vertex program's chunk may still name the pixel program's buffers. And bind points are not transferable:
the DirectX and Vulkan backends assign registers independently, so only names and offsets carry over.

## Argument Stability

Command-line arguments and their behavior may change in future releases. We do not guarantee stability of the CLI interface. If you are writing scripts that depend on specific arguments or output formats, be prepared to update them when upgrading to newer versions.

[The source code is available here.](https://github.com/ValveResourceFormat/ValveResourceFormat/blob/master/CLI/Decompiler.cs)
