// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;

namespace Wol.Games.Vagrant.Runtime;

/// <summary>
/// The game's audio output: master volume (a saved preference) and the retail Sound option's mono mix.
/// As on the original, Sound and Vibration return to Stereo/On at power-on, so only the volume persists.
/// </summary>
public partial class AudioSettings : Node
{

    private float _volume = 1f;
    private bool _mono;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            Apply();
        }
    }

    public bool Mono
    {
        get => _mono;
        set
        {
            _mono = value;
            Apply();
        }
    }

    public override void _Ready()
    {
        _volume = Math.Clamp(Wol.Engine.Display.DisplaySettings.Preference("audio", "volume", 1.0).AsSingle(), 0f, 1f);
        Apply();
    }

    private void Apply()
    {
        int bus = AudioServer.GetBusIndex("Master");
        AudioServer.SetBusVolumeDb(bus, _volume <= 0f ? -80f : Mathf.LinearToDb(_volume));
        AudioServer.SetBusMute(bus, _volume <= 0f);
        int index = -1;
        for (int i = 0; i < AudioServer.GetBusEffectCount(bus); i++)
            if (AudioServer.GetBusEffect(bus, i) is AudioEffectStereoEnhance) index = i;
        if (index < 0)
        {
            // Collapsing the side channel of the whole mix gives the retail mono output.
            AudioServer.AddBusEffect(bus, new AudioEffectStereoEnhance { PanPullout = 0f });
            index = AudioServer.GetBusEffectCount(bus) - 1;
        }
        AudioServer.SetBusEffectEnabled(bus, index, _mono);
    }

    public void SavePreferences()
    {
        Wol.Engine.Display.DisplaySettings.SetPreference("audio", "volume", _volume);
    }
}
