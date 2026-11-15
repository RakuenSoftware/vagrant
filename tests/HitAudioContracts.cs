// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Threading;
using Godot;
using Wol.Games.Vagrant.Runtime;
using Wol.Games.Vagrant.Scenes.Field;
namespace Wol.Games.Vagrant.Tests;

public partial class HitAudioContracts : Node
{
    public override async void _Ready()
    {
        SoundDriver? driver = null;
        AudioEffectCapture? capture = null;
        int captureSlot = -1;
        try
        {
            if (AudioServer.GetDriverName() == "Dummy") throw new InvalidOperationException("This check requires a real audio backend routed to a silent test sink.");
            var content = GetNode<RetailContent>("/root/RetailContent");
            if (!content.Installed) throw new InvalidOperationException(content.Failure);
            driver = new SoundDriver { Name = "TestSound" }; AddChild(driver); content.Boot(driver);
            var (sequence, bank) = content.Music("music_016"); driver.PlayMusic(sequence, bank, 127);
            await ToSignal(GetTree().CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
            var playback = (AudioStreamGeneratorPlayback)driver.GetNode<AudioStreamPlayer>("Output").GetStreamPlayback();
            capture = new AudioEffectCapture { BufferLength = 2 };
            captureSlot = AudioServer.GetBusEffectCount(0);
            AudioServer.AddBusEffect(0, capture);
            var shared = content.Data("effect_000");
            var sharedAtlas = content.Texture(shared["atlas"].AsString());
            if (!ReferenceEquals(shared, content.Data("effect_000"))
                || sharedAtlas.GetInstanceId() != content.Texture(shared["atlas"].AsString()).GetInstanceId())
                throw new InvalidOperationException("Repeated hits rebuild their shared effect document or GPU atlas.");
            int before = playback.GetSkips();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 12; i++)
            {
                var data = content.Data("effect_000");
                var atlas = content.Texture(data["atlas"].AsString());
                var effect = new FieldEffect(data, atlas, new FieldEffect.Entity { Point = Vector3.Zero }, new() { new() { Point = Vector3.Zero } }, 3) { Content = content.Field };
                effect.Free();
            }
            GD.Print($"HIT_PROFILE: twelve resident hit constructions took {clock.Elapsed.TotalMilliseconds:F1} ms");
            // A render/asset-load stall must not prevent the audio producer from running.
            Thread.Sleep(600);
            int after = playback.GetSkips();
            var mixed = capture.GetBuffer(capture.GetFramesAvailable());
            bool audible = false;
            foreach (var frame in mixed) audible |= frame.LengthSquared() > 0.000001f;
            GD.Print($"HIT_AUDIO: mixed {mixed.Length} frames during stalled game processing; nonzero={audible}");
            if (mixed.Length < AudioServer.GetMixRate() / 2 || !audible)
                throw new InvalidOperationException("Audio did not continue producing actual samples during the stall.");
            GD.Print($"HIT_AUDIO: underruns before={before} after={after}");
            if (driver.Failure.Length != 0) throw new InvalidOperationException(driver.Failure);
            if (after != before) throw new InvalidOperationException("Main-thread hit/stall work starved the audio buffer.");
            // Exercise command uploads and queries while the producer is running, then teardown/restart.
            for (int i = 0; i < 16; i++)
            {
                driver.PlayResident(13, 1, 128, 127); driver.Volume(100 + i); _ = driver.MusicBar; _ = driver.MusicPlaying;
                await ToSignal(GetTree().CreateTimer(0.01), SceneTreeTimer.SignalName.Timeout);
            }
            if (driver.Failure.Length != 0) throw new InvalidOperationException(driver.Failure);
            driver.Stop(); driver.Free(); driver = null;
            for (int i = 0; i < 3; i++)
            {
                driver = new SoundDriver(); AddChild(driver); content.Boot(driver); driver.PlayMusic(sequence, bank, 127);
                await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
                if (driver.Failure.Length != 0) throw new InvalidOperationException(driver.Failure);
                driver.Free(); driver = null;
            }
            AudioServer.RemoveBusEffect(0, captureSlot); captureSlot = -1;
            capture.Dispose(); capture = null;
            GD.Print("HIT_AUDIO_CONTRACTS_OK"); GetTree().Quit();
        }
        catch (Exception error) { driver?.Free(); if (captureSlot >= 0) AudioServer.RemoveBusEffect(0, captureSlot); capture?.Dispose(); GD.PrintErr("HIT_AUDIO_CONTRACTS_FAILED: " + error); GetTree().Quit(1); }
    }
}
