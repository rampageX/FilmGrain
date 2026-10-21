using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Security.Cryptography;

namespace FilmGrainStudioPreview
{
    internal static class FgsimTextureCore
    {
        private const long TextureBytes = 1048576;
        private const long MaxPngBytes = 16 * 1024 * 1024;
        internal const string TextureUrl = "https://raw.githubusercontent.com/kanzwataru/filmgrain-simplified/master/resources/LDR_RGBA_0.png";
        private static readonly object SyncRoot = new object();

        internal static bool TryEnsureLocal(string appRoot, out string rawPath, out string reason)
        {
            rawPath = "";
            reason = "";
            try
            {
                string root = Path.Combine(appRoot, "Utils", "_FilmGrainSimplified");
                string generated = Path.Combine(root, "Generated");
                rawPath = Path.Combine(generated, "Noise_512x512_RGBA8.rgba");
                lock (SyncRoot)
                {
                    if (File.Exists(rawPath) && new FileInfo(rawPath).Length == TextureBytes) return true;
                    string fallback = Path.Combine(root, "NoiseFallback_512x512_RGBA8.rgba");
                    if (!File.Exists(fallback) || new FileInfo(fallback).Length != TextureBytes)
                    {
                        reason = "本地 FGSIM 纹理与内置备用纹理均不可用。";
                        return false;
                    }
                    Directory.CreateDirectory(generated);
                    string staging = rawPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.Copy(fallback, staging, true);
                        ReplaceFile(staging, rawPath);
                        File.WriteAllText(Path.Combine(generated, "NOISE_SOURCE.txt"), "BUNDLED_FALLBACK\r\n", new UTF8Encoding(false));
                    }
                    finally { try { if (File.Exists(staging)) File.Delete(staging); } catch { } }
                    return true;
                }
            }
            catch (Exception ex) { reason = "本地 FGSIM 纹理准备失败：" + ex.Message; return false; }
        }

        // All three encoders share the exact Legacy shader substitutions and local texture.
        internal static bool TryPrepareHook(string appRoot, string preset, out string hook, out string reason)
        {
            hook = ""; reason = "";
            if (!GrainControlCore.IsFgsimPreset(preset))
            { reason = "FGSIM 预设无效。"; return false; }
            lock (SyncRoot)
            {
                string staging = "", stampStage = "";
                try
                {
                    string raw;
                    if (!TryEnsureLocal(appRoot, out raw, out reason)) return false;
                    string generated = Path.GetDirectoryName(raw);
                    string template = Path.Combine(Path.GetDirectoryName(generated), "FilmGrainSimplified.hook.template");
                    if (!File.Exists(template)) { reason = "FGSIM hook 模板缺失：" + template; return false; }
                    byte[] noise = File.ReadAllBytes(raw);
                    if (noise.Length != TextureBytes) throw new InvalidDataException("FGSIM 纹理大小无效。");
                    string source = File.ReadAllText(template);
                    string key = Hash(noise) + ":" + Hash(Encoding.UTF8.GetBytes(source)) + ":" + preset + ":1";
                    string target = Path.Combine(generated, "FilmGrainSimplified_" + preset + ".hook");
                    string stamp = target + ".sha256";
                    if (File.Exists(target) && File.Exists(stamp))
                    {
                        string[] record = File.ReadAllLines(stamp);
                        if (record.Length == 2 && record[0] == key && record[1] == Hash(File.ReadAllBytes(target)))
                        { hook = target; return true; }
                    }
                    string hex = BitConverter.ToString(noise).Replace("-", "").ToLowerInvariant();
                    StringBuilder lines = new StringBuilder(hex.Length + hex.Length / 256 + 1);
                    for (int i = 0; i < hex.Length; i += 256) lines.Append(hex, i, Math.Min(256, hex.Length - i)).Append('\n');
                    string strength = GrainControlCore.FgsimStrength(preset);
                    string body = source.Replace("@@TILE_SIZE@@", "2").Replace("@@STRENGTH@@", strength)
                        .Replace("@@HL_START@@", "0.55").Replace("@@HL_END@@", "0.85").Replace("@@HL_REDUCE@@", "0.50")
                        .Replace("@@NOISE_HEX@@", lines.ToString());
                    if (body.Contains("@@") || !source.Contains("@@NOISE_HEX@@"))
                        throw new InvalidDataException("FGSIM 模板含未支持的占位符或缺失纹理占位符。");
                    staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    stampStage = stamp + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(staging, body, new UTF8Encoding(false));
                    File.WriteAllText(stampStage, key + "\r\n" + Hash(File.ReadAllBytes(staging)) + "\r\n", new UTF8Encoding(false));
                    // Atomic replacement preserves the previous hook if publication fails.
                    NativeLibraryTools.Publish(staging, target);
                    NativeLibraryTools.Publish(stampStage, stamp);
                    hook = target; return true;
                }
                catch (Exception ex) { reason = "FGSIM hook 准备失败：" + ex.Message; return false; }
                finally
                {
                    NativeLibraryTools.DeleteTemp(staging); NativeLibraryTools.DeleteTemp(stampStage);
                }
            }
        }
        // FGSIM shaders can compile successfully yet render black on a particular adapter.
        // Select by name (Vulkan indices are unrelated to NVENC indices), then verify pixels.
        internal static bool TrySelectDevice(string ffmpeg, string hook, bool tenBit, out string device, out string reason, Func<bool> cancelled = null)
        {
            device = ""; reason = "";
            string directory = Path.Combine(Path.GetDirectoryName(hook), "Probe_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                File.Copy(hook, Path.Combine(directory, "probe.hook"));
                StringBuilder evidence = new StringBuilder();
                foreach (string candidate in new[] { "NVIDIA", "0" })
                {
                    if (cancelled != null && cancelled()) { reason = "FGSIM GPU validation cancelled"; return false; }
                    string detail;
                    if (ProbeDevice(ffmpeg, directory, candidate, tenBit, out detail, cancelled))
                    {
                        evidence.AppendLine(candidate + ": PASS " + detail);
                        File.WriteAllText(Path.Combine(directory, "DeviceProbe.log"), evidence.ToString(), new UTF8Encoding(false));
                        device = candidate; return true;
                    }
                    evidence.AppendLine(candidate + ": FAIL " + detail);
                }
                File.WriteAllText(Path.Combine(directory, "DeviceProbe.log"), evidence.ToString(), new UTF8Encoding(false));
                reason = "FGSIM GPU pixel validation failed; no verified adapter. See " + Path.Combine(directory, "DeviceProbe.log");
                return false;
            }
            catch (Exception ex) { reason = "FGSIM GPU pixel validation failed: " + ex.Message; return false; }
            finally { try { File.Delete(Path.Combine(directory, "probe.hook")); } catch { } }
        }

        private static bool ProbeDevice(string ffmpeg, string directory, string device, bool tenBit, out string detail, Func<bool> cancelled)
        {
            detail = "";
            string format = tenBit ? "yuv420p10le" : "yuv420p";
            string args = SharedFrucCore.JoinArguments(new[] { "-hide_banner", "-loglevel", "verbose", "-nostdin",
                "-init_hw_device", "vulkan=fgprobe:" + device, "-filter_hw_device", "fgprobe", "-f", "lavfi", "-i",
                "color=c=gray:s=64x64:r=1", "-vf", "format=" + format + ",hwupload,libplacebo=format=" + format +
                ":custom_shader_path=probe.hook,hwdownload,format=" + format + ",format=gray", "-frames:v", "1", "-threads:v", "1",
                "-c:v", "rawvideo", "-f", "rawvideo", "pipe:1" });
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(ffmpeg, args) { WorkingDirectory = directory, UseShellExecute = false,
                        CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    process.Start();
                    var pixels = System.Threading.Tasks.Task.Factory.StartNew(delegate {
                        using (MemoryStream stream = new MemoryStream()) { process.StandardOutput.BaseStream.CopyTo(stream); return stream.ToArray(); }
                    });
                    var errors = System.Threading.Tasks.Task.Factory.StartNew(delegate { return process.StandardError.ReadToEnd(); });
                    Stopwatch timer = Stopwatch.StartNew();
                    while (!process.WaitForExit(100))
                    {
                        if ((cancelled != null && cancelled()) || timer.ElapsedMilliseconds >= 15000)
                        {
                            try { process.Kill(); process.WaitForExit(5000); } catch { }
                            try { System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { pixels, errors }, 5000); } catch { }
                            detail = cancelled != null && cancelled() ? "CANCELLED" : "TIMEOUT"; return false;
                        }
                    }
                    if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { pixels, errors }, 5000))
                    { detail = "Output capture timeout"; return false; }
                    string log = errors.Result;
                    File.WriteAllText(Path.Combine(directory, device + ".log"), ffmpeg + " " + args + "\r\nexit=" + process.ExitCode + "\r\n" + log, new UTF8Encoding(false));
                    if (process.ExitCode != 0 || log.IndexOf("Registering hook pass", StringComparison.OrdinalIgnoreCase) < 0 ||
                        System.Text.RegularExpressions.Regex.IsMatch(log, @"(?i)error:|VK_ERROR_|Failed executing hook|Failed creating render pass|shaderc compile status .error"))
                    { detail = "Process/shader failure (including silently disabled hook)"; return false; }
                    byte[] data = pixels.Result;
                    if (data.Length != 4096) { detail = "Missing/invalid raw frame: " + data.Length; return false; }
                    int min = 255, max = 0; long sum = 0;
                    foreach (byte value in data) { min = Math.Min(min, value); max = Math.Max(max, value); sum += value; }
                    double mean = sum / 4096.0;
                    detail = "min=" + min + " max=" + max + " mean=" + mean.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    // Gray must remain gray, with visible spatial grain. A disabled hook gives a flat image.
                    return mean >= 32 && mean <= 224 && max - min >= 4;
                }
            }
            catch (Exception ex) { detail = ex.Message; return false; }
        }

        internal static void AttachVerifiedDevice(BridgeExecutionRequest request, string hook, bool tenBit)
        {
            if (request.DirectStages == null) return;
            foreach (BridgeExecutionStage stage in request.DirectStages)
            {
                if (stage.DirectArguments == null || !stage.DirectArguments.Contains("custom_shader_path=")) continue;
                BridgeExecutionStage target = stage;
                Func<bool> cancelled = null;
                Func<Func<bool>, string> priorCancellable = target.PrepareWithCancellation;
                target.PrepareWithCancellation = delegate(Func<bool> check) {
                    cancelled = check;
                    return priorCancellable == null ? "" : priorCancellable(check);
                };
                Func<string> prior = target.BeforeRunPreparation;
                target.BeforeRunPreparation = delegate {
                    string notice = prior == null ? "" : prior();
                    string device, reason;
                    if (!TrySelectDevice(target.ToolPath, hook, tenBit, out device, out reason, cancelled))
                    {
                        if (cancelled != null && cancelled()) return notice + "\n[FGSIM] GPU validation cancelled.";
                        throw new InvalidOperationException(reason);
                    }
                    // Timeline preparation may regenerate the command. Apply the verified device last.
                    target.DirectArguments = System.Text.RegularExpressions.Regex.Replace(target.DirectArguments, @"vulkan=vk:(?:0|NVIDIA)(?=[\s""]|$)", "vulkan=vk:" + device);
                    if (target.OriginalArguments != null)
                        for (int i = 0; i < target.OriginalArguments.Count; i++)
                            if (target.OriginalArguments[i] == "vulkan=vk:0" || target.OriginalArguments[i] == "vulkan=vk:NVIDIA") target.OriginalArguments[i] = "vulkan=vk:" + device;
                    return notice + "\n[FGSIM] Vulkan adapter=" + device + "; actual shader pixel validation passed.";
                };
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static bool TryUpdateOnline(string appRoot, string ffmpeg, string proxyMode, string proxyUrl, out string detail)
        {
            detail = "";
            string root = Path.Combine(appRoot, "Utils", "_FilmGrainSimplified");
            string generated = Path.Combine(root, "Generated");
            string raw = Path.Combine(generated, "Noise_512x512_RGBA8.rgba");
            string png = Path.Combine(generated, "LDR_RGBA_0." + Guid.NewGuid().ToString("N") + ".tmp.png");
            string rawStage = raw + "." + Guid.NewGuid().ToString("N") + ".tmp";
            lock (SyncRoot)
            {
                try
                {
                    Directory.CreateDirectory(generated);
                    DownloadPng(png, proxyMode, proxyUrl);
                    if (!DecodePng(ffmpeg, png, rawStage, out detail)) return false;
                    if (!File.Exists(rawStage) || new FileInfo(rawStage).Length != TextureBytes)
                    { detail = "下载的纹理未能转换为 512x512 RGBA8。"; return false; }
                    ReplaceFile(rawStage, raw);
                    string source = "ONLINE_UPDATED " + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + " " + TextureUrl + "\r\n";
                    File.WriteAllText(Path.Combine(generated, "NOISE_SOURCE.txt"), source, new UTF8Encoding(false));
                    foreach (string preset in GrainControlCore.FgsimPresets)
                    {
                        string hook = Path.Combine(generated, "FilmGrainSimplified_" + preset + ".hook");
                        try { if (File.Exists(hook)) File.Delete(hook); } catch { }
                    }
                    detail = "纹理已更新；新纹理会在下一次 FGSIM 编码时生成对应 hook 并生效。";
                    return true;
                }
                catch (Exception ex) { detail = "在线纹理更新失败：" + ex.Message; return false; }
                finally
                {
                    try { if (File.Exists(png)) File.Delete(png); } catch { }
                    try { if (File.Exists(rawStage)) File.Delete(rawStage); } catch { }
                }
            }
        }

        private static void DownloadPng(string destination, string proxyMode, string proxyUrl)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(TextureUrl);
            request.Proxy = NetworkProxyCore.CreateProxy(proxyMode, proxyUrl);
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            request.UserAgent = "FilmGrainStudio-FGSIM-Texture-Updater";
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.ContentLength > MaxPngBytes) throw new InvalidDataException("上游纹理文件超过 16 MiB 限制。");
                using (Stream input = response.GetResponseStream())
                using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920]; int read; long total = 0;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > MaxPngBytes) throw new InvalidDataException("上游纹理文件超过 16 MiB 限制。");
                        output.Write(buffer, 0, read);
                    }
                    if (total == 0) throw new InvalidDataException("上游纹理文件为空。");
                }
            }
        }

        private static bool DecodePng(string ffmpeg, string png, string output, out string detail)
        {
            detail = "";
            List<string> args = new List<string>{ "-hide_banner", "-loglevel", "error", "-y", "-i", png, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", output };
            ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, JoinArgs(args));
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true;
            using (Process process = new Process())
            {
                StringBuilder stderr = new StringBuilder();
                process.StartInfo = psi;
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs) { if (eventArgs.Data != null) stderr.AppendLine(eventArgs.Data); };
                process.Start(); process.BeginErrorReadLine();
                if (!process.WaitForExit(30000))
                {
                    try { process.Kill(); } catch { }
                    try { process.WaitForExit(); } catch { }
                    detail = "FFmpeg 解码纹理超时。"; return false;
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                { detail = "FFmpeg 解码纹理失败：" + FirstLine(stderr.ToString()); return false; }
                return true;
            }
        }

        private static void ReplaceFile(string source, string destination)
        {
            if (!File.Exists(destination)) { File.Move(source, destination); return; }
            try { File.Replace(source, destination, null); }
            catch (PlatformNotSupportedException) { File.Delete(destination); File.Move(source, destination); }
        }

        private static string JoinArgs(IEnumerable<string> args)
        {
            StringBuilder result = new StringBuilder();
            foreach (string value in args) { if (result.Length > 0) result.Append(' '); result.Append(Quote(value ?? "")); }
            return result.ToString();
        }

        private static string Quote(string value)
        {
            if (value.Length > 0 && value.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0) return value;
            StringBuilder result = new StringBuilder(); result.Append('"'); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); slashes = 0; continue; }
                if (slashes > 0) { result.Append('\\', slashes); slashes = 0; }
                result.Append(c);
            }
            if (slashes > 0) result.Append('\\', slashes * 2);
            result.Append('"'); return result.ToString();
        }

        private static string FirstLine(string value)
        {
            using (StringReader reader = new StringReader(value ?? "")) return reader.ReadLine() ?? "";
        }
    }
}
