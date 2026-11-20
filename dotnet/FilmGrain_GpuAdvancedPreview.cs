using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    public sealed class GpuAdvancedPreviewResult
    {
        public int Version { get; set; }
        public string FilmBorderMode { get; set; }
        public string Action { get; set; }
        public double MotionAmplitude { get; set; }
        public double MotionSpeed { get; set; }
        public double MotionFrequency { get; set; }
        public string PixelMode { get; set; }
        public int PlateStrength { get; set; }
        public int FgsimPreset { get; set; }
        public int GrainStrength { get; set; }
        public bool GrainEnabled { get; set; }
        public int LutStrength { get; set; }
        public bool LutEnabled { get; set; }
    }

    internal sealed partial class MainForm
    {
        private bool gpuAdvancedPreviewBusy;
        private bool filmBorderSettingsBusy;
        private double? borderPreviewAmp, borderPreviewSpeed, borderPreviewFrequency;
        private string borderPreviewPendingMode;
        private readonly object gpuPreviewProcessLock = new object();
        private Process gpuPreviewProcess;
        private bool gpuPreviewOwnerClosed;

        private void CloseOwnedGpuPreview()
        {
            lock (gpuPreviewProcessLock) {
                gpuPreviewOwnerClosed = true;
                try { if (gpuPreviewProcess != null && !gpuPreviewProcess.HasExited) gpuPreviewProcess.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception ex) { System.Diagnostics.Trace.WriteLine(ex.Message); }
            }
        }
        private const string HdrGpuPreviewResource = "FGS.HdrGpuPreview.exe";

        // ProRes is intra-frame. A bounded seek into a short, regular plate
        // avoids decoding earlier repetitions of a ten-second grain loop.
        // Unknown timelines retain the original FFmpeg -stream_loop route.
        private static double ProbeGpuPlateSeekCycle(string ffmpeg, string platePath)
        {
            try {
                string ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe");
                if (!File.Exists(ffprobe)) return 0;
                string output,reason;
                if (!SharedFrucCore.Run(ffprobe,
                    "-v error -select_streams v:0 -show_entries stream=codec_name,pix_fmt,r_frame_rate,nb_frames:format=duration -of json " +
                    SharedFrucCore.Quote(platePath),15000,out output,out reason)) return 0;
                var root = new JavaScriptSerializer().DeserializeObject(output) as Dictionary<string,object>;
                if (root == null || !root.ContainsKey("streams") || !root.ContainsKey("format")) return 0;
                var streams = root["streams"] as object[];
                var format = root["format"] as Dictionary<string,object>;
                if (streams == null || streams.Length == 0 || format == null) return 0;
                var video = streams[0] as Dictionary<string,object>;
                if (video == null || !video.ContainsKey("codec_name") || !video.ContainsKey("pix_fmt") ||
                    !video.ContainsKey("nb_frames") || !video.ContainsKey("r_frame_rate") || !format.ContainsKey("duration") ||
                    !string.Equals(Convert.ToString(video["codec_name"],CultureInfo.InvariantCulture),"prores",StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Convert.ToString(video["pix_fmt"],CultureInfo.InvariantCulture),"yuv422p10le",StringComparison.OrdinalIgnoreCase)) return 0;
                long frames;
                if (!long.TryParse(Convert.ToString(video["nb_frames"],CultureInfo.InvariantCulture),
                    NumberStyles.Integer,CultureInfo.InvariantCulture,out frames) || frames<2 || frames>3600) return 0;
                string[] rate = Convert.ToString(video["r_frame_rate"],CultureInfo.InvariantCulture).Split('/');
                double numerator,denominator,duration;
                if (rate.Length!=2 || !double.TryParse(rate[0],NumberStyles.Float,CultureInfo.InvariantCulture,out numerator) ||
                    !double.TryParse(rate[1],NumberStyles.Float,CultureInfo.InvariantCulture,out denominator) ||
                    !double.TryParse(Convert.ToString(format["duration"],CultureInfo.InvariantCulture),
                        NumberStyles.Float,CultureInfo.InvariantCulture,out duration) ||
                    numerator<=0 || denominator<=0 || duration<1 || duration>60) return 0;
                double fps=numerator/denominator;
                double cycle=(frames-1)/fps;
                // Regular CFR MOV: reported duration should be one frame beyond
                // the last packet PTS, which is FFmpeg's stream-loop boundary.
                if (fps<1 || fps>240 || cycle<0.5 || double.IsNaN(cycle) || double.IsInfinity(cycle) ||
                    Math.Abs(duration-frames/fps)>0.1) return 0;
                return cycle;
            } catch { return 0; }
        }

        // Completely separate process: the stable FFmpeg/NVEncC encoding path is untouched.
        private void OpenGpuAdvancedPreview()
        {
            if (gpuAdvancedPreviewBusy) return;
            if (bridgeTaskCoordinator != null && bridgeTaskCoordinator.IsActive) {
                FgsMessageBox.Show(this,UiText("请等待当前编码任务结束。","Wait for the encoding task to finish."));return;
            }
            string input = GetPreviewVideoPath();
            MediaProbeInfo media;
            if (string.IsNullOrEmpty(input) || !mediaProbeCache.TryGetValue(input,out media)) {
                FgsMessageBox.Show(this,UiText("请先选择并完成视频探测。","Select and probe a video first."));return;
            }
            bool fullRange = media.ColorRange=="pc" || media.ColorRange=="jpeg";
            bool supportedColor = media.IsHdr
                ? media.ColorPrimaries=="bt2020" && media.ColorSpace=="bt2020nc"
                : !fullRange && (string.IsNullOrEmpty(media.ColorSpace) || media.ColorSpace=="bt709");
            if (!supportedColor) {
                FgsMessageBox.Show(this,UiText("GPU 预览支持有限范围 BT.709 SDR 和 BT.2020 PQ／HLG，均可预览逐行或隔行素材。",
                    "GPU preview supports limited-range BT.709 SDR and BT.2020 PQ/HLG, progressive or interlaced."));return;
            }
            if (media.Width < 2 || media.Height < 2) {
                FgsMessageBox.Show(this,UiText("视频分辨率无效。","Invalid video dimensions."));return;
            }
            if (cmbCodec.SelectedIndex == 4) {
                FgsMessageBox.Show(this,UiText("AV1 不重编码路线暂不接入独立 GPU 预览，请使用原有预览。",
                    "The AV1 copy route is not integrated with GPU preview yet."));return;
            }
            if (media.IsHdr && !UsesHdrColorPreview(media)) {
                FgsMessageBox.Show(this,UiText("当前编码组合会转 SDR，请使用原有预览查看该输出路线。",
                    "The current encode route converts to SDR. Use the original preview for that route."));return;
            }
            string parity = media.IsInterlaced
                ? SharedHdrPreprocessCore.ResolveDeinterlaceParity("parity=auto",media).Substring("parity=".Length)
                : "";
            string hdrSignal = !media.IsHdr ? "" : media.ColorTransfer=="smpte2084" ? "pq" : "hlg";
            if (media.IsHdr && fullRange) hdrSignal += "-full";
            string borderMode = config.Get("FILM_BORDER_MODE");
            bool borderRoute = media.IsHdr ? cmbCodec.SelectedIndex == 1 && !fullRange :
                cmbCodec.SelectedIndex == 1 || cmbCodec.SelectedIndex == 3;
            bool filmBorder = borderRoute &&
                !chkInterpolation.Checked && (borderMode == "DARK_STATIC" || borderMode == "DARK_WEAVE");
            string borderAsset = Path.Combine(appRoot,"Utils","_FilmBorders","cinema35_dark_1920x1080.png");
            if (filmBorder && !File.Exists(borderAsset)) {
                FgsMessageBox.Show(this,UiText("找不到胶片边框素材。","Film border asset is missing."));return;
            }
            string exe = ResolveGpuAdvancedPreviewExecutable();
            bool pixelSelection = !ParameterGrainSelected();
            int selectedMode = CurrentGrainMode();
            bool fgsimMode = pixelSelection && selectedMode == (cmbCodec.SelectedIndex==0?4:2);
            bool digitalMode = pixelSelection && selectedMode == (cmbCodec.SelectedIndex==0?3:0);
            bool newPixelMode = fgsimMode || digitalMode;
            string pixelMode = fgsimMode ? "FGSIM" : digitalMode ? "DIGITAL" : "";
            bool grainInitiallyEnabled = IsGrainEnabled();
            // The stored pixel mode ID for Grain Plate is 1 (also on SVT-AV1).
            bool plateMode = IsGrainEnabled() && !ParameterGrainSelected() && CurrentGrainMode() == 1;
            bool embeddedPreview = !filmBorder && (plateMode || (!newPixelMode && media.IsHdr)) && typeof(MainForm).Assembly.GetManifestResourceInfo(HdrGpuPreviewResource)!=null;
            if (exe.Length == 0 && !embeddedPreview) {
                FgsMessageBox.Show(this,UiText("未找到独立 GPU 预览程序。请将 fgs_gpu_preview.exe 放在 FGS\\GpuAdvancedPreview 目录，或设置 FGS_GPU_PREVIEW_EXE 环境变量。",
                    "GPU preview is missing. Place fgs_gpu_preview.exe in FGS\\GpuAdvancedPreview or set FGS_GPU_PREVIEW_EXE."));return;
            }
            string ffmpeg = Path.Combine(config.Get("FFMPEG_DIR"),"ffmpeg.exe");
            if (!File.Exists(ffmpeg)) { FgsMessageBox.Show(this,UiText("找不到 FFmpeg。","FFmpeg not found."));return; }

            bool hasAv1Grain = IsGrainEnabled() && ParameterGrainSelected() &&
                (SvtSelected()?CurrentGrainMode()-3:CurrentGrainMode()) != 1;
            bool hasGrain = hasAv1Grain || plateMode || newPixelMode;
            string platePath = plateMode ? selectedGrainPlatePath : "";
            if (plateMode && (string.IsNullOrEmpty(platePath) || !File.Exists(platePath))) {
                FgsMessageBox.Show(this,UiText("扫描颗粒文件不存在，请重新选择。","Grain Plate is missing. Select a valid plate."));return;
            }
            bool hasLut = !string.IsNullOrEmpty(selectedLutPath) && File.Exists(selectedLutPath);
            string gradeRecipe=config.Get(ColorGrade.ConfigKey);
            bool hasGrade=ReadStudioGrade().IsActive;
            if (!hasGrain && !hasLut && !hasGrade && !media.IsHdr && !filmBorder) {
                FgsMessageBox.Show(this,UiText("请选择 AV1 胶片预设／颗粒表、扫描颗粒、Digital Grain、FGSIM 或 3D LUT。",
                    "Select an AV1 preset/table, Grain Plate, Digital Grain, FGSIM or a 3D LUT."));return;
            }
            if (!hasGrain && IsGrainEnabled() &&
                FgsMessageBox.Show(this,UiText("当前颗粒类型尚不支持 GPU 实时合成；本次仅调整 LUT，颗粒设置保持不变。继续？",
                    "This grain mode is not supported in GPU preview. Adjust LUT only?"),Text,MessageBoxButtons.YesNo) != DialogResult.Yes) return;

            string lut = hasLut ? selectedLutPath : "";
            int grainMode = SvtSelected()?CurrentGrainMode()-3:CurrentGrainMode();
            string model = hasAv1Grain && grainMode == 2 ? selectedAv1GrainTable : "";
            if (hasAv1Grain && grainMode == 2 && string.IsNullOrEmpty(model)) {
                FgsMessageBox.Show(this,UiText("请先选中一个 AV1 颗粒表。","Select an AV1 grain table first."));return;
            }
            if (hasAv1Grain && !string.IsNullOrEmpty(model) && !File.Exists(model)) {
                FgsMessageBox.Show(this,UiText("颗粒表文件不存在。","Grain table is missing."));return;
            }
            int initialGrain = fgsimMode ? Math.Max(0,Math.Min(3,fgsimPresetIndex)) :
                digitalMode ? GrainControlCore.SnapFive(proceduralStrength) :
                plateMode ? scannedGrainStrengthPercent : Av1GrainStrengthPercent;
            int initialLut = GetLutStrength();
            bool initialLutOn = IsLutEffectEnabled();
            string[] formats = {"Classic35","Modern35","16mm","Super8","MaxMid"};
            string[] stocks = {"","-1","-2","-3"};
            int fmt = Math.Max(0,Math.Min(4,av1FormatIndex));
            int stock = Math.Max(0,Math.Min(3,av1StockIndex));
            string presetName = formats[fmt]+(fmt<3?stocks[stock]:"");
            bool originalIndependent = independentFiles;
            int originalCodec = cmbCodec.SelectedIndex;
            string originalPreset = presetName;
            int originalMode = grainMode;
            double duration;
            if (!double.TryParse(media.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out duration) ||
                double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0) duration = 0;
            if (plateMode && duration <= 0) {
                FgsMessageBox.Show(this,UiText("扫描颗粒实时预览需要视频时长信息，请重新探测。",
                    "Live Grain Plate preview requires a valid source duration. Reprobe the video."));return;
            }
            // Reuse the already-tested grain-preview seek policy: a random,
            // safe timestamp for long or very short clips alike.
            double seconds = NativeGrainPreviewForm.ChooseInitialSeconds(duration,
                new Random(Guid.NewGuid().GetHashCode()).NextDouble());
            // Keep source pixels for a genuine 1:1 GPU inspection (up to the prototype limit).
            int width = media.Width & ~1;
            int height = media.Height & ~1;
            if (height < 2) height = 2;
            if (height > 8192 || width > 8192) {FgsMessageBox.Show(this,UiText("预览分辨率超出原型限制。","Preview dimensions exceed the prototype limit."));return;}

            string previewRoot = appRoot;
            string temporary = Path.Combine(Path.GetTempPath(),"FilmGrain_Studio","GpuPreview_"+Guid.NewGuid().ToString("N"));
            gpuAdvancedPreviewBusy = true;
            btnGpuAdvancedPreview.Enabled = false;
            btnGpuAdvancedPreview.Text = UiText("准备 GPU 预览…","Preparing GPU preview...");
            var worker = new Thread(delegate() {
                GpuAdvancedPreviewResult applied = null;
                string failure = "";
                string decodeTiming = "";
                try {
                    if (media.Identity != null) media.Identity.RequireCurrent();
                    Directory.CreateDirectory(temporary);
                    string table = model;
                    if (hasAv1Grain && table.Length == 0) {
                        table = Path.Combine(temporary,"preset.tbl");
                        File.WriteAllText(table,Av1GrainStrengthCore.PresetTable(presetName),new UTF8Encoding(false));
                    }
                    string fgsimNoise = "", fgsimShader = "";
                    if (fgsimMode) {
                        string reason;
                        if (!FgsimTextureCore.TryEnsureLocal(previewRoot,out fgsimNoise,out reason))
                            throw new InvalidOperationException(reason);
                        fgsimShader = Path.Combine(temporary,"fgsim.glsl");
                        WriteFgsimPreviewShader(previewRoot,fgsimShader);
                    }
                    string frame = Path.Combine(temporary,"frame.yuv");
                    string result = Path.Combine(temporary,"result.json");
                    string[] rateParts = (media.AvgFrameRate ?? "").Split('/');
                    int rateNum, rateDen;
                    bool validRate = rateParts.Length == 2 &&
                        int.TryParse(rateParts[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out rateNum) &&
                        int.TryParse(rateParts[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out rateDen) &&
                        rateNum > 0 && rateNum <= 1000000 && rateDen > 0 && rateDen <= 1000000 &&
                        (double)rateNum/rateDen >= 1 && (double)rateNum/rateDen <= 240;
                    string fps = validRate ? media.AvgFrameRate : "30/1";
                    string plateFrame = Path.Combine(temporary,"plate.yuv");
                    double plateCycle = plateMode ? ProbeGpuPlateSeekCycle(ffmpeg,platePath) : 0;
                    long bytes = (long)width*height*3;
                    var ffmpegLog = new StringBuilder();
                    var ffArgs = new List<string> {"-hide_banner","-v","error","-y","-ss",seconds.ToString("0.######",CultureInfo.InvariantCulture),
                        "-i",input,"-map","0:v:0","-an","-sn","-dn","-frames:v","1","-vf",
                        (parity.Length==0?"":"bwdif=mode=send_frame:parity="+parity+":deint=all,")+
                        "scale="+width.ToString(CultureInfo.InvariantCulture)+":"+height.ToString(CultureInfo.InvariantCulture)+":flags=bicubic"+
                        (!media.IsHdr?"":fullRange?":in_range=full:out_range=full":":in_range=limited:out_range=limited")+",format=yuv420p10le",
                        "-f","rawvideo",frame};
                    // Start both independent FFmpeg decoders before waiting for
                    // either. Keep their own files, arguments and error buffers.
                    Thread plateWorker = null;
                    Exception plateFailure = null;
                    long plateDecodeMs = -1;
                    if (plateMode) {
                        plateWorker = new Thread(delegate() {
                            var plateWatch = Stopwatch.StartNew();
                            try {
                                var plateLog = new StringBuilder();
                                var plateArgs = new List<string> {"-hide_banner","-nostdin","-v","error","-y","-stream_loop","-1"};
                                double plateSeconds = plateCycle>0 ? seconds%plateCycle : seconds;
                                plateArgs.AddRange(new[]{"-ss",plateSeconds.ToString("0.######",CultureInfo.InvariantCulture),
                                    "-i",platePath,"-map","0:v:0","-an","-sn","-dn","-frames:v","1",
                                    "-vf","fps="+fps+",scale="+width.ToString(CultureInfo.InvariantCulture)+":"+
                                        height.ToString(CultureInfo.InvariantCulture)+":flags=bilinear,format=yuv420p10le",
                                    "-f","rawvideo",plateFrame});
                                int plateRc = NativeLibraryTools.RunProcessEnvironment(ffmpeg,plateArgs,temporary,null,120000,
                                    CancellationToken.None,delegate(string line) {
                                        lock(plateLog) {if(plateLog.Length<12000) plateLog.AppendLine(line);}
                                    });
                                if (plateRc != 0 || !File.Exists(plateFrame) || new FileInfo(plateFrame).Length != bytes)
                                    throw new InvalidDataException("扫描颗粒抽帧失败："+plateLog);
                            } catch (Exception ex) {plateFailure=ex;}
                            finally {plateWatch.Stop();plateDecodeMs=plateWatch.ElapsedMilliseconds;}
                        });
                        plateWorker.IsBackground = true;
                    }
                    var pairWatch = Stopwatch.StartNew();
                    if (plateWorker != null) plateWorker.Start();
                    int rc = -1;
                    var videoWatch = Stopwatch.StartNew();
                    try {
                        rc = NativeLibraryTools.RunProcessEnvironment(ffmpeg,ffArgs,temporary,null,120000,CancellationToken.None,
                            delegate(string line) {lock(ffmpegLog) {if(ffmpegLog.Length<12000)ffmpegLog.AppendLine(line);} });
                    } finally {
                        videoWatch.Stop();
                        if (plateWorker != null) plateWorker.Join();
                        pairWatch.Stop();
                        decodeTiming = "[GPU Preview] 初次抽帧：视频 "+videoWatch.ElapsedMilliseconds+" ms，颗粒 "+
                            (plateMode?plateDecodeMs.ToString(CultureInfo.InvariantCulture)+" ms":"无")+
                            "，并行总耗时 "+pairWatch.ElapsedMilliseconds+" ms"+Environment.NewLine;
                    }
                    if (rc != 0) throw new InvalidOperationException("FFmpeg 抽帧失败："+ffmpegLog);
                    if (plateFailure != null) throw plateFailure;
                    if (!File.Exists(frame) || new FileInfo(frame).Length != bytes)
                        throw new InvalidDataException("抽帧大小不匹配，未启动 GPU 原型。");
                    if (media.Identity != null) media.Identity.RequireCurrent();

                    var args = new List<string> {frame,width.ToString(CultureInfo.InvariantCulture),height.ToString(CultureInfo.InvariantCulture),
                        hasAv1Grain?table:"-","10","0",seconds.ToString("0.######",CultureInfo.InvariantCulture)};
                    if (hasGrade)
                    {
                        var signal=ColorGradeCore.Signal(media);
                        var transform=ColorGradeCore.Compose(ColorGrade.Parse(gradeRecipe),signal,initialLutOn?lut:"",initialLut);
                        string composed=Path.Combine(temporary,"grade.cube");
                        ColorGradeCore.WriteCube(composed,33,signal,transform);
                        args.Add(composed);
                    }
                    else if (hasLut) args.Add(lut);
                    args.AddRange(new[]{"--result",result,"--grain",initialGrain.ToString(CultureInfo.InvariantCulture),
                        "--lut",(hasGrade?100:initialLut).ToString(CultureInfo.InvariantCulture),"--grain-on",hasGrain && grainInitiallyEnabled?"1":"0",
                        "--lut-on",hasGrade||initialLutOn?"1":"0"});
                    if (hasGrade) args.AddRange(new[]{"--combined-grade","1"});
                    if (lang.Code!="zh-CN") args.AddRange(new[]{"--hint-en","1"});
                    if (newPixelMode) args.AddRange(new[]{"--pixel-mode",pixelMode});
                    if (fgsimMode) args.AddRange(new[]{"--fgsim-noise",fgsimNoise,"--fgsim-shader",fgsimShader});
                    if (digitalMode) args.AddRange(new[]{"--digital-noise",Path.Combine(temporary,"digital.yuv"),"--noise-ffmpeg",ffmpeg});
                    if (plateMode) args.AddRange(new[]{"--plate",platePath,"--plate-frame",plateFrame});
                    if (filmBorder) {
                        string borderRaw = Path.Combine(temporary,"film-border.rgba");
                        var borderArgs = new List<string> {"-hide_banner","-nostdin","-v","error","-y",
                            "-i",borderAsset,"-frames:v","1"};
                        if (media.IsHdr) borderArgs.AddRange(new[]{"-vf",
                            "format=gbrap16le,zscale=matrixin=gbr:primariesin=709:transferin=iec61966-2-1:rangein=full"+
                            ":matrix=gbr:primaries=2020:transfer="+media.ColorTransfer+":range=full:npl=100:threads=1"});
                        borderArgs.AddRange(new[]{"-pix_fmt",media.IsHdr?"rgba64le":"rgba","-f","rawvideo",borderRaw});
                        int borderRc = NativeLibraryTools.RunProcessEnvironment(ffmpeg,borderArgs,temporary,null,30000,CancellationToken.None,null);
                        if (borderRc != 0 || !File.Exists(borderRaw) || new FileInfo(borderRaw).Length != 1920L*1080*(media.IsHdr?8:4))
                            throw new InvalidDataException("Film border texture decode failed.");
                        args.AddRange(new[]{"--film-border",borderRaw,"--film-weave",borderMode=="DARK_WEAVE"?"1":"0"});
                    }
                    if (plateCycle>0) args.AddRange(new[]{"--plate-cycle",plateCycle.ToString("0.#########",CultureInfo.InvariantCulture)});
                    // grav1synth advances AV1 grain seeds per output frame. Use the
                    // probed nominal rate to reproduce its per-event seed sequence.
                    if (validRate || plateMode) args.AddRange(new[]{"--fps",fps});
                    if (duration>0) args.AddRange(new[]{"--video",input,"--ffmpeg",ffmpeg,
                        "--duration",Math.Min(86400,duration).ToString("0.######",CultureInfo.InvariantCulture)});
                    if (media.IsHdr) args.AddRange(new[]{"--source-color",hdrSignal});
                    if (parity.Length>0) args.AddRange(new[]{"--deinterlace",parity});
                    string child = exe;
                    string runtime = null;
                    if (embeddedPreview) {
                        child = Path.Combine(temporary,"fgs_gpu_preview.exe");
                        runtime = exe.Length>0?Path.GetDirectoryName(exe):Path.Combine(appRoot,"GpuAdvancedPreview");
                        using (var payload = typeof(MainForm).Assembly.GetManifestResourceStream(HdrGpuPreviewResource))
                        using (var output = File.Create(child)) payload.CopyTo(output);
                    }
                    string frameTiming = RunGpuAdvancedPreviewProcess(child,args,runtime);
                    if (plateMode) decodeTiming += "[GPU Preview] Grain Plate 定位："+
                        (plateCycle>0 ? "ProRes 快速循环定位（周期 "+plateCycle.ToString("0.######",CultureInfo.InvariantCulture)+" s）" :
                            "普通循环定位（未满足快速路径条件）")+Environment.NewLine;
                    if (frameTiming.Length > 0) decodeTiming += frameTiming;
                    if (media.Identity != null) media.Identity.RequireCurrent();
                    if (File.Exists(result)) {
                        string json = File.ReadAllText(result,Encoding.UTF8);
                        applied = new JavaScriptSerializer().Deserialize<GpuAdvancedPreviewResult>(json);
                        if (applied == null || applied.Version != (newPixelMode?3:plateMode?2:1) ||
                            (newPixelMode && !string.Equals(applied.PixelMode,pixelMode,StringComparison.Ordinal)) ||
                            (fgsimMode && (applied.FgsimPreset < 0 || applied.FgsimPreset > 3)) ||
                            (digitalMode && (applied.GrainStrength < 15 || applied.GrainStrength > 100 || applied.GrainStrength%5!=0)) ||
                            (plateMode ? !string.Equals(applied.PixelMode,"PLATE",StringComparison.Ordinal) ||
                                (applied.PlateStrength < 0 || applied.PlateStrength > 100) :
                                applied.GrainStrength < 0 || applied.GrainStrength > 100) ||
                            applied.LutStrength < 0 || applied.LutStrength > 100 || (!hasGrain && applied.GrainEnabled) ||
                            (!hasLut && !hasGrade && applied.LutEnabled)) throw new InvalidDataException("GPU 回传参数无效。");
                    }
                } catch (Exception ex) {failure = ex.Message;}
                finally {
                    try {if(Directory.Exists(temporary))Directory.Delete(temporary,true);} catch {}
                    try {BeginInvoke((MethodInvoker)delegate {
                        gpuAdvancedPreviewBusy = false;
                        btnGpuAdvancedPreview.Enabled = true;
                        btnGpuAdvancedPreview.Text = UiText("高级预览调整…","Advanced GPU Preview...");
                        if (decodeTiming.Length > 0) AppendLog(decodeTiming);
                        if (failure.Length > 0) {
                            AppendLog("[GPU Preview] "+failure+Environment.NewLine);
                            FgsMessageBox.Show(this,failure,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
                        } else if (applied != null) {
                            if (independentFiles != originalIndependent || !string.Equals(GetPreviewVideoPath(),input,StringComparison.OrdinalIgnoreCase) ||
                                cmbCodec.SelectedIndex != originalCodec ||
                                config.Get(ColorGrade.ConfigKey)!=gradeRecipe ||
                                (hasGrain && (originalMode != (SvtSelected()?CurrentGrainMode()-3:CurrentGrainMode()) ||
                                    (plateMode && !string.Equals(selectedGrainPlatePath,platePath,StringComparison.OrdinalIgnoreCase)) ||
                                    (hasAv1Grain && originalMode==2 && !string.Equals(selectedAv1GrainTable,model,StringComparison.OrdinalIgnoreCase)) ||
                                    (hasAv1Grain && originalMode==0 && !string.Equals(formats[Math.Max(0,Math.Min(4,av1FormatIndex))]+
                                        (av1FormatIndex<3?stocks[Math.Max(0,Math.Min(3,av1StockIndex))]:""),originalPreset,StringComparison.Ordinal)))) ||
                                (hasLut && !string.Equals(selectedLutPath,lut,StringComparison.OrdinalIgnoreCase))) {
                                FgsMessageBox.Show(this,UiText("预览期间视频、颗粒或 LUT 已改变，本次结果未应用。","The video, grain mode or LUT changed; result discarded."));return;
                            }
                            string appliedGrain = "未更改";
                            if (newPixelMode) {
                                int value = fgsimMode ? applied.FgsimPreset : applied.GrainStrength/5;
                                if (trackFilmGrainStrength == null || value < trackFilmGrainStrength.Minimum ||
                                    value > trackFilmGrainStrength.Maximum) {
                                    FgsMessageBox.Show(this,UiText("颗粒控件已改变，本次结果未应用。",
                                        "Grain control changed; result discarded."));return;
                                }
                                trackFilmGrainStrength.Value = value;
                                chkGrainEnabled.Checked = applied.GrainEnabled;
                                appliedGrain = applied.GrainEnabled ? pixelMode+" "+lblFilmGrainValue.Text : "关闭";
                            } else if (plateMode) {
                                if (applied.GrainEnabled && applied.PlateStrength >= 1) {
                                    if (trackFilmGrainStrength == null || trackFilmGrainStrength.Minimum > applied.PlateStrength ||
                                        trackFilmGrainStrength.Maximum < applied.PlateStrength) {
                                        FgsMessageBox.Show(this,UiText("扫描颗粒控件已改变，本次结果未应用。",
                                            "Grain Plate control changed; result discarded."));return;
                                    }
                                    trackFilmGrainStrength.Value = applied.PlateStrength;
                                    chkGrainEnabled.Checked = true;
                                    appliedGrain = "Grain Plate "+applied.PlateStrength+"%";
                                } else {chkGrainEnabled.Checked = false;appliedGrain = "关闭";}
                            } else if (hasAv1Grain) {
                                if (applied.GrainEnabled && applied.GrainStrength > 0) {
                                    if (av1StrengthControl == null) {
                                        FgsMessageBox.Show(this,UiText("颗粒强度控件已改变，本次结果未应用。","Grain control changed; result discarded."));return;
                                    }
                                    av1StrengthControl.Value = applied.GrainStrength;
                                    chkGrainEnabled.Checked = true;
                                    appliedGrain = applied.GrainStrength+"%";
                                } else {chkGrainEnabled.Checked = false;appliedGrain = "关闭";}
                            }
                            if (hasLut && !hasGrade) {
                                trackLutStrength.Value = applied.LutStrength;
                                chkLut.Checked = applied.LutEnabled && applied.LutStrength > 0;
                                UpdateLutUi();
                            }
                            AppendLog("[GPU Preview] 已应用：颗粒 "+appliedGrain+
                                "，LUT "+(hasLut&&!hasGrade?(applied.LutEnabled?applied.LutStrength+"%":"关闭"):"未更改")+Environment.NewLine);
                        }
                    });} catch (InvalidOperationException) {}
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }


        // Use the existing GPU preview renderer and controls in border-only mode.
        // Its own window previews HDR/PQ/HLG correctly, with 1:1 pan and frame seeking.
        private void OpenFilmBorderSettings()
        {
            if (gpuAdvancedPreviewBusy || filmBorderSettingsBusy) return;
            if (bridgeTaskCoordinator != null && bridgeTaskCoordinator.IsActive) {
                FgsMessageBox.Show(this, UiText("请等待当前编码结束。", "Wait for encoding to finish.")); return;
            }
            string input = GetPreviewVideoPath();
            MediaProbeInfo media;
            if (string.IsNullOrEmpty(input) || !mediaProbeCache.TryGetValue(input, out media)) {
                FgsMessageBox.Show(this, UiText("请先选择并探测一个视频。", "Select and probe a video first.")); return;
            }
            if (cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 4 ||
                (cmbCodec.SelectedIndex == 2 && CpuCodec() == "SVT")) {
                FgsMessageBox.Show(this, UiText("AV1 视频边框编码尚未接入。", "AV1 video borders are not integrated.")); return;
            }
            bool borderHdrPreserved = cmbCodec.SelectedIndex == 2 && CpuCodec() == "X265"
                ? !SharedGrainCore.CpuHdrToSdr(media,CaptureCpuHdrRouteState())
                : UsesHdrColorPreview(media);
            if (media.IsHdr && !borderHdrPreserved) {
                FgsMessageBox.Show(this, UiText("当前编码路线会将 HDR 转成 SDR，暂不支持边框 HDR 预览。",
                    "This encoding route converts HDR to SDR; HDR border preview is not available.")); return;
            }
            bool full = media.ColorRange == "pc" || media.ColorRange == "jpeg";
            bool colorOk = media.IsHdr
                ? !full && media.ColorPrimaries == "bt2020" && media.ColorSpace == "bt2020nc" &&
                    (media.ColorTransfer == "smpte2084" || media.ColorTransfer == "arib-std-b67")
                : !full && (string.IsNullOrEmpty(media.ColorSpace) || media.ColorSpace == "bt709");
            if (!colorOk || media.Width < 2 || media.Height < 2 ||
                media.Width > 8192 || media.Height > 8192) {
                FgsMessageBox.Show(this, UiText("当前素材的色彩格式或尺寸暂不支持边框 GPU 预览。",
                    "Source color format or size is not supported by this preview.")); return;
            }
            string asset = Path.Combine(appRoot, "Utils", "_FilmBorders", "cinema35_dark_1920x1080.png");
            string ffmpeg = Path.Combine(config.Get("FFMPEG_DIR"), "ffmpeg.exe");
            string exe = ResolveGpuAdvancedPreviewExecutable();
            if (!File.Exists(asset) || !File.Exists(ffmpeg) || exe.Length == 0) {
                FgsMessageBox.Show(this, UiText("胶片边框素材、FFmpeg 或 GPU 预览程序缺失。",
                    "Border asset, FFmpeg or GPU preview executable is missing.")); return;
            }
            string saved = borderPreviewPendingMode ?? config.Get("FILM_BORDER_MODE");
            FilmBorderChoice selectedChoice;string resolveError;
            string mode = FilmBorderCatalogCore.TryResolve(appRoot,saved,out selectedChoice,out resolveError) ? selectedChoice.Mode : "OFF";
            borderPreviewPendingMode=null;
            double amp=borderPreviewAmp ?? FilmBorderMotionCore.Parse(config.Get(FilmBorderMotionCore.AmpKey),0,3);
            double speed=borderPreviewSpeed ?? FilmBorderMotionCore.Parse(config.Get(FilmBorderMotionCore.SpeedKey),0.25,3);
            double frequency=borderPreviewFrequency ?? FilmBorderMotionCore.Parse(config.Get(FilmBorderMotionCore.FrequencyKey),0.25,3);
            borderPreviewAmp=borderPreviewSpeed=borderPreviewFrequency=null;
            int codec = cmbCodec.SelectedIndex;
            bool independent = independentFiles;
            string work = Path.Combine(Path.GetTempPath(), "FilmGrain_Studio",
                "FilmBorder_" + Guid.NewGuid().ToString("N"));
            gpuAdvancedPreviewBusy = filmBorderSettingsBusy = true;
            btnFilmBorder.Enabled = false;
            btnGpuAdvancedPreview.Enabled = false;
            var worker = new Thread(delegate() {
                string error = "", selected = null, action = null;
                double resultAmp=amp,resultSpeed=speed,resultFrequency=frequency;
                try {
                    if (media.Identity != null) media.Identity.RequireCurrent();
                    Directory.CreateDirectory(work);
                    double duration;
                    if (!double.TryParse(media.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out duration) ||
                        double.IsNaN(duration) || double.IsInfinity(duration) || duration < 0) duration = 0;
                    double seconds = duration > 0 ? NativeGrainPreviewForm.ChooseInitialSeconds(duration, 0.5) : 0;
                    int width = media.Width & ~1, height = media.Height & ~1;
                    string frame = Path.Combine(work, "frame.yuv");
                    string result = Path.Combine(work, "settings.json");
                    string parity = media.IsInterlaced
                        ? SharedHdrPreprocessCore.ResolveDeinterlaceParity("parity=auto", media).Substring("parity=".Length)
                        : "";
                    string filter = (parity.Length > 0 ? "bwdif=mode=send_frame:parity="+parity+":deint=all," : "") +
                        "scale="+width+":"+height+":flags=bicubic" +
                        (media.IsHdr?":in_range=limited:out_range=limited":"") + ",format=yuv420p10le";
                    var fargs = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y",
                        "-ss", seconds.ToString("0.######",CultureInfo.InvariantCulture), "-i", input,
                        "-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1", "-vf", filter,
                        "-f", "rawvideo", frame };
                    int rc=NativeLibraryTools.RunProcessEnvironment(ffmpeg,fargs,work,null,120000,CancellationToken.None,null);
                    if(rc!=0 || !File.Exists(frame) || new FileInfo(frame).Length!=(long)width*height*3)
                        throw new InvalidDataException("胶片边框预览抽帧失败。");
                    string choices = FilmBorderCatalogCore.CreatePreviewChoices(appRoot,ffmpeg,work,
                        media.IsHdr,media.ColorTransfer,CancellationToken.None);
                    var args=new List<string>{frame,width.ToString(CultureInfo.InvariantCulture),
                        height.ToString(CultureInfo.InvariantCulture),"-","10","0",
                        seconds.ToString("0.######",CultureInfo.InvariantCulture),
                        "--result",result,"--grain","0","--grain-on","0","--lut-on","0",
                        "--film-border",asset,"--film-border-settings","1","--film-border-mode",mode,
                        "--film-motion-amp",amp.ToString("0.###",CultureInfo.InvariantCulture),
                        "--film-motion-speed",speed.ToString("0.###",CultureInfo.InvariantCulture),
                        "--film-motion-frequency",frequency.ToString("0.###",CultureInfo.InvariantCulture),
                        "--border-choices-file",choices};
                    if(duration>0)args.AddRange(new[]{"--video",input,"--ffmpeg",ffmpeg,"--duration",
                        Math.Min(86400,duration).ToString("0.######",CultureInfo.InvariantCulture)});
                    if(media.IsHdr)args.AddRange(new[]{"--source-color",
                        media.ColorTransfer=="smpte2084"?"pq":"hlg"});
                    if(parity.Length>0)args.AddRange(new[]{"--deinterlace",parity});
                    RunGpuAdvancedPreviewProcess(exe,args,null);
                    if(File.Exists(result)) {
                        var applied=new JavaScriptSerializer().Deserialize<GpuAdvancedPreviewResult>(
                            File.ReadAllText(result,Encoding.UTF8));
                        FilmBorderChoice resolved;string why;
                        if(applied==null || applied.Version!=4 ||
                           !FilmBorderCatalogCore.TryResolve(appRoot,applied.FilmBorderMode,out resolved,out why))
                            throw new InvalidDataException("边框模式回传无效："+why);
                        selected=resolved.Mode;
                        action=applied.Action??"APPLY";
                        resultAmp=applied.MotionAmplitude;
                        resultSpeed=applied.MotionSpeed;
                        resultFrequency=applied.MotionFrequency;
                        if(resultAmp<0 || resultAmp>3 || resultSpeed<0.25 || resultSpeed>3 ||
                            resultFrequency<0.25 || resultFrequency>3 ||
                            double.IsNaN(resultAmp) || double.IsNaN(resultSpeed) || double.IsNaN(resultFrequency))
                            throw new InvalidDataException("投影运动参数无效。");
                    }
                } catch(Exception ex){error=ex.Message;}
                finally {
                    try {if(Directory.Exists(work))Directory.Delete(work,true);}catch {}
                    try {BeginInvoke((MethodInvoker)delegate {
                        gpuAdvancedPreviewBusy=filmBorderSettingsBusy=false;
                        btnFilmBorder.Enabled=true;
                        btnGpuAdvancedPreview.Enabled=true;
                        if(error.Length>0) {
                            AppendLog("[Film Border] "+error+Environment.NewLine);
                            FgsMessageBox.Show(this,error,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
                        } else if(selected!=null) {
                            if(action=="IMPORT" || action=="DELETE" || action=="EDIT") {
                                borderPreviewAmp=resultAmp;
                                borderPreviewSpeed=resultSpeed;
                                borderPreviewFrequency=resultFrequency;
                            }
                            if(action=="EDIT") {
                                borderPreviewPendingMode=selected;
                                EditClassicFilmText();
                                return;
                            }
                            if(action=="IMPORT") {
                                ImportFilmBorderFromPreview();
                                return;
                            }
                            if(action=="DELETE") {
                                DeleteFilmBorderFromPreview(selected);
                                return;
                            }
                            if(action!="APPLY")return;
                            if((bridgeTaskCoordinator!=null && bridgeTaskCoordinator.IsActive) ||
                               independentFiles!=independent || cmbCodec.SelectedIndex!=codec ||
                               !string.Equals(GetPreviewVideoPath(),input,StringComparison.OrdinalIgnoreCase)){
                                FgsMessageBox.Show(this,UiText("预览期间视频或任务已变化，未应用。",
                                    "Video or task changed during preview; no changes applied."));
                                return;
                            }
                            try {
                                config.Save(new Dictionary<string,string> {
                                    {"FILM_BORDER_MODE",selected},
                                    {FilmBorderMotionCore.AmpKey,resultAmp.ToString("0.###",CultureInfo.InvariantCulture)},
                                    {FilmBorderMotionCore.SpeedKey,resultSpeed.ToString("0.###",CultureInfo.InvariantCulture)},
                                    {FilmBorderMotionCore.FrequencyKey,resultFrequency.ToString("0.###",CultureInfo.InvariantCulture)}
                                });
                                UpdateFilmBorderButton();
                                AppendLog("[Film Border] "+selected+Environment.NewLine);
                                UpdateHdrRouteStatus(GetSelectedMediaInfo());
                            } catch(Exception ex){FgsMessageBox.Show(this,ex.Message);}
                        }
                    });}catch(InvalidOperationException){}
                }
            });
            worker.IsBackground=true;
            worker.Start();
        }

        private void UpdateFilmBorderButton()
        {
            if(btnFilmBorder==null)return;
            string mode=config.Get("FILM_BORDER_MODE");
            FilmBorderChoice choice;string error;
            if(!FilmBorderCatalogCore.TryResolve(appRoot,mode,out choice,out error))
                btnFilmBorder.Text=UiText("添加边框", "Film Border");
            else if(choice.Mode==FilmBorderCatalogCore.Off)
                btnFilmBorder.Text=UiText("添加边框", "Film Border");
            else btnFilmBorder.Text=UiText("添加边框 · ","Film Border · ")+choice.Label;
        }
        private void ImportFilmBorderFromPreview()
        {
            using(var picker=new OpenFileDialog()) {
                picker.Title=UiText("导入胶片边框（1920×1080 RGBA PNG）", "Import film border (1920×1080 RGBA PNG)");
                picker.Filter="PNG (*.png)|*.png";
                picker.CheckFileExists=true;
                if(picker.ShowDialog(this)!=DialogResult.OK){OpenFilmBorderSettings();return;}
                try {
                    string mode=FilmBorderCatalogCore.Import(appRoot,picker.FileName);
                    borderPreviewPendingMode=mode;
                    OpenFilmBorderSettings();
                } catch(Exception ex) {FgsMessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);OpenFilmBorderSettings();}
            }
        }
        private void DeleteFilmBorderFromPreview(string mode)
        {
            FilmBorderChoice choice;string error;
            if(!FilmBorderCatalogCore.TryResolve(appRoot,mode,out choice,out error) || !choice.User) {
                FgsMessageBox.Show(this,UiText("内置边框不能删除。","Built-in borders cannot be deleted."));
                OpenFilmBorderSettings();return;
            }
            if(FgsMessageBox.Show(this,UiText("删除用户边框：","Delete custom film border: ")+choice.Label+"?",
                Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes) {
                try {
                    FilmBorderCatalogCore.Delete(appRoot,mode);
                    string saved=config.Get("FILM_BORDER_MODE");
                    if(saved.StartsWith(choice.StyleId+"_",StringComparison.OrdinalIgnoreCase))
                        config.SaveValue("FILM_BORDER_MODE","OFF");
                    UpdateFilmBorderButton();
                } catch(Exception ex) {FgsMessageBox.Show(this,ex.Message);}
                borderPreviewPendingMode="OFF";
            } else borderPreviewPendingMode=mode;
            OpenFilmBorderSettings();
        }

        private void EditClassicFilmText()
        {
            string brand,serial;
            FilmBorderCatalogCore.ReadClassicText(appRoot,out brand,out serial);
            using(var dlg=new SettingsDialogForm()) {
                dlg.Text=UiText("经典纯黑 · 胶片边印文字","Classic Black · film edge markings");
                dlg.StartPosition=FormStartPosition.CenterParent;
                dlg.FormBorderStyle=FormBorderStyle.FixedDialog;
                dlg.MaximizeBox=false;dlg.MinimizeBox=false;
                dlg.ShowInTaskbar=false;dlg.ClientSize=new System.Drawing.Size(540,210);
                dlg.BackColor=selectedDarkTheme?UiTheme.Surface:System.Drawing.SystemColors.Control;
                dlg.ForeColor=selectedDarkTheme?UiTheme.TextColor:System.Drawing.SystemColors.ControlText;
                var name=new ThemeTextBox{Text=brand,Location=new System.Drawing.Point(140,37),Width=366,MaxLength=36};
                var code=new ThemeTextBox{Text=serial,Location=new System.Drawing.Point(140,95),Width=366,MaxLength=62};
                var save=new ThemeButton{Text=UiText("应用文字","Apply text"),Location=new System.Drawing.Point(298,160),Size=new System.Drawing.Size(110,32)};
                var cancel=new ThemeButton{Text=UiText("取消","Cancel"),Location=new System.Drawing.Point(419,160),Size=new System.Drawing.Size(87,32),DialogResult=DialogResult.Cancel};
                dlg.Controls.AddRange(new Control[]{
                    new ThemeLabel{Text=UiText("左上品牌","Upper-left brand"),Location=new System.Drawing.Point(18,43),AutoSize=true},name,
                    new ThemeLabel{Text=UiText("下方编号","Bottom inscription"),Location=new System.Drawing.Point(18,101),AutoSize=true},code,save,cancel});
                dlg.CancelButton=cancel;
                save.Click+=delegate {
                    try {FilmBorderCatalogCore.SaveClassicText(appRoot,name.Text,code.Text);dlg.DialogResult=DialogResult.OK;dlg.Close();}
                    catch(Exception ex){FgsMessageBox.Show(dlg,ex.Message,dlg.Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);}
                };
                AttachThemeWindow(dlg);
                dlg.ShowDialog(this);
            }
            OpenFilmBorderSettings();
        }

        private string ResolveGpuAdvancedPreviewExecutable()
        {
            string env = Environment.GetEnvironmentVariable("FGS_GPU_PREVIEW_EXE");
            string[] candidates = {env,Path.Combine(appRoot,"GpuAdvancedPreview","fgs_gpu_preview.exe"),
                Path.Combine(appRoot,"experiments","GpuSingleFrame","fgs_gpu_preview.exe")};
            foreach (string path in candidates) if (!string.IsNullOrEmpty(path) && File.Exists(path)) return Path.GetFullPath(path);
            return "";
        }

        private static void WriteFgsimPreviewShader(string root,string path)
        {
            string template = File.ReadAllText(Path.Combine(root,"Utils","_FilmGrainSimplified","FilmGrainSimplified.hook.template"));
            int start = template.IndexOf("const int FG_TILE",StringComparison.Ordinal);
            if (start < 0) throw new InvalidDataException("FGSIM shader template is invalid.");
            string shader = template.Substring(start)
                .Replace("@@TILE_SIZE@@","2").Replace("@@HL_START@@","0.55")
                .Replace("@@HL_END@@","0.85").Replace("@@HL_REDUCE@@","0.50")
                .Replace("const float FG_STRENGTH = @@STRENGTH@@;","")
                .Replace("vec4 hook()","vec4 fgsim_apply(vec4 src, ivec2 px, int frame, float strength)")
                .Replace("    vec4 src = HOOKED_tex(HOOKED_pos);","")
                .Replace("    ivec2 px = ivec2(floor(HOOKED_pos * HOOKED_size));","")
                .Replace("FG_STRENGTH","strength");
            if (shader.Contains("@@") || shader.Contains("HOOKED") || !shader.Contains("vec4 fgsim_apply("))
                throw new InvalidDataException("FGSIM shader template is incompatible with GPU preview.");
            File.WriteAllText(path,shader,new UTF8Encoding(false));
        }

        private string RunGpuAdvancedPreviewProcess(string executable,IList<string> arguments,string runtime)
        {
            var log = new StringBuilder();
            var frameTimings = new StringBuilder();
            using (var process = new Process()) {
                process.StartInfo = new ProcessStartInfo(executable,SharedFrucCore.JoinArguments(arguments)) {
                    UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                    WorkingDirectory=Path.GetDirectoryName(executable),StandardOutputEncoding=new UTF8Encoding(false),
                    StandardErrorEncoding=new UTF8Encoding(false)
                };
                string msys = Environment.GetEnvironmentVariable("MSYS2_UCRT64_BIN");
                if (string.IsNullOrEmpty(msys)) {
                    foreach (string candidate in new[]{@"D:\msys64\ucrt64\bin",@"C:\msys64\ucrt64\bin"})
                        if (Directory.Exists(candidate)) {msys=candidate;break;}
                }
                if (!string.IsNullOrEmpty(msys) && Directory.Exists(msys))
                    process.StartInfo.EnvironmentVariables["PATH"] = msys+Path.PathSeparator+process.StartInfo.EnvironmentVariables["PATH"];
                if (!string.IsNullOrEmpty(runtime) && Directory.Exists(runtime))
                    process.StartInfo.EnvironmentVariables["PATH"] = runtime+Path.PathSeparator+process.StartInfo.EnvironmentVariables["PATH"];
                DataReceivedEventHandler record = delegate(object sender,DataReceivedEventArgs e) {
                    if (e.Data == null) return;
                    lock(log) {if(log.Length<14000)log.AppendLine(e.Data);}
                    if (e.Data.StartsWith("FRAME_DECODE,",StringComparison.Ordinal)) {
                        lock(frameTimings) {
                            if (frameTimings.Length < 6000)
                                frameTimings.AppendLine("[GPU Preview] 切换帧抽取（视频/颗粒/总计 ms）："+e.Data.Substring(13));
                        }
                    }
                };
                process.OutputDataReceived += record;
                process.ErrorDataReceived += record;
                lock (gpuPreviewProcessLock) {
                    if (gpuPreviewOwnerClosed) throw new OperationCanceledException("FGS 已关闭。");
                    if (!process.Start()) throw new IOException("无法启动 GPU 预览程序。");
                    gpuPreviewProcess = process;
                }
                try {
                    process.BeginOutputReadLine();process.BeginErrorReadLine();
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new InvalidOperationException("GPU 预览退出代码 "+process.ExitCode+"："+
                        (log.Length>0?log.ToString():"无控制台输出，请检查 MSYS2 UCRT64 运行时 DLL 是否可加载。"));
                } finally {
                    lock (gpuPreviewProcessLock) { if (gpuPreviewProcess == process) gpuPreviewProcess = null; }
                }
            }
            return frameTimings.ToString();
        }
    }
}
