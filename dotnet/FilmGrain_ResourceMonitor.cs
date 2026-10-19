using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FilmGrainStudioPreview {
    // No UI handles or callbacks are held by the sampler. Latest-only mailbox.
    internal sealed class ResourceSample {
        internal double Cpu=-1, Gpu=-1, MemoryUsed=-1, MemoryTotal=-1, VramUsed=-1, VramTotal=-1;
        internal string GpuName="Unavailable", Error="", Bus="";
        internal long Milliseconds;
        internal double NvmlGpu=-1;
        internal string GpuSource="Unavailable", BusiestEngine="", Luid="";
        internal int EngineInstances;
    }
    // Windows engine counters are per process. Sum processes on the SAME physical
    // engine first, then take the busiest engine; never sum independent engines.
    internal sealed class WindowsGpuEngines : IDisposable {
        IntPtr query,counter;
        bool primed;
        DateTime retryAfter;
        internal bool Priming;
        internal string Error="",Engine="";
        internal int MatchedInstances;
        [StructLayout(LayoutKind.Sequential)] struct Value {internal uint Status;internal double Number;}
        [StructLayout(LayoutKind.Sequential)] struct Item {internal IntPtr Name;internal Value Data;}
        [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhOpenQueryW(string source,UIntPtr data,out IntPtr query);
        [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query,string path,UIntPtr data,out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter,uint format,ref uint size,out uint count,IntPtr buffer);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
        internal sealed class Reading {
            internal string Name;internal double Number;internal uint Status;
            internal Reading(string name,double number,uint status){Name=name;Number=number;Status=status;}
        }
        static readonly System.Text.RegularExpressions.Regex Instance=new System.Text.RegularExpressions.Regex(
            @"(?:^|_)luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.+?)(?:#\d+)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        internal static double Aggregate(System.Collections.Generic.IEnumerable<Reading> readings,string luid,uint nodeMask,out string engine,out int matched) {
            var totals=new System.Collections.Generic.Dictionary<string,double>();
            var names=new System.Collections.Generic.Dictionary<string,string>();matched=0;engine="";
            foreach(var reading in readings){
                if(reading.Status>1||reading.Number<0||double.IsNaN(reading.Number)||double.IsInfinity(reading.Number))continue;
                var m=Instance.Match(reading.Name??"");if(!m.Success)continue;
                uint high,low;int node;
                if(!uint.TryParse(m.Groups[1].Value,System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out high)
                    ||!uint.TryParse(m.Groups[2].Value,System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out low)
                    ||!int.TryParse(m.Groups[3].Value,out node)||node<0||node>=32||(nodeMask&(1u<<node))==0)continue;
                string id=high.ToString("x8")+"_"+low.ToString("x8");if(!string.Equals(id,luid,StringComparison.OrdinalIgnoreCase))continue;
                string key=node+":"+m.Groups[4].Value;
                double prior;totals.TryGetValue(key,out prior);totals[key]=prior+reading.Number;
                names[key]=m.Groups[5].Value+" (node "+node+", engine "+m.Groups[4].Value+")";matched++;
            }
            double maximum=-1;
            foreach(var pair in totals)if(pair.Value>maximum){maximum=pair.Value;engine=names[pair.Key];}
            return maximum<0?-1:Math.Min(100,maximum);
        }
        internal double Sample(string luid,uint nodeMask){
            Priming=false;Engine="";MatchedInstances=0;Error="";
            if(string.IsNullOrEmpty(luid)||nodeMask==0){Error="CUDA/WDDM LUID mapping unavailable";return -1;}
            try {
                if(query==IntPtr.Zero){
                    if(DateTime.UtcNow<retryAfter){Error="PDH retry pending";return -1;}
                    uint rc=PdhOpenQueryW(null,UIntPtr.Zero,out query);if(rc!=0)throw new InvalidOperationException("PdhOpenQuery=0x"+rc.ToString("x"));
                    rc=PdhAddEnglishCounterW(query,@"\GPU Engine(*)\Utilization Percentage",UIntPtr.Zero,out counter);
                    if(rc!=0)throw new InvalidOperationException("PdhAddEnglishCounter=0x"+rc.ToString("x"));
                }
                uint status=PdhCollectQueryData(query);if(status!=0)throw new InvalidOperationException("PdhCollect=0x"+status.ToString("x"));
                if(!primed){primed=true;Priming=true;return -1;}
                for(int attempt=0;attempt<3;attempt++){
                    uint size=0,count;status=PdhGetFormattedCounterArrayW(counter,0x200|0x8000,ref size,out count,IntPtr.Zero);
                    if(status!=0x800007d2||size==0||size>32*1024*1024)throw new InvalidOperationException("PdhArray size=0x"+status.ToString("x")+", bytes="+size);
                    IntPtr buffer=Marshal.AllocHGlobal((int)size);
                    try {
                        uint capacity=size;status=PdhGetFormattedCounterArrayW(counter,0x200|0x8000,ref size,out count,buffer);
                        if(status==0x800007d2)continue;
                        if(status!=0)throw new InvalidOperationException("PdhArray=0x"+status.ToString("x"));
                        int stride=Marshal.SizeOf(typeof(Item));if((ulong)count*(uint)stride>capacity)throw new InvalidOperationException("Invalid PDH item count");
                        var readings=new System.Collections.Generic.List<Reading>();
                        for(int i=0;i<count;i++){
                            Item item=(Item)Marshal.PtrToStructure(IntPtr.Add(buffer,i*stride),typeof(Item));
                            readings.Add(new Reading(Marshal.PtrToStringUni(item.Name),item.Data.Number,item.Data.Status));
                        }
                        double result=Aggregate(readings,luid,nodeMask,out Engine,out MatchedInstances);
                        if(result<0)Error="No valid GPU Engine instances for LUID "+luid;
                        return result;
                    }finally{Marshal.FreeHGlobal(buffer);}
                }
                throw new InvalidOperationException("PDH instances changed repeatedly");
            }catch(Exception ex){Error=ex.GetType().Name+": "+ex.Message;Dispose();retryAfter=DateTime.UtcNow.AddSeconds(30);return -1;}
        }
        internal string ReleaseStatus="not opened";
        public void Dispose(){if(query!=IntPtr.Zero){try{ReleaseStatus="GPU PdhCloseQuery="+PdhCloseQuery(query);}catch(Exception ex){ReleaseStatus=ex.Message;}query=counter=IntPtr.Zero;}primed=false;}
    }
    internal sealed class ResourceSampler : IDisposable {
        readonly object gate=new object();
        readonly WindowsGpuEngines windowsGpu=new WindowsGpuEngines();
        string luid="",luidError="";uint nodeMask;
        System.Threading.Timer timer;
        volatile bool stopped;
        internal volatile bool Slow;
        internal ResourceSample Latest;
        bool nvml, initialized;
        DateTime retryAfter=DateTime.MinValue;
        IntPtr device, module, cpuQuery, cpuCounter;
        bool cpuQueryTried, cpuQueryPrimed;
        ulong previousIdle, previousKernel, previousUser;
        internal volatile string ReleaseStatus="pending";
        [StructLayout(LayoutKind.Sequential)] struct Times { internal uint Low,High; internal ulong Value {get{return ((ulong)High<<32)|Low;}} }
        [StructLayout(LayoutKind.Sequential)] struct Memory { internal uint Length,Load; internal ulong Total,Available,PageTotal,PageAvailable,VirtualTotal,VirtualAvailable,Extended; }
        [StructLayout(LayoutKind.Sequential)] struct Util {internal uint Gpu,Memory;}
        [StructLayout(LayoutKind.Sequential)] struct VideoMemory {internal ulong Total,Free,Used;}
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out Times idle,out Times kernel,out Times user);
        [DllImport("kernel32.dll")] static extern uint GetActiveProcessorCount(ushort group);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool GlobalMemoryStatusEx(ref Memory memory);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        [DllImport("nvcuda.dll",CallingConvention=CallingConvention.Winapi)] static extern int cuInit(uint flags);
        [DllImport("nvcuda.dll",CallingConvention=CallingConvention.Winapi)] static extern int cuDeviceGet(out int device,int ordinal);
        [DllImport("nvcuda.dll",CallingConvention=CallingConvention.Winapi)] static extern int cuDeviceGetPCIBusId(StringBuilder id,int length,int device);
        [DllImport("nvcuda.dll",CallingConvention=CallingConvention.Winapi)] static extern int cuDeviceGetLuid([Out] byte[] luid,out uint mask,int device);
        [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] static extern int nvmlInit_v2();
        [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] static extern int nvmlShutdown();
        [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] static extern int nvmlDeviceGetHandleByPciBusId_v2(string id,out IntPtr device);
        [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] static extern int nvmlDeviceGetName(IntPtr device,StringBuilder name,uint length);
        [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] static extern int nvmlDeviceGetUtilizationRates(IntPtr device,out Util value);
        [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] static extern int nvmlDeviceGetMemoryInfo(IntPtr device,out VideoMemory value);
        [StructLayout(LayoutKind.Sequential)] struct CounterValue { internal uint Status; internal double Value; }
        [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhOpenQueryW(string source,UIntPtr data,out IntPtr query);
        [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query,string path,UIntPtr data,out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter,uint format,out uint type,out CounterValue value);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
        double GroupCpu() {
            if(!cpuQueryTried){
                cpuQueryTried=true;
                if(PdhOpenQueryW(null,UIntPtr.Zero,out cpuQuery)!=0)return -1;
                if(PdhAddEnglishCounterW(cpuQuery,@"\Processor Information(_Total)\% Processor Time",UIntPtr.Zero,out cpuCounter)!=0)return -1;
            }
            if(cpuCounter==IntPtr.Zero||PdhCollectQueryData(cpuQuery)!=0)return -1;
            if(!cpuQueryPrimed){cpuQueryPrimed=true;return -1;}
            uint type;CounterValue value;
            if(PdhGetFormattedCounterValue(cpuCounter,0x200,out type,out value)!=0 || value.Status>1)return -1;
            return Math.Max(0,Math.Min(100,value.Value));
        }
        string gpuName="Unavailable",bus="",initError="";
        internal ResourceSampler(){timer=new System.Threading.Timer(Tick,null,1000,Timeout.Infinite);}
        void InitializeGpu() {
            initialized=true;initError="";retryAfter=DateTime.UtcNow.AddSeconds(30);
            try {
                int cuda; var pci=new StringBuilder(80);
                if(cuInit(0)!=0||cuDeviceGet(out cuda,0)!=0||cuDeviceGetPCIBusId(pci,pci.Capacity,cuda)!=0) throw new InvalidOperationException("CUDA ordinal 0 PCI mapping unavailable");
                bus=pci.ToString();luid="";nodeMask=0;luidError="";
                try {
                    byte[] bytes=new byte[8];int code=cuDeviceGetLuid(bytes,out nodeMask,cuda);
                    if(code==0 && nodeMask!=0)luid=BitConverter.ToUInt32(bytes,4).ToString("x8")+"_"+BitConverter.ToUInt32(bytes,0).ToString("x8");
                    else luidError="cuDeviceGetLuid="+code;
                }catch(Exception ex){luidError=ex.Message;}
                // Load only a known driver path, never the working directory.
                string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"nvml.dll");
                if(!File.Exists(path)) path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"NVIDIA Corporation\NVSMI\nvml.dll");
                if(!File.Exists(path)||(module=LoadLibraryW(path))==IntPtr.Zero) throw new InvalidOperationException("NVML driver library unavailable");
                int rc=nvmlInit_v2(); if(rc!=0) throw new InvalidOperationException("nvmlInit_v2="+rc);
                nvml=true;
                rc=nvmlDeviceGetHandleByPciBusId_v2(bus,out device); if(rc!=0) throw new InvalidOperationException("NVML PCI lookup="+rc);
                var name=new StringBuilder(128);rc=nvmlDeviceGetName(device,name,128);if(rc==0)gpuName=name.ToString();
            } catch(Exception ex){device=IntPtr.Zero;initError=ex.GetType().Name+": "+ex.Message;}
        }
        void Tick(object ignored) {
            if(!Monitor.TryEnter(gate))return;
            try {
                if(stopped)return;
                var watch=Stopwatch.StartNew();var s=new ResourceSample();
                try {
                    Times idle,kernel,user;
                    if(GetActiveProcessorCount(0xffff)>64){s.Cpu=GroupCpu();if(s.Cpu<0)s.Error="PDH all-group CPU unavailable/priming; ";}
                    else if(GetSystemTimes(out idle,out kernel,out user)) {
                        ulong k=kernel.Value,u=user.Value,i=idle.Value;
                        if(previousKernel>0 && k>=previousKernel && u>=previousUser && i>=previousIdle) {
                            ulong total=(k-previousKernel)+(u-previousUser), inactive=i-previousIdle;
                            if(total>0 && inactive<=total)s.Cpu=100.0*(total-inactive)/total;
                        }
                        previousIdle=i;previousKernel=k;previousUser=u;
                    } else s.Error="GetSystemTimes unavailable; ";
                }catch(Exception ex){s.Error+="CPU: "+ex.Message+"; ";}
                try {
                    Memory m=new Memory();m.Length=(uint)Marshal.SizeOf(typeof(Memory));
                    if(GlobalMemoryStatusEx(ref m)){s.MemoryTotal=m.Total;s.MemoryUsed=m.Total-m.Available;}else s.Error+="GlobalMemoryStatusEx failed; ";
                }catch(Exception ex){s.Error+="RAM: "+ex.Message+"; ";}
                try {
                    if(initialized && device==IntPtr.Zero && DateTime.UtcNow>=retryAfter){ReleaseGpu();initialized=false;}
                    if(!initialized)InitializeGpu();s.GpuName=gpuName;s.Bus=bus;s.Error+=initError;
                    if(device!=IntPtr.Zero){
                        Util util;int rc=nvmlDeviceGetUtilizationRates(device,out util);
                        if(rc==0 && util.Gpu<=100)s.NvmlGpu=util.Gpu;else {s.Error+=" GPU util="+rc;}
                        VideoMemory v;rc=nvmlDeviceGetMemoryInfo(device,out v);
                        if(rc==0 && v.Total>0 && v.Used<=v.Total){s.VramTotal=v.Total;s.VramUsed=v.Used;}else s.Error+=" VRAM="+rc;
                        if(rc==15){device=IntPtr.Zero;retryAfter=DateTime.UtcNow.AddSeconds(30);}
                    }
                }catch(Exception ex){s.Error+=ex.ToString();}
                try {
                    s.Luid=luid;s.Gpu=windowsGpu.Sample(luid,nodeMask);s.BusiestEngine=windowsGpu.Engine;s.EngineInstances=windowsGpu.MatchedInstances;
                    if(s.Gpu>=0)s.GpuSource="Windows busiest engine";
                    else if(windowsGpu.Priming)s.GpuSource="Windows priming";
                    else if(s.NvmlGpu>=0){s.Gpu=s.NvmlGpu;s.GpuSource="NVML fallback";}
                    s.Error+=(string.IsNullOrEmpty(luidError)?"":"; "+luidError)+(string.IsNullOrEmpty(windowsGpu.Error)?"":"; "+windowsGpu.Error);
                }catch(Exception ex){s.Error+="; Windows GPU: "+ex.Message;if(s.NvmlGpu>=0){s.Gpu=s.NvmlGpu;s.GpuSource="NVML fallback";}}
                s.Milliseconds=watch.ElapsedMilliseconds;Interlocked.Exchange(ref Latest,s);
            }finally {
                if(!stopped)try{timer.Change(Slow?5000:1000,Timeout.Infinite);}catch(ObjectDisposedException){}
                Monitor.Exit(gate);
            }
        }
        string ReleaseGpu(){
            string status;
            try{status=nvml?"nvmlShutdown="+nvmlShutdown():"no NVML session";}catch(Exception ex){status=ex.Message;}
            device=IntPtr.Zero;nvml=false;
            if(module!=IntPtr.Zero){status+="; FreeLibrary="+FreeLibrary(module);module=IntPtr.Zero;}
            return status;
        }
        public void Dispose(){
            stopped=true;timer.Dispose();ReleaseStatus="pending";
            ThreadPool.QueueUserWorkItem(delegate {lock(gate){
                windowsGpu.Dispose();string status=ReleaseGpu()+"; "+windowsGpu.ReleaseStatus;
                if(cpuQuery!=IntPtr.Zero){try{status+="; PdhCloseQuery="+PdhCloseQuery(cpuQuery);}catch(Exception ex){status+="; PDH release: "+ex.Message;}cpuQuery=IntPtr.Zero;}
                ReleaseStatus=status;
            }});
        }
    }

    internal sealed class ResourceMonitorPanel : Control,IThemeSurface {
        readonly ToolTip tip=new ToolTip();
        readonly string memoryLabel, videoLabel;
        readonly System.Windows.Forms.Timer pulse=new System.Windows.Forms.Timer();
        ResourceSampler sampler;ResourceSample sample;Form owner;
        public bool DarkTheme {get;set;}
        internal ResourceMonitorPanel(string memoryLabel,string videoLabel){
            this.memoryLabel=memoryLabel;this.videoLabel=videoLabel;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            Dock=DockStyle.Fill;Margin=new Padding(8,0,8,0);
            pulse.Interval=250;pulse.Tick+=delegate{
                if(sampler==null)return;sampler.Slow=owner!=null&&owner.WindowState==FormWindowState.Minimized;
                var latest=Interlocked.Exchange(ref sampler.Latest,null);if(latest==null)return;
                sample=latest;bool zh=memoryLabel=="内存";
                string help=zh?
                    "CPU：全部逻辑处理器的总占用；内存：系统物理内存已用 / 总量。\n"+
                    "监控 GPU："+latest.GpuName+"；CUDA 设备 0；PCI "+latest.Bus+"；LUID "+latest.Luid+"\n"+
                    "GPU 来源："+latest.GpuSource+"；最忙引擎："+latest.BusiestEngine+"\n"+
                    "Windows 口径：同一物理引擎各进程占用合计，取最忙引擎；不相加不同引擎。\n"+
                    "GPU* 表示 NVML 回退，与 nvidia-smi 接近，不等于 Windows 最忙引擎。\n"+
                    "显存：NVML 专用显存，不含共享内存；WDDM 保留量可能造成差异。\n"+
                    "Vulkan / 自动编码设备可能不同；这里明确监控 CUDA 设备 0。\n约每秒采样；最小化约 5 秒；不可用显示 —。":
                    "CPU: all logical processors; RAM: physical used / total.\nGPU: "+latest.GpuName+" | CUDA 0 | PCI "+latest.Bus+" | LUID "+latest.Luid+
                    "\nSource: "+latest.GpuSource+" | Busiest: "+latest.BusiestEngine+
                    "\nWindows: sum processes per physical engine, then take busiest engine. GPU* = NVML fallback (nvidia-smi metric)."+
                    "\nVRAM: dedicated NVML memory, no shared memory. Vulkan/automatic encoder device may differ. ~1s sampling, minimized ~5s.";
                tip.SetToolTip(this,help+(string.IsNullOrEmpty(latest.Error)?"":"\n"+latest.Error));Invalidate();
            };
        }
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);if(owner==null){owner=FindForm();if(owner!=null)owner.Shown+=Start;}}
        void Start(object sender,EventArgs e){if(sampler==null){sampler=new ResourceSampler();pulse.Start();}}
        protected override void Dispose(bool disposing){if(disposing){if(owner!=null)owner.Shown-=Start;pulse.Stop();pulse.Dispose();if(sampler!=null)sampler.Dispose();tip.Dispose();}base.Dispose(disposing);}
        protected override void OnPaint(PaintEventArgs e){
            ResourceMonitorPainter.Draw(e.Graphics,ClientRectangle,Font,DarkTheme,BackColor,DarkTheme?UiTheme.TextColor:ForeColor,sample,memoryLabel,videoLabel);
        }
    }
    internal static class ResourceMonitorPainter {
        internal static void Draw(Graphics graphics,Rectangle bounds,Font font,bool dark,Color background,Color foreground,ResourceSample sample,string memoryLabel,string videoLabel){
            graphics.Clear(background);float scale=graphics.DpiX/96f;
            int row=bounds.Height/4;if(row<1)return;
            var s=sample??new ResourceSample();
            string[] labels={"CPU",s.GpuSource=="NVML fallback"?"GPU*":"GPU",memoryLabel,videoLabel};
            double[] values={s.Cpu,s.Gpu,s.MemoryTotal>0?100*s.MemoryUsed/s.MemoryTotal:-1,s.VramTotal>0?100*s.VramUsed/s.VramTotal:-1};
            string[] texts={s.Cpu<0?"—":s.Cpu.ToString("0")+"%",s.Gpu<0?"—":s.Gpu.ToString("0")+"%",Capacity(s.MemoryUsed,s.MemoryTotal),Capacity(s.VramUsed,s.VramTotal)};
            var previousHint=graphics.TextRenderingHint;graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using(var labelFormat=new StringFormat(StringFormat.GenericTypographic))
            using(var numberFormat=new StringFormat(StringFormat.GenericTypographic))
            using(var ink=new SolidBrush(foreground))
            using(var track=new SolidBrush(dark?Color.FromArgb(49,53,56):Color.FromArgb(226,234,230)))
            using(var fill=new SolidBrush(dark?Color.FromArgb(37,116,91):Color.FromArgb(163,212,190))) {
                labelFormat.LineAlignment=numberFormat.LineAlignment=StringAlignment.Center;
                labelFormat.FormatFlags=numberFormat.FormatFlags=StringFormatFlags.NoWrap;
                numberFormat.Alignment=StringAlignment.Center;numberFormat.Trimming=StringTrimming.EllipsisCharacter;
                int left=(int)Math.Round(43*scale),gap=Math.Max(2,(int)Math.Round(3*scale));
                int height=Math.Min(row-gap,(int)Math.Ceiling(font.GetHeight(graphics)+4*scale));
                if(height<1)return;
                for(int i=0;i<4;i++){
                    int y=bounds.Top+i*row+(row-height)/2;int width=Math.Max(0,bounds.Width-left);
                    Rectangle bar=new Rectangle(bounds.Left+left,y,width,height);
                    graphics.FillRectangle(track,bar);
                    if(values[i]>=0)graphics.FillRectangle(fill,bar.X,bar.Y,(int)Math.Round(width*Math.Min(100,Math.Max(0,values[i]))/100),bar.Height);
                    graphics.DrawString(labels[i],font,ink,new RectangleF(bounds.Left,y,left-gap,height),labelFormat);
                    int inset=Math.Max(3,(int)Math.Round(5*scale));
                    graphics.DrawString(texts[i],font,ink,new RectangleF(bar.X+inset,bar.Y,Math.Max(0,bar.Width-inset*2),bar.Height),numberFormat);
                }
            }
            graphics.TextRenderingHint=previousHint;
        }
        static string Capacity(double used,double total){if(used<0||total<=0)return "—";double unit=total>=1073741824?1073741824:1048576;return (used/unit).ToString("0.0")+" / "+(total/unit).ToString("0.0")+(unit>1048576?" GB":" MB");}
    }

}
