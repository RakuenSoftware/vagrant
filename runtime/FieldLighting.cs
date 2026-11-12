// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;

namespace Wol.Games.Vagrant.Runtime;

/// <summary>
/// How the fields are lit: Original (the rooms' and effects' colours as the disc has them) or Enhanced (bright things
/// bloom, so torches, burning windows and spells glow into the dark round them; added effects such as fire and magic
/// are drawn brighter than white, which an HDR display shows brighter still; and they cast a flickering light on the
/// characters near them, when characters are drawn enhanced and so take light). The choice is a saved preference.
/// </summary>
public partial class FieldLighting : Node
{
    /// <summary>Whether the fields are lit enhanced.</summary>
    public static bool Enhanced { get; private set; }

    /// <summary>How much brighter added effects are drawn, enhanced.</summary>
    public const float EffectBoost = 3f;

    public override void _Ready()
    {
        Enhanced = Wol.Engine.Display.DisplaySettings.Preference("lighting", "enhanced", false);
    }

    /// <summary>Sets the choice and saves it (unless <paramref name="save"/> is false, as the checks have it).</summary>
    public static void Choose(bool enhanced, bool save = true)
    {
        Enhanced = enhanced;
        if (!save) return;
        Wol.Engine.Display.DisplaySettings.SetPreference("lighting", "enhanced", enhanced);
    }

    /// <summary>
    /// Sets a field's environment as chosen: enhanced, what is brighter than about nine tenths of white blooms,
    /// softly and wide, added over the picture; original, nothing does.
    /// </summary>
    public static void Apply(Godot.Environment environment)
    {
        if (environment.GlowEnabled == Enhanced) return;
        environment.GlowEnabled = Enhanced;
        if (!Enhanced) return;
        environment.GlowHdrThreshold = 0.9f;
        environment.GlowHdrScale = 2;
        environment.GlowIntensity = 1.3f;
        environment.GlowStrength = 1.2f;
        environment.GlowBloom = 0.02f;
        environment.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive;
        for (int level = 0; level < 7; level++) environment.SetGlowLevel(level, level is 2 or 3 or 4 or 5 ? 1 : 0);
    }

    /// <summary>
    /// The light an effect's added sprites cast this frame, from their summed colours and middle: its colour, and an
    /// energy that rises with how much of it there is (so a fire's light flickers with its flames), capped.
    /// </summary>
    public static void Cast(OmniLight3D light, Vector3 middle, Color sum, int sprites)
    {
        light.Visible = Enhanced && sprites > 0;
        if (!light.Visible) return;
        float total = sum.R + sum.G + sum.B;
        if (total <= 0) { light.Visible = false; return; }
        light.Position = middle;
        light.LightColor = new Color(sum.R / total * 3, sum.G / total * 3, sum.B / total * 3).Clamp(Colors.Black, Colors.White);
        light.LightEnergy = Math.Min(4f, total / sprites * MathF.Sqrt(sprites) * 0.6f);
    }
}
