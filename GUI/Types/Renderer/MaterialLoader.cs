using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ExCSS;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;
using SkiaSharp;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using VrfMaterial = ValveResourceFormat.ResourceTypes.Material;

namespace GUI.Types.Renderer
{
    record struct MipUploadData(
        int TextureHandle,
        int Level,
        int Width,
        int Height,
        int Depth,
        int BufferSize,
        byte[] Buffer,
        bool Is3D,
        bool IsCompressed,
        PixelFormat PixelFormat,
        PixelType PixelType,
        SizedInternalFormat SizedInternalFormat,
        int TotalMipLevels
    )
    {
        public static MipUploadData Create(
            (uint Level, int Width, int Height, int Depth, int BufferSize) mipData,
            int textureHandle,
            int minMipLevelAllowed,
            byte[] buffer,
            bool is3D,
            TextureFormatMapping format,
            SizedInternalFormat sizedInternalFormat,
            int totalMipLevels)
        {
            var realLevel = (int)mipData.Level - minMipLevelAllowed;

            return new MipUploadData(
                TextureHandle: textureHandle,
                Level: realLevel,
                Width: mipData.Width,
                Height: mipData.Height,
                Depth: mipData.Depth,
                BufferSize: mipData.BufferSize,
                Buffer: buffer,
                Is3D: is3D,
                IsCompressed: format.PixelType is null,
                PixelFormat: format.PixelFormat ?? default,
                PixelType: format.PixelType ?? default,
                SizedInternalFormat: sizedInternalFormat,
                TotalMipLevels: totalMipLevels
            );
        }

        public readonly void Upload()
        {
            Log.Debug(nameof(MaterialLoader), $"Uploading {Width}x{Height} texture {TextureHandle}  mip:{Level}");

            if (IsCompressed)
            {
                if (Is3D)
                {
                    GL.CompressedTextureSubImage3D(TextureHandle, Level, 0, 0, 0, Width, Height, Depth, (PixelFormat)SizedInternalFormat, BufferSize, Buffer);
                }
                else
                {
                    GL.CompressedTextureSubImage2D(TextureHandle, Level, 0, 0, Width, Height, (PixelFormat)SizedInternalFormat, BufferSize, Buffer);
                }
            }
            else
            {
                if (Is3D)
                {
                    GL.TextureSubImage3D(TextureHandle, Level, 0, 0, 0, Width, Height, Depth, PixelFormat, PixelType, Buffer);
                }
                else
                {
                    GL.TextureSubImage2D(TextureHandle, Level, 0, 0, Width, Height, PixelFormat, PixelType, Buffer);
                }
            }

            GL.TextureParameter(TextureHandle, TextureParameterName.TextureBaseLevel, Level);
            //GL.TextureParameter(TextureHandle, TextureParameterName.TextureMaxLevel, Level);
        }
    };

    record struct TextureFormatMapping(SizedInternalFormat InternalFormat, PixelFormat? PixelFormat = null, PixelType? PixelType = null, SizedInternalFormat? InternalSrgbFormat = null);

    class MaterialLoader
    {
        private readonly Dictionary<ulong, RenderMaterial> Materials = [];
        private readonly Dictionary<string, RenderTexture> Textures = [];
        private readonly Dictionary<string, RenderTexture> TexturesSrgb = [];
        private readonly VrfGuiContext VrfGuiContext;
        private RenderTexture? ErrorTexture;
        private RenderTexture? DefaultNormal;
        private RenderTexture? DefaultMask;
        public static float MaxTextureMaxAnisotropy { get; set; }
        public int MaterialCount => Materials.Count;

        private readonly Dictionary<int, ConcurrentQueue<Task>> PendingMipReadsByPriority = [];
        private readonly ConcurrentQueue<MipUploadData> PendingMip0Uploads = new();
        private readonly ConcurrentQueue<MipUploadData> PendingMipUploads = new();

        private readonly Dictionary<string, string[]> TextureAliases = new()
        {
            ["g_tLayer2Color"] = ["g_tColorB", "g_tColor2"],
            ["g_tColor"] = ["g_tColor2", "g_tColor1", "g_tColorA", "g_tColorB", "g_tColorC", "g_tGlassDust"],
            ["g_tNormal"] = ["g_tNormalA", "g_tNormalRoughness", "g_tLayer1NormalRoughness", "g_tNormalRoughness1"],
            ["g_tLayer2NormalRoughness"] = ["g_tNormalB", "g_tNormalRoughness2"],
            ["g_tAmbientOcclusion"] = ["g_tLayer1AmbientOcclusion"],
        };

        public MaterialLoader(VrfGuiContext guiContext)
        {
            VrfGuiContext = guiContext;
        }

        private static readonly byte[] NewLineArray = "\n"u8.ToArray();

        public RenderMaterial GetMaterial(string? name, Dictionary<string, byte>? shaderArguments)
        {
            // HL:VR has a world node that has a draw call with no material
            if (name == null)
            {
                return GetErrorMaterial();
            }

            Span<byte> valueSpan = stackalloc byte[1];
            var hash = new XxHash3(StringToken.MURMUR2SEED);
            hash.Append(MemoryMarshal.AsBytes(name.AsSpan()));

            if (shaderArguments != null)
            {
                foreach (var (key, value) in shaderArguments)
                {
                    hash.Append(NewLineArray);
                    hash.Append(MemoryMarshal.AsBytes(key.AsSpan()));
                    hash.Append(NewLineArray);

                    valueSpan[0] = value;
                    hash.Append(valueSpan);
                }
            }

            var cacheKey = hash.GetCurrentHashAsUInt64();

            if (Materials.TryGetValue(cacheKey, out var mat))
            {
                return mat;
            }

            var resource = VrfGuiContext.LoadFileCompiled(name);
            mat = LoadMaterial(resource, shaderArguments);

            Materials.Add(cacheKey, mat);

            return mat;
        }

        public RenderMaterial LoadMaterial(Resource? resource, Dictionary<string, byte>? shaderArguments = null)
        {
            if (resource == null)
            {
                return GetErrorMaterial();
            }

            var vrfMaterial = (VrfMaterial?)resource.DataBlock;
            Debug.Assert(vrfMaterial != null);
            var mat = new RenderMaterial(
                vrfMaterial,
                VrfGuiContext,
                shaderArguments
            );

            foreach (var (textureName, texturePath) in mat.Material.TextureParams)
            {
                if (TryBindTexture(mat, textureName, texturePath))
                {
                    continue;
                }

                foreach (var (possibleAlias, aliases) in TextureAliases)
                {
                    if (mat.Textures.ContainsKey(possibleAlias))
                    {
                        continue;
                    }

                    if (aliases.Contains(textureName))
                    {
                        if (TryBindTexture(mat, possibleAlias, texturePath))
                        {
                            break;
                        }
                    }
                }
            }

            bool TryBindTexture(RenderMaterial mat, string name, string path)
            {
                if (mat.Shader.UniformNames.Contains(name))
                {
                    var srgbRead = mat.Shader.SrgbUniforms.Contains(name);
                    mat.Textures[name] = GetTexture(path, srgbRead, anisotropicFiltering: true);
                    return true;
                }

                return false;
            }

            return mat;
        }


        public RenderTexture GetTexture(string name, bool srgbRead = false, bool anisotropicFiltering = false)
        {
            // TODO: Create texture view for srgb textures
            var cache = srgbRead ? TexturesSrgb : Textures;

            if (cache.TryGetValue(name, out var tex))
            {
                return tex;
            }

            tex = LoadTexture(name, srgbRead, async: true);
            cache.Add(name, tex);

            if (anisotropicFiltering && MaxTextureMaxAnisotropy >= 4)
            {
                GL.TextureParameter(tex.Handle, (TextureParameterName)ExtTextureFilterAnisotropic.TextureMaxAnisotropyExt, MaxTextureMaxAnisotropy);
            }

            return tex;
        }

        private RenderTexture LoadTexture(string name, bool srgbRead = false, bool async = false)
        {
            var textureResource = VrfGuiContext.LoadFileCompiled(name);

            if (textureResource == null)
            {
                return GetErrorTexture();
            }

            return LoadTexture(textureResource, srgbRead, isViewerRequest: false, async);
        }

        public void UploadMip0Textures()
        {
            using var _ = Profiler.Profiler.BeginZone(zoneName: $"Upload Base Mip Levels");

            var i = 0;
            while (PendingMip0Uploads.TryDequeue(out var upload))
            {
                upload.Upload();
                i++;
            }

            _.EmitText($"Uploaded {i} textures!");
        }

        public void UploadPendingTextures(int maxWait = 10)
        {
            // Only queue tasks from the first non empty queue
            foreach (var (level, queue) in PendingMipReadsByPriority)
            {
                if (queue.IsEmpty)
                {
                    continue;
                }

                using var __ = Profiler.Profiler.BeginZone(zoneName: $"Signal Pending Texture Tasks - Level {level}");
                while (queue.TryDequeue(out var task))
                {
                    if (task.Status <= TaskStatus.WaitingForActivation)
                    {
                        task.Start();
                    }
                }

                break;
            }

            using var _ = Profiler.Profiler.BeginZone(zoneName: $"Upload Pending Mip Levels");

            var i = 0;
            var time = Stopwatch.StartNew();
            while (PendingMipUploads.TryDequeue(out var upload))
            {
                upload.Upload();
                //ArrayPool<byte>.Shared.Return(upload.Buffer);
                i++;

                if (maxWait > 0 && time.ElapsedMilliseconds > maxWait)
                {
                    break;
                }
            }

            _.EmitText($"Uploaded {i} textures!");
        }

#pragma warning disable CA1822 // Mark members as static
        public RenderTexture LoadTexture(Resource textureResource, bool srgbRead = false, bool isViewerRequest = false, bool async = false)
#pragma warning restore CA1822 // Mark members as static
        {
            using var _ = Profiler.Profiler.BeginZone(zoneName: $"Load Texture: {textureResource.FileName} srgb: {srgbRead} async: {async}");
            var data = (Texture?)textureResource.DataBlock;
            Debug.Assert(data != null);

            if (data.IsRawAnyImage)
            {
                using var bitmap = data.GenerateBitmap();
                return LoadBitmapTexture(bitmap);
            }

            var target = TextureTarget.Texture2D;
            var is3d = false;
            var clampModeS = (data.Flags & VTexFlags.SUGGEST_CLAMPS) != 0 ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;
            var clampModeT = (data.Flags & VTexFlags.SUGGEST_CLAMPT) != 0 ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;
            var clampModeU = (data.Flags & VTexFlags.SUGGEST_CLAMPU) != 0 ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;

            if ((data.Flags & VTexFlags.CUBE_TEXTURE) != 0)
            {
                is3d = true;
                target = (data.Flags & VTexFlags.TEXTURE_ARRAY) != 0 ? TextureTarget.TextureCubeMapArray : TextureTarget.TextureCubeMap;
                clampModeS = TextureWrapMode.ClampToEdge;
                clampModeT = TextureWrapMode.ClampToEdge;
                clampModeU = TextureWrapMode.ClampToEdge;
            }
            else if ((data.Flags & (VTexFlags.TEXTURE_ARRAY | VTexFlags.VOLUME_TEXTURE)) != 0)
            {
                is3d = true;
                target = (data.Flags & VTexFlags.VOLUME_TEXTURE) != 0 ? TextureTarget.Texture3D : TextureTarget.Texture2DArray;
            }

            var tex = new RenderTexture(target, data);
            var format = GetTextureFormat(data.Format);
            var sizedInternalFormat = srgbRead && format.InternalSrgbFormat is not null ? format.InternalSrgbFormat.Value : format.InternalFormat;

#if DEBUG
            var textureName = System.IO.Path.GetFileName(textureResource.FileName);

            if (textureName != null)
            {
                tex.SetLabel(textureName);
            }
#endif

            var texDepth = data.Depth;

            if (target == TextureTarget.TextureCubeMap || target == TextureTarget.TextureCubeMapArray)
            {
                texDepth *= 6;
            }

            var minMipLevelAllowed = 0;
            var texWidth = data.Width;
            var texHeight = data.Height;

            if (!isViewerRequest && !is3d && data.NumMipLevels > 1)
            {
                var maxUserTextureSize = Settings.Config.MaxTextureSize;

                while (minMipLevelAllowed + 1 < data.NumMipLevels && (texWidth > maxUserTextureSize || texHeight > maxUserTextureSize))
                {
                    minMipLevelAllowed++;

                    texWidth >>= 1;
                    texHeight >>= 1;
                }
            }

            tex.SetFiltering(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear);
            tex.SetWrapMode(TextureWrapMode.Repeat);

            if (is3d && target != TextureTarget.TextureCubeMap)
            {
                GL.TextureStorage3D(tex.Handle, data.NumMipLevels - minMipLevelAllowed, sizedInternalFormat, texWidth, texHeight, texDepth);
            }
            else
            {
                GL.TextureStorage2D(tex.Handle, data.NumMipLevels - minMipLevelAllowed, sizedInternalFormat, texWidth, texHeight);
            }

            if (async)
            {
                var i = 0;
                foreach (var mipData in data.GetEveryMipLevelMetrics())
                {
                    if (mipData.Level < minMipLevelAllowed)
                    {
                        continue;
                    }

                    var capturedMipData = mipData;
                    var capturedPriority = i;
                    var task = new Task(() =>
                    {
                        using var __ = Profiler.Profiler.BeginZone(zoneName: $"Read {capturedMipData.Width}x{capturedMipData.Height} {capturedMipData.Level} for {tex.Handle}");

                        var mipBuffer = new byte[capturedMipData.BufferSize]; // ArrayPool<byte>.Shared.Rent(capturedMipData.BufferSize);

                        try
                        {
                            data.ReadTextureMipLevel(mipBuffer.AsSpan(0, capturedMipData.BufferSize), capturedMipData.Level);

                            // Queue for upload on render thread
                            var uploadData = MipUploadData.Create(
                                capturedMipData,
                                tex.Handle,
                                minMipLevelAllowed,
                                mipBuffer,
                                is3d,
                                format,
                                sizedInternalFormat,
                                data.NumMipLevels - minMipLevelAllowed
                            );

                            var collection = capturedPriority == 0
                                ? PendingMip0Uploads
                                : PendingMipUploads;

                            collection.Enqueue(uploadData);

                            var nextPriority = PendingMipReadsByPriority[capturedPriority].IsEmpty
                                ? capturedPriority + 1
                                : capturedPriority;

                            if (PendingMipReadsByPriority.TryGetValue(nextPriority, out var nextQueue))
                            {
                                if (nextQueue.TryDequeue(out var nextTask))
                                {
                                    if (nextTask.Status == TaskStatus.Created)
                                    {
                                        nextTask.Start();
                                        return;
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Log.Error(nameof(MaterialLoader), $"Error reading mip level {capturedMipData.Level} for texture {tex.Handle}: {e}");
                            ArrayPool<byte>.Shared.Return(mipBuffer);
                            throw;
                        }
                    });

                    if (!PendingMipReadsByPriority.TryGetValue(i, out var queue))
                    {
                        PendingMipReadsByPriority[i] = new ConcurrentQueue<Task>();
                    }


                    if (i == 0)
                    {
                        if (mipData.Width <= 16 && mipData.Height <= 16)
                        {
                            task.RunSynchronously();
                        }
                        else
                        {
                            task.Start();
                        }
                    }
                    else
                    {
                        PendingMipReadsByPriority[i].Enqueue(task);
                    }

                    i++;
                }

                return tex;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(data.GetBiggestBufferSize());

            try
            {
                foreach (var (level, width, height, depth, bufferSize) in data.GetEveryMipLevelTexture(buffer, minMipLevelAllowed))
                {
                    var upload = MipUploadData.Create(
                        mipData: (level, width, height, depth, bufferSize),
                        textureHandle: tex.Handle,
                        minMipLevelAllowed: minMipLevelAllowed,
                        buffer: buffer,
                        is3D: is3d,
                        format: format,
                        sizedInternalFormat: sizedInternalFormat,
                        totalMipLevels: data.NumMipLevels - minMipLevelAllowed
                    );

                    upload.Upload();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return tex;
        }

        private static TextureFormatMapping GetTextureFormat(VTexFormat vformat) => vformat switch
        {
#pragma warning disable format
            VTexFormat.ATI1N           => new((SizedInternalFormat)InternalFormat.CompressedRedRgtc1),
            VTexFormat.ATI2N           => new((SizedInternalFormat)InternalFormat.CompressedRgRgtc2),
            VTexFormat.BC6H            => new((SizedInternalFormat)InternalFormat.CompressedRgbBptcUnsignedFloat),
            VTexFormat.BC7             => new((SizedInternalFormat)InternalFormat.CompressedRgbaBptcUnorm,        InternalSrgbFormat: (SizedInternalFormat)InternalFormat.CompressedSrgbAlphaBptcUnorm),
            VTexFormat.DXT1            => new((SizedInternalFormat)InternalFormat.CompressedRgbaS3tcDxt1Ext,      InternalSrgbFormat: (SizedInternalFormat)InternalFormat.CompressedSrgbAlphaS3tcDxt1Ext),
            VTexFormat.DXT5            => new((SizedInternalFormat)InternalFormat.CompressedRgbaS3tcDxt5Ext,      InternalSrgbFormat: (SizedInternalFormat)InternalFormat.CompressedSrgbAlphaS3tcDxt5Ext),
            VTexFormat.ETC2            => new((SizedInternalFormat)InternalFormat.CompressedRgb8Etc2,             InternalSrgbFormat: (SizedInternalFormat)InternalFormat.CompressedSrgb8Etc2),
            VTexFormat.ETC2_EAC        => new((SizedInternalFormat)InternalFormat.CompressedRgba8Etc2Eac,         InternalSrgbFormat: (SizedInternalFormat)InternalFormat.CompressedSrgb8Alpha8Etc2Eac),

            VTexFormat.R16             => new(SizedInternalFormat.R16,        PixelFormat.Red,    PixelType.UnsignedShort),
            VTexFormat.RG1616          => new(SizedInternalFormat.Rg16,       PixelFormat.Rg,     PixelType.UnsignedShort),
            VTexFormat.RGBA16161616    => new(SizedInternalFormat.Rgba16,     PixelFormat.Rgba,   PixelType.UnsignedShort),

            VTexFormat.R16F            => new(SizedInternalFormat.R16f,       PixelFormat.Red,    PixelType.HalfFloat),
            VTexFormat.RG1616F         => new(SizedInternalFormat.Rg16f,      PixelFormat.Rg,     PixelType.HalfFloat),
            VTexFormat.RGBA16161616F   => new(SizedInternalFormat.Rgba16f,    PixelFormat.Rgba,   PixelType.HalfFloat),

            VTexFormat.R32F            => new(SizedInternalFormat.R32f,       PixelFormat.Red,    PixelType.Float),
            VTexFormat.RG3232F         => new(SizedInternalFormat.Rg32f,      PixelFormat.Rg,     PixelType.Float),
            VTexFormat.RGBA32323232F   => new(SizedInternalFormat.Rgba32f,    PixelFormat.Rgba,   PixelType.Float),

            VTexFormat.RGBA8888        => new(SizedInternalFormat.Rgba8,      PixelFormat.Rgba,   PixelType.UnsignedByte,     SizedInternalFormat.Srgb8Alpha8),
            VTexFormat.BGRA8888        => new(SizedInternalFormat.Rgba8,      PixelFormat.Bgra,   PixelType.UnsignedByte,     SizedInternalFormat.Srgb8Alpha8),
            VTexFormat.I8              => new(SizedInternalFormat.R8,         PixelFormat.Red,    PixelType.UnsignedByte),

            //VTexFormat.IA88
            //VTexFormat.R11_EAC
            //VTexFormat.RG11_EAC
            //VTexFormat.RGB323232F
#pragma warning restore format

            _ => throw new NotImplementedException($"Unsupported texture format {vformat}")
        };

        public static readonly HashSet<string> ReservedTextures = [.. Enum.GetNames<ReservedTextureSlots>(), "g_tLPV"];

        private RenderMaterial GetErrorMaterial()
        {
            var errorMat = new RenderMaterial(VrfGuiContext.ShaderLoader.LoadShader("vrf.error"));
            return errorMat;
        }

        public RenderTexture GetErrorTexture()
        {
            if (ErrorTexture == null)
            {
                ReadOnlySpan<byte> color1 = [100, 25, 75];
                ReadOnlySpan<byte> color2 = [0, 127, 0];

                var color = new byte[16 * 3];

                for (var i = 0; i < 16; i++)
                {
                    var checkerboardX = i / 4 % 2;
                    var colorToUse = i % 2 == checkerboardX ? color1 : color2;
                    var pixel = color.AsSpan(i * 3, 3);
                    colorToUse.CopyTo(pixel);
                }

                ErrorTexture = GenerateColorTexture(4, 4, color);
            }

            return ErrorTexture;
        }

        private static RenderTexture CreateSolidTexture(byte r, byte g, byte b) => GenerateColorTexture(1, 1, [r, g, b]);
        public RenderTexture GetDefaultNormal() => DefaultNormal ??= CreateSolidTexture(127, 127, 255);
        public RenderTexture GetDefaultMask() => DefaultMask ??= CreateSolidTexture(255, 255, 255);

        public static RenderTexture LoadBitmapTexture(SKBitmap bitmap)
        {
            var texture = new RenderTexture(TextureTarget.Texture2D, bitmap.Width, bitmap.Height, 1, 1);

            var isHdr = bitmap.ColorType == Texture.HdrBitmapColorType;
            var store = GLTextureDecoder.GetImageExportFormat(isHdr);

            GL.TextureStorage2D(texture.Handle, 1, store.SizedInternalFormat, texture.Width, texture.Height);
            GL.TextureSubImage2D(texture.Handle, 0, 0, 0, texture.Width, texture.Height, store.PixelFormat, store.PixelType, bitmap.GetPixels());

            return texture;
        }

        private static RenderTexture GenerateColorTexture(int width, int height, byte[] color)
        {
            var texture = new RenderTexture(TextureTarget.Texture2D, width, height, 1, 1);
            texture.SetFiltering(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
            texture.SetWrapMode(TextureWrapMode.Repeat);

            GL.TextureStorage2D(texture.Handle, 1, SizedInternalFormat.Rgb8, width, height);
            GL.TextureSubImage2D(texture.Handle, 0, 0, 0, width, height, PixelFormat.Rgb, PixelType.UnsignedByte, color);

#if DEBUG
            texture.SetLabel(width > 1 ? "ErrorTexture" : "ColorTexture");
#endif

            return texture;
        }
    }
}
