// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using Wol.Engine.World;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>A room primitive uses its farthest corner, and retains its ordering corners when near faces change.</summary>
public partial class RoomOrderingContracts : Node
{
    public override void _Ready()
    {
        FieldScene? field = null;
        FieldView? adapter = null;
        try
        {
            // One quad spans the near cut; the other lies wholly in front of it.
            Vector3[][] quads = [
                [new(-.2f, -.2f, -.25f), new(.2f, -.2f, -8), new(-.2f, .2f, -.25f), new(.2f, .2f, -8)],
                [new(-.2f, -.2f, -.25f), new(.2f, -.2f, -.25f), new(-.2f, .2f, -.25f), new(.2f, .2f, -.25f)]
            ];
            int[] order = [0, 1, 2, 1, 3, 2];
            var vertices = new List<float>();
            var corners = new List<float>();
            foreach (var quad in quads)
                foreach (int index in order)
                {
                    vertices.AddRange([quad[index].X, quad[index].Y, quad[index].Z]);
                    foreach (var point in quad) corners.AddRange([point.X, point.Y, point.Z]);
                }
            string fixture = JsonSerializer.Serialize(new {
                schema = 1, walkmeshes = Array.Empty<object>(),
                models = new[] { new { blocks = new[] { new { groups = new[] { new {
                    texture = "", unshaded = true, vertices, uv = new float[24],
                    colors = Enumerable.Repeat(1f, 48).ToArray(), faces = new[] { 2, 2 }, order_corners = corners
                } } } } } },
                objects = new[] { new { id = 0, model = 0, position = new[] { 0, 0, 0 } } },
                room_groups = new[] { new { mask = 255, first_person = false } }
            });
            var document = Godot.Json.ParseString(fixture).AsGodotDictionary();
            field = new FieldScene(); AddChild(field); field.SetProcess(false); field.SetPhysicsProcess(false);
            if (field.LoadField(document, new()) != Error.Ok) throw new InvalidOperationException("Cannot load the synthetic room.");
            field.Camera.GlobalTransform = Transform3D.Identity;
            adapter = new FieldView();
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(FieldView).GetProperty("Field")!.SetValue(adapter, field);
            typeof(FieldView).GetProperty("Document")!.SetValue(adapter, document);
            var read = typeof(FieldView).GetMethod("ReadRoomFaces", hidden)!;
            var camera = typeof(FieldView).GetMethod("ApplyCamera", hidden)!;
            var drop = typeof(FieldView).GetMethod("DropNearFaces", hidden)!;
            read.Invoke(adapter, [document]); camera.Invoke(adapter, null); drop.Invoke(adapter, null);
            if (field.ModelOrderOf("0") != ModelOrder.Farthest)
                throw new InvalidOperationException("Room depth rule was lost.");
            var mesh = (ArrayMesh)((MeshInstance3D)field.Actors["0"].GetChild(0)).Mesh;
            foreach (float eyeZ in new[] { 0f, -7f, 0f })
            {
                field.Camera.GlobalPosition = new Vector3(0, 0, eyeZ); drop.Invoke(adapter, null);
                var arrays = mesh.SurfaceGetArrays(0);
                int count = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
                if (count != (eyeZ == 0 ? 6 : 3)) throw new InvalidOperationException("The near cut used the nearest corner or failed to restore a face.");
                for (int channel = 1; channel <= 3; channel++)
                {
                    var data = arrays[(int)Mesh.ArrayType.Custom0 + channel].AsFloat32Array();
                    if (data.Length != count * 4) throw new InvalidOperationException("Ordering corners were dropped during a mesh rebuild.");
                    for (int v = 0; v < count; v++) for (int k = 0; k < 4; k++)
                        if (Math.Abs(data[v * 4 + k] - corners[(channel - 1) * 4 + k]) > .00001)
                            throw new InvalidOperationException("A rebuilt face changed its ordering corners.");
                }
            }
            typeof(FieldView).GetField("_hideFirstPerson", hidden)!.SetValue(adapter, true); camera.Invoke(adapter, null);
            if (field.ModelOrderOf("0") != ModelOrder.Depth) throw new InvalidOperationException("First-person view lost its material near cut.");
            typeof(FieldView).GetField("_hideFirstPerson", hidden)!.SetValue(adapter, false); camera.Invoke(adapter, null);
            if (field.ModelOrderOf("0") != ModelOrder.Farthest) throw new InvalidOperationException("The room ordering rule was not restored after first-person view.");
            adapter.Free(); adapter = null; field.Free(); field = null;
            GD.Print("ROOM_ORDERING_CONTRACTS_OK: farthest corner, near-cut rebuild/restore, ordering attributes, first-person handoff");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            adapter?.Free(); field?.Free(); GD.PrintErr("ROOM_ORDERING_CONTRACTS_FAILED: " + error); GetTree().Quit(1);
        }
    }
}
