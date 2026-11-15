// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;
namespace Wol.Games.Vagrant.Tests;

/// <summary>Synthetic attached rigs must keep their own skeleton when projected near the eye; no game data.</summary>
public partial class ActorProjectionContracts : Node
{
    public override void _Ready()
    {
        Node3D? root = null;
        Camera3D? camera = null;
        try
        {
            foreach (bool named in new[] { false, true })
            {
                root = new Node3D(); AddChild(root);
                var body = new Skeleton3D { Name = "Body" }; root.AddChild(body);
                body.AddBone("body_root"); body.SetBoneRest(0, Transform3D.Identity);
                body.SetBonePosePosition(0, new Vector3(0, 0, -1));
                var bodyMesh = new MeshInstance3D { Name = "BodyMesh", Skeleton = ".." };
                body.AddChild(bodyMesh);
                var attachment = new Node3D { Name = "Attachment", Position = new Vector3(2, 0, 0) }; body.AddChild(attachment);
                var weapon = new Skeleton3D { Name = "Weapon" }; attachment.AddChild(weapon);
                weapon.AddBone("weapon_root"); weapon.SetBoneRest(0, Transform3D.Identity);
                weapon.SetBonePosePosition(0, new Vector3(0, 0, -1));
                var weaponMesh = new MeshInstance3D { Name = "WeaponMesh", Skeleton = ".." }; weapon.AddChild(weaponMesh);
                foreach (var mesh in new[] { bodyMesh, weaponMesh })
                {
                    var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
                    arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(-0.05f,-0.05f,0),new Vector3(0.05f,-0.05f,0),new Vector3(0,0.05f,0) };
                    arrays[(int)Mesh.ArrayType.Bones] = new int[12];
                    arrays[(int)Mesh.ArrayType.Weights] = new float[] {1,0,0,0,1,0,0,0,1,0,0,0};
                    arrays[(int)Mesh.ArrayType.Index] = new[] {0,1,2};
                    var surface = new ArrayMesh(); surface.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); mesh.Mesh = surface;
                    var skin = new Skin();
                    if (named && mesh == weaponMesh) skin.AddNamedBind("weapon_root",Transform3D.Identity);
                    else skin.AddBind(0,Transform3D.Identity);
                    mesh.Skin=skin;
                }
                camera = new Camera3D { Near=0.01f }; AddChild(camera);
                var actor = new EventActor("synthetic",0,root);
                actor.ProjectAsRetail(camera,512,4096);
                var projected = weapon.GetNode<MeshInstance3D>("RetailView");
                var vertices = projected.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                foreach (var vertex in vertices)
                    if(vertex.X<0.8f) throw new InvalidOperationException("The attachment was projected through the body's skeleton instead of its own.");
                var bodyVertices = body.GetNode<MeshInstance3D>("RetailView").Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                foreach(var vertex in bodyVertices)
                    if(Math.Abs(vertex.X)>0.1f) throw new InvalidOperationException("Attachment placement changed the body's projection.");
                attachment.Position=new Vector3(-2,0,0);
                actor.ProjectAsRetail(camera,512,4096);
                vertices=projected.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                foreach(var vertex in vertices)
                    if(vertex.X>-0.8f) throw new InvalidOperationException("Projection retained the attachment's previous transform.");
                attachment.Visible=false; actor.ProjectAsRetail(camera,512,4096);
                if(projected.IsVisibleInTree()) throw new InvalidOperationException("A hidden attachment left projected geometry visible.");
                attachment.Visible=true; actor.ProjectAsRetail(camera,512,4096);
                if(!projected.IsVisibleInTree()) throw new InvalidOperationException("A visible attachment did not return.");
                root.Free(); root=null; camera.Free();camera=null;
            }
            root = new Node3D(); AddChild(root);
            var mountingRig = new Node3D(); root.AddChild(mountingRig);
            var mountingBones = new Skeleton3D(); mountingRig.AddChild(mountingBones);
            mountingBones.AddBone("b0"); mountingBones.SetBoneRest(0,Transform3D.Identity);
            var prop = new Node3D(); root.AddChild(prop);
            var propBones = new Skeleton3D {Name="PropBones"}; prop.AddChild(propBones);
            propBones.AddBone("b0"); propBones.AddBone("b1"); propBones.SetBoneParent(1,0);
            propBones.SetBoneRest(0,Transform3D.Identity); propBones.SetBoneRest(1,Transform3D.Identity);
            var player = new AnimationPlayer(); prop.AddChild(player);
            var rest = new Animation {Length=1};
            int track=rest.AddTrack(Animation.TrackType.Position3D); rest.TrackSetPath(track,"PropBones:b0");
            rest.PositionTrackInsertKey(track,0,new Vector3(0,3,0));
            track=rest.AddTrack(Animation.TrackType.Rotation3D); rest.TrackSetPath(track,"PropBones:b1");
            rest.RotationTrackInsertKey(track,0,new Quaternion(Vector3.Right,Mathf.Pi/2));
            var library=new AnimationLibrary(); library.AddAnimation("rest",rest); player.AddAnimationLibrary("",library);
            player.Play("rest"); player.Advance(0);
            var standalone = new EventActor("prop",0,prop);
            standalone.Apply();
            if(propBones.GetBonePosePosition(0).Y!=3) throw new InvalidOperationException("Standalone prop lost its resting pose.");
            standalone.Parent=new EventActor("mount",0,mountingRig); standalone.ParentBone=0;
            standalone.Apply();
            if(!propBones.GetBonePose(0).IsEqualApprox(Transform3D.Identity)
                || !propBones.GetBonePose(1).IsEqualApprox(Transform3D.Identity))
                throw new InvalidOperationException("Attached prop added its standalone lift/turn to the mounting frame.");
            standalone.Parent=null; player.Advance(0);
            if(propBones.GetBonePosePosition(0).Y!=3) throw new InvalidOperationException("Detaching a prop did not restore its standalone pose.");
            root.Free(); root=null;
            GD.Print("ACTOR_PROJECTION_CONTRACTS_OK: numeric/named skin binds, body independence, moving and hidden attachments, static prop attachment pose");
            GetTree().Quit();
        }
        catch(Exception error){root?.Free();camera?.Free();GD.PrintErr("ACTOR_PROJECTION_CONTRACTS_FAILED: "+error);GetTree().Quit(1);}
    }
}
