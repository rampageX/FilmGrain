using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
namespace FilmGrainStudioPreview
{
    internal delegate void SetupDownload(string url,string path,long limit,CancellationToken token,Action<string> log);
    internal delegate int SetupProcess(string exe,IList<string> args,string cwd,IDictionary<string,string> environment,CancellationToken token,Action<string> log);
    internal sealed class NativeSetupCore
    {
        const long MB=1024*1024;
        readonly string root,deps,stage;readonly SetupDownload download;readonly SetupProcess process;
        readonly CancellationToken token;readonly Action<string> log;readonly bool systemPython;
        internal NativeSetupCore(string root,bool systemPython,CancellationToken token,Action<string> log,SetupDownload download,SetupProcess process)
        {
            this.root=Path.GetFullPath(root);deps=Path.Combine(this.root,"_Dependencies");stage=Path.Combine(deps,"_stage_NET_"+Guid.NewGuid().ToString("N"));
            this.systemPython=systemPython;this.token=token;this.log=log;this.download=download;this.process=process;
        }
        internal LibraryToolResult Install(string tool)
        {
            Directory.CreateDirectory(stage);
            try {
                switch(tool){case "FFmpeg":InstallFfmpeg();break;case "Grav1synth":InstallGrav();break;case "Python":InstallPython();break;case "OpenSVPFlow":InstallSvp();break;case "OpenSVPFlowUpdate":return UpdateSvp();default:throw new InvalidDataException("未知组件："+tool);}
                log("安装与验证完成："+tool);return new LibraryToolResult{Success=1};
            }finally{try{Directory.Delete(stage,true);}catch{log("安装临时目录未能清理："+stage);}}
        }
        internal static void Download(string url,string path,long limit,string mode,string proxy,CancellationToken token,Action<string> log)
        {
            NetworkProxyCore.EnableTls12();Uri current=new Uri(url);log("下载："+current.GetLeftPart(UriPartial.Path));
            try {
                for(int redirect=0;redirect<=5;redirect++) {
                    token.ThrowIfCancellationRequested();if(current.Scheme!=Uri.UriSchemeHttps || current.UserInfo.Length>0)throw new IOException("下载只接受 HTTPS 地址");
                    var request=(HttpWebRequest)WebRequest.Create(current);request.Proxy=NetworkProxyCore.CreateProxy(mode,proxy);request.AllowAutoRedirect=false;
                    request.Timeout=30000;request.ReadWriteTimeout=30000;request.UserAgent="FilmGrainStudio-NativeSetup";request.Accept="application/vnd.github+json";
                    using(token.Register(request.Abort))using(var response=(HttpWebResponse)request.GetResponse()) {
                        int status=(int)response.StatusCode;
                        if(status>=300&&status<400){if(redirect==5||string.IsNullOrEmpty(response.Headers["Location"]))throw new IOException("下载重定向超限");current=new Uri(current,response.Headers["Location"]);continue;}
                        if(status!=200||response.ContentLength>limit)throw new IOException("下载响应或大小无效");
                        using(var input=response.GetResponseStream())using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write)) {
                            long total=0,last=0;byte[] buffer=new byte[65536];int n;
                            while((n=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();total+=n;if(total>limit)throw new IOException("下载超过大小限制");output.Write(buffer,0,n);
                                if(total-last>=4*MB){log("下载进度："+total/MB+" MB"+(response.ContentLength>0?" / "+response.ContentLength/MB+" MB":""));last=total;}}
                            if(total==0||(response.ContentLength>=0&&total!=response.ContentLength))throw new IOException("下载长度不符");
                        }
                        token.ThrowIfCancellationRequested();return;
                    }
                }
            }catch(Exception){NativeLibraryTools.DeleteTemp(path);token.ThrowIfCancellationRequested();throw;}
        }
        string Get(string url,string name,long limit){string path=Path.Combine(stage,name);download(url,path,limit,token,log);if(!File.Exists(path)||new FileInfo(path).Length==0||new FileInfo(path).Length>limit)throw new IOException("下载结果无效");return path;}
        int Run(string exe,IList<string> args,IDictionary<string,string> environment,Action<string> capture)
        {
            token.ThrowIfCancellationRequested();return process(exe,args,root,environment,token,delegate(string line){log(line);if(capture!=null)capture(line);});
        }
        string Verify(string exe,params string[] args)
        {
            log("验证："+Path.GetFileName(exe)+" "+string.Join(" ",args));var text=new StringBuilder();
            if(Run(exe,args,null,x=>{if(text.Length<=4000000)text.AppendLine(x);})!=0)throw new IOException("组件验证失败："+Path.GetFileName(exe));if(text.Length>4000000)throw new IOException("组件输出超过限制");return text.ToString();
        }
        internal static void CheckHash(string path,string expected)
        {
            using(var sha=SHA256.Create())using(var input=File.OpenRead(path))if(BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant()!=expected.ToLowerInvariant())throw new IOException("SHA256 校验不一致");
        }
        internal static string SafeAsset(string name)
        {
            NativeArchiveCore.SafeName(name,new HashSet<string>(StringComparer.OrdinalIgnoreCase));if(name.Contains("/")||name.Contains("\\"))throw new IOException("发布资产名必须是文件名");return name;
        }
        string Cache(string name,string url,long size,string hash,long limit)
        {
            name=SafeAsset(name);string dir=Path.Combine(deps,"Downloads");Directory.CreateDirectory(dir);string cache=Path.Combine(dir,name);
            bool valid=File.Exists(cache)&&(size<0||new FileInfo(cache).Length==size)&&new FileInfo(cache).Length<=limit;
            if(valid&&!string.IsNullOrEmpty(hash)){try{CheckHash(cache,hash);}catch{valid=false;}}
            if(valid){log("使用已检查下载缓存："+name);return cache;}
            string temp=Get(url,"download_"+Guid.NewGuid().ToString("N"),limit);
            if(size>=0&&new FileInfo(temp).Length!=size)throw new IOException("发布资产长度不符");if(!string.IsNullOrEmpty(hash))CheckHash(temp,hash);
            token.ThrowIfCancellationRequested();NativeLibraryTools.Publish(temp,cache);return cache;
        }
        internal static void PublishDirectory(string source,string destination)
        {
            if(Directory.Exists(destination)||File.Exists(destination))throw new IOException("保留已有安装目录："+destination);
            Directory.Move(source,destination);
        }
        string UniqueExe(string directory,string filename)
        {
            var paths=Directory.GetFiles(directory,filename,SearchOption.AllDirectories);if(paths.Length!=1)throw new IOException("安装包必须只有一个 "+filename);return paths[0];
        }
        internal string TarPath {get;set;}
        void InstallFfmpeg()
        {
            string dest=Path.Combine(deps,"FFmpeg");if(Directory.Exists(dest)||File.Exists(dest))throw new IOException("保留已有 FFmpeg 目录，请指定或备份后重试");
            string url="https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.7z";
            Match match=Regex.Match(File.ReadAllText(Get(url+".sha256","full.sha256",4096)),@"\b[0-9a-fA-F]{64}\b");if(!match.Success)throw new IOException("发布方 SHA256 无效");
            string archive=Cache("ffmpeg-release-full.7z",url,-1,match.Value,600*MB),tar=TarPath??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","tar.exe");
            if(!File.Exists(tar))throw new IOException("需要 Windows tar.exe 解包 Full 7z；可直接指定已有 FFmpeg 目录");
            var names=new List<string>();if(Run(tar,new[]{"-tf",archive},null,x=>{if(names.Count<=25000)names.Add(x);})!=0)throw new IOException("tar 无法列出安装包");
            if(names.Count==0||names.Count>25000)throw new IOException("7z 条目数量无效");var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(string name in names)NativeArchiveCore.SafeName(name,seen);
            bool unsafeEntry=false;if(Run(tar,new[]{"-tvf",archive},null,x=>{if(x.Length>0&&"lhbcps".Contains(x[0]))unsafeEntry=true;})!=0||unsafeEntry)throw new IOException("7z 含链接或特殊文件");
            string unpacked=Path.Combine(stage,"unpacked");Directory.CreateDirectory(unpacked);
            if(Run(tar,new[]{"-xf",archive,"-C",unpacked},null,null)!=0)throw new IOException("7z 解包失败");
            long total=0;foreach(string file in Directory.GetFiles(unpacked,"*",SearchOption.AllDirectories)){if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||(total+=new FileInfo(file).Length)>2*1024*MB)throw new IOException("解包输出越界/超限");}
            string ff=UniqueExe(unpacked,"ffmpeg.exe"),probe=Path.Combine(Path.GetDirectoryName(ff),"ffprobe.exe");if(!File.Exists(probe))throw new IOException("缺少同目录 ffprobe");
            Verify(ff,"-version");Verify(probe,"-version");if(!Regex.IsMatch(Verify(ff,"-filters"),@"\blibplacebo\b"))throw new IOException("Full 构建缺少 libplacebo");
            string runtime=Path.Combine(stage,"FFmpeg");Directory.CreateDirectory(runtime);File.Copy(ff,Path.Combine(runtime,"ffmpeg.exe"));File.Copy(probe,Path.Combine(runtime,"ffprobe.exe"));
            token.ThrowIfCancellationRequested();PublishDirectory(runtime,dest);
        }
        Dictionary<string,object> Release(string url)
        {
            return new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Get(url,"release_"+Guid.NewGuid().ToString("N")+".json",2*MB),Encoding.UTF8)) as Dictionary<string,object>;
        }
        void InstallGrav()
        {
            string dest=Path.Combine(deps,"Grav1synth");if(Directory.Exists(dest)||File.Exists(dest))throw new IOException("保留已有 Grav1synth 目录");
            var release=Release("https://api.github.com/repos/rampageX/grav1synth/releases/latest");if(release==null||!release.ContainsKey("assets"))throw new IOException("发布信息无效");
            var selected=((object[])release["assets"]).Cast<Dictionary<string,object>>().Where(x=>Regex.IsMatch(Convert.ToString(x["name"]),@"\.(zip|exe)$",RegexOptions.IgnoreCase)&&Regex.IsMatch(Convert.ToString(x["name"]),@"windows|win64|x86_64|amd64|grav1synth",RegexOptions.IgnoreCase)).ToArray();
            if(selected.Length!=1)throw new IOException("无法唯一选择 Windows grav1synth 资产；请指定已有程序");var asset=selected[0];long size=Convert.ToInt64(asset["size"]);if(size<=0||size>200*MB)throw new IOException("资产大小无效");
            string digest=asset.ContainsKey("digest")?Convert.ToString(asset["digest"]):"",hash=Regex.IsMatch(digest,@"^sha256:[0-9a-fA-F]{64}$")?digest.Substring(7):"";
            string name=SafeAsset(Convert.ToString(asset["name"])),cache=Cache(name,Convert.ToString(asset["browser_download_url"]),size,hash,200*MB),runtime=Path.Combine(stage,"grav_runtime");
            if(name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)){var entries=NativeArchiveCore.Inspect(cache,10000,300*MB,200*MB);NativeArchiveCore.Extract(cache,runtime,entries,x=>true,token);}
            else{Directory.CreateDirectory(runtime);File.Copy(cache,Path.Combine(runtime,"grav1synth.exe"));}
            string exe=UniqueExe(runtime,"grav1synth.exe");Verify(exe,"--version");token.ThrowIfCancellationRequested();PublishDirectory(Path.GetDirectoryName(exe),dest);
        }
        internal static List<string> PythonCandidates(string deps,bool includeSystem)
        {
            var list=new List<string>();string local=Path.Combine(deps,"Python","tools","python.exe");if(File.Exists(local))list.Add(local);
            if(includeSystem)foreach(string part in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))foreach(string file in new[]{"python.exe","py.exe"}){
                try{string path=Path.Combine(Environment.ExpandEnvironmentVariables(part.Trim().Trim('"')),file);if(path.IndexOf("\\WindowsApps\\",StringComparison.OrdinalIgnoreCase)<0&&File.Exists(path)&&!list.Contains(path))list.Add(path);}catch{}
            }return list;
        }
        string FindPython()
        {
            foreach(string path in PythonCandidates(deps,systemPython)) {
                try {
                    string version=Verify(path,"-c","import sys,venv,ensurepip;print(sys.version_info.major*100+sys.version_info.minor)");int n;
                    if(!int.TryParse(version.Trim(),out n)||n<312)continue;
                    string resolved=Verify(path,"-c","import sys;print(sys.executable)").Trim();if(!File.Exists(resolved))continue;
                    string probe=Path.Combine(stage,"venv_check_"+Guid.NewGuid().ToString("N"));
                    try{Verify(resolved,"-m","venv",probe);Verify(Path.Combine(probe,"Scripts","python.exe"),"-m","pip","--version");return resolved;}finally{if(Directory.Exists(probe))Directory.Delete(probe,true);}
                }catch(OperationCanceledException){throw;}catch(Exception ex){log("Python 候选不可用："+ex.Message);}
            }return "";
        }
        string InstallPython()
        {
            string existing=FindPython();if(existing.Length>0){log("复用已验证 Python："+existing);return existing;}
            string dest=Path.Combine(deps,"Python");if(Directory.Exists(dest)||File.Exists(dest))throw new IOException("已有 Python 目录不可用，保留原目录；请备份后重试或勾选使用系统 Python");
            string archive=Get("https://api.nuget.org/v3-flatcontainer/python/3.14.0/python.3.14.0.nupkg","python.nupkg",150*MB),runtime=Path.Combine(stage,"Python");
            var entries=NativeArchiveCore.Inspect(archive,30000,500*MB,100*MB);NativeArchiveCore.Extract(archive,runtime,entries,x=>x.StartsWith("tools/",StringComparison.OrdinalIgnoreCase),token);
            string exe=Path.Combine(runtime,"tools","python.exe");Verify(exe,"-c","import sys,venv,ensurepip;print(sys.version)");
            string probe=Path.Combine(stage,"python_venv_verify");
            try{Verify(exe,"-m","venv",probe);Verify(Path.Combine(probe,"Scripts","python.exe"),"-m","pip","--version");}finally{if(Directory.Exists(probe))Directory.Delete(probe,true);}
            token.ThrowIfCancellationRequested();PublishDirectory(runtime,dest);return Path.Combine(dest,"tools","python.exe");
        }
        internal LibraryToolResult UpdateSvp()
        {
            string svp = Path.Combine(root, "_OpenSVPFlow"), plugins = Path.Combine(svp, "Plugins");
            string pipe = Path.Combine(svp, ".venv", "Scripts", "vspipe.exe"), check = Path.Combine(svp, "Check_OpenSVPFlow.vpy");
            string profile = Path.Combine(svp, "_UserConfig");
            if (!File.Exists(pipe) || !File.Exists(check) || !File.Exists(Path.Combine(profile, "vapoursynth", "vapoursynth.toml")))
                throw new IOException("更新前须安装完整 OpenSVPFlow 与 FGS 私有配置；请先使用依赖安装入口。");
            var env = new Dictionary<string,string>{{"APPDATA",profile},{"PYTHONUTF8","1"},{"PYTHONIOENCODING","utf-8"}};
            log("[1/7] 查询 OpenSVPFlow 最新发布");
            var release = Release("https://api.github.com/repos/Z1xus/open-svpflow/releases/latest");
            if (release == null || !release.ContainsKey("tag_name") || !release.ContainsKey("assets")) throw new IOException("发布信息不完整");
            string tag = Convert.ToString(release["tag_name"]);
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 200 || tag.Any(char.IsControl)) throw new IOException("发布版本标记无效");
            string marker = Path.Combine(plugins, "open-svpflow-version.txt");
            string old = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "unknown";
            log("当前：" + old + "；目标：" + tag);
            Action<string> smoke = dir => {
                foreach (string gpu in new[]{"0","1"}) {
                    token.ThrowIfCancellationRequested();
                    if (Run(pipe,new[]{"--arg","plugin_dir="+dir,"--arg","gpu="+gpu,"--arg","algo=13","--start","0","--end","0",check,"--"},env,null)!=0)
                        throw new IOException("OpenSVPFlow " + (gpu=="0"?"CPU":"GPU/OpenCL") + " 自检失败：" + dir);
                }
            };
            string[] files = {"svpflow1_vs.dll","svpflow2_vs.dll","open-svpflow-version.txt"};
            if (old == tag && files.All(x=>File.Exists(Path.Combine(plugins,x)) && new FileInfo(Path.Combine(plugins,x)).Length > 0)) {
                log("[5/7] 版本相同，验证现有插件"); smoke(plugins); token.ThrowIfCancellationRequested();
                log("已是最新版本，插件未改动。"); return new LibraryToolResult{Skipped=1};
            }
            var selected = ((object[])release["assets"]).Cast<Dictionary<string,object>>().Where(x=>x.ContainsKey("name") && Convert.ToString(x["name"])=="x86_64-pc-windows-msvc.zip").ToArray();
            if (selected.Length != 1) throw new IOException("Windows 更新资产缺失或不唯一");
            var asset=selected[0];long size=Convert.ToInt64(asset["size"]);if(size<=0||size>200*MB)throw new IOException("更新资产大小无效");
            string digest=asset.ContainsKey("digest")?Convert.ToString(asset["digest"]):"",hash="";
            if(!string.IsNullOrEmpty(digest)) {
                if(!Regex.IsMatch(digest,@"^sha256:[0-9a-fA-F]{64}$"))throw new IOException("发布资产 digest 无效或不支持");
                hash=digest.Substring(7);
            }else log("发布方未提供 digest；采用长度、ZIP CRC 与双模式运行验证，未执行发布方 SHA256 对照。");
            log("[2/7] 下载 Windows 插件包");
            string cacheKey;
            using(var sha=SHA256.Create())cacheKey=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(tag+"\n"+Convert.ToString(asset["browser_download_url"])))).Replace("-","").ToLowerInvariant();
            string archive=Cache("svp_"+cacheKey+"_"+SafeAsset(Convert.ToString(asset["name"])),Convert.ToString(asset["browser_download_url"]),size,hash,200*MB);
            log("[3/7] 验证并解包");
            var entries=NativeArchiveCore.Inspect(archive,10000,400*MB,200*MB);
            string unpacked=Path.Combine(stage,"svp_unpack"),ready=Path.Combine(stage,"svp_ready");
            NativeArchiveCore.Extract(archive,unpacked,entries,x=>true,token);Directory.CreateDirectory(ready);
            foreach(string name in files.Take(2)) {
                var paths=Directory.GetFiles(unpacked,name,SearchOption.AllDirectories);
                if(paths.Length!=1 || new FileInfo(paths[0]).Length==0)throw new IOException("插件 DLL 缺失、为空或不唯一："+name);
                File.Copy(paths[0],Path.Combine(ready,name));
            }
            File.WriteAllText(Path.Combine(ready,files[2]),tag+"\r\n",new UTF8Encoding(false));
            log("[4/7] 新插件 CPU / GPU 自检");smoke(ready);token.ThrowIfCancellationRequested();
            log("[5/7] 备份原插件和版本标记");Directory.CreateDirectory(plugins);
            string backup=Path.Combine(svp,"_PluginBackup","UpdateNET_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
            var existed=new HashSet<string>();
            foreach(string name in files)if(File.Exists(Path.Combine(plugins,name))) {
                File.Copy(Path.Combine(plugins,name),Path.Combine(backup,name));existed.Add(name);
            }
            File.WriteAllText(Path.Combine(backup,"backup-info.txt"),"PreviousTag="+old+"\r\nTargetTag="+tag+"\r\n",new UTF8Encoding(false));
            bool touched=false;
            try {
                log("[6/7] 安装新插件");
                foreach(string name in files) {
                    token.ThrowIfCancellationRequested();touched=true;File.Copy(Path.Combine(ready,name),Path.Combine(plugins,name),true);
                }
                log("[7/7] 验证安装后的插件");smoke(plugins);token.ThrowIfCancellationRequested();
            }catch(Exception original) {
                if(touched) {
                    log("更新未完成，恢复原插件状态…");var errors=new List<string>();
                    foreach(string name in files)try {
                        string target=Path.Combine(plugins,name);
                        if(existed.Contains(name))File.Copy(Path.Combine(backup,name),target,true);
                        else if(File.Exists(target))File.Delete(target);
                    }catch(Exception ex){errors.Add(name+"："+ex.Message);}
                    if(errors.Count>0)throw new IOException("回滚未完全成功，请关闭占用程序并从 "+backup+" 恢复："+string.Join("；",errors.ToArray()),original);
                    log("已恢复更新前的插件和版本标记。备份："+backup);
                }
                throw;
            }
            log("更新与双模式验证完成："+tag+"；备份："+backup+"。使用前请重启 FGS 并重检测硬件。");
            return new LibraryToolResult{Success=1};
        }

        void InstallSvp()
        {
            string svp=Path.Combine(root,"_OpenSVPFlow"),venv=Path.Combine(svp,".venv"),venvPython=Path.Combine(venv,"Scripts","python.exe"),pipe=Path.Combine(venv,"Scripts","vspipe.exe"),profile=Path.Combine(svp,"_UserConfig");
            string plugins=Path.Combine(svp,"Plugins"),check=Path.Combine(svp,"Check_OpenSVPFlow.vpy");if(!File.Exists(check))throw new IOException("缺少 OpenSVPFlow 自检文件");
            bool reuse=File.Exists(Path.Combine(plugins,"svpflow1_vs.dll"))&&File.Exists(Path.Combine(plugins,"svpflow2_vs.dll"));
            string testPlugins=plugins;
            if(!reuse){testPlugins=Path.Combine(stage,"Plugins");Directory.CreateDirectory(testPlugins);const string tag="nightly-20260804-5ef4260";
                foreach(string dll in new[]{"svpflow1_vs.dll","svpflow2_vs.dll"})download("https://github.com/Z1xus/open-svpflow/releases/download/"+tag+"/"+dll,Path.Combine(testPlugins,dll),100*MB,token,log);
                File.WriteAllText(Path.Combine(testPlugins,"open-svpflow-version.txt"),tag+"\r\n",new UTF8Encoding(false));
            }else log("优先复用包内现有 SVP 插件，不覆盖已升级版本");
            bool created=false;
            try{
                if(!File.Exists(venvPython)) {
                    if(Directory.Exists(venv)||File.Exists(venv))throw new IOException("已有 .venv 不完整；保留目录，请备份后重试");
                    string python=InstallPython();token.ThrowIfCancellationRequested();created=true;Verify(python,"-m","venv",venv);
                }
                Verify(venvPython,"-c","import sys;assert sys.version_info >= (3,12);print(sys.version)");
                Directory.CreateDirectory(profile);var env=new Dictionary<string,string>{{"APPDATA",profile},{"PYTHONUTF8","1"},{"PYTHONIOENCODING","utf-8"}};
                Action<IList<string>> pythonRun=a=>{if(Run(venvPython,a,env,null)!=0)throw new IOException("Python 安装步骤失败："+string.Join(" ",a.ToArray()));};
                log("[1/5] 更新隔离环境 pip");pythonRun(new[]{"-m","pip","install","--disable-pip-version-check","--timeout","30","--retries","2","--upgrade","pip"});
                log("[2/5] 安装 VapourSynth 79 / BestSource 21.0");pythonRun(new[]{"-m","pip","install","--disable-pip-version-check","--timeout","30","--retries","2","vapoursynth==79","vapoursynth-bestsource==21.0"});
                log("[3/5] 配置 FGS 私有 VapourSynth");string cli=Path.Combine(venv,"Scripts","vapoursynth.exe");
                if(File.Exists(cli)){if(Run(cli,new[]{"config"},env,null)!=0)throw new IOException("私有 VS 配置失败");}else pythonRun(new[]{"-m","vapoursynth","config"});
                if(!File.Exists(Path.Combine(profile,"vapoursynth","vapoursynth.toml")))throw new IOException("私有 VS 配置未生成");
                log("[4/5] 验证 vspipe / CPU / GPU");if(Run(pipe,new[]{"--version"},env,null)!=0)throw new IOException("vspipe 版本验证失败");
                foreach(string gpu in new[]{"0","1"})if(Run(pipe,new[]{"--arg","plugin_dir="+testPlugins,"--arg","gpu="+gpu,"--arg","algo=13","--start","0","--end","0",check,"--"},env,null)!=0)throw new IOException("SVP "+(gpu=="0"?"CPU":"GPU/OpenCL")+" 自检失败");
                token.ThrowIfCancellationRequested();
                if(!reuse) {
                    Directory.CreateDirectory(plugins);string backup=Path.Combine(svp,"_PluginBackup","SetupNET_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
                    string[] files={"svpflow1_vs.dll","svpflow2_vs.dll","open-svpflow-version.txt"};var touched=new List<string>();
                    try{foreach(string name in files){string target=Path.Combine(plugins,name);if(File.Exists(target))File.Copy(target,Path.Combine(backup,name));touched.Add(name);File.Copy(Path.Combine(testPlugins,name),target,true);}}
                    catch{foreach(string name in touched){string old=Path.Combine(backup,name),target=Path.Combine(plugins,name);if(File.Exists(old))File.Copy(old,target,true);else NativeLibraryTools.DeleteTemp(target);}throw;}
                }
                log("[5/5] OpenSVPFlow 安装完成，私有环境与插件验证通过");created=false;
            }finally{if(created&&Directory.Exists(venv)){try{Directory.Delete(venv,true);}catch{log("新建 .venv 未能清理："+venv);}}}
        }
    }
}
