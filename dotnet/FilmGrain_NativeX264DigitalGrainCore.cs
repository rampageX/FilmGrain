using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FilmGrainStudioPreview
{
    internal static class NativeX264DigitalGrainCore
    {


        internal static bool TryPrepare(string appRoot, string input, MediaProbeInfo info, IDictionary<string,string> state, out BridgePreparedExecution prepared, out string reason, string gpuName = "")
        {
            prepared = null;
            reason = "";
            if (state == null || info == null) { reason = "missing state/media probe"; return false; }
            if (!Eq(state, "FG_MODE", "X264") && !CpuEncoderCore.IsNewCpu(state)) { reason = "codec is not x264"; return false; }
            IDictionary<string,string> normalized; string bypassNotice;
            if (!SharedInterpolationBypassCore.TryResolve(info,state,out normalized,out bypassNotice,out reason)) return false;
            if (normalized != null)
            {
                bool success = TryPrepare(appRoot,input,info,normalized,out prepared,out reason,gpuName);
                if (success) SharedInterpolationBypassCore.Attach(prepared,bypassNotice);
                return success;
            }
            if(Eq(state,"FG_MODE","SVT") && SharedGrainCore.Engine(state)=="AV1_METADATA")return TryPrepareMetadata(appRoot,input,info,state,out prepared,out reason,gpuName);
            if (!SharedGrainCore.IsEnabled(state)) return TryPrepareEngine(appRoot, input, info, state, false, out prepared, out reason, gpuName);
            if (SharedGrainCore.Engine(state) == "PROCEDURAL") return TryPrepareEngine(appRoot, input, info, state, false, out prepared, out reason, gpuName);
            if (SharedGrainCore.Engine(state) == "NATIVE") return TryPrepareEngine(appRoot, input, info, state, true, out prepared, out reason, gpuName);
            if (SharedGrainCore.Engine(state) == "FGSIM") return TryPrepareEngine(appRoot, input, info, state, false, out prepared, out reason, gpuName);
            reason = "unsupported native x264 grain engine";
            return false;
        }

        internal static string RetargetMetadataEncode(string command,string oldOutput,string cleanOutput)
        {
            // Timeline/device adapters may serialize the same tokens with different quoting.
            // Compare parsed arguments, never raw command text; paths/filters stay intact.
            var args=HardwareDecodeCore.SplitArguments(command);
            if(args.Count==0 || args[args.Count-1]!=oldOutput)throw new InvalidOperationException("SVT metadata encode boundary changed");
            for(int i=0;i+1<args.Count;i++)if(args[i]=="-movflags") {
                if(args[i+1]!="+faststart")throw new InvalidOperationException("Unexpected SVT MP4 mux flags");
                args.RemoveRange(i,2);i--;
            }
            args[args.Count-1]=cleanOutput;
            args.InsertRange(args.Count-1,new[]{"-f","matroska"});
            return SharedFrucCore.JoinArguments(args);
        }

        // External models replace source parameter grain. Do not synthesize the old
        // model into pixels first. This adds no media stage or intermediate file.
        internal static string Av1SourceDecodeArguments(string command,string input)
        {
            var args=HardwareDecodeCore.SplitArguments(command);
            int first=args.IndexOf("-i");
            if(first<0 || first+1>=args.Count || !string.Equals(args[first+1],input,StringComparison.OrdinalIgnoreCase))return command;
            for(int i=first+2;i+1<args.Count;i++)
                if((args[i]=="-c" || args[i]=="-c:v" || args[i]=="-codec:v") && args[i+1]=="copy")return command;
            if(first<6 || args[first-6]!="-hwaccel" || args[first-5]!="none" || args[first-4]!="-c:v" ||
                args[first-3]!="libdav1d" || args[first-2]!="-filmgrain" || args[first-1]!="0")
                args.InsertRange(first,new[]{"-hwaccel","none","-c:v","libdav1d","-filmgrain","0"});
            // AV_FRAME_DATA_FILM_GRAIN_PARAMS=21 (libavutil/frame.h). Numeric type
            // also works on FFmpeg versions whose sidedata option lacks its name.
            const string strip="sidedata=mode=delete:type=21,";
            for(int i=0;i+1<args.Count;i++) {
                if(args[i]=="-filter_complex")args[i+1]=args[i+1].Replace("[0:v:0]"+strip,"[0:v:0]").Replace("[0:v:0]","[0:v:0]"+strip);
                else if(args[i]=="-vf" && !args[i+1].StartsWith(strip,StringComparison.Ordinal))args[i+1]=strip+args[i+1];
            }
            return SharedFrucCore.JoinArguments(args);
        }

        internal static void AttachAv1SourceDecode(BridgeExecutionRequest request,string input)
        {
            foreach(var stage in request.DirectStages) {
                if(!string.IsNullOrEmpty(stage.PipeProducerArguments)) {
                    var producer=HardwareDecodeCore.SplitArguments(stage.PipeProducerArguments);
                    if(producer.Contains("input="+input))throw new InvalidOperationException(
                        "源 AV1 参数颗粒替换与 OpenSVPFlow 组合尚不能关闭 BestSource 的源颗粒合成；请关闭 OpenSVPFlow。普通编码、缩放和 FRUC 不受此限制。");
                }
                if(!Path.GetFileName(stage.ToolPath).Equals("ffmpeg.exe",StringComparison.OrdinalIgnoreCase))continue;
                var current=stage;
                current.DirectArguments=Av1SourceDecodeArguments(current.DirectArguments,input);
                Action previous=current.PreparedObserver;
                current.PreparedObserver=delegate {
                    if(previous!=null)previous();
                    current.DirectArguments=Av1SourceDecodeArguments(current.DirectArguments,input);
                };
            }
            request.SuppressSourceAv1Grain=true;
            request.ExecutionNotice=(request.ExecutionNotice??"")+Environment.NewLine+
                "[SVT source AV1] libdav1d filmgrain=0; source parameter grain is not baked; only film-grain frame side data is removed before filters. External model replaces it after encoding. Source decode uses software; GPU filters remain available. No extra media file/stage. Grain OFF is unchanged (no source grain removal).";
        }

        // CPU encoding remains on its established route. Only selected AV1 parameter grain
        // adds container postprocessing; NVENC routes and hardware cache rules are untouched.
        private static bool TryPrepareMetadata(string appRoot,string input,MediaProbeInfo info,IDictionary<string,string> state,
            out BridgePreparedExecution prepared,out string reason,string gpuName)
        {
            prepared=null;reason="";
            var config=new FgsConfig(appRoot,null);config.Load();
            string grav=config.Get("GRAV1SYNTH");
            if(!File.Exists(grav)){reason="grav1synth not found: "+grav;return false;}
            string version,error;
            if(!SharedFrucCore.Run(grav,"--version",5000,out version,out error) || version.Trim()!="grav1synth 0.2.2") {
                reason="SVT container grain route requires verified grav1synth 0.2.2: "+version+" "+error;return false;
            }
            var model=new List<string>();string tag;
            string mode=Get(state,"FG_AV1_GRAIN_MODE").ToUpperInvariant();
            if(!NativeAv1GrainReplaceCore.TryBuildGrainArgs(mode,state,model,out tag,out reason))return false;
            int percent=Av1GrainStrengthCore.Read(state);
            string root;if(!TryResolveTempRoot(input,state,out root,out reason))return false;
            string job=Path.Combine(root,"__FGS_SVT_MODEL_"+Guid.NewGuid().ToString("N"));
            var cleanState=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
            cleanState["FG_GRAIN_ENABLED"]="0";
            cleanState["FG_OUTPUT_MODE"]="CUSTOM";cleanState["FG_OUTPUT_CUSTOM_DIR"]=job;
            BridgePreparedExecution baseline;
            if(!TryPrepareEngine(appRoot,input,info,cleanState,false,out baseline,out reason,gpuName)) {
                try{if(Directory.Exists(job))Directory.Delete(job,false);}catch{}return false;
            }
            var request=baseline.ExecutionRequest;
            try {
                if(string.Equals(info.VideoCodec,"av1",StringComparison.OrdinalIgnoreCase))
                    AttachAv1SourceDecode(request,input);
                string old=request.OutputFiles[0];
                string output=Path.Combine(ResolveOutputDirectory(input,state),Path.GetFileNameWithoutExtension(old)+"_GS_"+tag+(percent==100?"":"_GS"+percent.ToString(CultureInfo.InvariantCulture))+Path.GetExtension(old));
                if(File.Exists(output))throw new IOException("Output already exists: "+output);
                // Job directory already isolates tasks; source names must not inflate internal paths.
                string clean=Path.Combine(job,"video.clean.mkv"),grain=Path.Combine(job,"video.grain.mkv"),strength=Path.Combine(job,"video.strength.mkv");
                var temps=new List<string>();if(request.TemporaryFiles!=null)temps.AddRange(request.TemporaryFiles);
                temps.Add(clean);temps.Add(grain);temps.Add(old);
                request.TemporaryFiles=temps;
                request.TemporaryDirectories=new List<string>{job};
                int encodeCount=0;
                foreach(var stage in request.DirectStages) {
                    if(stage.OriginalArguments==null || stage.OriginalArguments.Count==0 || stage.OriginalArguments[stage.OriginalArguments.Count-1]!=old)continue;
                    encodeCount++;
                    var encode=stage;
                    Action observer=encode.PreparedObserver;
                    encode.PreparedObserver=delegate {
                        if(observer!=null)observer();
                        encode.DirectArguments=RetargetMetadataEncode(encode.DirectArguments,old,clean);
                    };
                    encode.StageText+=" / clean MKV for parameter grain (one encode)";
                }
                if(encodeCount!=1)throw new InvalidOperationException("SVT metadata plan requires exactly one final encode");
                var stages=new List<BridgeExecutionStage>(request.DirectStages);
                var applyArgs=new List<string>{"apply",clean,"-o",grain};applyArgs.AddRange(model);applyArgs.AddRange(new[]{"--replace","-y"});
                var apply=MakeStage(grav,job,applyArgs,request.ProgressDurationSeconds,0,0,"SVT AV1 parameter grain / "+mode);
                if(percent!=100 && mode=="PRESET")Av1GrainStrengthCore.AttachPresetScale(apply,applyArgs,grain+".preset.tbl",percent,temps);
                if(percent!=100 && mode=="TABLE")Av1GrainStrengthCore.AttachTableScale(apply,applyArgs,Get(state,"FG_AV1_GRAIN_TABLE"),grain+".scaled.tbl",percent,temps);
                if(request.Hdr10Proof!=null)SvtHdr10Core.Before(apply,delegate {
                    request.Hdr10Proof.Verify(request.ToolPath,clean);
                    return "[SVT HDR10] clean encode: HDR color signaling checked before grain apply; static OBU completeness is not a veto";
                });
                var later=percent!=100&&mode=="ISO"?Av1GrainStrengthCore.Stages(grav,job,delegate{return grain;},strength,percent,temps):new List<BridgeExecutionStage>();
                if(later.Count>0 && !info.IsHdr && !info.IsInterlaced && Get(state,"FG_SVP_INTERPOLATE")!="1" && Av1RouteOptimizationCore.Enabled())
                    Av1IsoStrengthCore.Attach(apply,later,request.ToolPath,grav,job,clean,grain,strength,model,percent,temps);
                stages.Add(apply);stages.AddRange(later);
                var mux=new List<string>{"-hide_banner","-nostdin","-y","-copyts","-avoid_negative_ts","disabled","-i",later.Count>0?strength:grain,"-i",clean,
                    "-map","0:v:0","-map","1:a?"};
                if(Eq(state,"FG_CONTAINER","MKV"))mux.AddRange(new[]{"-map","1:s?","-map","1:t?"});
                mux.AddRange(new[]{"-map_metadata","1","-map_chapters","1","-c","copy"});
                if(Eq(state,"FG_CONTAINER","MP4"))mux.AddRange(new[]{"-movflags","+faststart"});
                mux.Add(output);
                var finalize=MakeStage(request.ToolPath,job,mux,request.ProgressDurationSeconds,0,0,"SVT final container / stream copy, audio from clean encode");
                finalize.BeforeRunPreparation=delegate {
                    string coded=later.Count>0?strength:grain;
                    if(request.Hdr10Proof!=null)request.Hdr10Proof.Verify(request.ToolPath,coded);
                    string probe=Path.Combine(Path.GetDirectoryName(request.ToolPath),"ffprobe.exe");
                    var before=SharedFrucCore.ProbeTimeline(probe,clean);var after=SharedFrucCore.ProbeTimeline(probe,coded);
                    if(Math.Abs(before.VideoOrigin-after.VideoOrigin)>0.0011)throw new InvalidDataException("SVT grain processing changed the video clock; stopped before final mux");
                    string fields="-v error -select_streams v:0 -show_entries stream=width,height,pix_fmt,color_range,color_primaries,color_transfer,color_space -of default=noprint_wrappers=1 ";
                    string a,b,why;
                    if(!SharedFrucCore.Run(probe,fields+Quote(clean),8000,out a,out why)||!SharedFrucCore.Run(probe,fields+Quote(coded),8000,out b,out why)||a.Trim()!=b.Trim())
                        throw new InvalidDataException("SVT grain processing changed or failed format/color verification: "+why);
                    long bytes=0;int files=0;var inventory=new List<string>();
                    foreach(string file in temps)if(File.Exists(file)){long length=new FileInfo(file).Length;bytes+=length;files++;inventory.Add(Path.GetFileName(file)+"="+length.ToString(CultureInfo.InvariantCulture));}
                    return "[SVT container] video origin and probed format/color match clean encode; copy mux retains clean audio clock. Temporary files generated before final mux: "+files+"; logical bytes="+bytes.ToString(CultureInfo.InvariantCulture)+"; "+string.Join("; ",inventory.ToArray())+". Full frame cadence and grain validation remain acceptance-test checks.";
                };
                stages.Add(finalize);
                Av1GrainStrengthCore.NumberStages(stages);
                request.DirectStages=stages;request.OutputFiles=new List<string>{output};
                request.Environment=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
                request.ExecutionNotice=(request.ExecutionNotice??"").Replace("颗粒：关闭 / Grain: disabled (no grain removal)","颗粒：开启 / Grain: enabled (external AV1 model)").Replace("pixel grain baked before encoding","external parameter grain applied after encoding").Replace("no added pixel grain","external parameter grain applied after encoding")+Environment.NewLine+
                    "[SVT parameter grain] external "+mode+" / "+percent+"%; internal film-grain=0; no pixel-grain filter. One CPU encode -> clean MKV -> model apply -> final copy mux. Audio encoded/copied once. ISO fallback may require an extra model rewrite; no retry after full apply failure.";
                prepared=baseline;return true;
            } catch(Exception ex) {
                BridgeExecutionCore.CleanupTemporaryFiles(request);reason="SVT metadata preparation failed: "+ex.Message;return false;
            }
        }

        private static bool TryPrepareEngine(string appRoot, string input, MediaProbeInfo info, IDictionary<string,string> state, bool plateMode, out BridgePreparedExecution prepared, out string reason, string gpuName = "")
        {
            prepared = null;
            reason = "";
            bool newCpu=CpuEncoderCore.IsNewCpu(state);
            bool grainEnabled = SharedGrainCore.IsEnabled(state);
            if (!SharedGrainCore.ValidateHdr(info,state,out reason)) return false;
            bool fgsimMode = SharedGrainCore.Engine(state) == "FGSIM";
            string passMode = newCpu ? "VBR1" : Get(state, "FG_X264_PASS_MODE").ToUpperInvariant();
            if (passMode != "VBR1" && passMode != "2PASS" && passMode != "3PASS") { reason = "unsupported x264 pass mode"; return false; }
            bool mkv = Eq(state, "FG_CONTAINER", "MKV");
            if (!mkv && !Eq(state, "FG_CONTAIN