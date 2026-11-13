// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;

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
    public void Stop()
    {
        foreach (var channel in _channels) { channel.On = false; channel.Data = null; channel.Motors[0].On = channel.Motors[1].On = false; channel.Motors[0].Level = channel.Motors[1].Level = 0; }
        Send(0, 0);
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
                    int duration = Byte(data, motor.At), level = Byte(data, motor.At + 1) << 8;
                    motor.Left = duration;
                    motor.Target = motor.Level = level;
                    if (duration == 0) { motor.Level = 0; motor.On = false; }
                    else
                    {
                        motor.Target = Byte(data, motor.At + 3) << 8;
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

    private static int Byte(byte[] data, int at) => at >= 0 && at < data.Length ? data[at] : 0;

    /// <summary>Sets the pad's motors: the small one is on or off, the large one runs at its level.</summary>
    private void Send(int small, int large)
    {
        Levels = (small, large);
        float weak = small > 0 ? 1 : 0, strong = large / 255f;
        if (weak == _small && strong == _large) return;
        (_small, _large) = (weak, strong);
        foreach (int pad in Input.GetConnectedJoypads())
        {
            if (weak == 0 && strong == 0) Input.StopJoyVibration(pad);
            else Input.StartJoyVibration(pad, weak, strong, 0);
        }
    }
}
