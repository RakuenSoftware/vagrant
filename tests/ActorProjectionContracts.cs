// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;
using Wol.Engine.World;
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
                // The stand-ins hold screen places, so the camera carries them (its drawn pose is interpolated between ticks).
                MeshInstance3D? Stand(MeshInstance3D piece) => camera.GetNodeOrNull<MeshInstance3D>($"RetailView{piece.GetInstanceId()}") is { } stand && !stand.IsQueuedForDeletion() ? stand : null;
                actor.ProjectAsRetail(camera,512,4096);
                var projected = Stand(weaponMesh) ?? throw new InvalidOperationException("The attachment near the eye has no stand-in on the camera.");
                var vertices = projected.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                foreach (var vertex in vertices)
                    if(vertex.X<0.8f) throw new InvalidOperationException("The attachment was projected through the body's skeleton instead of its own.");
                var bodyVertices = (Stand(bodyMesh) ?? throw new InvalidOperationException("The body near the eye has no stand-in on the camera.")).Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                foreach(var vertex in bodyVertices)
                    if(Math.Abs(vertex.X)>0.1f) throw new InvalidOperationException("Attachment placement changed the body's projection.");
                attachment.Position=new Vector3(-2,0,0);
                actor.ProjectAsRetail(camera,512,4096);
                vertices=projected.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                foreach(var vertex in vertices)
                    if(vertex.X>-0.8f) throw new InvalidOperationException("Projection retained the attachment's previous transform.");
                attachment.Visible=false; actor.ProjectAsRetail(camera,512,4096);
                if(Stand(weaponMesh) is { } hidden && hidden.IsVisibleInTree()) throw new InvalidOperationException("A hidden attachment left projected geometry visible.");
                attachment.Visible=true; actor.ProjectAsRetail(camera,512,4096);
                if(Stand(weaponMesh) is not { } returned || !returned.IsVisibleInTree()) throw new InvalidOperationException("A visible attachment did not return.");
                // Moving the camera after the projection (its presentation sample) keeps each corner where the screen had it.
                var before = camera.UnprojectPosition(returned.GlobalTransform * returned.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array()[0]);
                camera.GlobalPosition += new Vector3(0.3f,0,0);
                var after = camera.UnprojectPosition(returned.GlobalTransform * returned.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array()[0]);
                if(before.DistanceTo(after)>0.01f) throw new InvalidOperationException("A stand-in left in the world slid across the screen as the drawn camera moved.");
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
            player.Advance(0);
            if(!propBones.GetBonePose(0).IsEqualApprox(Transform3D.Identity) || !propBones.GetBonePose(1).IsEqualApprox(Transform3D.Identity))
                throw new InvalidOperationException("The standalone rest animation overwrote the attached prop's pose.");
            standalone.Parent=null; standalone.Apply();
            if(propBones.GetBonePosePosition(0).Y!=3) throw new InvalidOperationException("Detaching a prop did not restore its standalone pose.");
            root.Free(); root=null;
            root = new FieldScene(); AddChild(root);
            var field = (FieldScene)root;
            field.SetProcess(false); field.SetPhysicsProcess(false);
            using var source = Json.ParseString("""{"schema":1,"models":[],"objects":[{"id":0,"model":-1,"position":[0,0,0]},{"id":1,"model":-1,"position":[0,0,0]}],"walkmeshes":[]}""").AsGodotDictionary();
            if(field.LoadField(source,new())!=Error.Ok) throw new InvalidOperationException("Cannot load the synthetic prop motion field.");
            var mount = new Skeleton3D {Transform=new Transform3D(new Basis(Vector3.Back,-Mathf.Pi/2).Scaled(Vector3.One/128),new Vector3(3,1,-4))};
            field.Actors["0"].AddChild(mount);
            mount.AddBone("b0"); mount.AddBone("b1"); mount.SetBoneParent(1,0);
            mount.SetBoneRest(0,Transform3D.Identity);
            mount.SetBoneRest(1,new Transform3D(Basis.Identity,new Vector3(-100,0,0))); mount.ResetBonePoses();
            var swordBones = new Skeleton3D {Transform=EventActor.ModelSpace}; field.Actors["1"].AddChild(swordBones);
            swordBones.AddBone("b0"); swordBones.AddBone("b1"); swordBones.AddBone("b2");
            swordBones.SetBoneParent(1,0); swordBones.SetBoneParent(2,1);
            swordBones.SetBoneRest(0,Transform3D.Identity); swordBones.SetBoneRest(1,Transform3D.Identity);
            swordBones.SetBoneRest(2,new Transform3D(Basis.Identity,new Vector3(50,0,0))); swordBones.ResetBonePoses();
            var mounted = new EventActor("1",0,field.Actors["1"]) {Scene=field,Parent=new EventActor("0",0,field.Actors["0"]) {Scene=field},Visible=true};
            var offset = new Vector3(-100,0,0);
            if(field.Present(default,new ActorGlide("1",EventActor.RetailSpace*offset,true,0) {FrameRate=30},_=>{})!=Error.Ok)
                throw new InvalidOperationException("Cannot place the synthetic sword with a relative glide.");
            mounted.Apply();
            var grip=swordBones.GlobalTransform*swordBones.GetBoneGlobalPose(1).Origin;
            var tip=swordBones.GlobalTransform*swordBones.GetBoneGlobalPose(2)*new Vector3(50,0,0);
            var hand=mount.GlobalTransform*mount.GetBoneGlobalPose(1).Origin;
            if(grip.DistanceTo(hand)>.0001f || tip.DistanceTo(mount.GlobalPosition)>.0001f || grip.Y<=tip.Y)
                throw new InvalidOperationException("The scripted offset did not embed the tip with the grip at the raised hand.");
            if(mounted.Position.DistanceTo(offset)>.0001f || field.Actors["1"].Position.DistanceTo(EventActor.RetailSpace*offset)>.0001f)
                throw new InvalidOperationException("Drawing an attachment overwrote its logical glide position.");
            if(field.Present(default,new ActorGlide("1",EventActor.RetailSpace*new Vector3(0,0,128),true,8) {FrameRate=30},_=>{})!=Error.Ok)
                throw new InvalidOperationException("Cannot start the attached sword's timed glide.");
            field.AdvancePresentation(); mounted.Apply();
            if(mounted.Position.Z<=0 || mounted.Position.Z>=128) throw new InvalidOperationException("The attached glide did not advance over time.");
            for(int tick=0;tick<32;tick++) {field.AdvancePresentation(); mounted.Apply();}
            if(mounted.Position.DistanceTo(offset+new Vector3(0,0,128))>.0001f)
                throw new InvalidOperationException("An attached relative glide lost its destination across drawing updates.");
            if(field.Present(default,new ActorFace("1",Mathf.Pi/2),_=>{})!=Error.Ok)
                throw new InvalidOperationException("Cannot turn an attached sword.");
            mounted.Apply();
            grip=swordBones.GlobalTransform*swordBones.GetBoneGlobalPose(1).Origin;
            tip=swordBones.GlobalTransform*swordBones.GetBoneGlobalPose(2)*new Vector3(50,0,0);
            if(tip.DistanceTo(grip+mount.GlobalBasis*new Vector3(0,0,100))>.0001f)
                throw new InvalidOperationException("The attachment ignored its scripted heading.");
            field.Present(default,new ActorFace("1",0),_=>{});
            mounted.ParentBone=1; mounted.Position=Vector3.Zero; mounted.Apply();
            grip=swordBones.GlobalTransform*swordBones.GetBoneGlobalPose(1).Origin;
            if(grip.DistanceTo(hand)>.0001f) throw new InvalidOperationException("Reattachment did not place the grip at the grabbing hand.");
            mounted.Parent=null; mounted.Apply();
            if(!swordBones.Transform.IsEqualApprox(EventActor.ModelSpace)) throw new InvalidOperationException("Detaching retained the mounting frame in the drawing.");
            root.Free(); root=null;
            GD.Print("ACTOR_PROJECTION_CONTRACTS_OK: numeric/named skin binds, body independence, moving and hidden attachments, static prop attachment pose, embedded tip/grip, instant/timed relative glides and handoff");
            GetTree().Quit();
        }
        catch(Exception error){root?.Free();camera?.Free();GD.PrintErr("ACTOR_PROJECTION_CONTRACTS_FAILED: "+error);GetTree().Quit(1);}
    }
}
