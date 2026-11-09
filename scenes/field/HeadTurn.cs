// SPDX-License-Identifier: AGPL-3.0-only
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// Turns a rig's head bone about its own y axis after the animation has posed it: retail's head tracking (opcodes 34,
/// 38 and 39), which the field sets each frame.
/// </summary>
public partial class HeadTurn : SkeletonModifier3D
{
    public int Bone = -1;
    /// <summary>The turn, in retail angle units (4096 a turn).</summary>
    public float Yaw;

    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (skeleton == null || Bone < 0 || Bone >= skeleton.GetBoneCount() || Yaw == 0) return;
        skeleton.SetBonePoseRotation(Bone, skeleton.GetBonePoseRotation(Bone) * new Quaternion(Vector3.Up, Yaw * Mathf.Tau / 4096f));
    }
}
