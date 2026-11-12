// SPDX-License-Identifier: AGPL-3.0-only
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// A screen drawn as retail draws it over the whole window (the HUD, the menus, the boxes): it fills the window, lets
/// the pointer through, and keeps its pictures' pixels sharp.
/// </summary>
public abstract partial class RetailScreen : Control
{
    protected RetailScreen()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AnchorRight = 1;
        AnchorBottom = 1;
        TextureFilter = TextureFilterEnum.Nearest;
    }
}
