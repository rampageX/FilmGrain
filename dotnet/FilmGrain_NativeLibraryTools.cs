using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace FilmGrainStudioPreview
{
    internal sealed class LibraryToolResult
    {
        public int Success, Skipped, Failed, Unsupported;
        public bool Cancelled;
        public string Summary { get { return (Cancelled ? "已取消" : Failed > 0 ? "完成，有失败项" : "完成") +
            "：成功 " + Success + "，跳过 " + Skipped + "，不支持 " + Unsupported + "，失败 " + Failed; } }
    }
    internal sealed class LutGalleryEntry
    {
        public string Name { get; set; }
        public string Relative { get; set; }
        public string LutPath { get; set; }
        public string PreviewPath { get; set; }
    }
    internal delegate int LibraryToolRunner(string executable, IList<string> arguments, string cwd, CancellationToken token, Action<string> log);
    // These utilities own their temporary files; only verified complete outputs are published.
    internal static class NativeLibraryTools
    {
        internal static int RunProcess(string executable, IList<string> arguments, string cwd, CancellationToken token, Action<string> log)
        {
            return RunProcessEnvironment(executable,arguments,cwd,null,0,token,log);
        }
        internal static int RunProcessEnvironment(string executable,IList<string> arguments,string cwd,IDictionary<string,string> environment,int timeoutMs,CancellationToken token,Action<string> log)
        {
            token.ThrowIfCancellationRequested();
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(executable, SharedFrucCore.JoinArguments(arguments)) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, WorkingDirectory = cwd, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
                if(environment!=null)foreach(var pair in environment)process.StartInfo.EnvironmentVariables[pair.Key]=pair.Value;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) log(e.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) log(e.Data); };
                if (!process.Start()) throw new IOException("无法启动 " + executable);
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                try
                {
                    var watch=Stopwatch.StartNew();
                    while (!process.WaitForExit(100)) {token.ThrowIfCancellationRequested();if(timeoutMs>0&&watch.ElapsedMilliseconds>timeoutMs)throw new TimeoutException("组件执行超时："+executable);}
                    process.WaitForExit();
                    token.ThrowIfCancellationRequested();
                    return process.ExitCode;
                }
                finally
                {
                    try { if (!process.HasExited) {
                        if(Environment.OSVersion.Platform==PlatformID.Win32NT) {
                            try{using(var kill=Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","taskkill.exe"),"/PID "+process.Id+" /T /F"){UseShellExecute=false,CreateNoWindow=true}))kill.WaitForExit(5000);}catch{}
                        }
                        if(!process.HasExited)process.Kill();process.WaitForExit();
                    } } catch { }
                }
            }
        }
        internal static void Publish(string temp, string destination)
        {
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new IOException("输出为空：" + temp);
            if (File.Exists(destination)) File.Replace(temp, destination, null);
            else File.Move(temp, destination);
        }
        internal static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp_" + Guid.NewGuid().ToString("N");
            try {
                File.WriteAllText(temp, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value), new UTF8Encoding(false));
                Publish(temp, path);
            } finally { DeleteTemp(temp); }
        }
        internal static void DeleteTemp(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
        static string RealDirectory(string path)
        {
            string full = Path.GetFullPath(path);
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("非 Windows 检查不遍历链接目录：" + path);
                return full;
            }
            using (SafeFileHandle h = CreateFile(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
                if (h.IsInvalid) throw new IOException("不能解析目录：" + path);
                StringBuilder buffer = new StringBuilder(32768);
                uint n = GetFinalPathNameByHandle(h, buffer, (uint)buffer.Capacity, 0);
                if (n == 0 || n >= buffer.Capacity) throw new IOException("不能解析目录：" + path);
                return buffer.ToString();
            }
        }
        internal static List<string> Scan(string root, string excluded, string[] extensions, Action<string> log)
        {
            var found = new List<string>(); var stack = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string exclude = string.IsNullOrEmpty(excluded) ? "" : Path.GetFullPath(excluded).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            stack.Push(Path.GetFullPath(root));
            while (stack.Count > 0) {
                string dir = stack.Pop();
                if (exclude.Length > 0 && (dir + Path.DirectorySeparatorChar).StartsWith(exclude, StringComparison.OrdinalIgnoreCase)) continue;
                try {
                    if (!seen.Add(RealDirectory(dir))) continue;
                    foreach (string file in Directory.GetFiles(dir)) if (extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) found.Add(file);
                    foreach (string child in Directory.GetDirectories(dir)) stack.Push(child);
                } catch (Exception ex) { log("目录扫描失败：" + dir + "：" + ex.Message); throw; }
            }
            found.Sort(StringComparer.OrdinalIgnoreCase); return found;
        }
        internal static string Relative(string root, string path)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件不在根目录内：" + path);
            return full.Substring(prefix.Length);
        }
        internal static string PreviewPath(string root, string output, string lut)
        {
            string rel = Relative(root, lut);
            return Path.Combine(output, Path.GetDirectoryName(rel) ?? "", Path.GetFileNameWithoutExtension(rel) + "_preview.jpg");
        }
        internal static List<LutGalleryEntry> ReadIndex(string path)
        {
            if (!File.Exists(path) || string.IsNullOrWhiteSpace(File.ReadAllText(path, Encoding.UTF8))) return new List<LutGalleryEntry>();
            object parsed = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
            IEnumerable rows = parsed is IDictionary<string, object> ? new object[] { parsed } : parsed as IEnumerable;
            var result = new List<LutGalleryEntry>();
            if (rows == null) throw new IOException("无效 LUT 索引");
            foreach (object row in rows) {
                var map = row as IDictionary<string, object>;
                if (map == null) throw new IOException("无效 LUT 索引记录");
                Func<string,string> get = k => map.ContainsKey(k) ? Convert.ToString(map[k]) : "";
                result.Add(new LutGalleryEntry { Name=get("Name"), Relative=get("Relative"), LutPath=get("LutPath"), PreviewPath=get("PreviewPath") });
            }
            return result;
        }
        internal static List<LutGalleryEntry> GalleryEntries(string root, string previews)
        {
            var indexed=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            try {
                foreach(var row in ReadIndex(Path.Combine(previews,"_LUT_GALLERY_INDEX.json"))) {
                    string candidate=row.LutPath;
                    if(!File.Exists(candidate) && !string.IsNullOrEmpty(row.Relative) && !Path.IsPathRooted(row.Relative) && !row.Relative.Split('\\','/').Contains(".."))candidate=Path.Combine(root,row.Relative);
                    if(!string.IsNullOrEmpty(candidate)&&File.Exists(row.PreviewPath))indexed[Path.GetFullPath(candidate)]=row.PreviewPath;
                }
            }catch { /* A damaged index does not hide previews found on disk. */ }
            var result = new List<LutGalleryEntry>();
            foreach (string path in Scan(root, previews, new[] { ".cube" }, delegate { })) {
                string preview=PreviewPath(root,previews,path),fallback;
                if(!File.Exists(preview)&&indexed.TryGetValue(Path.GetFullPath(path),out fallback))preview=fallback;
                if (File.Exists(preview)) result.Add(new LutGalleryEntry { Name=Path.GetFileNameWithoutExtension(path), Relative=Relative(root,path), LutPath=path, PreviewPath=preview });
            }
            return result;
        }
        internal static string PrepareCube(string lut, string workspace)
        {
            string[] lines = File.ReadAllLines(lut, Encoding.UTF8);
            if (!lines.Any(x => Regex.IsMatch(x, @"^\s*LUT_3D_SIZE\s+\d+", RegexOptions.IgnoreCase))) return "";
            var converted = new List<string>();
            foreach (string line in lines) {
                Match m = Regex.Match(line, @"^\s*LUT_3D_INPUT_RANGE\s+([^\s#]+)\s+([^\s#]+)", RegexOptions.IgnoreCase);
                if (m.Success) {
                    converted.Add("DOMAIN_MIN " + string.Join(" ", Enumerable.Repeat(m.Groups[1].Value,3).ToArray()));
                    converted.Add("DOMAIN_MAX " + string.Join(" ", Enumerable.Repeat(m.Groups[2].Value,3).ToArray()));
                } else converted.Add(line);
            }
            string safe = Path.Combine(workspace,"lut.cube");
            File.WriteAllLines(safe, converted.ToArray(), new UTF8Encoding(false)); return safe;
        }
        internal static LibraryToolResult BuildPreviews(string root, string output, string reference, string ffmpeg,
            bool overwrite, bool syncDeleted, CancellationToken token, Action<string> log, LibraryToolRunner run)
        {
            var result = new LibraryToolResult(); Directory.CreateDirectory(output);
            if (!File.Exists(reference)) throw new IOException("参考图不存在：" + reference);
            var luts = Scan(root, output, new[] { ".cube", ".3dl", ".dat", ".m3d", ".csp" }, log);
            string index = Path.Combine(output,"_LUT_GALLERY_INDEX.json");
            List<LutGalleryEntry> old = new List<LutGalleryEntry>();
            try { old=ReadIndex(index); } catch(Exception ex) { log("旧索引无法读取，本次不清理旧预览：" + ex.Message); syncDeleted=false; }
            var rows = new List<LutGalleryEntry>();
            try {
                for (int n=0;n<luts.Count;n++) {
                    token.ThrowIfCancellationRequested(); string lut=luts[n]; string jpg=PreviewPath(root,output,lut);
                    var row=new LutGalleryEntry { Name=Path.GetFileNameWithoutExtension(lut), Relative=Relative(root,lut), LutPath=lut, PreviewPath=jpg };
                    log("["+(n+1)+"/"+luts.Count+"] LUT 预览："+row.Relative);
                    if (File.Exists(jpg) && !overwrite) { result.Skipped++; rows.Add(row); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(jpg));
                    string workspace=Path.Combine(Path.GetDirectoryName(jpg),".fgs_lut_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
                    string temp=jpg+".tmp_"+Guid.NewGuid().ToString("N")+".jpg";
                    try {
                        string prepared;
                        if (Path.GetExtension(lut).Equals(".cube",StringComparison.OrdinalIgnoreCase)) prepared=PrepareCube(lut,workspace);
                        else { prepared=Path.Combine(workspace,"lut"+Path.GetExtension(lut)); File.Copy(lut,prepared); }
                        if (prepared.Length==0) { result.Unsupported++; log("不支持：纯 1D CUBE，不传给 lut3d"); continue; }
                        // ASCII relative filename keeps Unicode, quote and drive colon out of the filter grammar.
                        var args=new List<string> { "-hide_banner","-loglevel","error","-nostdin","-y","-i",Path.GetFullPath(reference),"-frames:v","1","-vf",
                            "lut3d=file='"+Path.GetFileName(prepared)+"',scale='min(1920,iw)':-2:flags=lanczos","-q:v","2",Path.GetFullPath(temp) };
                        if(run(ffmpeg,args,workspace,token,log)!=0) throw new IOException("FFmpeg 预览生成失败");
                        token.ThrowIfCancellationRequested(); Publish(temp,jpg); rows.Add(row); result.Success++;
                    } catch(OperationCanceledException) { throw; }
                    catch(Exception ex) { result.Failed++; log("失败："+lut+"："+ex.Message); if(File.Exists(jpg)) rows.Add(row); }
                    finally { DeleteTemp(temp); try { Directory.Delete(workspace,true); } catch { } }
                }
                token.ThrowIfCancellationRequested();
                if (syncDeleted) SyncDeletedPreviews(root,output,old,log,token);
                token.ThrowIfCancellationRequested();
                WriteJson(index,rows.OrderBy(x=>x.Relative,StringComparer.OrdinalIgnoreCase).ToArray());
            } catch(OperationCanceledException) { result.Cancelled=true; log("已取消；原索引保留，已完成预览可在下次扫描中使用。"); }
            log(result.Summary); return result;
        }
        internal static void SyncDeletedPreviews(string root,string output,IEnumerable<LutGalleryEntry> old,Action<string> log,CancellationToken token = default(CancellationToken))
        {
            string prefix=Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            foreach(var row in old) {
                token.ThrowIfCancellationRequested();
                if(File.Exists(row.LutPath)) continue;
                if(!string.IsNullOrEmpty(row.Relative) && !Path.IsPathRooted(row.Relative) && !row.Relative.Split('\\','/').Contains("..") && File.Exists(Path.Combine(root,row.Relative))) continue;
                if(string.IsNullOrEmpty(row.PreviewPath)) continue;
                string path=Path.GetFullPath(row.PreviewPath);
                if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!path.EndsWith("_preview.jpg",StringComparison.OrdinalIgnoreCase)) continue;
                // Reconstruct expected path from the indexed source: never delete an arbitrary indexed image.
                try { if(!string.Equals(path,PreviewPath(root,output,row.LutPath),StringComparison.OrdinalIgnoreCase)) continue; } catch { continue; }
                // Resolve parent before deletion; a replaced junction must not redirect cleanup outside the tree.
                if(!Directory.Exists(Path.GetDirectoryName(path))) continue;
                string realRoot=RealDirectory(output).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
                string realParent=RealDirectory(Path.GetDirectoryName(path)).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
                if(!realParent.StartsWith(realRoot,StringComparison.OrdinalIgnoreCase)) continue;
                DeleteTemp(path); log("同步删除预览："+path);
                using(var sha=System.Security.Cryptography.SHA256.Create()) {
                    string hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()))).Replace("-","").ToLowerInvariant();
                    string thumbs=Path.Combine(output,"_GALLERY_THUMBS_v3_240x135");
                    if(Directory.Exists(thumbs) && (RealDirectory(thumbs).TrimEnd('\\','/')+Path.DirectorySeparatorChar).StartsWith(realRoot,StringComparison.OrdinalIgnoreCase)) DeleteTemp(Path.Combine(thumbs,hash+".jpg"));
                }
                string parent=Path.GetDirectoryName(path);
                while(!string.Equals(parent,Path.GetFullPath(output),StringComparison.OrdinalIgnoreCase)) {
                    try { Directory.Delete(parent,false); } catch { break; } parent=Path.GetDirectoryName(parent);
                }
            }
        }
    }
}
