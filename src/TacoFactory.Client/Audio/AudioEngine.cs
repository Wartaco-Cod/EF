using NVorbis;
using Silk.NET.OpenAL;

namespace TacoFactory.Client.Audio;

/// <summary>
/// Sound for the game: short effects from assets/sound/*.wav and looping music from assets/music/*.ogg (or .wav),
/// played through OpenAL Soft. If the PC has no audio device the game simply runs silent.
/// </summary>
public sealed unsafe class AudioEngine : IDisposable
{
    private readonly string _root;
    private readonly AL? _al;
    private readonly ALContext? _alc;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly Dictionary<string, uint?> _buffers = new();
    private readonly List<uint> _voices = [];
    private int _nextVoice;
    private readonly uint[] _music = new uint[2];
    private int _musicActive;
    private string? _musicName;
    private float _fade = 1;
    private readonly Dictionary<string, LoopVoice> _loops = new();

    private sealed class LoopVoice { public uint Source; public float Gain, Target; }

    public bool Enabled { get; }
    public float MusicVolume { get; set; } = 0.6f;
    public float EffectsVolume { get; set; } = 0.8f;
    public string? Problem { get; }

    public AudioEngine(string assetRoot)
    {
        _root = assetRoot;
        try
        {
            _alc = ALContext.GetApi(true);
            _al = AL.GetApi(true);
            _device = _alc.OpenDevice("");
            if (_device == null) throw new InvalidOperationException("no audio device");
            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);
            for (int i = 0; i < 24; i++) _voices.Add(_al.GenSource());
            for (int i = 0; i < 2; i++)
            {
                _music[i] = _al.GenSource();
                _al.SetSourceProperty(_music[i], SourceBoolean.Looping, true);
                _al.SetSourceProperty(_music[i], SourceBoolean.SourceRelative, true);
            }
            Enabled = true;
        }
        catch (Exception e)
        {
            Problem = e.Message;
            Enabled = false;
        }
    }

    // ------------------------------------------------------------------ effects

    /// <summary>Plays an effect. <paramref name="pan"/> -1 (left) to 1 (right); <paramref name="volume"/> 0 to 1.</summary>
    public void Play(string name, float volume = 1, float pan = 0, float pitch = 1)
    {
        if (!Enabled || _al is null || volume <= 0.01f || EffectsVolume <= 0) return;
        if (Buffer($"sound/{name}") is not uint buffer) return;
        // Round-robin voices: the oldest sound gives way when many play at once.
        uint v = _voices[_nextVoice];
        _nextVoice = (_nextVoice + 1) % _voices.Count;
        _al.SourceStop(v);
        _al.SetSourceProperty(v, SourceInteger.Buffer, (int)buffer);
        _al.SetSourceProperty(v, SourceBoolean.SourceRelative, true);
        _al.SetSourceProperty(v, SourceVector3.Position, Math.Clamp(pan, -1, 1), 0, -0.6f);
        _al.SetSourceProperty(v, SourceFloat.Gain, Math.Clamp(volume, 0, 1) * EffectsVolume);
        _al.SetSourceProperty(v, SourceFloat.Pitch, pitch);
        _al.SourcePlay(v);
    }

    /// <summary>An effect at a point in the world, heard from the listener's position (fades with distance, pans left/right).</summary>
    public void PlayAt(string name, float dx, float dy, float volume = 1, float range = 1800)
    {
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d > range) return;
        Play(name, volume * (1 - d / range), Math.Clamp(dx / 900f, -1, 1), 0.94f + Random.Shared.NextSingle() * 0.12f);
    }

    // ------------------------------------------------------------------ ambience

    /// <summary>
    /// Asks for a looping ambience layer (assets/sound/amb_*.ogg) at a level 0..1 for this frame. Layers fade toward
    /// the level asked; a layer not asked for in a frame fades out and stops, so callers just ask every frame.
    /// </summary>
    public void Loop(string name, float level)
    {
        if (!Enabled || _al is null) return;
        if (!_loops.TryGetValue(name, out var v))
        {
            if (level <= 0.001f || Buffer($"sound/{name}") is not uint buffer) return;
            uint src = _al.GenSource();
            _al.SetSourceProperty(src, SourceInteger.Buffer, (int)buffer);
            _al.SetSourceProperty(src, SourceBoolean.Looping, true);
            _al.SetSourceProperty(src, SourceBoolean.SourceRelative, true);
            _al.SetSourceProperty(src, SourceFloat.Gain, 0f);
            _al.SourcePlay(src);
            _loops[name] = v = new LoopVoice { Source = src };
        }
        v.Target = Math.Max(v.Target, Math.Clamp(level, 0, 1));
    }

    private void UpdateLoops(float dt)
    {
        List<string>? done = null;
        foreach (var (name, v) in _loops)
        {
            v.Gain += (v.Target - v.Gain) * Math.Min(1, dt * 1.2f);
            _al!.SetSourceProperty(v.Source, SourceFloat.Gain, v.Gain * EffectsVolume * 0.45f);
            if (v.Target <= 0 && v.Gain < 0.002f) (done ??= []).Add(name);
            v.Target = 0;
        }
        if (done is null) return;
        foreach (var name in done)
        {
            _al!.SourceStop(_loops[name].Source); _al.DeleteSource(_loops[name].Source);
            _loops.Remove(name);
        }
    }

    // ------------------------------------------------------------------ music

    /// <summary>Crossfades to a looping track (by name, e.g. "town_act2"). The same name again does nothing.</summary>
    public void Music(string? name)
    {
        if (!Enabled || _al is null || name == _musicName) return;
        _musicName = name;
        _musicActive ^= 1;
        uint src = _music[_musicActive];
        _al.SourceStop(src);
        if (name is null) return;
        if (Buffer($"music/{name}") is not uint buffer) return;
        _al.SetSourceProperty(src, SourceInteger.Buffer, (int)buffer);
        _al.SetSourceProperty(src, SourceFloat.Gain, 0f);
        _al.SourcePlay(src);
        _fade = 0;
    }

    /// <summary>Call every frame: moves the music crossfade along and follows volume changes.</summary>
    public void Update(double dt)
    {
        if (!Enabled || _al is null) return;
        _fade = Math.Min(1, _fade + (float)dt / 2.5f);
        _al.SetSourceProperty(_music[_musicActive], SourceFloat.Gain, _fade * MusicVolume * 0.7f);
        _al.SetSourceProperty(_music[_musicActive ^ 1], SourceFloat.Gain, (1 - _fade) * MusicVolume * 0.7f);
        if (_fade >= 1) _al.SourceStop(_music[_musicActive ^ 1]);
        UpdateLoops((float)dt);
    }

    // ------------------------------------------------------------------ loading

    private uint? Buffer(string key)
    {
        if (_buffers.TryGetValue(key, out var b)) return b;
        uint? made = null;
        try
        {
            string ogg = Path.Combine(_root, key + ".ogg"), wav = Path.Combine(_root, key + ".wav");
            (short[] pcm, int channels, int rate)? data = File.Exists(ogg) ? ReadOgg(ogg) : File.Exists(wav) ? ReadWav(wav) : null;
            if (data is { } d)
            {
                uint buf = _al!.GenBuffer();
                fixed (short* p = d.pcm)
                    _al.BufferData(buf, d.channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16, p, d.pcm.Length * 2, d.rate);
                made = buf;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { Console.Error.WriteLine($"[audio] {key}: {e.Message}"); }
        _buffers[key] = made;
        return made;
    }

    private static (short[], int, int) ReadOgg(string path)
    {
        using var reader = new VorbisReader(path);
        var all = new List<float>((int)Math.Min(int.MaxValue, reader.TotalSamples * reader.Channels));
        var chunk = new float[reader.SampleRate * reader.Channels];
        int n;
        while ((n = reader.ReadSamples(chunk, 0, chunk.Length)) > 0) for (int i = 0; i < n; i++) all.Add(chunk[i]);
        var pcm = new short[all.Count];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp(all[i] * 32767f, -32768, 32767);
        return (pcm, reader.Channels, reader.SampleRate);
    }

    /// <summary>Plain 16-bit PCM WAV files (what tools/audio writes).</summary>
    private static (short[], int, int) ReadWav(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a WAV file");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAV file");
        int channels = 1, rate = 22050, bits = 16;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            string id = new(r.ReadChars(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                r.BaseStream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                if (bits != 16) throw new InvalidDataException("only 16-bit WAV is supported");
                var pcm = new short[size / 2];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = r.ReadInt16();
                return (pcm, channels, rate);
            }
            else r.BaseStream.Seek(size, SeekOrigin.Current);
        }
        throw new InvalidDataException("no audio data");
    }

    public void Dispose()
    {
        if (!Enabled || _al is null || _alc is null) return;
        foreach (var v in _voices) { _al.SourceStop(v); _al.DeleteSource(v); }
        foreach (var m in _music) { _al.SourceStop(m); _al.DeleteSource(m); }
        foreach (var l in _loops.Values) { _al.SourceStop(l.Source); _al.DeleteSource(l.Source); }
        foreach (var b in _buffers.Values) if (b is uint id) _al.DeleteBuffer(id);
        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
    }
}
