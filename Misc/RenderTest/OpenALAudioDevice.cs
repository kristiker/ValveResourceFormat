using System.Threading;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using ValveResourceFormat.Renderer.Audio;

/// <summary>
/// Cross platform <see cref="IAudioDevice"/> backed by OpenAL Soft, whose native binaries ship for
/// Windows, Linux and macOS in the Silk.NET.OpenAL.Soft.Native package, so nothing has to be
/// installed system wide. The GUI uses WASAPI through NAudio instead, which is Windows only.
/// </summary>
/// <remarks>
/// The mixer already spatializes everything into a stereo bed, so this streams a single source
/// relative to the listener rather than using OpenAL's own 3D panning.
/// </remarks>
internal sealed unsafe class OpenALAudioDevice : IAudioDevice
{
    /// <summary>ALC_FREQUENCY, the output rate to ask the device for.</summary>
    private const int AlcFrequency = 0x1007;

    /// <summary>
    /// The mixer submits 512 frame chunks, so four buffers is roughly 43ms of queued audio, in the
    /// same ballpark as the mix-ahead the GUI's WASAPI device keeps.
    /// </summary>
    private const int BufferCount = 4;

    private readonly ALContext alc;
    private readonly AL al;
    private readonly Device* device;
    private readonly Context* context;
    private readonly uint source;
    private readonly uint[] buffers = new uint[BufferCount];
    private readonly Queue<uint> freeBuffers = new(BufferCount);
    private readonly BufferFormat format;

    /// <summary>
    /// Held across every OpenAL call so <see cref="Dispose"/> cannot delete the source and buffers
    /// out from under the mixing thread. The wait for a free buffer happens outside it.
    /// </summary>
    private readonly Lock alLock = new();
    private bool disposed;

    /// <summary>Staging area for the 16 bit fallback, unused when AL_EXT_FLOAT32 is present.</summary>
    private short[] pcmScratch = [];

    /// <inheritdoc/>
    public int SampleRate { get; }

    /// <inheritdoc/>
    public int Channels => 2;

    /// <summary>
    /// Opens the default OpenAL output device.
    /// </summary>
    /// <param name="sampleRate">Output rate to request from the device.</param>
    /// <exception cref="InvalidOperationException">There is no usable output device.</exception>
    public OpenALAudioDevice(int sampleRate = 48000)
    {
        SampleRate = sampleRate;

        alc = ALContext.GetApi(soft: true);
        al = AL.GetApi(soft: true);

        try
        {
            device = alc.OpenDevice(string.Empty);

            if (device == null)
            {
                throw new InvalidOperationException("OpenAL found no audio output device.");
            }

            int* attributes = stackalloc int[] { AlcFrequency, SampleRate, 0 };
            context = alc.CreateContext(device, attributes);

            if (context == null || !alc.MakeContextCurrent(context))
            {
                throw new InvalidOperationException("Failed to make an OpenAL context current.");
            }

            // OpenAL Soft always has the float extension; Apple's deprecated OpenAL does not.
            format = al.IsExtensionPresent("AL_EXT_FLOAT32")
                ? (BufferFormat)FloatBufferFormat.Stereo
                : BufferFormat.Stereo16;

            source = al.GenSource();
            al.SetSourceProperty(source, SourceBoolean.SourceRelative, true);

            for (var i = 0; i < buffers.Length; i++)
            {
                buffers[i] = al.GenBuffer();
                freeBuffers.Enqueue(buffers[i]);
            }
        }
        catch
        {
            DestroyContext();
            alc.Dispose();
            al.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void SubmitSamples(ReadOnlySpan<float> samples)
    {
        while (true)
        {
            using (alLock.EnterScope())
            {
                if (disposed)
                {
                    return;
                }

                RecycleProcessedBuffers();

                if (freeBuffers.Count > 0)
                {
                    QueueSamples(freeBuffers.Dequeue(), samples);
                    return;
                }
            }

            // The device retires a buffer roughly every 10ms, this paces the mixing thread to it.
            Thread.Sleep(1);
        }
    }

    private void RecycleProcessedBuffers()
    {
        al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out var processed);

        for (var i = 0; i < processed; i++)
        {
            uint buffer;
            al.SourceUnqueueBuffers(source, 1, &buffer);
            freeBuffers.Enqueue(buffer);
        }
    }

    private void QueueSamples(uint buffer, ReadOnlySpan<float> samples)
    {
        if (format == BufferFormat.Stereo16)
        {
            if (pcmScratch.Length < samples.Length)
            {
                pcmScratch = new short[samples.Length];
            }

            for (var i = 0; i < samples.Length; i++)
            {
                pcmScratch[i] = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            }

            fixed (short* data = pcmScratch)
            {
                al.BufferData(buffer, format, data, samples.Length * sizeof(short), SampleRate);
            }
        }
        else
        {
            fixed (float* data = samples)
            {
                al.BufferData(buffer, format, data, samples.Length * sizeof(float), SampleRate);
            }
        }

        al.SourceQueueBuffers(source, 1, &buffer);

        // Also restarts after an underrun, which leaves the source stopped with an empty queue.
        al.GetSourceProperty(source, GetSourceInteger.SourceState, out var state);

        if ((SourceState)state != SourceState.Playing)
        {
            al.SourcePlay(source);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        using (alLock.EnterScope())
        {
            if (disposed)
            {
                return;
            }

            // Unblocks the mixing thread if it is waiting for a free buffer, which the player
            // relies on being able to join it after disposing us.
            disposed = true;

            if (source != 0)
            {
                al.SourceStop(source);
                al.DeleteSource(source);
            }

            foreach (var buffer in buffers)
            {
                al.DeleteBuffer(buffer);
            }

            DestroyContext();
        }

        alc.Dispose();
        al.Dispose();
    }

    private void DestroyContext()
    {
        if (context != null)
        {
            alc.MakeContextCurrent((Context*)null);
            alc.DestroyContext(context);
        }

        if (device != null)
        {
            alc.CloseDevice(device);
        }
    }
}
