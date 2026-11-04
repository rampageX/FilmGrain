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
            if (!mkv && !Eq(state, "FG_CONTAINER", "MP4")) { reason = "unsupported native x264 container"; return false; }
            bool autoFps = Eq(state, "FG_FPS_MODE", "AUTO");
            if (!autoFps && !Eq(state, "FG_FPS_MODE", "SOURCE") && !Eq(state, "FG_FPS_MODE", "KEEP")) { reason = "unsupported native x264 FPS mode"; return false; }
            bool interpolate = Get(state, "FG_SVP_INTERPOLATE") == "1";
            bool fruc = interpolate && Eq(state, "FG_INTERPOLATION_ENGINE", "FRUC");
            bool svp = interpolate && !fruc;
            bool shareInterpolation=SharedInterpolationEncodeCore.CanShare(interpolate,info.IsInterlaced,passMode);
            bool mergeFrucHdr=fruc&&SharedGrainCore.CpuHdrToSdr(info,state);
            if (interpolate && !fruc && !NvencInterpolationCore.Enabled(state) && !Eq(state, "FG_INTERPOLATION_ENGINE", "SVP") && !string.IsNullOrEmpty(Get(state, "FG_INTERPOLATION_ENGINE")))
            { reason = "unsupported interpolation engine"; return false; }
            if (fruc && info.IsInterlaced) { reason = "interlaced FRUC is not supported"; return false; }
            bool transportSvp = svp && !info.IsHdr && IsTransportInput(input);
            bool hdrToSdr = SharedGrainCore.CpuHdrToSdr(info,state);
            bool hdrPreserve=info.IsHdr&&!hdrToSdr;
            if(hdrPreserve && !SvtHdr10Core.CheckScope(info,state,grainEnabled,interpolate,out reason))return false;
            if(hdrPreserve&&fruc)shareInterpolation=false;
            string tonemap = Get(state, "FG_TONEMAP_ALGO").ToLowerInvariant();
            if (tonemap != "mobius" && tonemap != "reinhard" && tonemap != "gamma" && tonemap != "linear" && tonemap != "clip") tonemap = "hable";
            string tonemapLabel = char.ToUpperInvariant(tonemap[0]) + tonemap.Substring(1);
            // Match Legacy Bridge: interlaced input only enters the deinterlace route when UI selects AUTO.
            // "OFF" keeps the original field-encoded source path without BWDIF/W3FDIF conversion.
            bool deinterlace = info.IsInterlaced && Eq(state, "FG_DEINTERLACE", "AUTO");
            // OFF follows Legacy: no field filter or bob; it does not enable interlaced encoding.
            if(info.IsInterlaced && !deinterlace && !Eq(state,"FG_DEINTERLACE","OFF")) { reason="unsupported deinterlace policy"; return false; }
            bool hdrOffSdr=hdrToSdr && info.IsInterlaced && !deinterlace;
            bool bwdifCuda = deinterlace && Eq(state, "FG_DEINT_METHOD", "BWDIF_CUDA");
            bool bwdifVulkan = deinterlace && Eq(state, "FG_DEINT_METHOD", "BWDIF_VULKAN");
            if (info.IsInterlaced && Eq(state, "FG_DEINTERLACE", "AUTO") && (!Eq(state, "FG_DEINT_METHOD", "W3FDIF") && !bwdifCuda && !bwdifVulkan)) { reason = "unsupported x264 deinterlace method"; return false; }
            bool high10 = newCpu || Get(state, "FG_H264_HIGH10") == "1";
            if (info.Width <= 0 || info.Height <= 0) { reason = "invalid dimensions"; return false; }

            long bitrate;
            if (newCpu && CpuEncoderCore.IsCrf(state)) bitrate=1;
            else if (!long.TryParse(Get(state, "FG_BITRATE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out bitrate) || bitrate <= 0) { reason = "invalid bitrate"; return false; }
            string fps = info.AvgFrameRate;
            if (string.IsNullOrWhiteSpace(fps) || fps == "0/0") { reason = "invalid source FPS"; return false; }
            string fpsFilter = "", fpsSuffix = "";
            if (deinterlace)
            {
                if (!TryDoubleSourceFps(fps, out fps)) { reason = "invalid field-rate FPS"; return false; }
            }
            else if (interpolate)
            {
                InterpolationFrameRate targetRate; string rateError;
                if (!InterpolationFrameRateCore.TryParse(Get(state, "FG_INTERPOLATION_TARGET_FPS"), info.AvgFrameRate, out targetRate, out rateError))
                { reason = "invalid interpolation target FPS: " + rateError; return false; }
                fps = targetRate.Rational;
                fpsSuffix = (NvencInterpolationCore.Enabled(state) ? (Eq(state,"FG_INTERPOLATION_ENGINE","NVENC_AI")?"_NVAI":"_NVFRUC") : fruc ? "_FRUC" : "_SVP") + targetRate.Label;
            }
            else if (autoFps && !TryResolveAutoFps(fps, out fps, out fpsFilter, out fpsSuffix)) { reason = "invalid automatic FPS"; return false; }
            if (interpolate) autoFps = true;
            string deinterlaceFilter = deinterlace ? SharedHdrPreprocessCore.DeinterlaceFilter(Get(state,"FG_DEINT_METHOD"),info) : "";
            string fieldFilter=deinterlaceFilter;
            bool fieldWorkfile=deinterlace && hdrToSdr;
            bool separateDeint=deinterlace && !hdrToSdr && bwdifVulkan && (plateMode || fgsimMode);
            if(fieldWorkfile || separateDeint)deinterlaceFilter="";
            string deinterlaceSuffix = bwdifVulkan ? "_DI_BWV" : bwdifCuda ? "_DI_BWC" : deinterlace ? "_DI_W3F" : "";
            double durationSeconds = 0.0;
            double.TryParse(info.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds);
            if (plateMode && durationSeconds <= 0.0) { reason = "invalid source duration for Grain Plate loop bound"; return false; }
            string duration = durationSeconds > 0.0 ? durationSeconds.ToString("0.######", CultureInfo.InvariantCulture) : "";

            string ffmpeg = Path.Combine(GetFfmpegDir(appRoot), "ffmpeg.exe");
            string ffprobe = Path.Combine(GetFfmpegDir(appRoot), "ffprobe.exe");
            if (fruc && Get(state, "FG_FRUC_USE_MAIN_FFMPEG") == "0" && string.IsNullOrWhiteSpace(Get(state, "FG_FRUC_FFMPEG_PATH")))
            { reason = "custom FRUC FFmpeg directory is empty"; return false; }
            if (fruc && !string.IsNullOrWhiteSpace(Get(state, "FG_FRUC_FFMPEG_PATH"))) ffmpeg = Get(state, "FG_FRUC_FFMPEG_PATH");
            if(Eq(state,"FG_MODE","SVT") && Get(state,"FG_FRUC_USE_MAIN_FFMPEG")=="0") {
                string selected=Get(state,"FG_FRUC_FFMPEG_PATH");
                if(string.IsNullOrWhiteSpace(selected)){reason="SVT special FFmpeg path is empty";return false;}
                ffmpeg=selected;
            }
            if (!File.Exists(ffmpeg)) { reason = "ffmpeg.exe not found: " + ffmpeg; return false; }
            if (fruc && !SharedFrucCore.TryPreflight(ffmpeg, Get(state, "FG_FRUC_PERF").ToLowerInvariant(), Get(state, "FG_FRUC_GRID").ToLowerInvariant(), out reason)) return false;
            if (fruc && hdrPreserve && !SharedFrucCore.TryPreflight10(ffmpeg, Get(state,"FG_FRUC_PERF").ToLowerInvariant(), Get(state,"FG_FRUC_GRID").ToLowerInvariant(), out reason)) return false;
            string vspipe = Path.Combine(appRoot, "_OpenSVPFlow", ".venv", "Scripts", "vspipe.exe");
            string vpy = Path.Combine(appRoot, "_OpenSVPFlow", "FilmGrain_OpenSVPFlow.vpy");
            string svpPlugins = Path.Combine(appRoot, "_OpenSVPFlow", "Plugins");
            if (svp && !NvencInterpolationCore.Enabled(state) && (!File.Exists(vspipe) || !File.Exists(vpy) || !File.Exists(Path.Combine(svpPlugins, "svpflow1_vs.dll")) || !File.Exists(Path.Combine(svpPlugins, "svpflow2_vs.dll"))))
            { reason = "OpenSVPFlow dependencies are missing"; return false; }
            string fgsimHook = "", fgsimTag = "";
            if (fgsimMode)
            {
                string fgsimPreset = Get(state, "FG_FGSIM_PRESET").ToUpperInvariant();
                if (!GrainControlCore.IsFgsimPreset(fgsimPreset)) { reason = "unsupported FGSIM preset"; return false; }
                fgsimTag = GrainControlCore.FgsimTag(fgsimPreset);
                fgsimHook = Path.Combine(appRoot, "Utils", "_FilmGrainSimplified", "Generated", "FilmGrainSimplified_" + fgsimPreset + ".hook");
                if (!TryPrepareFgsimHook(appRoot, fgsimHook, fgsimPreset, out reason)) return false;
            }
            if (newCpu && !CpuEncoderCore.TryPreflight(ffmpeg,state,out reason)) return false;
            if (!newCpu && high10 && !SupportsHigh10(ffmpeg)) { reason = "this libx264 build does not advertise yuv420p10le"; return false; }

            CinematicFramePlan frame;
            if (!CinematicFrameCore.TryBuild(info, state, out frame, out reason)) return false;
            ResizePlan resize;
            if(!ResizeCore.TryBuild(info,state,frame,out resize,out reason))return false;
            int processingWidth=resize.Enabled?resize.Width:info.Width, processingHeight=resize.Enabled?resize.Height:info.Height;
            bool highMotion = Get(state, "FG_HIGH_MOTION") == "1";
            if (!(newCpu&&CpuEncoderCore.IsCrf(state)) && Eq(state, "FG_BITRATE_MODE", "AUTO"))
            {
                double rateFps = ParseFps(fps);
                bitrate = BitrateRecommendationCore.GetRecommendedBitrate(newCpu?(Eq(state,"FG_MODE","SVT")?0:1):2, frame.ActiveWidth, frame.ActiveHeight, rateFps, highMotion);
            }

            string colorFilter, colorSuffix;
            if (!TryBuildColorFilter(state, out colorFilter, out colorSuffix, out reason)) return false;
            if(hdrPreserve)colorFilter=HdrColorProcessingCore.Cc(state,info);
            string preprocessing;
            if(!PreprocessingCore.TryBuild(state,out preprocessing,out reason))return false;
            if(preprocessing.Length>0 && !PreprocessingCore.TryCheckTool(ffmpeg,out reason))return false;
            if(resize.Enabled && !ResizeCore.TryCheckTool(ffmpeg,out reason))return false;
            colorFilter=preprocessing+resize.Prefix+colorFilter;
            if(preprocessing.Length>0)colorSuffix="_BIL"+colorSuffix;
            string lutNamingSuffix = BuildLutNamingSuffix(state);
            string preset = Get(state, "FG_X264_PRESET").ToLowerInvariant();
            if (preset != "medium" && preset != "slow") preset = "faster";
            string x264Suffix = newCpu ? "_P"+CpuEncoderCore.Preset(state) : BuildX264FileSuffix(preset, passMode);

            string grainInput = "";
            bool grainScale = false;
            string grainTag = "";
            string grainOpacity = "";
            int plateStrengthPercent = 85;
            int procStrength = 55;
            string procMask = "0.55";
            if (plateMode)
            {
                string plate = Get(state, "FG_HEVC_GRAIN_PATH");
                if (string.IsNullOrWhiteSpace(plate) || !File.Exists(plate)) { reason = "selected Grain Plate is missing: " + plate; return false; }
                grainInput = plate;
                grainScale = SharedGrainCore.NeedsPlateScaling(ffprobe, plate, processingWidth, processingHeight);

                int strengthSel;
                if (!int.TryParse(Get(state, "FG_HEVC_STRENGTH_SEL"), NumberStyles.Integer, CultureInfo.InvariantCulture, out strengthSel)) strengthSel = 3;
                plateStrengthPercent = GrainControlCore.PlatePercent(state);
                grainOpacity = (GrainControlCore.PlatePercent(state)/100.0).ToString("0.00",CultureInfo.InvariantCulture);
                grainTag = ResolvePlateTag(plate, Get(state, "FG_HEVC_GRAIN_TAG"));
            }
            else
            {
                if (!int.TryParse(Get(state, "FG_PROC_STRENGTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out procStrength)) procStrength = 55;
                procStrength = Math.Max(10, Math.Min(100, procStrength));
                procMask = procStrength == 100 ? "1.00" : "0." + procStrength.ToString("00", CultureInfo.InvariantCulture);
            }

            string outDir = ResolveOutputDirectory(input, state);
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { reason = "cannot create output directory: " + ex.Message; return false; }

            string subSuffix = Get(state, "FG_SUBTITLE") == "1" ? "_SUB" : "";
            int pixelPercent = grainEnabled ? PixelGrainStrengthCore.Read(state) : 100;
            string grainFileName = !grainEnabled ? "" : plateMode ? "_FG_" + grainTag + "_STR" + plateStrengthPercent.ToString(CultureInfo.InvariantCulture) : fgsimMode ? "_FG_FGSIM_" + fgsimTag : "_FG_DG" + procStrength.ToString(CultureInfo.InvariantCulture);
            if (grainEnabled && pixelPercent != 100) grainFileName += "_GS" + pixelPercent.ToString(CultureInfo.InvariantCulture);
            string output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + "_" + (newCpu?Get(state,"FG_MODE"):"X264") + x264Suffix + "_" + (newCpu&&CpuEncoderCore.IsCrf(state)?"CRF"+Get(state,"FG_NVENC_CQ"):bitrate.ToString(CultureInfo.InvariantCulture)+"k") + (high10 ? "_HIGH10" : "") + fpsSuffix + deinterlaceSuffix + frame.FileSuffix + grainFileName + colorSuffix + lutNamingSuffix + (hdrToSdr ? "_SDR_" + tonemapLabel : "") + subSuffix + HardwareDecodeCore.FileSuffix(state) + (mkv ? ".mkv" : ".mp4"));
            if (File.Exists(output)) { reason = "native output already exists: " + output; return false; }

            string lutCompatPath = "", lutCompatName = "", lutOpacity = "";
            if (!TryPrepareLut(ffmpeg, state, out lutCompatPath, out lutCompatName, out lutOpacity, out reason)) return false;
            if(hdrPreserve && !string.IsNullOrEmpty(lutCompatName)) {
                colorFilter+=HdrColorProcessingCore.Lut(info,EscapeFilterPath(lutCompatName),lutOpacity);
                lutCompatName=""; // Already applied in the shared HDR prefix; keep the LUT file for execution.
            }
            string subtitleAss = "";
            if (!NativeSubtitlePrepareCore.TryPrepare(ffmpeg, ffprobe, input, state, frame.ActiveWidth, frame.ActiveHeight, out subtitleAss, out reason))
            {
                TryDelete(lutCompatPath);
                return false;
            }

            string workingDirectory=string.IsNullOrEmpty(lutCompatPath)?Path.GetDirectoryName(input):Path.GetDirectoryName(lutCompatPath);
            string subtitleFilterPath;
            if(!NativeSubtitlePrepareCore.TryStageForFilter(subtitleAss,workingDirectory,out subtitleFilterPath,out reason))
            {TryDelete(lutCompatPath);TryDelete(subtitleAss);return false;}
            string passlog = "";
            List<string> temporaryFiles = new List<string>();
            if (!string.IsNullOrEmpty(lutCompatPath)) temporaryFiles.Add(lutCompatPath);
            if (!string.IsNullOrEmpty(subtitleAss)) temporaryFiles.Add(subtitleAss);
            if(!string.IsNullOrEmpty(subtitleFilterPath)&&subtitleFilterPath!=subtitleAss)temporaryFiles.Add(subtitleFilterPath);
            try
            {
                string subtitleFilter=NativeSubtitlePrepareCore.BurnInFilter(subtitleFilterPath);
                string filter;
                if (!grainEnabled)
                {
                    filter = SharedGrainCore.BuildWithoutGrain(deinterlaceFilter + fpsFilter + colorFilter,
                        EscapeFilterPath(lutCompatName), lutOpacity,
                        frame.Filter + subtitleFilter + ",format=" + (high10 ? "yuv420p10le" : "yuv420p"));
                }
                else if (plateMode)
                {
                    string grain = "[1:v:0]fps=" + fps + ",format=p010le,setpts=PTS-STARTPTS,hwupload" + (grainScale ? ",scale_vulkan=w=" + processingWidth.ToString(CultureInfo.InvariantCulture) + ":h=" + processingHeight.ToString(CultureInfo.InvariantCulture) + ":scaler=bilinear" : "") + "[grainvk]";
                    string baseFilter;
                    if (!string.IsNullOrEmpty(lutCompatName))
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + EscapeFilterPath(lutCompatName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=p010le,hwupload[basevk]";
                    else
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=p010le,setpts=PTS-STARTPTS,hwupload[basevk]";
                    filter = baseFilter + ";" + grain + ";[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=" + grainOpacity + ",hwdownload,format=p010le" + frame.Filter + subtitleFilter + ",format=" + (high10 ? "yuv420p10le" : "yuv420p") + "[vout]";
                }
                else if (fgsimMode)
                {
                    string baseFilter;
                    if (!string.IsNullOrEmpty(lutCompatName))
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + EscapeFilterPath(lutCompatName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=yuv420p[fgsimbase]";
                    else
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=yuv420p[fgsimbase]";
                    filter = baseFilter + ";[fgsimbase]hwupload,libplacebo=format=yuv420p:custom_shader_path=" + EscapeFgsimShaderPath(fgsimHook) + ",hwdownload,format=yuv420p" + frame.Filter + subtitleFilter + ",format=" + (high10 ? "yuv420p10le" : "yuv420p") + "[vout]";
                }
                else
                {
                    int pw = Even((processingWidth * 4 + 2) / 3);
                    int ph = Even((processingHeight * 4 + 2) / 3);
                    string baseFilter;
                    if (!string.IsNullOrEmpty(lutCompatName))
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + EscapeFilterPath(lutCompatName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=yuv420p[procbase]";
                    else
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=yuv420p[procbase]";
                    filter = baseFilter + ";[procbase]split=4[seed][masksrc][base][blacksrc];[seed]scale=" + pw.ToString(CultureInfo.InvariantCulture) + ":" + ph.ToString(CultureInfo.InvariantCulture) + ",lutyuv=y=128:u=128:v=128,noise=c0s=100:c0f=t+u,deflate=threshold0=15,dilation=threshold0=10,eq=contrast=3,scale=" + processingWidth.ToString(CultureInfo.InvariantCulture) + ":" + processingHeight.ToString(CultureInfo.InvariantCulture) + "[n];[masksrc]lutyuv=y='" + procMask + "*(182-abs(75-val))':u=128:v=128[o];[n][o]blend=c0_mode=multiply,negate[a];[base][a]alphamerge[c];[blacksrc]drawbox=color=black:t=fill[black];[black][c]overlay=shortest=1" + frame.Filter + subtitleFilter + ",format=" + (high10 ? "yuv420p10le" : "yuv420p") + "[vout]";
                }

                if(hdrPreserve)filter=filter.Replace("[vout]",SvtHdr10Core.ColorFilter(info)+"[vout]");
                filter=LutStrengthCore.OptimizeGraph(filter);
                string pixelFull = grainEnabled ? filter : null;
                string pixelClean = grainEnabled ? SharedGrainCore.BuildWithoutGrain(deinterlaceFilter + fpsFilter + colorFilter,
                    EscapeFilterPath(lutCompatName), lutOpacity, frame.Filter + subtitleFilter + ",format=" + (high10 ? "yuv420p10le" : "yuv420p")) : null;
                if (grainEnabled && (plateMode)) pixelClean=pixelClean.Replace("[0:v:0]","[0:v:0]setpts=PTS-STARTPTS,");
                if(hdrPreserve&&!string.IsNullOrEmpty(pixelClean))pixelClean=pixelClean.Replace("[vout]",SvtHdr10Core.ColorFilter(info)+"[vout]");
                pixelClean=LutStrengthCore.OptimizeGraph(pixelClean);
                if (grainEnabled) filter = PixelGrainStrengthCore.Wrap(filter,pixelClean,pixelPercent);

                if (fruc)
                {
                    string perf = Get(state, "FG_FRUC_PERF").ToLowerInvariant();
                    if (perf != "slow" && perf != "medium" && perf != "fast") { reason = "unsupported FRUC perf setting"; return false; }
                    string grid = Get(state, "FG_FRUC_GRID").ToLowerInvariant();
                    if (grid != "auto" && grid != "1" && grid != "2" && grid != "4" && grid != "8") { reason = "unsupported FRUC grid setting"; return false; }
                    filter = filter.Replace("[0:v:0]", "[0:v:0]setpts=PTS-STARTPTS,format="+(hdrPreserve?"p010le":"nv12")+",hwupload,fruc_vulkan=fps=" + fps + ":perf=" + perf + ":grid=" + grid + ",hwdownload,format="+(hdrPreserve?"p010le":"nv12")+",");
                }
                if (svp && plateMode) filter = filter.Replace("[1:v:0]", "[2:v:0]");

                long max = bitrate * 3L;
                long buf = bitrate * 6L;

                List<string> requestInputs = new List<string>(); requestInputs.Add(input); if (plateMode) requestInputs.Add(grainInput);
                BridgeExecutionRequest request = new BridgeExecutionRequest();
                request.PixelGrainFullFilter = pixelFull;
                request.PixelGrainCleanFilter = pixelClean;
                request.ToolPath = ffmpeg;
                request.WorkingDirectory = workingDirectory;
                request.InputFiles = requestInputs;
                request.Environment = new Dictionary<string,string>(state, StringComparer.OrdinalIgnoreCase);
                if (svp && File.Exists(Path.Combine(appRoot, "_OpenSVPFlow", "_UserConfig", "vapoursynth", "vapoursynth.toml")))
                    request.Environment["APPDATA"] = Path.Combine(appRoot, "_OpenSVPFlow", "_UserConfig");
                request.DirectProcess = true;
                request.OutputFiles = new List<string>{ output };
                request.ProgressDurationSeconds = durationSeconds > 0.0 ? durationSeconds : 0.0;

                string encodeInput = input;
                List<BridgeExecutionStage> stages = new List<BridgeExecutionStage>();
                // Multiple analysis/final passes share one conversion instead of repeating HDR work per pass.
                bool mergedHdr=SharedHdrPreprocessCore.CanMergeEncode(hdrToSdr,interpolate,info.IsInterlaced) && passMode=="VBR1";
                bool hdrWorkfile=hdrToSdr && !mergedHdr && !mergeFrucHdr;
                int stageOffset = (hdrWorkfile || separateDeint ? 1 : 0)+(shareInterpolation?1:0);
                int stageTotal = stageOffset + (passMode == "VBR1" ? 1 : passMode == "3PASS" ? 3 : 2);
                if (hdrWorkfile || separateDeint)
                {
                    string tempRoot;
                    if (!TryResolveTempRoot(input, state, out tempRoot, out reason)) return false;
                    encodeInput = Path.Combine(tempRoot, (hdrToSdr ? "__FGS_HDR2SDR_" : "__FGS_FIELDS_") + Guid.NewGuid().ToString("N") + ".mkv");
                    temporaryFiles.Add(encodeInput);
                    stages.Add(MakeStage(ffmpeg, workingDirectory, hdrToSdr ? SharedHdrPreprocessCore.BuildToSdr(input,encodeInput,info,tonemap,true,fieldWorkfile?fieldFilter:"") : SharedHdrPreprocessCore.BuildDeinterlaced(input,encodeInput,info,fieldFilter), durationSeconds, 1, stageTotal, hdrToSdr ? "HDR to SDR / " + tonemapLabel : "Shared field deinterlace / separate Vulkan device"));
                    if(hdrToSdr)SharedHdrPreprocessCore.AttachAutomatic(stages[stages.Count-1],tonemap);
                }
                string interpolationAudio=input;
                if(shareInterpolation) {
                    string interpolationRoot;if(!TryResolveTempRoot(input,state,out interpolationRoot,out reason))return false;
                    string cached=Path.Combine(interpolationRoot,"__FGS_INTERPOLATED_"+Guid.NewGuid().ToString("N")+".mkv");
                    temporaryFiles.Add(cached);
                    // Cache the exact complete render graph once. Replaying interpolation through a
                    // different container clock before libplacebo changes its output frame queue.
                    var render=BuildEncodeArgs(encodeInput,grainInput,filter,cached,preset,highMotion,bitrate,max,buf,fps,duration,plateMode,autoFps,high10,mkv,deinterlace,0,"",false,svp,grainEnabled,state,info);
                    render=SharedInterpolationEncodeCore.BuildRenderCache(render,cached,high10?"yuv420p10le":"yuv420p");
                    var shared=MakeStage(ffmpeg,workingDirectory,render,durationSeconds,stageOffset,stageTotal,"Shared interpolation and effects render cache");
                    if(fruc)shared.FrucProbePath=ffprobe;
                    if(svp) {
                        SharedSvpCore.AttachProducer(shared,vspipe,vpy,svpPlugins,encodeInput,state,fps);
                        if(hdrToSdr)SharedHdrPreprocessCore.AttachPipeTimeline(shared,ffprobe,encodeInput,info,shared.OriginalArguments,input);
                        else SharedPipeTimelineCore.AttachTransportSource(shared,ffprobe,encodeInput,info,shared.OriginalArguments);
                    }
                    if(mergeFrucHdr)SharedHdrPreprocessCore.AttachMergedFruc(shared,input,info,tonemap,gpuName);
                    stages.Add(shared);encodeInput=cached;filter="[0:v:0]null[vout]";plateMode=false;
                }
                if (passMode == "VBR1")
                {
                    List<string> args = BuildEncodeArgs(encodeInput, grainInput, filter, output, preset, highMotion, bitrate, max, buf, fps, duration, plateMode, autoFps, high10, mkv, deinterlace, 0, "", true, svp && !shareInterpolation, grainEnabled, state, info);
                    request.PixelGrainEncodeArguments = args;
                    // Even a single ordinary grain encode must have a stage: shared timeline
                    // binding at the shared request entry requires stages, never a bare command.
                    stages.Add(MakeStage(ffmpeg, workingDirectory, args, durationSeconds, stageOffset + 1, stageTotal, newCpu?Get(state,"FG_MODE")+" CPU final encode":"x264 final encode"));
                    request.DirectStages = stages;
                }
                else
                {
                    string tempRoot;
                    if (!TryResolveTempRoot(input, state, out tempRoot, out reason)) return false;
                    passlog = Path.Combine(tempRoot, (plateMode ? "__FGS_X264_" : fgsimMode ? "__FGS_X264_FGSIM_" : "__FGS_X264_PROC_") + Guid.NewGuid().ToString("N"));
                    AddPasslogTemps(temporaryFiles, passlog);
                    stages.Add(MakeStage(ffmpeg, workingDirectory, BuildEncodeArgs(encodeInput, grainInput, filter, output, preset, highMotion, bitrate, max, buf, fps, duration, plateMode, autoFps, high10, mkv, deinterlace, 1, passlog, false, svp && !shareInterpolation, grainEnabled, state, info), durationSeconds, stageOffset + 1, stageTotal, !grainEnabled ? "pass 1: analysis / 颗粒：关闭" : plateMode ? "pass 1: Grain Plate analysis" : fgsimMode ? "pass 1: FGSIM analysis" : "pass 1: Digital Grain analysis"));
                    if (passMode == "3PASS")
                        stages.Add(MakeStage(ffmpeg, workingDirectory, BuildEncodeArgs(encodeInput, grainInput, filter, output, preset, highMotion, bitrate, max, buf, fps, duration, plateMode, autoFps, high10, mkv, deinterlace, 3, passlog, false, svp && !shareInterpolation, grainEnabled, state, info), durationSeconds, stageOffset + 2, stageTotal, "pass 3: stats refinement"));
                    stages.Add(MakeStage(ffmpeg, workingDirectory, BuildEncodeArgs(encodeInput, grainInput, filter, output, preset, highMotion, bitrate, max, buf, fps, duration, plateMode, autoFps, high10, mkv, deinterlace, 2, passlog, true, svp && !shareInterpolation, grainEnabled, state, info), durationSeconds, stageTotal, stageTotal, !grainEnabled ? "pass 2: final encode / 颗粒：关闭" : plateMode ? "pass 2: Grain Plate final encode" : fgsimMode ? "pass 2: FGSIM final encode" : "pass 2: Digital Grain final encode"));
                    request.DirectStages = stages;
                }

                if (svp && !shareInterpolation)
                    for (int i = stageOffset; i < stages.Count; i++)
                    {
                        SharedSvpCore.AttachProducer(stages[i],vspipe,vpy,svpPlugins,encodeInput,state,fps);
                        if (hdrToSdr) SharedHdrPreprocessCore.AttachPipeTimeline(stages[i],ffprobe,encodeInput,info,stages[i].OriginalArguments,input);
                        else if (transportSvp || NvencInterpolationCore.Enabled(state)) SharedPipeTimelineCore.AttachTransportSource(stages[i],ffprobe,encodeInput,info,stages[i].OriginalArguments);
                    }

                if(fieldWorkfile || separateDeint || hdrOffSdr)
                    for(int i=stageOffset;i<stages.Count;i++)
                        SharedHdrPreprocessCore.AttachFieldTimeline(stages[i],ffprobe,encodeInput,info,hdrToSdr,!hdrOffSdr);

                request.TemporaryFiles = temporaryFiles.Count == 0 ? null : temporaryFiles;
                if (fruc && request.DirectStages != null)
                    foreach (BridgeExecutionStage stage in request.DirectStages)
                        if (stage.FrucArguments != null) stage.FrucProbePath = ffprobe;
                if(shareInterpolation)
                    for(int i=stageOffset;i<stages.Count;i++)
                        SharedInterpolationEncodeCore.AttachConsumer(stages[i],ffprobe,encodeInput,interpolationAudio,fps,fruc,hdrToSdr);
                else if(mergeFrucHdr)
                    for(int i=stageOffset;i<stages.Count;i++)
                        SharedHdrPreprocessCore.AttachMergedFruc(stages[i],input,info,tonemap,gpuName);
                else if(mergedHdr)
                    for(int i=stageOffset;i<stages.Count;i++)
                        SharedHdrPreprocessCore.AttachMergedEncode(stages[i],ffprobe,input,info,tonemap,gpuName);
                else if(hdrWorkfile && !interpolate && !info.IsInterlaced)
                    for(int i=stageOffset;i<stages.Count;i++)
                        SharedHdrPreprocessCore.AttachSharedWorkfileEncode(stages[i],ffprobe,encodeInput,gpuName);

                if(hdrPreserve)SvtHdr10Core.Attach(request,input,info);

                if(info.IsHdr && info.IsInterlaced && !deinterlace)request.ExecutionNotice="[Native .NET] 反交错 OFF：沿用不反交错的逐帧处理，不启用 bob/光流或双倍场率；不保证隔行编码标记。 / Deinterlace OFF: frame processing without bob/interpolation; no interlaced-encoding flags are added.";
            if(newCpu)request.ExecutionNotice=(request.ExecutionNotice??"")+Environment.NewLine+"[CPU] "+Get(state,"FG_MODE")+" "+(CpuEncoderCore.IsCrf(state)?"CRF "+Get(state,"FG_NVENC_CQ"):"VBR")+" preset="+CpuEncoderCore.Preset(state)+"; "+(grainEnabled?"pixel grain baked before encoding":"no added pixel grain")+"; GPU preprocessing may still be used.";
            if (fgsimMode) FgsimTextureCore.AttachVerifiedDevice(request, fgsimHook, false);
            HdrColorProcessingCore.Notice(request,info,state,hdrPreserve);
            prepared = new BridgePreparedExecution(request, false, ffmpeg, requestInputs);
                lutCompatPath = "";
                subtitleAss = "";
                subtitleFilterPath="";
                return true;
            }
            finally
            {
                if (!string.IsNullOrEmpty(lutCompatPath)) TryDelete(lutCompatPath);
                if (!string.IsNullOrEmpty(subtitleAss)) TryDelete(subtitleAss);
                if(!string.IsNullOrEmpty(subtitleFilterPath))TryDelete(subtitleFilterPath);
            }
        }

        // StudioBridge :DOUBLE_SOURCE_FPS: preserve the ffprobe denominator.
        private static bool TryDoubleSourceFps(string source, out string doubled)
        {
            doubled = "";
            string[] parts = source.Split('/');
            if (parts.Length < 1 || parts.Length > 2) return false;
            long numerator, denominator = 1;
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out numerator) || numerator <= 0 || numerator > long.MaxValue / 2) return false;
            if (parts.Length == 2 && (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator) || denominator <= 0)) return false;
            doubled = (numerator * 2).ToString(CultureInfo.InvariantCulture);
            if (denominator != 1) doubled += "/" + denominator.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool SupportsHigh10(string ffmpeg)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, "-hide_banner -h encoder=libx264");
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process process = new Process())
                {
                    process.StartInfo = psi; process.Start();
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } return false; }
                    return process.ExitCode == 0 && (stdout + stderr).IndexOf("yuv420p10le", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private static double ParseFps(string value)
        {
            string[] parts = value.Split('/');
            double n, d = 1.0;
            if (parts.Length < 1 || parts.Length > 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return 0.0;
            if (parts.Length == 2 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0.0;
            return d > 0.0 && n > 0.0 ? n / d : 0.0;
        }

        // Matches StudioBridge :AUTO_CINEMA_FPS, including its exact-rate cases and 0.25 tolerance.
        private static bool TryResolveAutoFps(string source, out string target, out string filter, out string suffix)
        {
            target = source; filter = ""; suffix = "";
            double rate = ParseFps(source);
            if (rate <= 0.0 || double.IsNaN(rate) || double.IsInfinity(rate)) return false;
            bool same23976 = source == "24000/1001";
            bool same24 = source == "24/1" || source == "24";
            bool family23976 = same23976 || source == "30000/1001" || source == "48000/1001" || source == "60000/1001" || source == "120000/1001";
            bool family24 = same24 || source == "25/1" || source == "25" || source == "30/1" || source == "30" || source == "48/1" || source == "48" || source == "50/1" || source == "50" || source == "60/1" || source == "60" || source == "100/1" || source == "100" || source == "120/1" || source == "120";
            if (!family23976 && !family24)
            {
                double best = double.MaxValue;
                foreach (double known in new double[]{23.976023976,29.970029970,47.952047952,59.940059940,119.880119880})
                { double distance = Math.Abs(rate - known); if (distance < best) { best = distance; family23976 = true; family24 = false; } }
                foreach (double known in new double[]{24,25,30,48,50,60,100,120})
                { double distance = Math.Abs(rate - known); if (distance < best) { best = distance; family23976 = false; family24 = true; } }
                if (best > 0.25) { family23976 = false; family24 = false; }
            }
            if (family23976) { target = "24000/1001"; suffix = "_23976p"; if (!same23976) filter = "fps=24000/1001,"; }
            else if (family24) { target = "24"; suffix = "_24p"; if (!same24) filter = "fps=24,"; }
            return true;
        }

        private static List<string> BuildHdrToSdrArgs(string input, string output, MediaProbeInfo info, string tonemap)
        {
            return SharedHdrPreprocessCore.BuildToSdr(input,output,info,tonemap,true);
        }

        private static List<string> BuildEncodeArgs(string input, string grainInput, string filter, string output, string preset, bool highMotion, long bitrate, long max, long buf, string fps, string duration, bool plateMode, bool autoFps, bool high10, bool mkv, bool deinterlace, int passNumber, string passlog, bool finalOutput, bool svp, bool grainEnabled, IDictionary<string,string> audioState, MediaProbeInfo audioInfo)
        {
            List<string> a = new List<string>{"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-y"};
            if (plateMode || filter.Contains("libplacebo=") || filter.Contains("fruc_vulkan=")) { a.Add("-init_hw_device"); a.Add(VulkanDevicePolicy.Spec("vk")); a.Add("-filter_hw_device"); a.Add("vk"); }
            else if (filter.Contains("bwdif_vulkan=")) { a.Add("-init_hw_device"); a.Add(VulkanDevicePolicy.Spec("deintvk")); a.Add("-filter_hw_device"); a.Add("deintvk"); }
            a.Add("-i"); a.Add(input);
            if (plateMode)
            {
                a.Add("-stream_loop"); a.Add("-1");
                a.Add("-t"); a.Add(duration);
                a.Add("-i"); a.Add(grainInput);
            }
            a.Add("-filter_complex"); a.Add(filter);
            a.Add("-map"); a.Add("[vout]");
            if (finalOutput)
            {
                a.Add("-map"); a.Add("0:a?");
                if (mkv) { a.Add("-map"); a.Add("0:s?"); a.Add("-map"); a.Add("0:t?"); }
                a.Add("-map_metadata"); a.Add("0"); a.Add("-map_chapters"); a.Add("0");
            }
            else a.Add("-an");
            if(CpuEncoderCore.IsNewCpu(audioState))CpuEncoderCore.AddEncoderArgs(a,audioState,grainEnabled,bitrate,max,buf,mkv,fps);
            else {
            a.Add("-c:v"); a.Add("libx264"); a.Add("-profile:v"); a.Add(high10 ? "high10" : "high"); a.Add("-pix_fmt"); a.Add(high10 ? "yuv420p10le" : "yuv420p");
            a.Add("-preset"); a.Add(preset); SharedGrainCore.AddX264Tune(a,SharedGrainCore.GrainTune(audioState,"FG_X264_GRAIN_TUNE",grainEnabled));
            if (highMotion) { a.Add("-b_strategy"); a.Add("2"); }
            a.Add("-b:v"); a.Add(bitrate.ToString(CultureInfo.InvariantCulture) + "k");
            a.Add("-maxrate"); a.Add(max.ToString(CultureInfo.InvariantCulture) + "k");
            a.Add("-bufsize"); a.Add(buf.ToString(CultureInfo.InvariantCulture) + "k");
            if (passNumber > 0)
            {
                a.Add("-pass"); a.Add(passNumber.ToString(CultureInfo.InvariantCulture));
                a.Add("-passlogfile"); a.Add(passlog);
            }
            }
            if (!filter.Contains("fruc_vulkan=") && (autoFps || deinterlace || plateMode || passNumber > 0)) { a.Add("-r"); a.Add(fps); }
            bool svtHdr=(Eq(audioState,"FG_MODE","SVT")||Eq(audioState,"FG_MODE","X265"))&&audioInfo!=null&&audioInfo.IsHdr&&!SharedGrainCore.CpuHdrToSdr(audioInfo,audioState);
            bool sourceClock=svtHdr&&!autoFps&&!deinterlace&&!filter.Contains("fruc_vulkan=");
            a.Add("-fps_mode:v"); a.Add(sourceClock||filter.Contains("fruc_vulkan=") ? "passthrough" : "cfr");
            if(sourceClock)a.AddRange(new[]{"-enc_time_base:v","demux"});
            if(svtHdr)SvtHdr10Core.AddColorArguments(a,audioInfo);
            if (plateMode && !string.IsNullOrEmpty(duration)) { a.Add("-t"); a.Add(duration); }
            else if (passNumber > 0 && !string.IsNullOrEmpty(duration)) { a.Add("-t"); a.Add(duration); }
            if (finalOutput)
            {
                if (mkv) { a.Add("-c:a"); a.Add("copy"); a.Add("-c:s"); a.Add("copy"); a.Add("-c:t"); a.Add("copy"); }
                else { a.Add("-c:a"); a.Add("aac"); a.Add("-b:a"); a.Add("256k"); a.Add("-movflags"); a.Add("+faststart"); }
                a.Add(output);
            }
            else { a.Add("-f"); a.Add("null"); a.Add("NUL"); }
            if (svp) {
                ConvertToPipeInput(a, input);
                if(!NvencInterpolationCore.Enabled(audioState))SharedInterpolationEncodeCore.NormalizeRenderedCadence(a,fps);
                a.InsertRange(a.Count-1,new[]{"-enc_time_base:v",fps.Contains("/")?fps.Split('/')[1]+"/"+fps.Split('/')[0]:"1/"+fps});
            }
            if(finalOutput)AudioOutputCore.Apply(a,audioState,audioInfo,mkv,false,false);
            return a;
        }

        private static bool IsTransportInput(string input)
        {
            string extension=Path.GetExtension(input);
            return extension.Equals(".ts",StringComparison.OrdinalIgnoreCase)||extension.Equals(".m2ts",StringComparison.OrdinalIgnoreCase)||extension.Equals(".mts",StringComparison.OrdinalIgnoreCase);
        }

        private static void ConvertToPipeInput(List<string> args, string input)
        {
            int index = args.IndexOf("-i");
            if (index < 0 || index + 1 >= args.Count || args[index + 1] != input)
                throw new InvalidOperationException("OpenSVPFlow x264 input mapping changed");
            args.InsertRange(index, new string[]{"-f", "yuv4mpegpipe"});
            args[index + 3] = "pipe:0";
            args.InsertRange(index + 4, new string[]{"-i", input});
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == "0:a?") args[i] = "1:a?";
                if (args[i] == "0:s?") args[i] = "1:s?";
                if (args[i] == "0:t?") args[i] = "1:t?";
                if (args[i] == "-map_metadata" || args[i] == "-map_chapters")
                    if (i + 1 < args.Count && args[i + 1] == "0") args[i + 1] = "1";
            }
        }

        private static BridgeExecutionStage MakeStage(string ffmpeg, string workingDirectory, IList<string> args, double duration, int current, int total, string text)
        {
            BridgeExecutionStage stage = new BridgeExecutionStage();
            stage.ToolPath = ffmpeg;
            stage.WorkingDirectory = workingDirectory;
            stage.DirectArguments = JoinArgs(args);
            stage.OriginalArguments = new List<string>(args);
            stage.OriginalArguments = new List<string>(args);
            int frucFilterIndex = args.IndexOf("-filter_complex");
            if (frucFilterIndex >= 0 && frucFilterIndex + 1 < args.Count && args[frucFilterIndex + 1].Contains("fruc_vulkan="))
                stage.FrucArguments = new List<string>(args);
            stage.ProgressDurationSeconds = duration > 0.0 ? duration : 0.0;
            stage.StageCurrent = current;
            stage.StageTotal = total;
            stage.StageText = text;
            return stage;
        }



        private static string BuildX264FileSuffix(string preset, string passMode)
        {
            string suffix = preset == "medium" ? "_MEDIUM" : preset == "slow" ? "_SLOW" : "";
            if (passMode == "2PASS") suffix += "_2PASS";
            else if (passMode == "3PASS") suffix += "_3PASS";
            return suffix;
        }

        private static bool TryResolveTempRoot(string input, IDictionary<string,string> state, out string root, out string reason)
        {
            root = "";
            reason = "";
            string mode = Get(state, "FG_TEMP_MODE").ToUpperInvariant();
            if (mode == "SYSTEM") root = Path.Combine(Path.GetTempPath(), "FilmGrain_Studio");
            else if (mode == "CUSTOM")
            {
                string custom = Get(state, "FG_TEMP_CUSTOM_DIR");
                if (string.IsNullOrWhiteSpace(custom)) { reason = "custom temporary directory is not configured"; return false; }
                root = Path.Combine(custom, "FilmGrain_Studio");
            }
            else root = Path.GetDirectoryName(input);
            try { Directory.CreateDirectory(root); }
            catch (Exception ex) { reason = "could not create temporary directory: " + ex.Message; return false; }
            return true;
        }

        private static void AddPasslogTemps(List<string> files, string prefix)
        {
            string[] suffixes = new string[]{"-0.log","-0.log.mbtree","-0.mbtree","-0.log.temp","-0.log.mbtree.temp","-0.mbtree.temp",".log",".log.mbtree",".mbtree",".log.temp",".log.mbtree.temp",".mbtree.temp"};
            foreach (string suffix in suffixes) files.Add(prefix + suffix);
        }

        private static bool TryBuildColorFilter(IDictionary<string,string> state, out string filter, out string suffix, out string reason)
        {
            filter = ""; suffix = ""; reason = "";
            if (Get(state, "FG_COLOR_ENABLED") != "1") return true;
            double contrast, brightness, saturation, gamma;
            if (!TryReadDouble(state, "FG_COLOR_CONTRAST", out contrast) || !TryReadDouble(state, "FG_COLOR_BRIGHTNESS", out brightness) || !TryReadDouble(state, "FG_COLOR_SATURATION", out saturation) || !TryReadDouble(state, "FG_COLOR_GAMMA", out gamma))
            { reason = "invalid color correction value"; return false; }
            filter = "eq=contrast=" + F2(contrast) + ":brightness=" + F2(brightness) + ":saturation=" + F2(saturation) + ":gamma=" + F2(gamma) + ",";
            if (Get(state, "FG_COLOR_BLACK_WHITE") == "1") filter += "hue=s=0,";
            suffix = "_CC";
            return true;
        }

        private static string BuildLutNamingSuffix(IDictionary<string,string> state)
        {
            string path = Get(state, "FG_LUT_PATH");
            if (string.IsNullOrWhiteSpace(path)) return "";
            int strength = NormalizeLutStrength(Get(state, "FG_LUT_STRENGTH"));
            return "_LUT_" + Path.GetFileNameWithoutExtension(path) + "_" + strength.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryPrepareLut(string ffmpeg, IDictionary<string,string> state, out string compatPath, out string compatName, out string opacity, out string reason)
        {
            compatPath = ""; compatName = ""; opacity = ""; reason = "";
            string path = Get(state, "FG_LUT_PATH");
            if (string.IsNullOrWhiteSpace(path)) return true;
            if (!File.Exists(path)) { reason = "selected LUT is missing: " + path; return false; }
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) { reason = "cannot read selected LUT: " + ex.Message; return false; }
            bool has3d = false;
            List<string> output = new List<string>(lines.Length + 2);
            foreach (string line in lines)
            {
                if (line.TrimStart().StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase)) has3d = true;
                string trimmed = line.Trim();
                string[] parts = trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && string.Equals(parts[0], "LUT_3D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add("DOMAIN_MIN " + parts[1] + " " + parts[1] + " " + parts[1]);
                    output.Add("DOMAIN_MAX " + parts[2] + " " + parts[2] + " " + parts[2]);
                }
                else output.Add(line);
            }
            if (!has3d) { reason = "selected CUBE does not contain LUT_3D_SIZE"; return false; }
            string tempDir = Path.Combine(Path.GetTempPath(), "FilmGrain_Studio", "NativeLut");
            try
            {
                Directory.CreateDirectory(tempDir);
                compatName = "__FGS_LUT_" + Guid.NewGuid().ToString("N") + ".cube";
                compatPath = Path.Combine(tempDir, compatName);
                File.WriteAllLines(compatPath, output.ToArray(), Encoding.ASCII);
                int lutPercent=NormalizeLutStrength(Get(state,"FG_LUT_STRENGTH"));
                opacity=LutStrengthCore.BakeFile(compatPath,lutPercent)?"1.00":(lutPercent/100.0).ToString("0.00",CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                TryDelete(compatPath); compatPath = ""; compatName = "";
                reason = "could not prepare FFmpeg-compatible LUT: " + ex.Message;
                return false;
            }
            string validationError;
            if (!ValidateLut(ffmpeg, tempDir, compatName, out validationError))
            {
                TryDelete(compatPath); compatPath = ""; compatName = "";
                reason = "FFmpeg cannot initialize selected LUT" + (string.IsNullOrWhiteSpace(validationError) ? "" : ": " + validationError);
                return false;
            }
            // Opacity was resolved when preparing the LUT; baked tables use full strength.
            return true;
        }

        private static bool ValidateLut(string ffmpeg, string workingDirectory, string compatName, out string error)
        {
            error = "";
            try
            {
                string filter = "format=gbrp16le,lut3d=file='" + EscapeFilterPath(compatName) + "':interp=tetrahedral";
                List<string> a = new List<string>{"-hide_banner","-v","error","-f","lavfi","-i","color=c=gray:s=32x32:d=0.04","-vf",filter,"-frames:v","1","-f","null","NUL"};
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ffmpeg; psi.Arguments = JoinArgs(a); psi.WorkingDirectory = workingDirectory;
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
                using (Process p = new Process())
                {
                    p.StartInfo = psi; p.Start();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } error = "validation timed out"; return false; }
                    string stdOut = p.StandardOutput.ReadToEnd(); string stdErr = p.StandardError.ReadToEnd();
                    if (p.ExitCode != 0) { error = FirstLine(string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr); return false; }
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string ResolvePlateTag(string plate, string fallback)
        {
            string n = Path.GetFileName(plate);
            if (string.Equals(n, "CT 35mm Grain 4K DCI.mov", StringComparison.OrdinalIgnoreCase)) return "CT35";
            if (string.Equals(n, "Filmgrain_4KDCI_35mm_24fps.mov", StringComparison.OrdinalIgnoreCase)) return "35L";
            if (string.Equals(n, "Filmgrain_4KDCI_Super_35mm_24fps.mov", StringComparison.OrdinalIgnoreCase)) return "S35L";
            if (string.Equals(n, "Filmgrain_4KDCI_16mm_24fps.mov", StringComparison.OrdinalIgnoreCase)) return "16L";
            if (string.Equals(n, "Filmgrain_4KDCI_Super_16mm_24fps.mov", StringComparison.OrdinalIgnoreCase)) return "S16L";
            if (string.Equals(n, "Filmgrain_4KDCI_8mm_24fps.mov", StringComparison.OrdinalIgnoreCase)) return "8L";
            if (string.Equals(n, "Filmgrain_4KDCI_35mm_24fps_Heavy.mov", StringComparison.OrdinalIgnoreCase)) return "35H";
            if (string.Equals(n, "Filmgrain_4KDCI_Super_35mm_24fps_Heavy.mov", StringComparison.OrdinalIgnoreCase)) return "S35H";
            if (string.Equals(n, "Filmgrain_4KDCI_16mm_24fps_Heavy.mov", StringComparison.OrdinalIgnoreCase)) return "16H";
            if (string.Equals(n, "Filmgrain_4KDCI_Super_16mm_24fps_Heavy.mov", StringComparison.OrdinalIgnoreCase)) return "S16H";
            if (string.Equals(n, "Filmgrain_4KDCI_8mm_24fps_Heavy.mov", StringComparison.OrdinalIgnoreCase)) return "8H";
            return string.IsNullOrWhiteSpace(fallback) ? "SCAN" : fallback;
        }

        private static int NormalizeLutStrength(string value) { int v; if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return 75; return v == 25 || v == 50 || v == 100 ? v : 75; }
        private static bool TryReadDouble(IDictionary<string,string> s, string k, out double v) { return double.TryParse(Get(s, k), NumberStyles.Float, CultureInfo.InvariantCulture, out v); }
        private static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        private static string EscapeFilterPath(string value) { return (value ?? "").Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'"); }
        // Match StudioBridge's two-level escaping for libplacebo custom_shader_path.
        private static bool TryPrepareFgsimHook(string appRoot, string hook, string preset, out string reason)
        {
            string prepared; return FgsimTextureCore.TryPrepareHook(appRoot, preset, out prepared, out reason);
        }

        private static string EscapeFgsimShaderPath(string value)
        {
            string path = (value ?? "").Replace("\\", "/").Replace(":", "\\:").Replace(",", "\\,").Replace(";", "\\;").Replace("[", "\\[").Replace("]", "\\]");
            return "'" + path.Replace("'", "'" + new string('\\', 3) + "''") + "'";
        }
        private static int Even(int v) { return ((v + 1) / 2) * 2; }
        private static string Get(IDictionary<string,string> s, string k) { string v; return s.TryGetValue(k, out v) ? (v ?? "") : ""; }
        private static bool Eq(IDictionary<string,string> s, string k, string v) { return string.Equals(Get(s, k), v, StringComparison.OrdinalIgnoreCase); }
        private static string GetFfmpegDir(string root)
        {
            string cfg = Path.Combine(root, "FilmGrain_Config.ini"); if (!File.Exists(cfg)) cfg = Path.Combine(root, "FilmGrain_Config.default.ini");
            foreach (string line in File.ReadAllLines(cfg)) { int p = line.IndexOf('='); if (p > 0 && line.Substring(0, p).Trim().Equals("FFMPEG_DIR", StringComparison.OrdinalIgnoreCase)) return line.Substring(p + 1).Trim().Trim('"'); }
            return "";
        }
        private static string ResolveOutputDirectory(string input, IDictionary<string,string> s) { if (Eq(s, "FG_OUTPUT_MODE", "CUSTOM") && !string.IsNullOrWhiteSpace(Get(s, "FG_OUTPUT_CUSTOM_DIR"))) return Get(s, "FG_OUTPUT_CUSTOM_DIR"); return Path.GetDirectoryName(input); }
        private static string JoinArgs(IEnumerable<string> args) { StringBuilder b = new StringBuilder(); foreach (string x in args) { if (b.Length > 0) b.Append(' '); b.Append(Quote(x ?? "")); } return b.ToString(); }
        private static string Quote(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new char[]{' ','\t','\n','\v','"'}) < 0) return s;
            StringBuilder b = new StringBuilder(); b.Append('"'); int bs = 0;
            foreach (char c in s) { if (c == '\\') { bs++; continue; } if (c == '"') { b.Append('\\', bs * 2 + 1); b.Append('"'); bs = 0; continue; } if (bs > 0) { b.Append('\\', bs); bs = 0; } b.Append(c); }
            if (bs > 0) b.Append('\\', bs * 2); b.Append('"'); return b.ToString();
        }
        private static void TryDelete(string path) { try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { } }
        private static string FirstLine(string value) { if (string.IsNullOrWhiteSpace(value)) return ""; using (StringReader r = new StringReader(value.Trim())) return r.ReadLine() ?? ""; }
    }
}

namespace FilmGrainStudioPreview
{
    // Final encoder boundary shared by GUI and frozen command-line requests.
    internal static class CpuEncoderCore
    {
        private static readonly Dictionary<string,string> cache=new Dictionary<string,string>();
        internal static bool IsNewCpu(IDictionary<string,string> s) {string m=Get(s,"FG_MODE","");return m=="SVT"||m=="X265";}
        private static string Get(IDictionary<string,string> s,string k,string d) {string v;return s!=null&&s.TryGetValue(k,out v)?v:d;}
        internal static bool IsCrf(IDictionary<string,string> s) {return Get(s,"FG_NVENC_RATE_CONTROL","VBR")=="CQ";}
        internal static string Preset(IDictionary<string,string> s)
        {
            bool svt=Get(s,"FG_MODE","")=="SVT";string p=Get(s,svt?"FG_CPU_SVT_PRESET":"FG_CPU_X265_PRESET",svt?"6":"medium");
            int n;if(svt?(!int.TryParse(p,out n)||n<0||n>13):Array.IndexOf("ultrafast superfast veryfast faster fast medium slow slower veryslow placebo".Split(' '),p)<0)throw new InvalidDataException("Invalid CPU preset: "+p);
            return p;
        }
        internal const string HandBrakeParameters="enable-variance-boost=1:enable-qm=1:ac-bias=1:tf-strength=1:qp-scale-compress-strength=1:sharpness=1";
        internal static string SvtParameters(string scheme,bool grain)
        {
            if(scheme!="FGS"&&scheme!="HANDBRAKE")throw new InvalidDataException("Invalid SVT tuning scheme: "+scheme);
            return "hbd-mds=1:film-grain=0:tune="+(grain?"6":"0")+(scheme=="HANDBRAKE"?":"+HandBrakeParameters:"");
        }
        internal static void AddEncoderArgs(List<string> a,IDictionary<string,string> s,bool grain,long bitrate,long max,long buf,bool mkv,string fps="24")
        {
            bool svt=Get(s,"FG_MODE","")=="SVT";
            string rc=Get(s,"FG_NVENC_RATE_CONTROL","VBR");if(rc!="CQ"&&rc!="VBR")throw new InvalidDataException("Invalid CPU rate control: "+rc);
            string threaded=Get(s,"FG_CPU_X265_THREADED_ME","false");if(threaded!="true"&&threaded!="false")throw new InvalidDataException("Invalid threaded-me value");
            bool me=!svt&&threaded=="true";
            // x265 disables ThreadedME with VBV, and with fewer than 32 logical CPUs.
            if(me&&(!IsCrf(s)||Environment.ProcessorCount<32))throw new InvalidDataException("x265 threaded-me 需要 CRF 且至少 32 个逻辑处理器；当前组合会被 x265 自动关闭，请明确关闭此选项。");
            a.AddRange(new[]{"-c:v",svt?"libsvtav1":"libx265","-pix_fmt","yuv420p10le","-preset",Preset(s)});
            grain=SharedGrainCore.GrainTune(s,svt?"FG_CPU_SVT_GRAIN_TUNE":"FG_CPU_X265_GRAIN_TUNE",grain);
            if(svt)a.AddRange(new[]{"-svtav1-params",SvtParameters(Get(s,"FG_CPU_SVT_TUNING","FGS"),grain)});
            else {if(grain)a.AddRange(new[]{"-tune","grain"});if(me)a.AddRange(new[]{"-x265-params","threaded-me=1"});if(!mkv)a.AddRange(new[]{"-tag:v","hvc1"});}
            if(svt&&Get(s,"FG_CPU_SVT_TUNING","FGS")=="HANDBRAKE"){
                string[] rate=fps.Split('/');double numerator,denominator=1;
                if(rate.Length>2||!double.TryParse(rate[0],NumberStyles.Float,CultureInfo.InvariantCulture,out numerator)||(rate.Length==2&&!double.TryParse(rate[1],NumberStyles.Float,CultureInfo.InvariantCulture,out denominator))||numerator<=0||denominator<=0||double.IsNaN(numerator)||double.IsInfinity(numerator)||double.IsNaN(denominator)||double.IsInfinity(denominator)||numerator/denominator>1000)throw new InvalidDataException("Invalid SVT output frame rate: "+fps);
                a.AddRange(new[]{"-g",Math.Max(1,(int)Math.Round(numerator/denominator*10)).ToString(CultureInfo.InvariantCulture)});
            }
            if(IsCrf(s)){
                string q=Get(s,"FG_NVENC_CQ","");double quality;
                if(!NvencRateControlCore.TryQuality(svt?"AV1":"HEVC",q,out quality))throw new InvalidDataException("Invalid CPU CRF: "+q);
                a.AddRange(new[]{"-crf",q});
            } else {if(bitrate<=0)throw new InvalidDataException("Invalid CPU bitrate");a.AddRange(new[]{"-b:v",bitrate.ToString(CultureInfo.InvariantCulture)+"k"});}
        }
        internal static bool TryPreflight(string ffmpeg,IDictionary<string,string> s,out string reason)
        {
            reason="";
            try {
                var file=new FileInfo(ffmpeg);
                bool grain=SharedGrainCore.IsEnabled(s);var args=new List<string>{"-hide_banner","-nostdin","-f","lavfi","-i","testsrc2=size=64x64:rate=24","-frames:v","2","-an"};
                AddEncoderArgs(args,s,grain,1000,1500,3000,true);args.AddRange(new[]{"-f","null","-"});
                string key=file.FullName+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks+"|"+string.Join(" ",args.ToArray());
                lock(cache){string cached;if(cache.TryGetValue(key,out cached)){reason=cached;return reason.Length==0;}}
                string output;bool success=Probe(ffmpeg,args,out output);
                if(!success)reason="CPU 编码器实测失败："+output;
                else if(Get(s,"FG_MODE","")=="SVT"&&output.IndexOf("SVT-AV1-HDR",StringComparison.OrdinalIgnoreCase)<0)reason="当前 FFmpeg 未检测到 SVT-AV1-HDR。请使用对应构建，不自动替换为标准 SVT-AV1。";
                else if(output.IndexOf("Unknown option",StringComparison.OrdinalIgnoreCase)>=0||output.IndexOf("Error parsing option",StringComparison.OrdinalIgnoreCase)>=0)reason="CPU 参数未被当前构建接受："+output;
                lock(cache){cache[key]=reason;}return reason.Length==0;
            } catch(Exception ex){reason=ex.Message;return false;}
        }
        private static bool Probe(string tool,List<string> args,out string output)
        {
            var text=new StringBuilder();object gate=new object();
            using(var p=new Process()){
                p.StartInfo=new ProcessStartInfo(tool,Join(args)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                DataReceivedEventHandler receive=delegate(object sender,DataReceivedEventArgs e){if(e.Data!=null)lock(gate){if(text.Length<32000)text.AppendLine(e.Data);}};
                p.OutputDataReceived+=receive;p.ErrorDataReceived+=receive;p.Start();p.BeginOutputReadLine();p.BeginErrorReadLine();
                if(!p.WaitForExit(30000)){try{p.Kill();p.WaitForExit();}catch{}output="CPU probe timed out (30 s)";return false;}
                p.WaitForExit();lock(gate){output=text.ToString();}return p.ExitCode==0;
            }
        }
        private static string Join(List<string> args)
        {var a=new List<string>();foreach(string value in args)a.Add(SharedEncodePreviewCore.Quote(value));return string.Join(" ",a.ToArray());}
    }
}

namespace FilmGrainStudioPreview
{
    // Bounded first-access-unit proof, not a scan or promise about dynamic metadata.
    internal sealed class SvtHdr10Proof
    {
        internal Dictionary<string,long> Expected;
        internal int Range;
        internal MediaProbeInfo SourceInfo;
        internal void Verify(string ffmpeg,string path)
        {
            // Static metadata completeness is not a color-conversion veto.
            string ffprobe=Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe"),text,error;
            if(!SharedFrucCore.Run(ffprobe,"-v error -select_streams v:0 -show_entries stream=pix_fmt,color_range,color_primaries,color_transfer,color_space -of default=noprint_wrappers=1 "+SharedFrucCore.Quote(path),8000,out text,out error))
                throw new InvalidDataException("CPU HDR output stream probe failed: "+error);
            if(SourceInfo==null)return;
            foreach(var pair in new Dictionary<string,string>{{"color_range",SourceInfo.ColorRange},{"color_primaries",SourceInfo.ColorPrimaries},{"color_transfer",SourceInfo.ColorTransfer},{"color_space",SourceInfo.ColorSpace}}) {
                if(string.IsNullOrWhiteSpace(pair.Value)||pair.Value=="unknown"||pair.Value=="unspecified")continue;
                foreach(string line in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
                    if(line.StartsWith(pair.Key+"=",StringComparison.Ordinal)&&line!=pair.Key+"=unknown"&&line!=pair.Key+"=unspecified"&&line!=pair.Key+"=reserved"&&line!=pair.Key+"="+pair.Value)
                        throw new InvalidDataException("CPU HDR color signaling changed: "+line+" expected="+pair.Value);
            }
        }
    }

    internal static class SvtHdr10Core
    {
        private static string Get(IDictionary<string,string> s,string k){string v;return s.TryGetValue(k,out v)?v:"";}
        internal static bool CheckScope(MediaProbeInfo info,IDictionary<string,string> s,bool grain,bool interpolate,out string reason)
        {
            reason="";
            if(Get(s,"FG_MODE")!="SVT"&&Get(s,"FG_MODE")!="X265")reason="当前此 CPU 编码路线尚未接入 HDR 保留。";
            else if(grain&&SharedGrainCore.Engine(s)=="FGSIM")reason="当前 CPU FGSIM 使用 SDR shader 路线；请选 AUTO/转SDR，或关闭 FGSIM 后保留 HDR。";
            return reason.Length==0;
        }
        internal static string ColorFilter(MediaProbeInfo info)
        {
            var fields=new List<string>();
            foreach(var pair in ColorFields(info))if(Known(pair.Value))fields.Add(pair.Key+"="+pair.Value);
            return ",format=yuv420p10le"+(fields.Count==0?"":",setparams="+string.Join(":",fields.ToArray()));
        }
        private static bool Known(string v){return !string.IsNullOrWhiteSpace(v)&&v!="unknown"&&v!="unspecified"&&v!="reserved";}
        private static Dictionary<string,string> ColorFields(MediaProbeInfo info)
        {return new Dictionary<string,string>{{"range",info.ColorRange},{"color_primaries",info.ColorPrimaries},{"color_trc",info.ColorTransfer},{"colorspace",info.ColorSpace}};}
        internal static void AddColorArguments(List<string> args,MediaProbeInfo info)
        {
            foreach(var pair in new Dictionary<string,string>{{"-color_range",info.ColorRange},{"-color_primaries",info.ColorPrimaries},{"-color_trc",info.ColorTransfer},{"-colorspace",info.ColorSpace}})
                if(Known(pair.Value)){args.Add(pair.Key);args.Add(pair.Value);}
        }
        internal static void Before(BridgeExecutionStage stage,Func<string> check)
        {
            var prior=stage.BeforeRunPreparation;
            stage.BeforeRunPreparation=delegate {string first=prior==null?"":prior();return first+Environment.NewLine+check();};
        }
        internal static void Attach(BridgeExecutionRequest request,string input,MediaProbeInfo info)
        {
            if(request.DirectStages==null||request.DirectStages.Count==0)throw new InvalidDataException("CPU HDR encode stage is missing");
            var proof=new SvtHdr10Proof{Range=info.ColorRange=="pc"?1:0,SourceInfo=info};request.Hdr10Proof=proof;
            bool x265=Get(request.Environment,"FG_MODE")=="X265";
            string encoder=x265?"x265":"SVT";
            string staticParameters=x265?"cll=0":"";
            if(request.DirectStages.Count==1&&!info.IsInterlaced&&Get(request.Environment,"FG_SVP_INTERPOLATE")!="1")
                SharedOrdinaryTimelineCore.Attach(request.DirectStages[0],Path.Combine(Path.GetDirectoryName(request.ToolPath),"ffprobe.exe"),input);
            Before(request.DirectStages[0],delegate {
                proof.Expected=new Dictionary<string,long>();string notice="源静态信息不参与HDR放行判断。";
                if(NvencHdr10StreamCore.HasSourceHdr10(info))try {
                    string trace=Trace(request.ToolPath,input);
                    proof.Expected=ReadHevcAvailable(trace,proof.Range);
                    var parts=new List<string>();if(x265)parts.Add(proof.Expected.ContainsKey("max_cll")?"cll=1":"cll=0");
                    if(proof.Expected.ContainsKey("luminance_max")) {
                        Func<string,string> chroma=delegate(string key){return x265?Field(trace,key).ToString(CultureInfo.InvariantCulture):((decimal)Field(trace,key)/50000).ToString("0.00000",CultureInfo.InvariantCulture);};
                        Func<string,string> lum=delegate(string key){return x265?Field(trace,key).ToString(CultureInfo.InvariantCulture):((decimal)Field(trace,key)/10000).ToString("0.0000",CultureInfo.InvariantCulture);};
                        parts.Add((x265?"master-display=G(":"mastering-display=G(")+chroma("display_primaries_x[0]")+","+chroma("display_primaries_y[0]")+")B("+chroma("display_primaries_x[1]")+","+chroma("display_primaries_y[1]")+")R("+chroma("display_primaries_x[2]")+","+chroma("display_primaries_y[2]")+")WP("+chroma("white_point_x")+","+chroma("white_point_y")+")L("+lum("max_display_mastering_luminance")+","+lum("min_display_mastering_luminance")+")");
                    }
                    if(proof.Expected.ContainsKey("max_cll"))parts.Add((x265?"max-cll=":"content-light=")+proof.Expected["max_cll"]+","+proof.Expected["max_fall"]);
                    staticParameters=string.Join(":",parts.ToArray());
                    notice="MDCV="+(proof.Expected.ContainsKey("luminance_max")?"有":"无")+" CLLI="+(proof.Expected.ContainsKey("max_cll")?"有":"无")+"；存在的静态信息传给编码器，缺失项不编造。";
                }catch(Exception ex){notice="静态信息未取得，继续保留HDR信号："+ex.Message;}
                return "["+encoder+" HDR] "+notice;
            });
            foreach(var stage in request.DirectStages)Before(stage,delegate {
                if(staticParameters.Length==0)return "";
                var args=HardwareDecodeCore.SplitArguments(stage.DirectArguments);
                if(!args.Contains(x265?"libx265":"libsvtav1"))return "";
                string option=x265?"-x265-params":"-svtav1-params";int at=args.IndexOf(option);
                if(at>=0&&at+1<args.Count)args[at+1]+=":"+staticParameters;
                else args.InsertRange(args.Count-1,new[]{option,staticParameters});
                stage.DirectArguments=SharedFrucCore.JoinArguments(args);
                return "["+encoder+" HDR] 设置源静态信息参数，缺失值不编造；一次原编码，无额外媒体文件。";
            });
            var prior=request.PostRunValidation;
            request.PostRunValidation=delegate {string error=prior==null?null:prior();if(!string.IsNullOrEmpty(error))return error;proof.Verify(request.ToolPath,request.OutputFiles[0]);return null;};
            request.ExecutionNotice=(request.ExecutionNotice??"")+Environment.NewLine+"["+encoder+" HDR] 默认保留源HDR，不因硬解、源编码/位深/采样、帧率、画幅、缩放或静态metadata缺失而阻止。仅实际色彩冲突报错；不增加编码或整片中间文件。";
        }
        private static string Describe(Dictionary<string,long> values)
        {
            var parts=new List<string>();foreach(var kv in values)parts.Add(kv.Key+"="+kv.Value.ToString(CultureInfo.InvariantCulture));return string.Join("; ",parts.ToArray());
        }
        internal static long Field(string text,string name)
        {
            var matches=System.Text.RegularExpressions.Regex.Matches(text,@"(?m)^.*?\]\s+\d+\s+"+System.Text.RegularExpressions.Regex.Escape(name)+@"\s+[01]+\s*=\s*(\d+)\s*$");
            if(matches.Count==0)throw new InvalidDataException("HDR prefix field missing: "+name);
            long value=-1;
            foreach(System.Text.RegularExpressions.Match m in matches) {
                long next=long.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture);
                if(value>=0&&value!=next)throw new InvalidDataException("HDR prefix conflicting field: "+name);value=next;
            }
            return value;
        }
        private static void Require(string text,string key,long value)
        {if(Field(text,key)!=value)throw new InvalidDataException("HDR prefix unsupported "+key);}
        private static long ConvertUnits(long raw,long inputScale,long outputScale)
        {return checked((long)Math.Round((decimal)raw*outputScale/inputScale,0,MidpointRounding.AwayFromZero));}
        internal static Dictionary<string,long> ReadHevc(string text,int range)
        {
            Require(text,"chroma_format_idc",1);Require(text,"bit_depth_luma_minus8",2);Require(text,"bit_depth_chroma_minus8",2);
            var result=ReadHevcAvailable(text,range);
            foreach(string key in new[]{"max_cll","max_fall","luminance_max","luminance_min"})
                if(!result.ContainsKey(key))throw new InvalidDataException("HDR prefix field missing: "+key);
            return result;
        }
        internal static Dictionary<string,long> ReadHevcAvailable(string text,int range)
        {
            Require(text,"colour_primaries",9);Require(text,"transfer_characteristics",16);Require(text,"matrix_coefficients",9);Require(text,"video_full_range_flag",range);
            // Reject DV RPU/enhancement NAL and registered user data in this prefix.
            // Later access units are deliberately outside this bounded proof.
            if(System.Text.RegularExpressions.Regex.IsMatch(text,@"(?:nal_unit_type\s+[01]+\s*=\s*(?:62|63)|payload_type\s+[01]+\s*=\s*4)\s*(?:\r?\n|$)"))
                throw new InvalidDataException("SVT HDR10 prefix contains dynamic/registered metadata outside this route");
            var result=new Dictionary<string,long>();
            // HEVC ordering G,B,R; AV1 ordering R,G,B.
            bool mastering=System.Text.RegularExpressions.Regex.IsMatch(text,@"display_primaries_x\[0\]\s+[01]+\s*=\s*\d+");
            if(mastering) {
            int[] map={2,0,1};
            for(int i=0;i<3;i++)foreach(string xy in new[]{"x","y"}) {
                long raw=Field(text,"display_primaries_"+xy+"["+map[i]+"]");
                if(raw>50000)throw new InvalidDataException("HDR source chromaticity outside range");
                result["primary_chromaticity_"+xy+"["+i+"]"]=ConvertUnits(raw,50000,65536);
            }
            foreach(string xy in new[]{"x","y"}) {
                long raw=Field(text,"white_point_"+xy);if(raw>50000)throw new InvalidDataException("HDR source white point outside range");
                result["white_point_chromaticity_"+xy]=ConvertUnits(raw,50000,65536);
            }
            result["luminance_max"]=ConvertUnits(Field(text,"max_display_mastering_luminance"),10000,256);
            result["luminance_min"]=ConvertUnits(Field(text,"min_display_mastering_luminance"),10000,16384);
            }
            if(System.Text.RegularExpressions.Regex.IsMatch(text,@"max_content_light_level\s+[01]+\s*=\s*\d+")) {
                result["max_cll"]=Field(text,"max_content_light_level");result["max_fall"]=Field(text,"max_pic_average_light_level");
            }
            foreach(var kv in result)if(kv.Value<0||kv.Value>(kv.Key.StartsWith("luminance_",StringComparison.Ordinal)?uint.MaxValue:65535))
                throw new InvalidDataException("HDR value cannot be represented in AV1: "+kv.Key);
            return result;
        }
        internal static Dictionary<string,long> ReadAv1(string text)
        {
            Require(text,"seq_profile",0);Require(text,"high_bitdepth",1);Require(text,"mono_chrome",0);
            Require(text,"color_primaries",9);Require(text,"transfer_characteristics",16);Require(text,"matrix_coefficients",9);
            // metadata_type may legitimately contain both CLL(1) and MDCV(2).
            foreach(int type in new[]{1,2})if(!System.Text.RegularExpressions.Regex.IsMatch(text,@"metadata_type\s+[01]+\s*=\s*"+type+@"\s*(?:\r?\n|$)"))
                throw new InvalidDataException("AV1 HDR metadata OBU missing: type "+type);
            var result=new Dictionary<string,long>();
            for(int i=0;i<3;i++)foreach(string xy in new[]{"x","y"}) {string key="primary_chromaticity_"+xy+"["+i+"]";result[key]=Field(text,key);}
            foreach(string key in new[]{"white_point_chromaticity_x","white_point_chromaticity_y","luminance_max","luminance_min","max_cll","max_fall","color_range"})result[key]=Field(text,key);
            return result;
        }
        internal static string Trace(string ffmpeg,string path)
        {
            // One compressed access unit, no pixel decode or media output; separate bounded log.
            // Use a complete capped capture, never SharedFruc.Run's silently clipped 64 KiB.
            var text=new StringBuilder();bool overflow=false;object sync=new object();
            using(var process=new Process()) {
                process.StartInfo=new ProcessStartInfo(ffmpeg,"-hide_banner -nostdin -v info -i "+SharedFrucCore.Quote(path)+" -map 0:v:0 -c:v copy -frames:v 1 -bsf:v trace_headers -f null -") {
                    UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                DataReceivedEventHandler capture=delegate(object sender,DataReceivedEventArgs e) {
                    if(e.Data==null)return;lock(sync) {if(text.Length+e.Data.Length+2>4*1024*1024)overflow=true;else text.AppendLine(e.Data);}
                };
                process.OutputDataReceived+=capture;process.ErrorDataReceived+=capture;
                process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
                if(!process.WaitForExit(8000)) {try{process.Kill();process.WaitForExit();}catch{}throw new InvalidDataException("HDR prefix trace timed out");}
                process.WaitForExit();
                if(process.ExitCode!=0||overflow)throw new InvalidDataException("HDR prefix trace failed or exceeded complete-log limit");
            }
            return text.ToString();
        }
    }
}
