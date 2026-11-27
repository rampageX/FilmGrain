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
        public double MotionSpeed { get; set; }        public double MotionFrequency { get; set; }
        public double FocusX { get; set; }
        public double FocusY { get; set; }
        public double Zoom { get; set; }
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
        private bool filmBorderSettingsBusy;        private double? borderPreviewAmp, borderPreviewSpeed, borderPreviewFrequency;
        private double? borderPreviewFocusX, borderPreviewFocusY, borderPreviewZoom;
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
            bool softwareBorder = cmbCodec.SelectedIndex == 2 &&
                (CpuCodec() == "X265" || CpuCodec() == "X264");
            bool borderRoute = media.IsHdr ? !fullRange &&
                (cmbCodec.SelectedIndex == 1 || (softwareBorder && CpuCodec() == "X265")) :
                cmbCodec.SelectedIndex == 1 || cmbCodec.SelectedIndex == 3 || softwareBorder;
            FilmBorderChoice previewBorderChoice;string borderResolveError;
            bool selectedBorder=FilmBorderCatalogCore.TryResolve(appRoot,borderMode,
                out previewBorderChoice,out borderResolveError) &&
                previewBorderChoice.Mode!=FilmBorderCatalogCore.Off;
            bool filmBorder = borderRoute && !chkInterpolation.Checked && selectedBorder;
            int expandPx=filmBorder?FilmBorderExpandPx():0;
            if(expandPx>0 && !CheckExpandPreviewGeometry(media,expandPx))return;
            string borderAsset=filmBorder ? previewBorderChoice.PngPath : "";
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
                        var geometry=expandPx>0?FilmBorderExpandCanvasCore.NaturalGeometry(width,height,
                            previewBorderChoice.ApertureWidth1080,previewBorderChoice.ApertureHeight1080):null;
                        string previewStock=geometry!=null?FilmBorderExpandCanvasCore.WriteTexture(borderAsset,
                            previewBorderChoice.ApertureWidth1080,previewBorderChoice.ApertureHeight1080,geometry,temporary):borderAsset;
                        var borderArgs = new List<string> {"-hide_banner","-nostdin","-v","error","-y",
                            "-i",previewStock,"-frames:v","1"};
                        if (media.IsHdr) borderArgs.AddRange(new[]{"-vf",
                            "format=gbrap16le,zscale=matrixin=gbr:primariesin=709:transferin=iec61966-2-1:rangein=full"+
                            ":matrix=gbr:primaries=2020:transfer="+media.ColorTransfer+":range=full:npl=100:threads=1"});
                        borderArgs.AddRange(new[]{"-pix_fmt",media.IsHdr?"rgba64le":"rgba","-f","rawvideo",borderRaw});
                        int borderRc = NativeLibraryTools.RunProcessEnvironment(ffmpeg,borderArgs,temporary,null,30000,CancellationToken.None,null);
                        long expectedBorderBytes=(geometry==null?1920L*1080:(long)geometry.CanvasWidth*geometry.CanvasHeight)*(media.IsHdr?8:4);
                        if (borderRc != 0 || !File.Exists(borderRaw) || new FileInfo(borderRaw).Length != expectedBorderBytes)
                            throw new InvalidDataException("Film border texture decode failed.");
                        args.AddRange(new[]{"--film-border",borderRaw,"--film-border-mode",previewBorderChoice.Mode,
                            "--film-border-aperture-w",previewBorderChoice.ApertureWidth1080.ToString(CultureInfo.InvariantCulture),
                            "--film-border-aperture-h",previewBorderChoice.ApertureHeight1080.ToString(CultureInfo.InvariantCulture),
                            "--film-weave",previewBorderChoice.Weave?"1":"0",
                            "--film-motion-amp",FilmBorderMotionCore.Parse(config.Get(FilmBorderMotionCore.AmpKey),0,3).ToString("0.###",CultureInfo.InvariantCulture),
                            "--film-motion-speed",FilmBorderMotionCore.Parse(config.Get(FilmBorderMotionCore.SpeedKey),0.25,3).ToString("0.###",CultureInfo.InvariantCulture),
                            "--film-motion-frequency",FilmBorderMotionCore.Parse(config.Get(FilmBorderMotionCore.FrequencyKey),0.25,3).ToString("0.###",CultureInfo.InvariantCulture),
                            "--film-focus-x",FilmBorderFramingCore.N(FilmBorderFramingCore.Parse(config.Get(FilmBorderFramingCore.FocusXKey),0.5,0,1)),
                            "--film-focus-y",FilmBorderFramingCore.N(FilmBorderFramingCore.Parse(config.Get(FilmBorderFramingCore.FocusYKey),0.5,0,1)),
                            "--film-zoom",FilmBorderFramingCore.N(FilmBorderFramingCore.Parse(config.Get(FilmBorderFramingCore.ZoomKey),1,1,3))});
                        AddExpandPreviewArguments(args,media,expandPx);
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
            int expandPx=FilmBorderExpandPx();
            if(expandPx>0 && !CheckExpandPreviewGeometry(media,expandPx))return;
            string saved = borderPreviewPendingMode ?? config.Get("FILM_BORDER_MODE");
            FilmBorderChoice selectedChoice;string resolveError;
            string mode = FilmBorderCatalogCore.TryResolve(appRoot,saved,out selectedChoice,out resolveError) ? selectedChoice.Mode : "OFF";
            borderPreviewPendingMode=null;
            double amp=borderPreviewAmp ?? FilmBorderMotionCore.Parse(config.Get(FilmBorderMotionCore.AmpKey),0,3);
            double speed=borderPreviewSpeed ?? FilmBorderMotionCore.Parse(config.Get(FilmBorderMotionCore.SpeedKey),0.25,3);
            double frequency=borderPreviewFrequency ?? FilmBorderMotionCore.Parse(config.Get(FilmBorderMotionCore.FrequencyKey),0.25,3);
            double focusX=borderPreviewFocusX ?? FilmBorderFramingCore.Parse(config.Get(FilmBorderFramingCore.FocusXKey),0.5,0,1);
            double focusY=borderPreviewFocusY ?? FilmBorderFramingCore.Parse(config.Get(FilmBorderFramingCore.FocusYKey),0.5,0,1);
            double zoom=borderPreviewZoom ?? FilmBorderFramingCore.Parse(config.Get(FilmBorderFramingCore.ZoomKey),1,1,3);
            borderPreviewAmp=borderPreviewSpeed=borderPreviewFrequency=null;
            borderPreviewFocusX=borderPreviewFocusY=borderPreviewZoom=null;
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
                 double resultFocusX=focusX,resultFocusY=focusY,resultZoom=zoom;
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
                        media.IsHdr,media.ColorTransfer,CancellationToken.None,expandPx>0,width,height);
                    var args=new List<string>{frame,width.ToString(CultureInfo.InvariantCulture),
                        height.ToString(CultureInfo.InvariantCulture),"-","10","0",
                        seconds.ToString("0.######",CultureInfo.InvariantCulture),
                        "--result",result,"--grain","0","--grain-on","0","--lut-on","0",
                        "--film-border",asset,"--film-border-settings","1","--film-border-mode",mode,
                        "--film-motion-amp",amp.ToString("0.###",CultureInfo.InvariantCulture),
                        "--film-motion-speed",speed.ToString("0.###",CultureInfo.InvariantCulture),
                        "--film-motion-frequency",frequency.ToString("0.###",CultureInfo.InvariantCulture),
                         "--film-focus-x",FilmBorderFramingCore.N(focusX),
                         "--film-focus-y",FilmBorderFramingCore.N(focusY),
                         "--film-zoom",FilmBorderFramingCore.N(zoom),
                         "--border-choices-file",choices};
                    AddExpandPreviewArguments(args,media,expandPx);
                    if(duration>0)args.AddRange(new[]{"--video",input,"--ffmpeg",ffmpeg,"--duration",
                        Math.Min(86400,duration).ToString("0.######",CultureInfo.InvariantCulture)});
                    if(media.IsHdr)args.AddRange(new[]{"--source-color",
                        media.ColorTransfer=="smpte2084"?"pq":"hlg"});
                    if(parity.Length>0)args.AddRange(new[]{"--deinterlace",parity});
                    RunGpuAdvancedPreviewProcess(exe,args,null);
                    if(File.Exists(result)) {
                        var applied=new JavaScriptSerializer().Deserialize<GpuAdvancedPreviewResult>(
                            File.ReadAllText(result,Encoding.UTF8));
                        FilmBorderChoice resolved;string why="";
                        if(applied==null || applied.Version!=5 ||
                           !FilmBorderCatalogCore.TryResolve(appRoot,applied.FilmBorderMode,out resolved,out why))
                            throw new InvalidDataException("边框模式回传无效："+why);
                        selected=resolved.Mode;
                        action=applied.Action??"APPLY";
                        resultAmp=applied.MotionAmplitude;
                        resultSpeed=applied.MotionSpeed;
                        resultFrequency=applied.MotionFrequency;
                         resultFocusX=applied.FocusX;
                         resultFocusY=applied.FocusY;
                         resultZoom=applied.Zoom;
                         if(resultFocusX<0 || resultFocusX>1 || resultFocusY<0 || resultFocusY>1 ||
                             resultZoom<1 || resultZoom>3 ||
                             double.IsNaN(resultFocusX) || double.IsNaN(resultFocusY) || double.IsNaN(resultZoom) ||
                             double.IsInfinity(resultFocusX) || double.IsInfinity(resultFocusY) || double.IsInfinity(resultZoom))
                             throw new InvalidDataException("画面构图参数无效。");
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
                        UpdateFilmBorderButton();
                        btnGpuAdvancedPreview.Enabled=true;
                        if(error.Length>0) {
                            AppendLog("[Film Border] "+error+Environment.NewLine);
                            FgsMessageBox.Show(this,error,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
                        } else if(selected!=null) {
                            if(action=="IMPORT" || action=="DELETE" || action=="EDIT" || action=="REBUILD") {
                                borderPreviewAmp=resultAmp;
                                borderPreviewSpeed=resultSpeed;
                                borderPreviewFrequency=resultFrequency;
                                 borderPreviewFocusX=resultFocusX;
                                 borderPreviewFocusY=resultFocusY;
                                 borderPreviewZoom=resultZoom;
                             }
                             if(action=="REBUILD") {
                                try {
                                    FilmBorderCatalogCore.RebuildPreviewThumbnails(appRoot);
                                    borderPreviewPendingMode=selected;
                                    OpenFilmBorderSettings();
                                } catch(Exception ex) {
                                    FgsMessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
                                }
                                return;
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
                                    {FilmBorderMotionCore.FrequencyKey,resultFrequency.ToString("0.###",CultureInfo.InvariantCulture)},
                                     {FilmBorderFramingCore.FocusXKey,FilmBorderFramingCore.N(resultFocusX)},
                                     {FilmBorderFramingCore.FocusYKey,FilmBorderFramingCore.N(resultFocusY)},
                                     {FilmBorderFramingCore.ZoomKey,FilmBorderFramingCore.N(resultZoom)}
                                 });
                                UpdateFilmBorderButton();
                                AppendLog("[Film Border] "+selected+Environment.NewLine);
                                UpdateBitrateDisplays();
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
            // Preserve the saved border while viewing an AV1-only encoder.
            bool nvencAv1=cmbCodec!=null&&cmbCodec.SelectedIndex==0;
            bool svtAv1=cmbCodec!=null&&cmbCodec.SelectedIndex==2&&CpuCodec()=="SVT";
            bool av1Copy=cmbCodec!=null&&cmbCodec.SelectedIndex==4;
            if(nvencAv1||svtAv1||av1Copy)
            {
                SetEffectButtonState(btnFilmBorder,false);
                btnFilmBorder.Text=nvencAv1?UiText("NVENC_AV1 暂不可用","NVENC AV1 unavailable"):
                    svtAv1?UiText("SVT-AV1 暂不可用","SVT-AV1 unavailable"):
                    UiText("AV1 仅替换颗粒 · 暂不可用","AV1 copy · unavailable");
                btnFilmBorder.Enabled=false;
                return;
            }
            string mode=config.Get("FILM_BORDER_MODE");
            FilmBorderChoice choice;string error;
            bool active=FilmBorderCatalogCore.TryResolve(appRoot,mode,out choice,out error) &&
                choice.Mode!=FilmBorderCatalogCore.Off;
            int expandPx=FilmBorderExpandPx();
            btnFilmBorder.Text=active
                ? (expandPx>0?UiText("自然外扩 · ","Natural expand · "):UiText("添加边框 · ","Film Border · "))+choice.Label
                : (expandPx>0?UiText("添加外扩边框","Add expanded border"):UiText("添加边框", "Film Border"));
            MediaProbeInfo expandMedia=GetSelectedMediaInfo();
            FilmBorderExpandGeometry expandGeometry=active && expandPx>0 && expandMedia!=null?
                TryNaturalBorderGeometry(expandMedia.Width,expandMedia.Height,choice):null;
            if(expandGeometry!=null)
                lutToolTip.SetToolTip(btnFilmBorder,UiText("外扩画布：","Expanded canvas: ")+
                    expandGeometry.CanvasWidth+"×"+expandGeometry.CanvasHeight+
                    UiText("；厚度按素材比例自动决定。","; thickness follows stock proportions."));
            else
                lutToolTip.SetToolTip(btnFilmBorder,UiText("左键：打开边框预览；右键：合成模式或清空。","Left-click: border preview; right-click: composition or clear."));
            SetEffectButtonState(btnFilmBorder,active);
            btnFilmBorder.Enabled=!(gpuAdvancedPreviewBusy||filmBorderSettingsBusy);
        }
        private static FilmBorderExpandGeometry TryNaturalBorderGeometry(int width,int height,FilmBorderChoice choice)
        {
            if(choice==null || choice.Mode==FilmBorderCatalogCore.Off)return null;
            try {return FilmBorderExpandCanvasCore.NaturalGeometry(width,height,choice.ApertureWidth1080,choice.ApertureHeight1080);}
            catch(ArgumentException){return null;}
        }

        private int FilmBorderExpandPx()
        {
            int pixels;
            return FilmBorderExpandCanvasCore.TryParseThickness(config.Get(FilmBorderExpandCanvasCore.ThicknessKey),out pixels) && pixels>0?
                FilmBorderExpandCanvasCore.DefaultThickness:0;
        }

        private static void AddExpandPreviewArguments(List<string> args,MediaProbeInfo media,int pixels)
        {
            if(pixels==0)return;
            args.AddRange(new[]{"--film-expand-px",pixels.ToString(CultureInfo.InvariantCulture),
                "--film-expand-sar",CinematicCropGeometry.SampleAspect(media).ToString("0.########",CultureInfo.InvariantCulture)});
        }

        private bool CheckExpandPreviewGeometry(MediaProbeInfo media,int pixels)
        {
            try
            {
                if(media.IsInterlaced || chkInterpolation.Checked)
                    throw new ArgumentException(UiText("外扩测试版仅支持逐行、不插帧。","Expand candidate requires progressive input without interpolation."));
                var state=PreprocessEnvironment();
                CinematicFramePlan frame; ResizePlan resize;string reason;
                if(!CinematicFrameCore.TryBuild(media,state,out frame,out reason) ||
                    !ResizeCore.TryBuild(media,state,frame,out resize,out reason))
                    throw new ArgumentException(reason);
                if(resize.Enabled || !String.IsNullOrEmpty(frame.Filter))
                    throw new ArgumentException(UiText("外扩测试版请关闭裁切、缩放和电影画幅。","Disable crop, resize and cinematic bars for this candidate."));
                int limit=cmbCodec.SelectedIndex==3?4096:cmbCodec.SelectedIndex==1?8192:16384;
                FilmBorderChoice choice;string choiceError;
                if(FilmBorderCatalogCore.TryResolve(appRoot,config.Get("FILM_BORDER_MODE"),out choice,out choiceError) &&
                    choice.Mode!=FilmBorderCatalogCore.Off)
                {
                    var geometry=FilmBorderExpandCanvasCore.NaturalGeometry(media.Width,media.Height,choice.ApertureWidth1080,choice.ApertureHeight1080);
                    if(geometry.CanvasWidth>limit || geometry.CanvasHeight>limit)
                        throw new ArgumentException(UiText("外扩后超过当前编码器尺寸限制。","Expanded canvas exceeds codec dimensions."));
                }
                return true;
            }
            catch(ArgumentException ex){FgsMessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);return false;}
        }

        private void OpenFilmBorderCompositionSettings()
        {
            if(gpuAdvancedPreviewBusy || filmBorderSettingsBusy || (bridgeTaskCoordinator!=null && bridgeTaskCoordinator.IsActive))return;
            int previous=FilmBorderExpandPx();
            using(var dlg=new SettingsDialogForm())
            {
                dlg.Text=UiText("胶片边框 · 合成模式","Film Border · Composition");
                dlg.StartPosition=FormStartPosition.CenterParent;
                dlg.FormBorderStyle=FormBorderStyle.FixedDialog;
                dlg.MaximizeBox=false;dlg.MinimizeBox=false;dlg.ShowInTaskbar=false;
                dlg.ClientSize=new System.Drawing.Size(480,220);
                dlg.BackColor=selectedDarkTheme?UiTheme.Surface:System.Drawing.SystemColors.Control;
                dlg.ForeColor=selectedDarkTheme?UiTheme.TextColor:System.Drawing.SystemColors.ControlText;
                var mode=new ThemeComboBox {DropDownStyle=ComboBoxStyle.DropDownList,
                    Location=new System.Drawing.Point(134,24),Size=new System.Drawing.Size(320,27)};
                mode.Items.AddRange(new object[]{UiText("覆盖式（默认）","Overlay (default)"),UiText("外扩式 · 原画面完整保留","Expand Canvas · keep picture")});
                var description=new ThemeLabel {Location=new System.Drawing.Point(18,78),
                    Size=new System.Drawing.Size(436,78),ForeColor=selectedDarkTheme?UiTheme.Muted:System.Drawing.SystemColors.GrayText};
                var apply=new ThemeButton {Text=UiText("应用并预览","Apply and preview"),Location=new System.Drawing.Point(238,172),Size=new System.Drawing.Size(122,32)};
                var cancel=new ThemeButton {Text=UiText("取消","Cancel"),Location=new System.Drawing.Point(370,172),Size=new System.Drawing.Size(84,32),DialogResult=DialogResult.Cancel};
                Action refresh=delegate {
                    description.Text=mode.SelectedIndex==1?
                        UiText("原画不裁切、不缩放。保留胶片边框原始比例，厚度随素材自动调整。",
                            "Keep the original picture. Preserve the stock proportions and calculate the border thickness automatically."):
                        UiText("沿用现有覆盖式边框及画面构图。","Keep existing overlay and framing.");
                };
                mode.SelectedIndexChanged+=delegate{refresh();};
                mode.SelectedIndex=previous>0?1:0;
                dlg.Controls.AddRange(new Control[]{
                    new ThemeLabel{Text=UiText("合成模式","Composition"),Location=new System.Drawing.Point(18,30),AutoSize=true},
                    mode,description,apply,cancel});
                dlg.AcceptButton=apply;dlg.CancelButton=cancel;
                apply.Click+=delegate{
                    int p=mode.SelectedIndex==1?FilmBorderExpandCanvasCore.DefaultThickness:0;
                    MediaProbeInfo m=GetSelectedMediaInfo();
                    if(p>0 && m!=null && !CheckExpandPreviewGeometry(m,p))return;
                    config.SaveValue(FilmBorderExpandCanvasCore.ThicknessKey,p.ToString(CultureInfo.InvariantCulture));
                    dlg.DialogResult=DialogResult.OK;dlg.Close();
                };
                AttachThemeWindow(dlg);
                if(dlg.ShowDialog(this)!=DialogResult.OK)return;
            }
            UpdateFilmBorderButton();UpdateBitrateDisplays();UpdateHdrRouteStatus(GetSelectedMediaInfo());
            OpenFilmBorderSettings();
        }

        private void ImportFilmBorderFromPreview()
        {
            using(var picker=new OpenFileDialog()) {
                picker.Title=UiText("导入胶片边框（1920×1080 RGBA PNG）", "Import film border (1920×1080 RGBA PNG)");
                picker.Filter="PNG (*.png)|*.png";
                picker.CheckFileExists=true;
                if(picker.ShowDialog(this)!=DialogResult.OK){OpenFilmBorderSettings();return;}
                string displayName;
                int apertureWidth,apertureHeight;
                string apertureLayout;
                if(!PromptImportedFilmBorderName(picker.FileName,out displayName,out apertureWidth,out apertureHeight,out apertureLayout)) {
                    OpenFilmBorderSettings();return;
                }
                try {
                    string mode=FilmBorderCatalogCore.ImportWithLayout(appRoot,picker.FileName,displayName,apertureWidth,apertureHeight,apertureLayout);
                    borderPreviewPendingMode=mode;
                    // Only a successful PNG import updates the remembered aperture.
                    config.Save(new Dictionary<string,string> {
                        {FilmBorderCatalogCore.ImportLastWidthKey,apertureWidth.ToString(CultureInfo.InvariantCulture)},
                        {FilmBorderCatalogCore.ImportLastHeightKey,apertureHeight.ToString(CultureInfo.InvariantCulture)},
                        {FilmBorderCatalogCore.ImportLastLayoutKey,apertureLayout},
                        {apertureLayout==FilmBorderCatalogCore.ImportLayoutSide?
                            FilmBorderCatalogCore.ImportSideLastWidthKey:FilmBorderCatalogCore.ImportTopBottomLastWidthKey,
                            apertureWidth.ToString(CultureInfo.InvariantCulture)},
                        {apertureLayout==FilmBorderCatalogCore.ImportLayoutSide?
                            FilmBorderCatalogCore.ImportSideLastHeightKey:FilmBorderCatalogCore.ImportTopBottomLastHeightKey,
                            apertureHeight.ToString(CultureInfo.InvariantCulture)}
                    });
                    OpenFilmBorderSettings();
                } catch(Exception ex) {FgsMessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);OpenFilmBorderSettings();}
            }
        }

        private static int ValidImportDialogAperture(int value,int nominal,int fallback)
        {
            return value>=nominal-FilmBorderCatalogCore.ImportApertureTolerance &&
                   value<=nominal+FilmBorderCatalogCore.ImportApertureTolerance ? value : fallback;
        }

        private bool PromptImportedFilmBorderName(string source,out string displayName,out int apertureWidth,out int apertureHeight,out string layout)
        {
            displayName=null;
            layout=config.Get(FilmBorderCatalogCore.ImportLastLayoutKey);
            if(layout!=FilmBorderCatalogCore.ImportLayoutTopBottom)layout=FilmBorderCatalogCore.ImportLayoutSide;
            apertureWidth=layout==FilmBorderCatalogCore.ImportLayoutSide?
                FilmBorderCatalogCore.ImportSideDefaultWidth:FilmBorderCatalogCore.ImportTopBottomDefaultWidth;
            apertureHeight=layout==FilmBorderCatalogCore.ImportLayoutSide?
                FilmBorderCatalogCore.ImportSideDefaultHeight:FilmBorderCatalogCore.ImportTopBottomDefaultHeight;
            string suggested=Path.GetFileNameWithoutExtension(source);
            if(suggested.Length>40)suggested=suggested.Substring(0,40);
            string acceptedName=null;
            int acceptedWidth=apertureWidth,acceptedHeight=apertureHeight;
            string acceptedLayout=layout;
            using(var dlg=new SettingsDialogForm()) {
                dlg.Text=UiText("导入胶片边框 · 名称与画窗","Import film border · Name and aperture");
                dlg.StartPosition=FormStartPosition.CenterParent;
                dlg.FormBorderStyle=FormBorderStyle.FixedDialog;
                dlg.MaximizeBox=false;dlg.MinimizeBox=false;dlg.ShowInTaskbar=false;
                dlg.ClientSize=new System.Drawing.Size(540,296);
                dlg.BackColor=selectedDarkTheme?UiTheme.Surface:System.Drawing.SystemColors.Control;
                dlg.ForeColor=selectedDarkTheme?UiTheme.TextColor:System.Drawing.SystemColors.ControlText;
                var name=new ThemeTextBox {Text=suggested,
                    Location=new System.Drawing.Point(126,31),Width=380,MaxLength=40};
                var hint=new ThemeLabel {Text=UiText(
                    "显示在边框样式下拉菜单中（最多40个字符）",
                    "Shown in the border style list (max 40 characters)"),
                    Location=new System.Drawing.Point(126,68),
                    Size=new System.Drawing.Size(380,30),
                    ForeColor=selectedDarkTheme?UiTheme.Muted:System.Drawing.SystemColors.GrayText};
                var direction=new ThemeComboBox { Name="FILM_IMPORT_LAYOUT",
                    DropDownStyle=ComboBoxStyle.DropDownList,
                    Location=new System.Drawing.Point(126,114),Size=new System.Drawing.Size(380,27)};
                direction.Items.AddRange(new object[]{
                    UiText("左右齿孔","Sprockets left / right"),
                    UiText("上下齿孔","Sprockets top / bottom")});
                var width=new ThemeNumericUpDown {
                    Name="FILM_IMPORT_APERTURE_WIDTH",Location=new System.Drawing.Point(126,163),
                    Size=new System.Drawing.Size(100,27),Minimum=1600,Maximum=1910,
                    Value=apertureWidth,Increment=2};
                var height=new ThemeNumericUpDown {
                    Name="FILM_IMPORT_APERTURE_HEIGHT",Location=new System.Drawing.Point(379,163),
                    Size=new System.Drawing.Size(127,27),Minimum=700,Maximum=1020,
                    Value=apertureHeight,Increment=2};
                var geometryHint=new ThemeLabel {
                    Location=new System.Drawing.Point(18,204),
                    Size=new System.Drawing.Size(488,34),
                    ForeColor=selectedDarkTheme?UiTheme.Muted:System.Drawing.SystemColors.GrayText};
                Action reloadGeometry=delegate {
                    bool side=direction.SelectedIndex==0;
                    width.Minimum=1600;
                    width.Maximum=side?1780:1910;
                    height.Minimum=side?900:700;
                    height.Maximum=side?1020:860;
                    int defaultW=side?FilmBorderCatalogCore.ImportSideDefaultWidth:
                        FilmBorderCatalogCore.ImportTopBottomDefaultWidth;
                    int defaultH=side?FilmBorderCatalogCore.ImportSideDefaultHeight:
                        FilmBorderCatalogCore.ImportTopBottomDefaultHeight;
                    int storedW=config.GetInt(side?FilmBorderCatalogCore.ImportSideLastWidthKey:
                        FilmBorderCatalogCore.ImportTopBottomLastWidthKey,defaultW);
                    int storedH=config.GetInt(side?FilmBorderCatalogCore.ImportSideLastHeightKey:
                        FilmBorderCatalogCore.ImportTopBottomLastHeightKey,defaultH);
                    width.Value=Math.Max((int)width.Minimum,Math.Min((int)width.Maximum,storedW));
                    height.Value=Math.Max((int)height.Minimum,Math.Min((int)height.Maximum,storedH));
                    geometryHint.Text=side?
                        UiText("左右齿孔：宽1600-1780px，高900-1020px；中心透明，画窗居中。",
                            "Side sprockets: W 1600-1780, H 900-1020; centered window."):
                        UiText("上下齿孔：宽1600-1910px，高700-860px；中心透明，画窗居中。",
                            "Top/bottom sprockets: W 1600-1910, H 700-860; centered window.");
                };
                direction.SelectedIndexChanged+=delegate {reloadGeometry();};
                direction.SelectedIndex=layout==FilmBorderCatalogCore.ImportLayoutSide?0:1;
                var apply=new ThemeButton {Text=UiText("导入","Import"),
                    Location=new System.Drawing.Point(304,248),Size=new System.Drawing.Size(99,32)};
                var cancel=new ThemeButton {Text=UiText("取消","Cancel"),
                    Location=new System.Drawing.Point(416,248),Size=new System.Drawing.Size(90,32),
                    DialogResult=DialogResult.Cancel};
                dlg.Controls.AddRange(new Control[]{
                    new ThemeLabel{Text=UiText("边框名称","Border name"),
                        Location=new System.Drawing.Point(18,37),AutoSize=true},
                    name,hint,
                    new ThemeLabel{Text=UiText("齿孔方向","Sprocket layout"),
                        Location=new System.Drawing.Point(18,120),AutoSize=true},
                    direction,
                    new ThemeLabel{Text=UiText("画窗宽度","Aperture width"),
                        Location=new System.Drawing.Point(18,168),AutoSize=true},
                    width,
                    new ThemeLabel{Text=UiText("画窗高度","Aperture height"),
                        Location=new System.Drawing.Point(262,168),AutoSize=true},
                    height,geometryHint,apply,cancel});
                dlg.AcceptButton=apply;dlg.CancelButton=cancel;
                apply.Click+=delegate {
                    try {
                        acceptedName=FilmBorderCatalogCore.ValidateUniqueDisplayName(appRoot,name.Text);
                        acceptedWidth=(int)width.Value;acceptedHeight=(int)height.Value;
                        acceptedLayout=direction.SelectedIndex==0?
                            FilmBorderCatalogCore.ImportLayoutSide:FilmBorderCatalogCore.ImportLayoutTopBottom;
                        FilmBorderCatalogCore.ValidateApertureForLayout(acceptedWidth,acceptedHeight,acceptedLayout);
                        dlg.DialogResult=DialogResult.OK;dlg.Close();
                    } catch(Exception ex) {
                        FgsMessageBox.Show(dlg,ex.Message,dlg.Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
                        name.Focus();
                    }
                };
                AttachThemeWindow(dlg);
                dlg.Shown+=delegate {name.Focus();name.SelectAll();};
                if(dlg.ShowDialog(this)!=DialogResult.OK)return false;
            }
            displayName=acceptedName;
            apertureWidth=acceptedWidth;apertureHeight=acceptedHeight;layout=acceptedLayout;
            return !string.IsNullOrEmpty(displayName);
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
                        (log.Length>0?log.ToString():
                            process.ExitCode==2?
                                "原生预览参数校验失败（退出码 2）。请检查样式名称与输入参数。":
                                "原生进程未输出诊断日志；请检查退出码及预览运行时依赖。"));
                } finally {
                    lock (gpuPreviewProcessLock) { if (gpuPreviewProcess == process) gpuPreviewProcess = null; }
                }
            }
            return frameTimings.ToString();
        }
    }
}
