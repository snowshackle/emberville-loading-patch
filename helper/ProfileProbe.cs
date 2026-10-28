using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleDiagnostic {
public static class Probe {
    static readonly object Gate = new object();
    static readonly string Root = BenchHooks.Output;
    static StreamWriter Writer;
    static Timer Sampler;
    static long NextId;
    static Process Self;
    static long LastFrame;
    static long LastFrameSample;
    static int Frames;
    static double MaxFrameGap;
    static UnityEngine.AsyncOperation Loading;
    static bool TestHighPriority=true;
    static UnityEngine.ThreadPriority? PreviousPriority;
    [StructLayout(LayoutKind.Sequential)] struct MemoryCounters { public uint cb, PageFaultCount; public UIntPtr PeakWorkingSet,WorkingSet,QuotaPeakPagedPool,QuotaPagedPool,QuotaPeakNonPagedPool,QuotaNonPagedPool,PagefileUsage,PeakPagefileUsage,PrivateUsage; }
    [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr process,out MemoryCounters counters,uint size);
    [StructLayout(LayoutKind.Sequential)] struct IOCounters { public ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes; }
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr process, out IOCounters counters);
    static Probe() {
        try {
            Directory.CreateDirectory(Root);
            Self = Process.GetCurrentProcess();
            string file = Path.Combine(Root,"trace-"+Self.Id+".tsv");
            Writer = new StreamWriter(new FileStream(file,FileMode.Create,FileAccess.Write,FileShare.Read),new System.Text.UTF8Encoding(false));
            Writer.WriteLine("utc_ticks\tqpc\tevent\tspan\tname\tthread\ta\tb\tc\td\te\tf");
            Record("metadata",0,"process",Self.StartTime.ToUniversalTime().Ticks.ToString(),Stopwatch.Frequency.ToString(),Self.Id.ToString(),"","","");
            Sampler = new Timer(Sample,null,0,500);
            AppDomain.CurrentDomain.ProcessExit += delegate { lock(Gate) { if(Writer!=null) Writer.Flush(); } };
        } catch { }
    }
    static void Record(string kind,long span,string name,string a,string b,string c,string d,string e,string f) {
        try { lock(Gate) {
            if(Writer==null) return;
            Writer.WriteLine(DateTime.UtcNow.Ticks+"\t"+Stopwatch.GetTimestamp()+"\t"+kind+"\t"+span+"\t"+name.Replace('\t',' ').Replace('\n',' ')+"\t"+Thread.CurrentThread.ManagedThreadId+"\t"+a+"\t"+b+"\t"+c+"\t"+d+"\t"+e+"\t"+f);
        }} catch { }
    }
    public static long Begin(string name) {
        BenchHooks.OnBegin(name);
        if(TestHighPriority && name=="SceneLoader.CallScene" && !PreviousPriority.HasValue) {
            PreviousPriority=Application.backgroundLoadingPriority;
            Application.backgroundLoadingPriority=UnityEngine.ThreadPriority.High;
            Mark("experiment.loadingPriority.High");
        }
        if(name=="GameManager.StartGameplay" && PreviousPriority.HasValue) {
            Application.backgroundLoadingPriority=PreviousPriority.Value;PreviousPriority=null;
            Mark("experiment.loadingPriority.Restored");
        }
        long id=Interlocked.Increment(ref NextId);Record("begin",id,name,"","","","","",""); return id;
    }
    public static void End(long id,string name,string status) { Record("end",id,name,status,"","","","",""); BenchHooks.OnEnd(name,status); }
    public static void Mark(string name) { Record("mark",0,name,"","","","","",""); }
    public static long FastBegin() { return Stopwatch.GetTimestamp(); }
    public static void FastEnd(long start,string name) {
        long elapsed=Stopwatch.GetTimestamp()-start;
        if(elapsed>Stopwatch.Frequency/1000) Record("slow_callback",0,name,start.ToString(),elapsed.ToString(),"","","","");
    }
    public static void ObserveLoading(UnityEngine.AsyncOperation op,bool allowed) {
        Loading=op;
        Record("activation",0,"scene",allowed.ToString(),op.progress.ToString("R",CultureInfo.InvariantCulture),"","","","");
        op.allowSceneActivation=allowed;
    }
    public static string DataPath() { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"EmbervillePatch","TestUserData"); }
    public static void Progress(float current,float target) {
        long now=Stopwatch.GetTimestamp();
        Frames++;
        if(LastFrameSample!=0) MaxFrameGap=Math.Max(MaxFrameGap,(double)(now-LastFrameSample)/Stopwatch.Frequency);
        LastFrameSample=now;
        if(now-LastFrame<Stopwatch.Frequency/4) return; LastFrame=now;
        Record("progress",0,"LoadingProgress",current.ToString("R",CultureInfo.InvariantCulture),target.ToString("R",CultureInfo.InvariantCulture),"","","","");
        Record("engine",0,SceneManager.GetActiveScene().name,Application.isFocused.ToString(),Frames.ToString(),MaxFrameGap.ToString("R",CultureInfo.InvariantCulture),Loading==null?"":Loading.progress.ToString("R",CultureInfo.InvariantCulture),Loading==null?"":Loading.isDone.ToString(),Application.backgroundLoadingPriority.ToString());
        Frames=0;MaxFrameGap=0;
    }
    static void Sample(object ignored) {
        try {
            Self.Refresh(); IOCounters io; GetProcessIoCounters(Self.Handle,out io);
            MemoryCounters mem;GetProcessMemoryInfo(Self.Handle,out mem,(uint)Marshal.SizeOf(typeof(MemoryCounters)));
            Record("sample",0,"process",Self.TotalProcessorTime.TotalSeconds.ToString("R",CultureInfo.InvariantCulture),mem.WorkingSet.ToUInt64().ToString(),mem.PrivateUsage.ToUInt64().ToString(),io.ReadBytes.ToString(),io.WriteBytes.ToString(),"native_memory");
            lock(Gate) { if(Writer!=null) Writer.Flush(); }
        } catch { }
    }
}
}
