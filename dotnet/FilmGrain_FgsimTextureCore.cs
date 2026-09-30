using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

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
                    foreach (string preset in new string[] { "LIGHT", "MEDIUM", "HEAVY" })
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
