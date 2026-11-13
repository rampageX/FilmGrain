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
            if ((media.IsInterlaced && !media.IsHdr) || !supportedColor) {
                FgsMessageBox.Show(this,UiText("GPU 预览支持逐行 BT.709 SDR（有限范围），以及逐行或隔行 BT.2020 PQ／HLG。",
                    "GPU preview supports progressive limited-range BT.709 SDR and progressive or interlaced BT.2020 PQ/HLG."));return;
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
            bool embeddedPreview = (plateMode || (!newPixelMode && media.IsHdr)) && typeof(MainForm).Assembly.GetManifestResourceInfo(HdrGpuPreviewResource)!=null;
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
            if (!hasGrain && !hasLut && !media.IsHdr) {
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
           