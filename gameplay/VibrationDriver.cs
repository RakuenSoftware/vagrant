// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace Wol.Games.Vagrant.Runtime;

/// <summary>
/// The executable's pad vibration (SLUS_010.40 0x800433B4 starts it, 0x800436B4 steps it, once a 30 Hz frame from
/// BATTLE.PRG's loop at 0x8007A694): two channels, each playing an envelope on the pad's two motors. An envelope's
/// first byte is the offset of the large motor's track, the small motor's starting at byte 1; a track is (duration,
/// level) pairs, ending with a duration of 0. Each pair holds its level, the large motor stepping straight toward the
/// next pair's level over its duration; levels are summed over the channels, held to 0 to 255 and sent to the pad.
/// </summary>
public sealed class VibrationDriver
{
    /// <summary>The title's vibration setting, reset to On when the game starts (SLUS [0x8006002b]).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Decodes the two motor tracks' extent, keeping their bytes for the frame interpreter.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) throw new System.IO.InvalidDataException("The vibration envelope is truncated.");
        int end = 1;
        foreach (int start in new[] { 1, (int)data[0] })
        {
            int at = start;
            while (at + 1 < data.Length && data[at] != 0) at += 2;
            if (at + 1 >= data.Length) throw new System.IO.InvalidDataException("The vibration track is truncated.");
            end = Math.Max(end, at + 2);
        }
        return data[..end].ToArray();
    }

    private sealed class Motor
    {
        public bool On;
        public int Level, Step, Target, At, Left;
    }

    private sealed class Channel
    {
        public bool On;
        public byte[]? Data;
        public int Id;
        public readonly Motor[] Motors = { new(), new() };
    }

    private readonly Action<float, float> _writeMotors;

    /// <summary>The host supplies device output; envelope decoding and gameplay timing use only .NET types.</summary>
    public VibrationDriver(Action<float, float> writeMotors) => _writeMotors = writeMotors ?? throw new ArgumentNullException(nameof(writeMotors));

    private readonly Channel[] _channels = { new(), new() };
    private float _small, _large;

    /// <summary>The levels last sent to the pad (0 to 255), for tests.</summary>
    public (int Small, int Large) Levels { get; private set; }

    /// <summary>Starts an envelope on the first free channel (0x800433B4 with channel 255); none free plays nothing.</summary>
    public void Start(byte[] data, int id)
    {
        if (data.Length < 2) return;
        var channel = Array.Find(_channels, c => !c.On);
        if (channel == null) return;
        channel.On = true;
        channel.Data = data;
        channel.Id = id;
        channel.Motors[0].Left = channel.Motors[1].Left = 0;
        channel.Motors[0].Level = channel.Motors[1].Level = 0;
        channel.Motors[0].At = 1;
        channel.Motors[1].At = data[0];
    }

    /// <summary>Stops every channel (0x80043668).</summary>
    public void Stop(int id = 0)
    {
        foreach (var channel in _channels)
        {
            if (id != 0 && channel.Id != id) continue;
            channel.On = false;
            channel.Data = null;
            channel.Motors[0].On = channel.Motors[1].On = false;
            channel.Motors[0].Level = channel.Motors[1].Level = 0;
        }
        if (id == 0) Send(0, 0);
    }

    /// <summary>One frame (0x800436B4).</summary>
    public void Step()
    {
        int small = 0, large = 0;
        foreach (var channel in _channels)
        {
            if (!channel.On) continue;
            if (channel.Data is not { } data) { channel.On = false; continue; }
            for (int m = 0; m < 2; m++)
            {
                var motor = channel.Motors[m];
                if (motor.Left == 0)
                {
                    int duration = (motor.At >= 0 && motor.At < data.Length ? data[motor.At] : 0), level = (motor.At + 1 >= 0 && motor.At + 1 < data.Length ? data[motor.At + 1] : 0) << 8;
                    motor.Left = duration;
                    motor.Target = motor.Level = level;
                    if (duration == 0) { motor.Level = 0; motor.On = false; }
                    else
                    {
                        motor.Target = (motor.At + 3 >= 0 && motor.At + 3 < data.Length ? data[motor.At + 3] : 0) << 8;
                        // Only the large motor slides between levels.
                        motor.Step = m == 1 ? (motor.Target - motor.Level) / duration : 0;
                        motor.On = true;
                        motor.At += 2;
                    }
                }
                if (motor.Left != 0) { motor.Level += motor.Step; motor.Left--; }
                else motor.Level = motor.Target;
                if (m == 0) small = Math.Clamp(small + motor.Level, 0, 0xFF00);
                else large = Math.Clamp(large + motor.Level, 0, 0xFF00);
            }
            if (!channel.Motors[0].On && !channel.Motors[1].On) { channel.On = false; channel.Data = null; }
        }
        Send(small >> 8, large >> 8);
    }

    /// <summary>Sets the pad's motors: the small one is on or off, the large one runs at its level.</summary>
    private void Send(int small, int large)
    {
        Levels = (small, large);
        float weak = Enabled && small > 0 ? 1 : 0, strong = Enabled ? large / 255f : 0;
        if (weak == _small && strong == _large) return;
        (_small, _large) = (weak, strong);
        _writeMotors(weak, strong);
    }
}
