// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Godot;
using Wol.Engine.World;
using Wol.Games.Vagrant.Installation;
using Wol.Games.Vagrant.Scenes.Field;
using Wol.Games.Vagrant.Runtime;
namespace Wol.Games.Vagrant.Tests;

/// <summary>Synthetic SEQ phases must survive glTF and hand off on the last script tick.</summary>
public partial class AnimationScriptContracts : Node
{
    public override void _Ready()
    {
        FieldScene? field=null; FieldView? adapter=null;
        try
        {
            var model=new byte[0x148]; model[0]=(byte)'H'; model[4]=1; model[0x13c]=255;
            var decoder=new CharacterConversion("synthetic",model,Array.Empty<(string,byte[],int)>());
            decoder.Prepare();
            const int slots=15, stride=14, start=16+2*stride, streams=start+slots;
            var seq=new byte[streams+24]; BitConverter.TryWriteBytes(seq.AsSpan(0),(ushort)slots);seq[2]=1;
            BitConverter.TryWriteBytes(seq.AsSpan(12),(uint)(start-8));
            Array.Fill(seq,(byte)255,start,slots);seq[start+13]=0;seq[start+14]=1;
            for(int i=0;i<2;i++)
            {
                int h=16+i*stride;BitConverter.TryWriteBytes(seq.AsSpan(h),(ushort)31);seq[h+2]=255;
                BitConverter.TryWriteBytes(seq.AsSpan(h+4),(ushort)(16+i*4));
                BitConverter.TryWriteBytes(seq.AsSpan(h+10),(ushort)9);
                seq[streams+16+i*4]=31;seq[streams+17+i*4]=(byte)(i==0?24:23);
            }
            seq[streams+6]=0x82;seq[streams+7]=2; // Three ticks of root velocity: final x=3, then hold.
            var lengths=new byte[65];lengths[23]=lengths[24]=1;
            decoder=new CharacterConversion("synthetic",model,Array.Empty<(string,byte[],int)>()){ScriptLengths=lengths};decoder.Prepare();
            var events=new JsonObject();var clips=decoder.Clips("base",seq,0,events);
            if(clips.Count!=2 || clips.Any(c=>c.Animation.LoopMode!=Animation.LoopModeEnum.None || Math.Abs(c.Animation.Length-31/30.0)>.00001))
                throw new InvalidOperationException("The decoder looped a half-stride or dropped its final script tick.");
            if(events["base_13"]![0]![1]!.GetValue<int>()!=24 || events["base_14"]![0]![1]!.GetValue<int>()!=23)
                throw new InvalidOperationException("Adjacent-animation script commands were discarded.");
            if(clips.Any(c=>Math.Abs(c.Animation.PositionTrackInterpolate(0,c.Animation.Length).X-3)>.00001))
                throw new InvalidOperationException("The trailing SEQ tick returned to the first pose instead of holding the final pose.");
            var modelNode=new Node3D {Name="Model"};var space=new Node3D {Name="Space"};modelNode.AddChild(space);space.Owner=modelNode;
            var skeleton=new Skeleton3D {Name="Skeleton"};space.AddChild(skeleton);skeleton.Owner=modelNode;skeleton.AddBone("b0");
            var player=new AnimationPlayer {Name="AnimationPlayer"};modelNode.AddChild(player);player.Owner=modelNode;
            var library=new AnimationLibrary();foreach(var clip in clips)library.AddAnimation(clip.Clip,clip.Animation);player.AddAnimationLibrary("",library);
            using var document=new GltfDocument();using var state=new GltfState();
            if(document.AppendFromScene(modelNode,state)!=Error.Ok)throw new InvalidOperationException("Cannot export the synthetic stride rig.");
            byte[] buffer=document.GenerateBuffer(state);modelNode.Free();
            using var imported=new GltfDocument();using var importedState=new GltfState();
            if(imported.AppendFromBuffer(buffer,"",importedState)!=Error.Ok || imported.GenerateScene(importedState) is not Node3D rig)
                throw new InvalidOperationException("Cannot import the synthetic stride rig.");
            field=new FieldScene();AddChild(field);field.SetProcess(false);field.SetPhysicsProcess(false);
            using var source=Json.ParseString("""{"schema":1,"models":[],"objects":[{"id":0,"model":-1,"position":[0,0,0]}],"walkmeshes":[]}""").AsGodotDictionary();
            if(field.LoadField(source,new())!=Error.Ok || field.PrepareRigBank("0","base",rig)!=Error.Ok || field.ActivateRig(field.RigBanks[("0","base")])!=Error.Ok)
                throw new InvalidOperationException("Cannot prepare the synthetic stride field.");
            var actor=new EventActor("0",0,field.Actors["0"]){Scene=field,Visible=true};
            foreach(var name in new[]{"base_13","base_14"})
            {
                var importedClip=actor.Player!.GetAnimation(name);
                for(int track=0;track<importedClip.GetTrackCount();track++)
                    if(importedClip.TrackGetType(track)==Animation.TrackType.Position3D && Math.Abs(importedClip.PositionTrackInterpolate(track,importedClip.Length).X-3)>.00001)
                        throw new InvalidOperationException("The final held pose was lost in the glTF round trip.");
            }
            adapter=new FieldView();adapter.Actors["0"]=actor;
            const BindingFlags hidden=BindingFlags.Instance|BindingFlags.NonPublic;
            var cached=(Dictionary<int,Godot.Collections.Dictionary>)typeof(FieldView).GetField("_clipEvents",hidden)!.GetValue(adapter)!;
            cached[0]=Json.ParseString(events.ToJsonString()).AsGodotDictionary();
            var step=typeof(FieldView).GetMethod("RunClipScript",hidden)!;
            foreach(bool controlled in new[]{false,true})
            {
                typeof(FieldView).GetProperty("Controlled")!.SetValue(adapter,controlled);
                actor.ScriptClip="";actor.ScriptFrame=-1;
                if(!actor.Animate("base_13",0))throw new InvalidOperationException("Cannot start the first walking phase.");
                var phases=new List<string>{"base_13"};
                for(int tick=0;tick<480;tick++)
                {
                    field.AdvancePresentation();step.Invoke(adapter,new object[]{actor});
                    string current=actor.Player!.AssignedAnimation;
                    if(current!=phases[^1])phases.Add(current);
                }
                if(phases.Count<5 || phases.Where((s,i)=>s!=(i%2==0?"base_13":"base_14")).Any())
                    throw new InvalidOperationException($"Walking repeated one foot phase or missed the end-frame command (controlled={controlled}).");
            }
            adapter.Free();adapter=null;field.Free();field=null;
            // A closed bipyramid has 24 edges without seam/pole degeneracies.
            var vertices=new List<Vector3>{new(0,1,0),new(0,-1,0)};
            for(int i=0;i<8;i++)vertices.Add(new Vector3(Mathf.Cos(i*Mathf.Tau/8),0,Mathf.Sin(i*Mathf.Tau/8)));
            var triangles=new List<int>();
            for(int i=0;i<8;i++){int a=2+i,b=2+(i+1)%8;triangles.AddRange(new[]{0,a,b,1,b,a});}
            int count=vertices.Count;
            var sphereArrays=new Godot.Collections.Array();sphereArrays.Resize((int)Mesh.ArrayType.Max);
            sphereArrays[(int)Mesh.ArrayType.Vertex]=vertices.ToArray();sphereArrays[(int)Mesh.ArrayType.Index]=triangles.ToArray();
            sphereArrays[(int)Mesh.ArrayType.Bones]=new int[count*4];var weights=new float[count*4];
            for(int i=0;i<count;i++)weights[i*4]=1;sphereArrays[(int)Mesh.ArrayType.Weights]=weights;
            sphereArrays[(int)Mesh.ArrayType.Color]=Enumerable.Repeat(new Color(.08f,.12f,.16f,.3f),count).ToArray();
            var shaded=new ArrayMesh();shaded.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,sphereArrays);
            var expected=shaded.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Color].AsColorArray()[0];
            var rounded=CharacterQuality.Rounded(shaded,0);var roundedArrays=rounded.SurfaceGetArrays(0);
            if(roundedArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length<=count || roundedArrays[(int)Mesh.ArrayType.Color].AsColorArray().Any(c=>Math.Abs(c.R-expected.R)>1/255f+.0001f || Math.Abs(c.G-expected.G)>1/255f+.0001f || Math.Abs(c.B-expected.B)>1/255f+.0001f || Math.Abs(c.A-expected.A)>1/255f+.0001f))
                throw new InvalidOperationException($"Enhanced head shading fixture: source={count}, result={roundedArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length}, expected={expected}, sample={roundedArrays[(int)Mesh.ArrayType.Color].AsColorArray()[^1]}");
            rounded.Dispose();shaded.Dispose();
            GD.Print("ANIMATION_SCRIPT_CONTRACTS_OK: adjacent phases, final held pose/tick, glTF round trip, scripted and controlled walking, enhanced head vertex colours/alpha");GetTree().Quit();
        }
        catch(Exception error){adapter?.Free();field?.Free();GD.PrintErr("ANIMATION_SCRIPT_CONTRACTS_FAILED: "+error);GetTree().Quit(1);}
    }
}
