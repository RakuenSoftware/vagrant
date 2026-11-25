// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Godot;

namespace Wol.Games.Vagrant.Runtime;

/// <summary>
/// Vagrant Story's own sound driver, played live as the PlayStation plays it. The driver's code comes from the
/// player's verified executable (SLUS_010.40) and runs in the shared engine's PlayStation audio sandbox: only its
/// sound code executes, the hardware calls it makes for timers, events and SPU transfers are answered by the
/// sandbox, and its SPU register writes drive the sandbox's DSP. It is ticked at its 240 Hz root-counter rate and
/// its 44.1 kHz stereo goes straight to the audio output. Music sequences, instrument banks and sound banks are
/// the disc's own files; music and sound effects share the driver's voices, as on the console.
/// </summary>
public sealed partial class SoundDriver : Node
{
    public const int SampleRate = 44100, TickRate = 240;
    private const long Budget = 4_000_000;

    // Sound driver and runtime library entry points in the verified US executable.
    private const long InitSound = 0x80011DAC, BindSfxBlob = 0x80011DEC, StartMusic = 0x80011FB4, StopMusic = 0x80011FDC, MusicVolume = 0x800128A0;
    private const long LoadResidentBank = 0x80012BB8, LoadResidentBankAt = 0x800131DC, LoadSoundFile = 0x80013188;
    private const long PlaySfxById = 0x8001217C, PlaySfxBlob = 0x800121F0, StopSfx = 0x80012288, FadeSfx = 0x8001264C, PanSfx = 0x80012738, StopAllSfx = 0x80012B98;
    private const long WriteSpu = 0x800134D0, Tick = 0x80019A58;
    // Driver commands 0x92 (the reverb, with its value) and 0x40 (the music snapshot), as event opcodes a8 and 98 send them.
    private const long SetReverb = 0x80012468, SnapshotMusic = 0x80012004;
    // Driver commands 0xE5 (a sound sequence's volume), 0xE0 (start one) and 0xE2 (stop it), as an effect's sound slot
    // plays and releases its sequence (SLUS 0x80046578, 0x80046634); the sequence sits after the music's 16 KB.
    private const long SequenceVolume = 0x80013378, StartSequence = 0x800132C4, StopSequenceCommand = 0x80013328, EffectSequence = 0x80168000;
    private const long SpuSetTransferStartAddr = 0x8001EBC4;
    private static readonly long[] HostRoutines =
    {
        0x8001D9DC, // SpuStart
        0x8001E604, // SpuQuit
        0x8001E684, // SpuInitMalloc
        0x8001E914, // SpuSetIRQ
        0x8001EA94, // SpuSetIRQCallback
        0x8001EC24, // SpuSetTransferMode
        0x8001EC54, // SpuSetTransferCallback
        0x8001F364, // SpuClearReverbWorkArea: the DSP's sound RAM starts clear
        0x800268D4, // OpenEvent
        0x80026914, // EnableEvent
        0x80026924, // DisableEvent
        0x80026A24, // SetRCnt
        0x80026AF8, // StartRCnt
    };
    // Where the resident effect blob, staged instrument banks, the music sequence and the sound banks sit in RAM.
    private const long SfxData = 0x80050478, Staging = 0x80100000, Sequence = 0x80160000, Banks = 0x801A0000, BanksEnd = 0x801FF000;
    private const int SpuControl = 0x1AA / 2;
    private const long MusicChannels = 0x80033690, ActiveMusicConfig = 0x80037820;

    /// <summary>
    /// The voice sets of the field's sound groups (BATTLE.PRG 0x800E9B34): 0 a character's own sounds, 1 the
    /// resident one-shots (the executable's player gives each the next of voices 1 to 6), 2 the resident ambience
    /// and room sounds, 3 and 4 the selected event bank's sounds (and the effect banks on 3).
    /// </summary>
    public static readonly long[] Groups = { 0x1, 0x7E, 0x200, 0xFF000, 0xF00000 };

    // The sandbox's methods, named once: a name made from a string per call goes through the engine's shared name table.
    private static readonly StringName AddReturnMethod = "add_return", AddSpuTransferMethod = "add_spu_transfer", CallMethod = "call", GetLastErrorMethod = "get_last_error", LoadExecutableMethod = "load_executable", ReadWordMethod = "read_word", RenderMethod = "render", SpuWriteMethod = "spu_write", WriteMemoryMethod = "write_memory";

    // The native interpreter and SPU are one mutable machine. Commands and rendering
    // share this gate; the audio producer never touches scene nodes.
    private readonly object _audioGate = new();
    private Thread? _worker;
    private volatile bool _stopping;
    private bool _device, _reportedFailure;
    private volatile string _failure = "";
    private readonly Vector2[] _shortFrames = new Vector2[SampleRate / TickRate];
    private readonly Vector2[] _longFrames = new Vector2[SampleRate / TickRate + 1];
    private GodotObject? _sandbox;
    private AudioStreamPlayer _player = null!;
    private AudioStreamGeneratorPlayback? _playback;
    private readonly Dictionary<string, long> _banks = new();
    private long _nextBank = Banks;
    private int _tick, _oneShot;
    private double _owed;
    private bool _music;

    /// <summary>The failure that stopped the driver, if any; the game then goes on silent.</summary>
    public string Failure => _failure;

    /// <summary>Starts the sound driver as the game does at boot: its resident instrument banks and sound effects.</summary>
    public void Boot(byte[] executable, byte[] wave0000, byte[] wave0005, byte[] wave0200, byte[] effects)
    {
        if (!ClassDB.ClassExists("NativePs1AudioSandbox")) throw new InvalidDataException("The shared engine's audio sandbox is unavailable.");
        _sandbox = ClassDB.Instantiate("NativePs1AudioSandbox").AsGodotObject();
        if (!_sandbox.Call(LoadExecutableMethod, executable).AsBool()) throw new InvalidDataException("The game executable could not be loaded for its sound driver.");
        foreach (long routine in HostRoutines) _sandbox.Call(AddReturnMethod, routine, 1);
        _sandbox.Call(AddSpuTransferMethod, SpuSetTransferStartAddr, WriteSpu, 0);
        Call(InitSound);
        _sandbox.Call(SpuWriteMethod, SpuControl, 0xC000);
        Stage(wave0000);
        Call(LoadResidentBank, Staging, 1);
        Stage(wave0005);
        Call(LoadResidentBank, Staging, 1);
        Stage(wave0200);
        Call(LoadResidentBankAt, Staging, 0, 1);
        _sandbox.Call(WriteMemoryMethod, SfxData, effects);
        Call(BindSfxBlob, SfxData);
        if (_device)
        {
            _worker = new Thread(ProduceAudio) { IsBackground = true, Name = "Vagrant audio" };
            _worker.Start();
        }
    }

    public override void _Ready()
    {
        var stream = new AudioStreamGenerator { MixRate = SampleRate, BufferLength = 0.15f };
        _player = new AudioStreamPlayer { Name = "Output", Stream = stream, Bus = "Master" };
        AddChild(_player);
        _player.Play();
        _playback = _player.GetStreamPlayback() as AudioStreamGeneratorPlayback;
        _device = _playback != null && AudioServer.GetDriverName() != "Dummy";
    }

    public override void _ExitTree()
    {
        _stopping = true;
        // Never join while holding the machine gate: the producer may be finishing a tick.
        _worker?.Join();
        _worker = null;
        lock (_audioGate) { _sandbox?.Dispose(); _sandbox = null; }
    }

    /// <summary>Dummy-audio checks retain the simulation clock, including accelerated fixed-fps runs.</summary>
    public override void _Process(double delta)
    {
        if (Failure.Length != 0)
        {
            if (!_reportedFailure) { GD.PrintErr("Sound driver stopped: " + Failure); _reportedFailure = true; }
            return;
        }
        if (_device || !DriverReady) return;
        _owed = Math.Min(_owed + delta * SampleRate, SampleRate / 4.0);
        try
        {
            while (true)
            {
                int count = (int)((long)(_tick + 1) * SampleRate / TickRate - (long)_tick * SampleRate / TickRate);
                if (_owed < count) break;
                RenderTick(count, false);
                _owed -= count;
            }
        }
        catch (InvalidDataException error) { _failure = error.Message; }
    }

    /// <summary>
    /// Keeps the output fed independently of hit effects, room loading and render stalls. Only this thread
    /// writes the playback ring buffer; commands serialize with one interpreter/SPU tick at a time.
    /// </summary>
    private void ProduceAudio()
    {
        try
        {
            while (!_stopping)
            {
                int count = (int)((long)(_tick + 1) * SampleRate / TickRate - (long)_tick * SampleRate / TickRate);
                if (!_playback!.CanPushBuffer(count)) { Thread.Sleep(1); continue; }
                lock (_audioGate)
                {
                    if (_stopping || !DriverReady) return;
                    RenderTick(count, true);
                }
            }
        }
        catch (Exception error)
        {
            _failure = "Audio production stopped: " + error.Message;
        }
    }

    private void RenderTick(int count, bool output)
    {
        Call(Tick);
        byte[] samples = _sandbox!.Call(RenderMethod, count).AsByteArray();
        _tick = (_tick + 1) % TickRate;
        if (!output) return;
        var frames = count == _shortFrames.Length ? _shortFrames : _longFrames;
        for (int i = 0; i < count; i++)
            frames[i] = new Vector2(BitConverter.ToInt16(samples, i * 4) / 32768f, BitConverter.ToInt16(samples, i * 4 + 2) / 32768f);
        if (!_playback!.PushBuffer(frames)) throw new InvalidDataException("The audio output rejected a prepared buffer.");
    }

    private void Stage(byte[] data) => _sandbox!.Call(WriteMemoryMethod, Staging, data);

    private int Call(long address, params long[] arguments)
    {
        var values = new int[arguments.Length];
        for (int i = 0; i < arguments.Length; i++) values[i] = unchecked((int)arguments[i]);
        int result = _sandbox!.Call(CallMethod, address, values, Budget).AsInt32();
        string error = _sandbox.Call(GetLastErrorMethod).AsString();
        if (error.Length != 0) throw new InvalidDataException("The sound driver failed: " + error);
        return result;
    }

    private bool DriverReady => _sandbox != null && Failure.Length == 0;

    /// <summary>Starts a music sequence with its instrument bank (loaded into sound RAM first), at a volume of 0 to 127.</summary>
    public void PlayMusic(byte[] sequence, byte[] bank, int volume)
    {
        lock (_audioGate)
        {
            if (!DriverReady) return;
            try
            {
                if (_music) Call(StopMusic, Sequence);
                Stage(bank);
                Call(LoadSoundFile, Staging, 0);
                _sandbox!.Call(WriteMemoryMethod, Sequence, sequence);
                Call(MusicVolume, 0, 0, Math.Clamp(volume, 0, 127));
                Call(StartMusic, Sequence);
                _music = true;
            }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>Stops the music (driver command 0x11, as the field's release does).</summary>
    public void Stop()
    {
        lock (_audioGate)
        {
            if (!DriverReady || !_music) return;
            try { Call(StopMusic, Sequence); }
            catch (InvalidDataException error) { _failure = error.Message; }
            _music = false;
        }
    }

    /// <summary>Sets the music's volume (0 to 127) at once (driver command 0xc1).</summary>
    public void Volume(int volume)
    {
        lock (_audioGate)
        {
            if (!DriverReady || !_music) return;
            try { Call(MusicVolume, 0, 0, Math.Clamp(volume, 0, 127)); }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>Whether a music sequence is playing: started, and with channels the driver still runs.</summary>
    public bool MusicPlaying
    {
        get
        {
            lock (_audioGate)
            {
                if (!DriverReady || !_music) return false;
                uint config = (uint)_sandbox!.Call(ReadWordMethod, ActiveMusicConfig).AsInt64();
                return config != 0 && _sandbox.Call(ReadWordMethod, (long)config + 4).AsInt64() != 0;
            }
        }
    }

    /// <summary>
    /// The playing music's bar (the executable's 0x80011F90: the halfword at +124 of the active music configuration),
    /// which event scripts wait on (9a); -1 without music.
    /// </summary>
    public int MusicBar
    {
        get
        {
            lock (_audioGate)
            {
                if (!DriverReady || !_music) return -1;
                uint config = (uint)_sandbox!.Call(ReadWordMethod, ActiveMusicConfig).AsInt64();
                if (config == 0) return -1;
                long word = _sandbox.Call(ReadWordMethod, (long)config + 124).AsInt64();
                return (short)(word & 0xFFFF);
            }
        }
    }

    /// <summary>
    /// Plays sound <paramref name="id"/> of a sound bank on a group's voices, at a pan (0x80 is centre) and volume
    /// (0 to 127). The bank stays in RAM under its name for later sounds.
    /// </summary>
    public void PlaySound(string name, byte[] bank, int id, int group, int pan, int volume)
    {
        lock (_audioGate)
        {
            if (!DriverReady || !FieldContent.HasSound(bank, id)) return;
            try
            {
                if (!_banks.TryGetValue(name, out long at))
                {
                    // Banks are laid side by side; when RAM runs out they start again from the first.
                    if (_nextBank + bank.Length > BanksEnd) { _banks.Clear(); _nextBank = Banks; }
                    at = _nextBank;
                    _nextBank = (_nextBank + bank.Length + 3) & ~3L;
                    _sandbox!.Call(WriteMemoryMethod, at, bank);
                    _banks[name] = at;
                }
                Call(PlaySfxBlob, at + BitConverter.ToInt32(bank, 4 + id * 4), Voices(group), pan, Math.Clamp(volume, 0, 127));
            }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>Plays a resident sound (from the boot-time effect blob) on a group's voices.</summary>
    public void PlayResident(int id, int group, int pan, int volume)
    {
        lock (_audioGate)
        {
            if (!DriverReady) return;
            try { Call(PlaySfxById, id, Voices(group), pan, Math.Clamp(volume, 0, 127)); }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>Stops the sounds on a group's voices (StopSfx with no sound named, as the game's 0x80045D64 does for id 0).</summary>
    public void StopSounds(int group)
    {
        lock (_audioGate)
        {
            if (!DriverReady) return;
            try { Call(StopSfx, 0, Groups[Math.Clamp(group, 0, Groups.Length - 1)]); }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>Releasing the selected event bank: StopSfx of -2, then of group 3's voices (the game's 0x800460C0).</summary>
    public void ReleaseEventBank()
    {
        lock (_audioGate)
        {
            if (!DriverReady) return;
            try
            {
                Call(StopSfx, -2, 0);
                Call(StopSfx, 0, Groups[3]);
            }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>Sets the driver's reverb (driver command 0x92, event opcode a8).</summary>
    public void Reverb(int value)
    {
        lock (_audioGate)
        {
            if (!DriverReady) return;
            try { Call(SetReverb, value); }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>Starts an effect's sound sequence (an AKAO block) at full volume.</summary>
    public void PlaySequence(byte[] sequence)
    {
        lock (_audioGate)
        {
            if (!DriverReady || sequence.Length == 0 || EffectSequence + sequence.Length > Banks) return;
            try
            {
                _sandbox!.Call(WriteMemoryMethod, EffectSequence, sequence);
                Call(SequenceVolume, 0, 127);
                Call(StartSequence, EffectSequence, 128, 1);
            }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>Stops the effect's sound sequence.</summary>
    public void StopSequence()
    {
        lock (_audioGate)
        {
            if (!DriverReady) return;
            try { Call(StopSequenceCommand); }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>Takes the driver's music snapshot (driver command 0x40, event opcode 98).</summary>
    public void Snapshot()
    {
        lock (_audioGate)
        {
            if (!DriverReady) return;
            try { Call(SnapshotMusic); }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>Stops every sound (driver command 0xf1, as the game's 0x80045DC0 does).</summary>
    public void StopAllSounds()
    {
        lock (_audioGate)
        {
            if (!DriverReady) return;
            try { Call(StopAllSfx); }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>
    /// Stops one sound, wherever it plays: a bank sound by its program's address, a resident one by its id, as the
    /// game's 0x80045D64 names it to StopSfx. A bank no longer in RAM has nothing playing.
    /// </summary>
    public void StopSound(string bankName, byte[]? bank, int id)
    {
        lock (_audioGate)
        {
            if (!DriverReady || Target(bankName, bank, id) is not long target) return;
            try { Call(StopSfx, target, 0); }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    /// <summary>
    /// Slides one sound's volume (0 to 127) or its pan (0 to 255, 128 the centre) over the frames given, as the game's
    /// 0x80045BFC and 0x80045CEC ask the driver (0x8001264C and 0x80012738).
    /// </summary>
    public void SlideSound(string bankName, byte[]? bank, int id, bool pan, int value, int frames)
    {
        lock (_audioGate)
        {
            if (Target(bankName, bank, id) is long target) Slide(target, 0, pan, value, frames);
        }
    }

    /// <summary>Slides the volume or pan of every voice of a group, as an id of 0 asks the driver.</summary>
    public void SlideGroup(int group, bool pan, int value, int frames) => Slide(0, Groups[Math.Clamp(group, 0, Groups.Length - 1)], pan, value, frames);

    /// <summary>The driver's volume or pan slide (0x8001264C, 0x80012738) of a sound (by program or id) or of a group's voices.</summary>
    private void Slide(long target, long voices, bool pan, int value, int frames)
    {
        lock (_audioGate)
        {
            if (!DriverReady) return;
            try { Call(pan ? PanSfx : FadeSfx, target, voices, frames & 0xFF, Math.Clamp(value, 0, pan ? 255 : 127)); }
            catch (InvalidDataException error) { _failure = error.Message; }
        }
    }

    private long? Target(string bankName, byte[]? bank, int id)
    {
        if (bank == null) return id;
        if (!FieldContent.HasSound(bank, id) || !_banks.TryGetValue(bankName, out long at)) return null;
        return at + BitConverter.ToInt32(bank, 4 + id * 4);
    }

    // Group 1 takes the next of voices 1 to 6 in turn (the executable's player, 0x80045754).
    private long Voices(int group)
    {
        if (group != 1) return Groups[Math.Clamp(group, 0, Groups.Length - 1)];
        _oneShot = _oneShot % 6 + 1;
        return 1L << _oneShot;
    }
}
