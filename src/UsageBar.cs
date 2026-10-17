using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageBar {
static class Json {
    public static Dictionary<string,object> Obj(object v) { return v as Dictionary<string,object> ?? new Dictionary<string,object>(); }
    public static object Get(object v,string k) { object r; return Obj(v).TryGetValue(k,out r)?r:null; }
    public static long Num(object v) { return v==null?0:Convert.ToInt64(v,CultureInfo.InvariantCulture); }
    public static string Str(object v) { return v==null?"":Convert.ToString(v,CultureInfo.InvariantCulture); }
    public static object Parse(string s) { return new JavaScriptSerializer{MaxJsonLength=16*1024*1024}.DeserializeObject(s); }
    public static string Encode(object v) { return new JavaScriptSerializer{MaxJsonLength=16*1024*1024}.Serialize(v); }
}
sealed class Tokens {
    public long Input,Output,Cached;
    public long Total { get { return Input+Output; } }
    public string Day; public int Files; public int Errors;
    public ConversationRate[] Conversations=new ConversationRate[0];
}
sealed class ConversationRate {
    public string Id,Title,TurnId;
    public bool Running,Stale;
    public DateTimeOffset Started,LastEvent;
    public long Input,Output,Cached;
    public long Total { get { return Input+Output; } }
    public double Seconds;
    public double? PerSecond { get { return Rate(Total); } }
    public double? OutputPerSecond { get { return Rate(Output); } }
    double? Rate(long count) { return !Running?0:Stale||Seconds<=0?null:(double?)count/Seconds; }
}
sealed class FileCursor {
    public long Offset,Input,Output,Cached;
    public string Carry="";
    public Decoder Decoder=Encoding.UTF8.GetDecoder();
    public string Id="",TurnId="";
    public bool Desktop,Running;
    public DateTimeOffset Created,Started,LastEvent;
    public long TurnInput,TurnOutput,TurnCached;
}
static class UserTime {
    public static DateTimeOffset InZone(DateTimeOffset instant,TimeZoneInfo zone) { return TimeZoneInfo.ConvertTime(instant,zone); }
    public static string Day(DateTimeOffset instant,TimeZoneInfo zone) { return InZone(instant,zone).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture); }
    public static DateTime DayStartUtc(DateTimeOffset instant,TimeZoneInfo zone) {
        DateTime midnight=DateTime.SpecifyKind(InZone(instant,zone).Date,DateTimeKind.Unspecified);
        // Some zones change clocks at midnight: use the first valid instant,
        // or the earlier occurrence when midnight repeats.
        while(zone.IsInvalidTime(midnight)) midnight=midnight.AddMinutes(1);
        TimeSpan offset=zone.IsAmbiguousTime(midnight)?zone.GetAmbiguousTimeOffsets(midnight).Max():zone.GetUtcOffset(midnight);
        return new DateTimeOffset(midnight,offset).UtcDateTime;
    }
}
sealed class TokenLedger {
    readonly string home;
    readonly Dictionary<string,FileCursor> files=new Dictionary<string,FileCursor>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> seen=new HashSet<string>();
    readonly Dictionary<string,string> titles=new Dictionary<string,string>();
    List<string> paths=new List<string>();
    DateTimeOffset nextDiscovery=DateTimeOffset.MinValue;
    Tokens tokens=new Tokens();
    TimeZoneInfo previousZone;
    public TokenLedger(string h) { home=h; }
    public Tokens Scan(DateTimeOffset now) {
        TimeZoneInfo.ClearCachedData();
        return Scan(now,TimeZoneInfo.Local);
    }
    internal Tokens Scan(DateTimeOffset now,TimeZoneInfo zone) {
        string day=UserTime.Day(now,zone);
        if(tokens.Day!=day||previousZone==null||!previousZone.Equals(zone)) {
            tokens=new Tokens{Day=day}; files.Clear(); seen.Clear(); nextDiscovery=DateTimeOffset.MinValue; previousZone=zone;
        }
        DateTime cutoff=UserTime.DayStartUtc(now,zone);
        // Only directory discovery is throttled. Known logs and the rate are read every second.
        if(now>=nextDiscovery) {
        paths=new List<string>(); nextDiscovery=now.AddSeconds(5);
        foreach(string dir in new[]{"sessions","archived_sessions"}) {
            string root=Path.Combine(home,dir);
            if(!Directory.Exists(root)) continue;
            try { paths.AddRange(Directory.EnumerateFiles(root,"*.jsonl",SearchOption.AllDirectories)
                .Where(p=>File.GetLastWriteTimeUtc(p)>=cutoff)); } catch { tokens.Errors++; }
        }
        try {
            using(FileStream fs=new FileStream(Path.Combine(home,"session_index.jsonl"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(StreamReader reader=new StreamReader(fs,Encoding.UTF8)) {
                string line;
                while((line=reader.ReadLine())!=null) {
                    object entry; try { entry=Json.Parse(line); } catch { continue; }
                    string id=Json.Str(Json.Get(entry,"id")),title=Json.Str(Json.Get(entry,"thread_name"));
                    if(id.Length>0&&title.Length>0) titles[id]=title;
                }
            }
        } catch { /* Titles are optional; session IDs still identify the conversation. */ }
        }
        int errors=0;
        foreach(string path in paths.OrderBy(p=>p,StringComparer.Ordinal)) {
            try {
                FileCursor c;
                if(!files.TryGetValue(path,out c)) { c=new FileCursor(); files[path]=c; }
                using(FileStream fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                    // A rewritten log must be fully reread; global event deduplication prevents replay.
                    if(fs.Length<c.Offset) { c=new FileCursor(); files[path]=c; }
                    if(fs.Length==c.Offset) continue;
                    fs.Position=c.Offset;
                    byte[] bytes=new byte[65536]; char[] chars=new char[65536]; int n;
                    while((n=fs.Read(bytes,0,bytes.Length))>0) {
                        int count=c.Decoder.GetChars(bytes,0,n,chars,0,false);
                        string chunk=c.Carry+new string(chars,0,count);
                        int start=0,end;
                        while((end=chunk.IndexOf('\n',start))>=0) {
                            string line=chunk.Substring(start,end-start); start=end+1;
                            if(line.IndexOf("\"token_count\"",StringComparison.Ordinal)>=0||
                               line.IndexOf("\"session_meta\"",StringComparison.Ordinal)>=0||
                               line.IndexOf("\"task_started\"",StringComparison.Ordinal)>=0||
                               line.IndexOf("\"task_complete\"",StringComparison.Ordinal)>=0||
                               line.IndexOf("\"turn_aborted\"",StringComparison.Ordinal)>=0) ParseLine(line,c,day,zone);
                        }
                        c.Carry=chunk.Substring(start);
                        c.Offset=fs.Position;
                    }
                }
            } catch { errors++; }
        }
        tokens.Files=paths.Count; tokens.Errors=errors;
        List<ConversationRate> conversations=new List<ConversationRate>();
        foreach(FileCursor c in files.Values.Where(c=>c.Desktop&&c.Started!=default(DateTimeOffset)&&c.Started<=now)) {
            string title; if(!titles.TryGetValue(c.Id,out title)) title=c.Id;
            conversations.Add(new ConversationRate{Id=c.Id,Title=title,TurnId=c.TurnId,Running=c.Running,
                Started=c.Started,LastEvent=c.LastEvent,Input=c.TurnInput,Output=c.TurnOutput,Cached=c.TurnCached,
                Seconds=Math.Max(0,(now-c.Started).TotalSeconds),Stale=c.Running&&now-c.LastEvent>TimeSpan.FromMinutes(15)});
        }
        // Archived copies of the same log never create a second selectable conversation.
        tokens.Conversations=conversations.GroupBy(c=>c.Id).Select(g=>g.OrderByDescending(c=>c.LastEvent).First())
            .OrderByDescending(c=>c.Started).ToArray();
        return new Tokens{Day=tokens.Day,Input=tokens.Input,Output=tokens.Output,Cached=tokens.Cached,Files=tokens.Files,Errors=tokens.Errors,Conversations=tokens.Conversations};
    }
    void ParseLine(string line,FileCursor c,string day,TimeZoneInfo zone) {
        object e;
        try { e=Json.Parse(line); } catch { return; }
        string eventType=Json.Str(Json.Get(e,"type"));
        object p=Json.Get(e,"payload");
        DateTimeOffset ts;
        if(!DateTimeOffset.TryParse(Json.Str(Json.Get(e,"timestamp")),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out ts)) return;
        if(eventType=="session_meta") {
            c.Id=Json.Str(Json.Get(p,"id")); c.Created=ts;
            c.Desktop=c.Id.Length>0&&Json.Get(p,"source") is string&&
                Json.Str(Json.Get(p,"originator")).Equals("Codex Desktop",StringComparison.OrdinalIgnoreCase);
            return;
        }
        if(eventType!="event_msg") return;
        string type=Json.Str(Json.Get(p,"type"));
        // Copied fork history predating this session does not describe its current activity.
        bool current=c.Desktop&&ts>=c.Created;
        if(type=="task_started"&&current) {
            string turn=Json.Str(Json.Get(p,"turn_id"));
            if(turn.Length>0&&turn!=c.TurnId) {
                c.TurnId=turn; c.Started=ts; c.LastEvent=ts; c.Running=true;
                c.TurnInput=0; c.TurnOutput=0; c.TurnCached=0;
            }
            return;
        }
        if((type=="task_complete"||type=="turn_aborted")&&current) {
            string turn=Json.Str(Json.Get(p,"turn_id"));
            if(turn.Length==0||turn==c.TurnId) { c.Running=false; c.LastEvent=ts; }
            return;
        }
        if(type!="token_count") return;
        object usage=Json.Get(Json.Get(p,"info"),"total_token_usage");
        if(usage==null) return;
        long input=Json.Num(Json.Get(usage,"input_tokens"));
        long output=Json.Num(Json.Get(usage,"output_tokens"));
        long cached=Json.Num(Json.Get(usage,"cached_input_tokens"));
        // Repeated quota-only token_count events carry the same cumulative snapshot.
        long di=input>=c.Input?input-c.Input:input;
        long dout=output>=c.Output?output-c.Output:output;
        long dc=cached>=c.Cached?cached-c.Cached:cached;
        c.Input=input; c.Output=output; c.Cached=cached;
        if(current&&c.Running&&ts>=c.Started) {
            c.TurnInput+=di; c.TurnOutput+=dout; c.TurnCached+=dc; c.LastEvent=ts;
        }
        if(UserTime.Day(ts,zone)!=day) return;
        string key=ts.UtcTicks.ToString()+":"+input+":"+output+":"+cached;
        if(!seen.Add(key)) return;
        tokens.Input+=di; tokens.Output+=dout; tokens.Cached+=dc;
    }
    public static ConversationRate Select(Tokens snapshot,string id) {
        if(!String.IsNullOrEmpty(id)) return snapshot.Conversations.FirstOrDefault(c=>c.Id==id);
        return snapshot.Conversations.FirstOrDefault(c=>c.Running&&!c.Stale)??snapshot.Conversations.FirstOrDefault();
    }
}
sealed class Quota {
    public double? Five,Week;
    public long FiveReset,WeekReset;
    public DateTimeOffset Updated;
    public string Error="";
    public static Quota From(object result) {
        object buckets=Json.Get(result,"rateLimitsByLimitId");
        object bucket=Json.Get(buckets,"codex")??Json.Get(result,"rateLimits");
        Quota q=new Quota{Updated=DateTimeOffset.UtcNow};
        foreach(string key in new[]{"primary","secondary"}) {
            object window=Json.Get(bucket,key); if(window==null) continue;
            object used=Json.Get(window,"usedPercent"); if(used==null) continue;
            double remaining=Math.Max(0,Math.Min(100,100-Convert.ToDouble(used,CultureInfo.InvariantCulture)));
            long mins=Json.Num(Json.Get(window,"windowDurationMins"));
            if(mins==300) { q.Five=remaining; q.FiveReset=Json.Num(Json.Get(window,"resetsAt")); }
            if(mins==10080) { q.Week=remaining; q.WeekReset=Json.Num(Json.Get(window,"resetsAt")); }
        }
        if(q.Five==null&&q.Week==null) q.Error="账号未返回这两个额度窗口";
        return q;
    }
}
sealed class QuotaNetwork {
    public string Source="inherited_environment_or_direct",Proxy="",Bypass="localhost,127.0.0.1,::1";
    public bool Direct;
    public string Endpoint { get { Uri u; return Uri.TryCreate(Proxy,UriKind.Absolute,out u)?u.Scheme+"://"+u.Host+":"+u.Port:""; } }
    public static QuotaNetwork FromCommandLine(string command) {
        if(Regex.IsMatch(command??"",@"(?:^|\s)--no-proxy-server(?:\s|$)",RegexOptions.IgnoreCase))
            return new QuotaNetwork{Source="codex_direct_argument",Direct=true};
        Match match=Regex.Match(command??"",@"(?:^|\s)--proxy-server(?:=|\s+)(?:""([^""]+)""|([^\s""]+))",RegexOptions.IgnoreCase);
        if(!match.Success) return null;
        string value=match.Groups[1].Success?match.Groups[1].Value:match.Groups[2].Value;
        string proxy=Normalize(value);
        if(proxy.Length==0) throw new InvalidOperationException("无法识别 Codex 的代理启动参数");
        QuotaNetwork plan=new QuotaNetwork{Source="codex_launch_argument",Proxy=proxy};
        Match bypass=Regex.Match(command??"",@"(?:^|\s)--proxy-bypass-list(?:=|\s+)(?:""([^""]+)""|([^\s""]+))",RegexOptions.IgnoreCase);
        if(bypass.Success) {
            string list=bypass.Groups[1].Success?bypass.Groups[1].Value:bypass.Groups[2].Value;
            plan.Bypass=String.Join(",",list.Split(';').Where(s=>s.Length>0&&!s.StartsWith("<",StringComparison.Ordinal)).Select(s=>s=="[::1]"?"::1":s));
        }
        return plan;
    }
    internal static string Normalize(string value) {
        if(String.IsNullOrWhiteSpace(value)) return "";
        value=value.Trim();
        if(Regex.IsMatch(value,@"^(?:https?|socks)=",RegexOptions.IgnoreCase)) {
            string[] parts=value.Split(';');
            string entry=parts.FirstOrDefault(s=>s.StartsWith("https=",StringComparison.OrdinalIgnoreCase))??parts.FirstOrDefault(s=>s.StartsWith("http=",StringComparison.OrdinalIgnoreCase));
            if(entry==null) return ""; value=entry.Substring(entry.IndexOf('=')+1);
        }
        if(value.IndexOf("://",StringComparison.Ordinal)<0) value="http://"+value;
        Uri uri;
        if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||String.IsNullOrEmpty(uri.Host)||uri.Port<=0) return "";
        if(!new[]{"http","https","socks5","socks5h"}.Contains(uri.Scheme)) return "";
        return value;
    }
    public static QuotaNetwork Discover() {
        IntPtr app=Native.FindApp(Native.GetForegroundWindow(),true);
        if(app!=IntPtr.Zero) {
            uint pid; Native.GetWindowThreadProcessId(app,out pid);
            string command="";
            // Read only the already-running Codex process's proxy launch switches.
            // Command lines and other process data are never stored in diagnostics.
            using(ManagementObjectSearcher search=new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId="+pid))
            using(ManagementObjectCollection results=search.Get()) {
                foreach(ManagementObject entry in results) using(entry) { command=Json.Str(entry["CommandLine"]); }
            }
            QuotaNetwork explicitPlan=FromCommandLine(command);
            if(explicitPlan!=null) return explicitPlan;
        }
        foreach(string key in new[]{"HTTPS_PROXY","ALL_PROXY","HTTP_PROXY"}) {
            string value=Environment.GetEnvironmentVariable(key);
            if(!String.IsNullOrWhiteSpace(value)) return new QuotaNetwork{Source="inherited_environment",Proxy=""};
        }
        using(var settings=Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings")) {
            if(settings!=null&&Json.Num(settings.GetValue("ProxyEnable"))==1) {
                string proxy=Normalize(Json.Str(settings.GetValue("ProxyServer")));
                if(proxy.Length>0) return new QuotaNetwork{Source="windows_system_proxy",Proxy=proxy};
            }
        }
        return new QuotaNetwork{Source="direct_or_tun"};
    }
    public void Apply(ProcessStartInfo info) {
        if(!Direct&&Proxy.Length==0) return;
        foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","http_proxy","https_proxy","all_proxy"}) info.EnvironmentVariables.Remove(key);
        if(Direct) { info.EnvironmentVariables.Remove("NO_PROXY"); info.EnvironmentVariables.Remove("no_proxy"); return; }
        foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY"}) info.EnvironmentVariables[key]=Proxy;
        info.EnvironmentVariables["NO_PROXY"]=Bypass;
    }
    public static int RetrySeconds(int failures) { return failures<=1?5:failures==2?10:failures==3?20:30; }
}
sealed class Rpc:IDisposable {
    Process process;
    int counter;
    readonly Dictionary<int,TaskCompletionSource<object>> pending=new Dictionary<int,TaskCompletionSource<object>>();
    readonly object gate=new object();
    public string NetworkSource="尚未查询",ProxyEndpoint="",Phase="";
    static string ResolveExe() {
        string env=Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if(!String.IsNullOrEmpty(env)&&File.Exists(env)) return env;
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenAI","Codex","bin");
        if(Directory.Exists(root)) {
            string exe=Directory.GetDirectories(root).Select(d=>Path.Combine(d,"codex.exe"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if(exe!=null) return exe;
        }
        string fallback=Path.Combine(Program.Home,".sandbox-bin","codex.exe");
        if(File.Exists(fallback)) return fallback;
        throw new Exception("找不到 Codex 程序");
    }
    public async Task<Quota> ReadQuota() {
        if(process==null||process.HasExited) {
            Dispose();
            Phase="检测 Codex 网络设置";
            QuotaNetwork network=await Task.Run(()=>QuotaNetwork.Discover());
            NetworkSource=network.Source; ProxyEndpoint=network.Endpoint;
            ProcessStartInfo startInfo=new ProcessStartInfo {
                FileName=ResolveExe(),Arguments="app-server --stdio",WorkingDirectory=Program.Root,
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
            };
            network.Apply(startInfo);
            process=new Process{StartInfo=startInfo,EnableRaisingEvents=true};
            process.OutputDataReceived+=OnLine;
            process.Exited+=OnExit;
            // Do not retain server logs: they may contain private paths or authentication errors.
            process.ErrorDataReceived+=delegate {};
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await Request("initialize",new {clientInfo=new{name="codex_usage_bar",title="Codex 额度条",version=Program.Version}});
            Send(new{method="initialized"});
        }
        return Quota.From(await Request("account/rateLimits/read",new{excludeResetCreditDetails=true}));
    }
    void Send(object message) {
        // Framework Process.StandardInput can inherit a legacy Windows code page.
        // JSON unicode escapes keep the transport ASCII and therefore valid UTF-8.
        StringBuilder ascii=new StringBuilder();
        foreach(char c in Json.Encode(message)) { if(c>127) ascii.Append("\\u"+((int)c).ToString("x4")); else ascii.Append(c); }
        process.StandardInput.WriteLine(ascii.ToString()); process.StandardInput.Flush();
    }
    async Task<object> Request(string method,object args) {
        Phase=method=="initialize"?"初始化查询后台":"读取额度";
        int id=Interlocked.Increment(ref counter);
        TaskCompletionSource<object> tcs=new TaskCompletionSource<object>();
        lock(gate) pending[id]=tcs;
        try {
            Send(new{id=id,method=method,@params=args});
            if(await Task.WhenAny(tcs.Task,Task.Delay(15000))!=tcs.Task) throw new TimeoutException("额度接口超时");
            return await tcs.Task;
        } finally { lock(gate) pending.Remove(id); }
    }
    void OnLine(object sender,DataReceivedEventArgs args) {
        if(!Object.ReferenceEquals(sender,process)) return;
        if(args.Data==null) return;
        try {
            object message=Json.Parse(args.Data),id=Json.Get(message,"id"); if(id==null) return;
            TaskCompletionSource<object> tcs;
            lock(gate) pending.TryGetValue((int)Json.Num(id),out tcs);
            if(tcs==null) return;
            object error=Json.Get(message,"error");
            if(error!=null) tcs.TrySetException(new Exception("额度查询失败，请检查 Codex 登录或网络"));
            else tcs.TrySetResult(Json.Get(message,"result"));
        } catch {}
    }
    void OnExit(object sender,EventArgs args) {
        if(!Object.ReferenceEquals(sender,process)) return;
        lock(gate) foreach(TaskCompletionSource<object> tcs in pending.Values.ToArray()) tcs.TrySetException(new IOException("额度查询后台已退出"));
    }
    public void Dispose() {
        Process p=process; process=null;
        if(p==null) return;
        try { p.StandardInput.Close(); if(!p.WaitForExit(1500)) p.Kill(); } catch {}
        p.Dispose();
    }
}
static class Native {
    public const int SW_HIDE=0,SW_SHOWNOACTIVATE=4;
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h,int index,IntPtr value);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h,uint command);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h,uint command);
    public delegate IntPtr MouseProc(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)] public static extern IntPtr SetWindowsHookEx(int type,MouseProc proc,IntPtr module,uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string module);
    [StructLayout(LayoutKind.Sequential)] public struct MouseData { public Point Point; public uint Data,Flags,Time; public IntPtr Extra; }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc,IntPtr data);
    public delegate bool EnumProc(IntPtr h,IntPtr data);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    public static float Scale(IntPtr h) { try { return GetDpiForWindow(h)/96f; } catch { return 1f; } }
    public static bool IsAboveWindow(IntPtr window,IntPtr other) {
        if(window==IntPtr.Zero||other==IntPtr.Zero||window==other) return false;
        IntPtr previous=GetWindow(other,3); // GW_HWNDPREV, toward the front of the Z order.
        for(int i=0;previous!=IntPtr.Zero&&i<2048;i++,previous=GetWindow(previous,3))
            if(previous==window) return true;
        return false;
    }
    public static bool EnsureAboveOwner(IntPtr window,IntPtr owner) {
        if(window==IntPtr.Zero||owner==IntPtr.Zero||!IsWindowVisible(window)||!IsWindowVisible(owner)||IsAboveWindow(window,owner)) return false;
        // Cross-process ownership alone does not prevent Electron from moving
        // its main window above the overlay. Insert directly before the owner,
        // retaining any menus/candidates already ahead of it. Never adopt the
        // topmost group by inserting after a topmost window.
        IntPtr after=GetWindow(owner,3);
        if(after==IntPtr.Zero||(GetWindowLongPtr(after,-20).ToInt64()&8)!=0) after=IntPtr.Zero; // HWND_TOP, ordinary group.
        return SetWindowPos(window,after,0,0,0,0,0x10|0x1|0x2|0x200);
    }
    public static bool IsAppWindow(IntPtr h,bool includeMinimized) {
        Rect r;
        return IsWindowVisible(h)&&(IsIconic(h)?includeMinimized:GetWindowRect(h,out r)&&r.Right-r.Left>420&&r.Bottom-r.Top>300);
    }
    public static IntPtr FindApp(IntPtr preferred) { return FindApp(preferred,false); }
    public static IntPtr FindApp(IntPtr preferred,bool includeMinimized) {
        Dictionary<int,bool> ids=new Dictionary<int,bool>();
        foreach(Process p in Process.GetProcessesByName("ChatGPT").Concat(Process.GetProcessesByName("Codex"))) {
            // Store apps can deny MainModule access even when their HWND is accessible.
            // Select by executable name, then require a visible top-level application-sized HWND.
            ids[p.Id]=true;
            p.Dispose();
        }
        IntPtr found=IntPtr.Zero;
        EnumWindows(delegate(IntPtr h,IntPtr d) {
            uint pid; GetWindowThreadProcessId(h,out pid);
            if(ids.ContainsKey((int)pid)&&IsAppWindow(h,includeMinimized)) {
                if(h==preferred) { found=h; return false; }
                if(found==IntPtr.Zero) found=h;
            }
            return true;
        },IntPtr.Zero);
        return found;
    }
}
// Installed only while a menu is open. No mouse history is stored, no keyboard
// input is observed, and every click is passed through unchanged to its target.
sealed class OutsideMenuClick:IDisposable {
    readonly ContextMenuStrip menu;
    readonly Native.MouseProc callback;
    IntPtr hook;
    bool queued;
    internal int Seen,Downs,Outside,Errors;
    public OutsideMenuClick(ContextMenuStrip m) {
        menu=m; callback=OnMouse;
        hook=Native.SetWindowsHookEx(14,callback,Native.GetModuleHandle(null),0);
        if(hook==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    static bool InsideMenu(ToolStripDropDown current,Point point) {
        if(current.Visible&&current.Bounds.Contains(point)) return true;
        foreach(ToolStripItem item in current.Items) {
            ToolStripDropDownItem child=item as ToolStripDropDownItem;
            if(child!=null&&child.HasDropDownItems&&child.DropDown.Visible&&InsideMenu(child.DropDown,point)) return true;
        }
        return false;
    }
    IntPtr OnMouse(int code,IntPtr message,IntPtr data) {
        Seen++;
        int msg=message.ToInt32();
        if(code>=0&&!queued&&(msg==0x201||msg==0x204||msg==0x207||msg==0x20b)) {
            Downs++;
            try {
                Native.MouseData mouse=(Native.MouseData)Marshal.PtrToStructure(data,typeof(Native.MouseData));
                if(menu.Visible&&!InsideMenu(menu,mouse.Point)) {
                    Outside++;
                    queued=true;
                    menu.BeginInvoke((Action)delegate { if(menu.Visible) menu.Close(); });
                }
            } catch { Errors++; }
        }
        return Native.CallNextHookEx(IntPtr.Zero,code,message,data);
    }
    public void Dispose() { if(hook!=IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook=IntPtr.Zero; } }
}
sealed class UsageForm:Form {
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer anchorTimer=new System.Windows.Forms.Timer{Interval=600};
    readonly System.Windows.Forms.Timer tokenTimer=new System.Windows.Forms.Timer{Interval=1000};
    readonly System.Windows.Forms.Timer quotaTimer=new System.Windows.Forms.Timer{Interval=1000};
    readonly TokenLedger ledger=new TokenLedger(Program.Home);
    readonly Rpc rpc=new Rpc();
    readonly Label five=new Label(),week=new Label(),fiveResetLabel=new Label(),weekResetLabel=new Label(),today=new Label(),rate=new Label(),outputRate=new Label(),cachedAmount=new Label(),cachedRatio=new Label(),status=new Label(),menuButton=new Label();
    readonly ToolTip tip=new ToolTip{AutoPopDelay=15000,InitialDelay=300,ReshowDelay=150};
    readonly ContextMenuStrip menu=new ContextMenuStrip();
    readonly ToolStripMenuItem trackingMenu=new ToolStripMenuItem("速率跟踪对话");
    string selectedThread="";
    OutsideMenuClick outsideMenuClick;
    Quota quota=new Quota(); Tokens tokens=new Tokens();
    bool quotaBusy,tokenBusy,paused,closing,preview;
    IntPtr app=IntPtr.Zero;
    float scale=1;
    bool compact,tiny,twoRows;
    int anchorTicks;
    DateTimeOffset nextQuota=DateTimeOffset.MinValue;
    int quotaFailures;
    readonly string statePath=Path.Combine(Program.State,"status.json");
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { CreateParams c=base.CreateParams; c.ExStyle|=0x08000000|0x80; return c; } }
    public UsageForm(bool demo) {
        preview=demo;
        if(!demo) try { selectedThread=Json.Str(Json.Get(Json.Parse(File.ReadAllText(Path.Combine(Program.State,"rate-preferences.json"))),"threadId")); } catch {}
        Text="Codex 额度条"; FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=false;
        AutoScaleMode=AutoScaleMode.None; DoubleBuffered=true;
        BackColor=Color.FromArgb(26,27,30); ForeColor=Color.FromArgb(232,234,238);
        ClientSize=new Size(630,42);
        foreach(Label label in new[]{five,week,fiveResetLabel,weekResetLabel,today,rate,outputRate,cachedAmount,cachedRatio,status,menuButton}) {
            label.AutoSize=false; label.TextAlign=ContentAlignment.MiddleLeft; label.BackColor=BackColor;
            label.ForeColor=ForeColor; label.Font=new Font("Microsoft YaHei UI",9f); Controls.Add(label);
            label.ContextMenuStrip=menu;
        }
        SetLabelFonts();
        fiveResetLabel.ForeColor=weekResetLabel.ForeColor=Color.FromArgb(166,173,185);
        cachedAmount.ForeColor=cachedRatio.ForeColor=Color.FromArgb(166,173,185);
        menuButton.Text="⋯"; menuButton.TextAlign=ContentAlignment.MiddleCenter; menuButton.Cursor=Cursors.Hand;
        menuButton.MouseDown+=delegate(object sender,MouseEventArgs e) {
            if(e.Button!=MouseButtons.Left) return;
            if(menu.Visible) menu.Close();
            else menu.Show(menuButton,new Point(menuButton.Width-menu.GetPreferredSize(Size.Empty).Width,menuButton.Height+4));
        };
        menu.Opened+=delegate {
            if(outsideMenuClick!=null) outsideMenuClick.Dispose();
            try { outsideMenuClick=new OutsideMenuClick(menu); } catch { outsideMenuClick=null; }
        };
        menu.Closed+=delegate { if(outsideMenuClick!=null) { outsideMenuClick.Dispose(); outsideMenuClick=null; } };
        menu.Opening+=delegate { FillTrackingMenu(); };
        menu.Items.Add("立即刷新",null,delegate{ nextQuota=DateTimeOffset.MinValue; RefreshQuota(); RefreshTokens(); });
        menu.Items.Add(trackingMenu);
        ToolStripMenuItem pauseItem=new ToolStripMenuItem("暂时隐藏");
        pauseItem.Click+=delegate{paused=!paused; pauseItem.Text=paused?"恢复显示":"暂时隐藏"; AnchorWindow();}; menu.Items.Add(pauseItem);
        menu.Items.Add("查看统计说明",null,delegate{ShowDetails();});
        menu.Items.Add("打开插件目录",null,delegate{Process.Start(new ProcessStartInfo("explorer.exe",Program.Root){UseShellExecute=true});});
        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem autoStart=new ToolStripMenuItem("随 Codex 启动"){Checked=Program.AutoStartEnabled()};
        autoStart.Click+=delegate { try { Program.SetAutoStart(!autoStart.Checked); autoStart.Checked=Program.AutoStartEnabled(); } catch { MessageBox.Show("无法修改当前用户的启动项。","Codex 额度条"); } }; menu.Items.Add(autoStart);
        menu.Items.Add("退出额度条",null,delegate{Close();});
        ContextMenuStrip=menu;
        tray=new NotifyIcon{Icon=SystemIcons.Information,Text="Codex 额度条 · 右键管理",Visible=!demo,ContextMenuStrip=menu};
        tray.DoubleClick+=delegate{paused=false; AnchorWindow();};
        anchorTimer.Tick+=delegate{AnchorWindow();}; tokenTimer.Tick+=delegate{RefreshTokens();};
        quotaTimer.Tick+=delegate{RefreshQuota();};
        Resize+=delegate{LayoutLabels();};
        Shown+=delegate {
            if(preview) return;
            AnchorWindow(); anchorTimer.Start(); tokenTimer.Start(); quotaTimer.Start();
            RefreshTokens(); RefreshQuota();
        };
        FormClosing+=delegate {
            closing=true; anchorTimer.Stop(); tokenTimer.Stop(); quotaTimer.Stop(); tray.Visible=false;
            if(outsideMenuClick!=null) outsideMenuClick.Dispose();
            rpc.Dispose(); if(!preview) Program.SignalStop();
        };
        LayoutLabels(); Render();
    }
    void LayoutLabels() {
        int logicalWidth=(int)Math.Round(Width/scale);
        compact=logicalWidth<610; tiny=logicalWidth<430; twoRows=logicalWidth<530;
        int gap=(int)((compact?8:12)*scale),h=ClientSize.Height,button=(int)(25*scale);
        int available=Width-gap-button,firstLine=(int)(18*scale),band=(int)(38*scale);
        Label[] groups={five,week,today,rate,outputRate};
        if(twoRows) {
            int columns=logicalWidth<290?2:3;
            for(int i=0;i<groups.Length;i++) {
                int col=i%columns;
                groups[i].SetBounds(gap+available*col/columns,1+(i/columns)*band,
                    available*(col+1)/columns-available*col/columns,firstLine);
            }
        } else {
            int[] edges={0,(int)(available*0.20),(int)(available*0.38),(int)(available*0.60),(int)(available*0.80),available};
            for(int i=0;i<groups.Length;i++) groups[i].SetBounds(gap+edges[i],1,edges[i+1]-edges[i],firstLine);
        }
        fiveResetLabel.SetBounds(five.Left,five.Top+firstLine,five.Width,band-firstLine-2);
        weekResetLabel.SetBounds(week.Left,week.Top+firstLine,week.Width,band-firstLine-2);
        cachedAmount.SetBounds(today.Left,today.Top+firstLine,today.Width,band-firstLine-2);
        cachedRatio.SetBounds(rate.Left,rate.Top+firstLine,rate.Width,band-firstLine-2);
        menuButton.SetBounds(Width-button,1,button,h-3);
        status.Visible=false;
        Render();
    }
    void SetLabelFonts() {
        foreach(Label label in new[]{five,week,fiveResetLabel,weekResetLabel,today,rate,outputRate,cachedAmount,cachedRatio,status,menuButton}) {
            Font old=label.Font;
            label.Font=new Font("Microsoft YaHei UI",(label==fiveResetLabel||label==weekResetLabel||label==cachedAmount||label==cachedRatio?8f:9f)*scale);
            if(old!=null) old.Dispose();
        }
    }
    static string Percent(double? v) { return v.HasValue?v.Value.ToString("0.#",CultureInfo.InvariantCulture)+"%":"—"; }
    internal static double? CacheHitPercent(long input,long cached) { return input>0?(double?)(100d*cached/input):null; }
    static string ShortTokens(long count) { return count>=1000000?(count/1000000d).ToString("0.#",CultureInfo.InvariantCulture)+"M":count>=1000?(count/1000d).ToString("0.#",CultureInfo.InvariantCulture)+"K":count.ToString(CultureInfo.InvariantCulture); }
    static string ShortRate(double count) { return count>=1000000?(count/1000000d).ToString("0.#",CultureInfo.InvariantCulture)+"M":count>=1000?(count/1000d).ToString("0.#",CultureInfo.InvariantCulture)+"K":count.ToString("0.#",CultureInfo.InvariantCulture); }
    ConversationRate CurrentRate { get { return TokenLedger.Select(tokens,selectedThread); } }
    void SelectThread(string id) {
        selectedThread=id;
        if(!preview) try {
            Directory.CreateDirectory(Program.State);
            File.WriteAllText(Path.Combine(Program.State,"rate-preferences.json"),Json.Encode(new{threadId=id}),new UTF8Encoding(false));
        } catch {}
        Render();
    }
    void FillTrackingMenu() {
        foreach(ToolStripItem item in trackingMenu.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
        trackingMenu.DropDownItems.Clear();
        ToolStripMenuItem automatic=new ToolStripMenuItem("自动：最近开始运行的桌面对话"){Checked=selectedThread.Length==0};
        automatic.Click+=delegate { SelectThread(""); }; trackingMenu.DropDownItems.Add(automatic);
        List<ConversationRate> choices=tokens.Conversations.Take(12).ToList();
        ConversationRate pinned=CurrentRate;
        if(selectedThread.Length>0&&pinned!=null&&!choices.Any(c=>c.Id==selectedThread)) choices.Add(pinned);
        foreach(ConversationRate candidate in choices) {
            string id=candidate.Id,title=candidate.Title;
            if(title.Length>28) title=title.Substring(0,28)+"…";
            ToolStripMenuItem item=new ToolStripMenuItem((candidate.Running?"运行中 · ":"已结束 · ")+title+" ["+id.Substring(0,Math.Min(8,id.Length))+"]"){Checked=selectedThread==id};
            item.Click+=delegate { SelectThread(id); }; trackingMenu.DropDownItems.Add(item);
        }
        if(selectedThread.Length>0&&!tokens.Conversations.Any(c=>c.Id==selectedThread)) {
            trackingMenu.DropDownItems.Add(new ToolStripMenuItem("指定对话未找到，可切回自动"){Enabled=false});
        }
    }
    string RateDetails() {
        ConversationRate active=CurrentRate;
        string mode=selectedThread.Length==0?"自动：最近开始运行的桌面对话":"已指定对话";
        string detail=active==null?(selectedThread.Length==0?"当前没有可识别的桌面对话。":"指定对话没有本机记录。"):
            "对话："+active.Title+"\nID："+active.Id+"\n状态："+(!active.Running?"空闲":active.Stale?"记录超过 15 分钟未更新，速率未知":"运行中")+
            "\n本轮已记录 "+active.Total.ToString("N0")+" Token，经过 "+active.Seconds.ToString("0.0")+" 秒。"+
            "\n输入 "+active.Input.ToString("N0")+"，输出 "+active.Output.ToString("N0")+"，其中缓存输入 "+active.Cached.ToString("N0")+" 已包含。";
        return mode+"\n"+detail+"\n消耗均速 = 本轮已记录的（输入 + 输出）Token / 本轮经过秒数，含缓存输入。\n输出均速 = 本轮已记录的输出 Token / 本轮经过秒数，含推理输出且不重复相加。\n两者均包含工具等待时间，是本轮平均值，不是逐 Token 实时生成速度。\n每秒刷新，Token 数量在模型调用结束写入记录后更新，新对话发现最多约 5 秒。\n同时运行多个对话时只统计选中的一个，可在菜单指定；不合并子代理。";
    }
    void Render() {
        SetQuotaText(five,"5小时剩余 ","5小时 ","5h ",quota.Five);
        SetQuotaText(week,"周剩余 ","周 ","周 ",quota.Week);
        fiveResetLabel.Text=ResetLabelText(quota.FiveReset,false,tiny,DateTimeOffset.UtcNow);
        weekResetLabel.Text=ResetLabelText(quota.WeekReset,true,tiny,DateTimeOffset.UtcNow);
        FitResetLabel(fiveResetLabel); FitResetLabel(weekResetLabel);
        today.Text=(tiny?"今日 ":compact?"今日 ":"本日 Token ")+(tokens.Day==null?"…":compact?ShortTokens(tokens.Total):tokens.Total.ToString("N0",CultureInfo.InvariantCulture));
        if(tokens.Day!=null&&TextRenderer.MeasureText(today.Text,today.Font).Width>today.Width) today.Text=(tiny||compact?"今日 ":"本日 Token ")+ShortTokens(tokens.Total);
        ConversationRate active=CurrentRate;
        double? speed=active==null?(selectedThread.Length==0&&tokens.Day!=null?(double?)0:null):active.PerSecond;
        double? outputSpeed=active==null?(selectedThread.Length==0&&tokens.Day!=null?(double?)0:null):active.OutputPerSecond;
        SetRateText(rate,"消耗均速 ","消耗 ","耗 ",speed);
        SetRateText(outputRate,"输出均速 ","输出 ","出 ",outputSpeed);
        rate.ForeColor=active!=null&&active.Running&&!active.Stale?Color.FromArgb(117,195,247):Color.FromArgb(146,153,166);
        outputRate.ForeColor=rate.ForeColor;
        double? cachePercent=tokens.Day==null?null:CacheHitPercent(tokens.Input,tokens.Cached);
        cachedAmount.Text=(compact?"缓存 ":"缓存命中 ")+(tokens.Day==null?"…":ShortTokens(tokens.Cached));
        if(TextRenderer.MeasureText(cachedAmount.Text,cachedAmount.Font).Width>cachedAmount.Width) cachedAmount.Text="缓"+(tokens.Day==null?"…":ShortTokens(tokens.Cached));
        cachedRatio.Text="命中率 "+Percent(cachePercent);
        if(TextRenderer.MeasureText(cachedRatio.Text,cachedRatio.Font).Width>cachedRatio.Width) cachedRatio.Text="命中 "+Percent(cachePercent);
        if(TextRenderer.MeasureText(cachedRatio.Text,cachedRatio.Font).Width>cachedRatio.Width) cachedRatio.Text=Percent(cachePercent);
        string cacheDetails="本日缓存命中量："+tokens.Cached.ToString("N0")+" 输入 Token。\n本日缓存命中率："+Percent(cachePercent)+"。\n命中率 = 缓存输入 Token / 总输入 Token（含缓存），不含输出。\n系统本地时区自然日，本机所有 Codex 会话，与本日 Token 范围相同。\n缓存已包含在本日输入 Token 中，不重复相加。\n以日志记录的缓存用量为准，不是请求命中次数或费用折扣。";
        tip.SetToolTip(cachedAmount,cacheDetails); tip.SetToolTip(cachedRatio,cacheDetails);
        tip.SetToolTip(rate,RateDetails()); tip.SetToolTip(outputRate,RateDetails());
        five.ForeColor=QuotaColor(quota.Five); week.ForeColor=QuotaColor(quota.Week);
        bool stale=quota.Updated!=default(DateTimeOffset)&&DateTimeOffset.UtcNow-quota.Updated>TimeSpan.FromMinutes(2);
        status.Text=!String.IsNullOrEmpty(quota.Error)?"离线":stale?"缓存":"本机";
        status.ForeColor=(!String.IsNullOrEmpty(quota.Error)||stale)?Color.FromArgb(239,180,90):Color.FromArgb(146,153,166);
        string quotaState="\n额度每 45 秒查询，状态："+status.Text+"。\n"+quota.Error;
        tip.SetToolTip(five,"账号共享额度，显示剩余百分比。\n重置："+ResetTime(quota.FiveReset)+quotaState);
        tip.SetToolTip(week,"账号共享额度，显示剩余百分比。\n重置："+ResetTime(quota.WeekReset)+quotaState);
        tip.SetToolTip(fiveResetLabel,tip.GetToolTip(five));
        tip.SetToolTip(weekResetLabel,tip.GetToolTip(week));
        tip.SetToolTip(today,"本日 Token："+tokens.Total.ToString("N0")+"（K=千，M=百万）。\n本地时间 "+tokens.Day+" 00:00–24:00，本机所有 Codex 会话。\n输入 "+tokens.Input.ToString("N0")+"，输出 "+tokens.Output.ToString("N0")+"。\n其中缓存输入 "+tokens.Cached.ToString("N0")+" 已包含在输入里，不重复相加。\n推理 Token 已包含在输出里。读取 "+tokens.Files+" 个会话记录。"+(tokens.Errors>0?"\n有 "+tokens.Errors+" 个文件暂时无法读取，统计可能不完整。":""));
        tip.SetToolTip(status,"额度每 45 秒查询；Token 每秒读取。\n最近成功："+(quota.Updated==default(DateTimeOffset)?"尚未查询":UserTime.InZone(quota.Updated,TimeZoneInfo.Local).ToString("HH:mm:ss"))+"\n"+quota.Error);
        Invalidate();
        WriteStatus();
    }
    void SetQuotaText(Label label,string full,string shortLabel,string minimal,double? value) {
        string amount=Percent(value);
        label.Text=full+amount;
        if(TextRenderer.MeasureText(label.Text,label.Font).Width>label.Width) label.Text=shortLabel+amount;
        if(TextRenderer.MeasureText(label.Text,label.Font).Width>label.Width) label.Text=minimal+amount;
    }
    void SetRateText(Label label,string full,string shortLabel,string minimal,double? value) {
        string amount=value.HasValue?ShortRate(value.Value):"—";
        label.Text=full+amount+(value.HasValue?" T/s":"");
        if(TextRenderer.MeasureText(label.Text,label.Font).Width>label.Width)
            label.Text=shortLabel+amount+(value.HasValue?"/s":"");
        if(TextRenderer.MeasureText(label.Text,label.Font).Width>label.Width)
            label.Text=minimal+amount+(value.HasValue?"/s":"");
    }
    internal static int LayoutHeight(int width) { return width<290?114:width<530?76:38; }
    static Color QuotaColor(double? v) { return !v.HasValue?Color.FromArgb(146,153,166):v.Value<=10?Color.FromArgb(255,119,119):v.Value<=25?Color.FromArgb(239,180,90):Color.FromArgb(117,221,184); }
    internal static DateTimeOffset? ResetDate(long epoch) {
        return ResetDate(epoch,TimeZoneInfo.Local);
    }
    internal static DateTimeOffset? ResetDate(long epoch,TimeZoneInfo zone) {
        if(epoch<=0) return null;
        try { return UserTime.InZone(new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(epoch),zone); }
        catch(ArgumentOutOfRangeException) { return null; }
    }
    static string ResetTime(long epoch) { DateTimeOffset? reset=ResetDate(epoch); return reset.HasValue?reset.Value.ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture)+"（本地时间 UTC"+reset.Value.ToString("zzz",CultureInfo.InvariantCulture)+"）":"未知"; }
    internal static string ResetLabelText(long epoch,bool weekly,bool shortLabel,DateTimeOffset now) {
        return ResetLabelText(epoch,weekly,shortLabel,now,TimeZoneInfo.Local);
    }
    internal static string ResetLabelText(long epoch,bool weekly,bool shortLabel,DateTimeOffset now,TimeZoneInfo zone) {
        DateTimeOffset? reset=ResetDate(epoch,zone);
        string prefix=shortLabel?"":"重置 ";
        if(!reset.HasValue) return prefix+"未知";
        bool includeDate=weekly||reset.Value.Date!=UserTime.InZone(now,zone).Date;
        return prefix+reset.Value.ToString(includeDate?"M/d HH:mm":"HH:mm",CultureInfo.InvariantCulture);
    }
    void FitResetLabel(Label label) {
        // Keep both the date and time visible even at the narrowest supported width.
        for(float size=8f;size>=7f;size-=0.5f) {
            if(Math.Abs(label.Font.Size-size*scale)>0.01f) { Font old=label.Font; label.Font=new Font("Microsoft YaHei UI",size*scale); old.Dispose(); }
            if(TextRenderer.MeasureText(label.Text,label.Font).Width<=label.Width) break;
        }
    }
    void ShowDetails() {
        MessageBox.Show("五小时剩余："+Percent(quota.Five)+"\n五小时重置："+ResetTime(quota.FiveReset)+"\n周剩余："+Percent(quota.Week)+"\n周重置："+ResetTime(quota.WeekReset)+"\n\n本日 Token："+tokens.Total.ToString("N0")+"\n输入："+tokens.Input.ToString("N0")+"\n输出："+tokens.Output.ToString("N0")+"\n缓存命中量："+tokens.Cached.ToString("N0")+"（输入中已包含）\n缓存命中率："+Percent(CacheHitPercent(tokens.Input,tokens.Cached))+"（缓存输入 / 总输入）\n\n"+RateDetails()+"\n\nToken 范围：系统本地时区自然日，本机所有本地 Codex 会话，包括桌面版和 CLI。\n不包含其他设备或尚未同步到本机的云端记录。\n按会话累计量的增量统计，重复日志不会重复计数。\n\n额度来自官方 app-server，只调用只读接口。\n悬浮条是独立窗口，未修改桌面程序。","Codex 额度条",MessageBoxButtons.OK,MessageBoxIcon.Information);
    }
    void WriteStatus() {
        if(preview) return;
        try {
            ConversationRate active=CurrentRate;
            Directory.CreateDirectory(Path.GetDirectoryName(statePath));
            File.WriteAllText(statePath,Json.Encode(new {
                version=Program.Version,followCodexEnabled=Program.AutoStartEnabled(),fiveHourRemaining=quota.Five,weeklyRemaining=quota.Week,
                fiveHourResetsAt=ResetDate(quota.FiveReset).HasValue?(long?)quota.FiveReset:null,weeklyResetsAt=ResetDate(quota.WeekReset).HasValue?(long?)quota.WeekReset:null,
                fiveHourResetTime=ResetTime(quota.FiveReset),weeklyResetTime=ResetTime(quota.WeekReset),
                fiveHourResetText=fiveResetLabel.Text,weeklyResetText=weekResetLabel.Text,
                quotaUpdatedUtc=quota.Updated==default(DateTimeOffset)?null:quota.Updated.ToString("o"),quotaError=quota.Error,
                quotaNetworkSource=rpc.NetworkSource,quotaProxyEndpoint=rpc.ProxyEndpoint,quotaQueryPhase=rpc.Phase,
                quotaConsecutiveFailures=quotaFailures,nextQuotaAttemptUtc=nextQuota.ToString("o"),
                date=tokens.Day,timeZone=TimeZoneInfo.Local.Id,timeZoneDisplayName=TimeZoneInfo.Local.DisplayName,todayTokens=tokens.Total,inputTokens=tokens.Input,
                outputTokens=tokens.Output,cachedInputTokens=tokens.Cached,cacheHitPercent=tokens.Day==null?null:CacheHitPercent(tokens.Input,tokens.Cached),
                cacheHitDefinition="cached_input_tokens_divided_by_input_tokens",source="local_codex_session_logs",files=tokens.Files,unreadableFiles=tokens.Errors,
                activeThreadId=active==null?null:active.Id,activeTurnId=active==null?null:active.TurnId,
                rateMode=selectedThread.Length==0?"automatic_latest_started_desktop_turn":"selected_thread",
                tokensPerSecond=active==null?(selectedThread.Length==0&&tokens.Day!=null?(double?)0:null):active.PerSecond,
                rateDefinition="recorded_turn_input_plus_output_divided_by_elapsed_seconds_including_cached_input",
                outputTokensPerSecond=active==null?(selectedThread.Length==0&&tokens.Day!=null?(double?)0:null):active.OutputPerSecond,
                outputRateDefinition="recorded_turn_output_including_reasoning_divided_by_elapsed_seconds_including_tool_waits",
                activeTurnOutputTokens=active==null?0:active.Output,
                activeTurnTokens=active==null?0:active.Total,activeTurnElapsedSeconds=active==null?0:active.Seconds,
                activeRunning=active!=null&&active.Running,rateStale=active!=null&&active.Stale,
                activeConversations=tokens.Conversations.Count(c=>c.Running&&!c.Stale),
                appWindow=app.ToInt64(),barWindow=Handle.ToInt64(),visible=Visible,aboveApp=Native.IsAboveWindow(Handle,app),titleAnchor=TitleAnchor.Measured?"help_right":"left_fallback",topmost=(Native.GetWindowLongPtr(Handle,-20).ToInt64()&8)!=0,
                ownerWindow=Native.GetWindowLongPtr(Handle,-8).ToInt64(),menuVisible=menu.Visible,
                rectangle=new{x=Left,y=Top,width=Width,height=Height}
            }),new UTF8Encoding(false));
        } catch {}
    }
    async void RefreshQuota() {
        if(closing||quotaBusy||preview||DateTimeOffset.UtcNow<nextQuota) return;
        quotaBusy=true; nextQuota=DateTimeOffset.UtcNow.AddSeconds(45);
        try { quota=await rpc.ReadQuota(); quotaFailures=0; nextQuota=DateTimeOffset.UtcNow.AddSeconds(45); }
        catch(Exception e) {
            quotaFailures++; int retry=QuotaNetwork.RetrySeconds(quotaFailures);
            nextQuota=DateTimeOffset.UtcNow.AddSeconds(retry);
            quota.Error=rpc.Phase+"失败（"+e.GetType().Name+"）；"+retry+" 秒后自动重新读取 Codex 网络设置并重试。"+
                (quota.Updated==default(DateTimeOffset)?"尚未取得额度，显示未知。":"显示上次结果，可能已过期。");
            rpc.Dispose();
        }
        finally { quotaBusy=false; if(!closing) Render(); }
    }
    async void RefreshTokens() {
        if(closing||tokenBusy||preview) return;
        tokenBusy=true;
        try { tokens=await Task.Run(()=>ledger.Scan(DateTimeOffset.UtcNow)); }
        catch { tokens.Errors++; }
        finally { tokenBusy=false; if(!closing) Render(); }
    }
    void AnchorWindow() {
        if(preview||closing) return;
        if(++anchorTicks%4==1||app==IntPtr.Zero) app=Native.FindApp(Native.GetForegroundWindow());
        Native.Rect rect;
        if(paused||app==IntPtr.Zero||!Native.IsWindowVisible(app)||Native.IsIconic(app)||!Native.GetWindowRect(app,out rect)) { if(Visible) Hide(); WriteStatus(); return; }
        IntPtr fg=Native.GetForegroundWindow();
        if(fg!=app&&fg!=Handle&&!menu.Visible) { if(Visible) Hide(); WriteStatus(); return; }
        float newScale=Native.Scale(app);
        if(Math.Abs(newScale-scale)>0.01) { scale=newScale; SetLabelFonts(); }
        Rectangle area=Screen.FromHandle(app).WorkingArea;
        Rectangle placement=PlaceBar(rect,area,scale);
        int left=TitleAnchor.Left(app,rect.Left,scale);
        int remaining=rect.Right-left-(int)(144*scale);
        if(remaining<190*scale) {
            placement.Y=rect.Top+(int)(40*scale);
            remaining=rect.Right-left-(int)(8*scale);
            if(remaining<190*scale) {left=rect.Left+(int)(64*scale);remaining=rect.Right-left-(int)(8*scale);}
        }
        placement.X=Math.Max(area.Left,Math.Min(left,area.Right-(int)(190*scale)));
        placement.Width=Math.Min((int)(630*scale),remaining);
        placement.Height=(int)(LayoutHeight((int)Math.Round(placement.Width/scale))*scale);
        if(!Visible) Show();
        BindOwner(app);
        // Move and resize without changing the Z order or stealing focus.
        // IME candidates and other popups can stay above this ordinary owned window.
        Native.SetWindowPos(Handle,IntPtr.Zero,placement.X,placement.Y,placement.Width,placement.Height,0x10|0x40|0x4|0x200);
        Native.EnsureAboveOwner(Handle,app);
        if(quota.Updated==default(DateTimeOffset)||DateTimeOffset.UtcNow>=nextQuota) RefreshQuota();
        WriteStatus();
    }
    internal void BindOwner(IntPtr owner) {
        if(owner!=IntPtr.Zero&&Native.GetWindowLongPtr(Handle,-8)!=owner) {
            Native.SetWindowLongPtr(Handle,-8,owner);
            Native.SetWindowPos(Handle,new IntPtr(-2),0,0,0,0,0x10|0x1|0x2|0x200);
        }
    }
    internal ContextMenuStrip TestMenu { get { return menu; } }
    internal bool TestMouseWatcherActive { get { return outsideMenuClick!=null; } }
    internal OutsideMenuClick TestMouseWatcher { get { return outsideMenuClick; } }
    internal static Rectangle PlaceBar(Native.Rect appRect,Rectangle area,float dpi) {
        int leftReserve=(int)(64*dpi),rightReserve=(int)(144*dpi);
        int width=Math.Min((int)(630*dpi),appRect.Right-appRect.Left-leftReserve-rightReserve);
        width=Math.Max((int)(190*dpi),width);
        int height=(int)(LayoutHeight((int)Math.Round(width/dpi))*dpi);
        int x=appRect.Left+(appRect.Right-appRect.Left-width)/2;
        x=Math.Min(x,appRect.Right-rightReserve-width);
        x=Math.Max(x,appRect.Left+leftReserve);
        x=Math.Max(area.Left,Math.Min(x,area.Right-width));
        int y=Math.Max(area.Top+2,appRect.Top+(int)(6*dpi));
        return new Rectangle(x,y,width,height);
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        using(Pen border=new Pen(Color.FromArgb(62,66,74))) e.Graphics.DrawRectangle(border,0,0,Width-1,Height-1);
    }
    public void MakePreview(string path,int dpi,int logicalWidth) {
        scale=dpi/96f; ClientSize=new Size((int)(logicalWidth*scale),(int)(LayoutHeight(logicalWidth)*scale));
        SetLabelFonts();
        DateTimeOffset epoch=new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero);
        quota=new Quota{Five=46,Week=92,FiveReset=(long)(DateTimeOffset.UtcNow.AddHours(3)-epoch).TotalSeconds,WeekReset=(long)(DateTimeOffset.UtcNow.AddDays(4)-epoch).TotalSeconds,Updated=DateTimeOffset.UtcNow};
        tokens=new Tokens{Day="2026-10-04",Input=12345678,Output=87654,Cached=10456789,Conversations=new[]{new ConversationRate{Id="preview",Title="演示对话",TurnId="demo",Running=true,Seconds=20,Input=48000,Output=1234}}}; Render(); LayoutLabels();
        Location=new Point(-5000,-5000); Show(); Application.DoEvents();
        using(Bitmap image=new Bitmap(Width,Height)) { DrawToBitmap(image,ClientRectangle); image.Save(path,System.Drawing.Imaging.ImageFormat.Png); }
        Hide();
    }
    public void CaptureLive(string path) {
        using(UsageForm copy=new UsageForm(true)) {
            copy.quota=quota; copy.tokens=tokens; copy.scale=scale; copy.selectedThread=selectedThread;
            copy.ClientSize=ClientSize;
            copy.SetLabelFonts();
            copy.Render(); copy.LayoutLabels(); copy.Location=new Point(-5000,-5000); copy.Show(); Application.DoEvents();
            using(Bitmap image=new Bitmap(copy.Width,copy.Height)) { copy.DrawToBitmap(image,copy.ClientRectangle); image.Save(path,System.Drawing.Imaging.ImageFormat.Png); }
            copy.Hide();
        }
    }
    internal void VerifyCacheLayout() {
        foreach(int dpi in new[]{96,120,144,192}) foreach(int width in new[]{190,289,290,292,420,529,530,609,610,630}) {
            scale=dpi/96f; ClientSize=new Size((int)(width*scale),(int)(LayoutHeight(width)*scale));
            SetLabelFonts();
            quota=new Quota{Five=100,Week=99.9};
            tokens=new Tokens{Day="2026-10-13",Input=99999999,Cached=99999999,
                Conversations=new[]{new ConversationRate{Id="test",Running=true,Seconds=40,Input=800000,Output=2000}}}; LayoutLabels();
            foreach(Label label in new[]{five,week,cachedAmount,cachedRatio,rate,outputRate}) {
                Size textSize=TextRenderer.MeasureText(label.Text,label.Font);
                if(textSize.Width>label.Width||textSize.Height>label.Height||!ClientRectangle.Contains(label.Bounds))
                    throw new Exception("统计指标被裁切："+width+"px @"+dpi+"dpi，"+label.Text+" text="+textSize+" bounds="+label.Size);
            }
            Label[] labels={five,week,fiveResetLabel,weekResetLabel,today,rate,outputRate,cachedAmount,cachedRatio,menuButton};
            for(int i=0;i<labels.Length;i++) for(int j=i+1;j<labels.Length;j++)
                if(labels[i].Bounds.IntersectsWith(labels[j].Bounds)) throw new Exception("统计标签重叠");
            if(!outputRate.Text.Contains("50")||!rate.Text.Contains("20.1K")) throw new Exception("输出与消耗均速未分开展示");
            foreach(double? remaining in new double?[]{100,99.9,9.9,0,null}) {
                quota.Five=quota.Week=remaining; Render();
                foreach(Label label in new[]{five,week}) {
                    Size textSize=TextRenderer.MeasureText(label.Text,label.Font);
                    if(!label.Text.EndsWith(Percent(remaining),StringComparison.Ordinal)||textSize.Width>label.Width||textSize.Height>label.Height)
                        throw new Exception("额度百分比被裁切："+width+"px @"+dpi+"dpi，"+label.Text+" text="+textSize+" bounds="+label.Size);
                }
            }
        }
    }
}
static class Program {
    public const string Version="1.0.10";
    public static readonly string Root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));
    public static readonly string Home=Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
    public static readonly string State=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexUsageBar");
    internal const string Name="Local\\HP.CodexUsageBar.v1";
    public static readonly string StopFile=Path.Combine(State,"stop.signal");
    public static readonly string CaptureFile=Path.Combine(State,"capture.request");
    public static void SignalStop() { try { File.WriteAllText(StopFile,DateTimeOffset.UtcNow.ToString("o")); } catch {} }
    public static string StartupCommand { get { return "\""+Application.ExecutablePath+"\" --watch-codex"; } }
    public static string ReadStartupCommand() { using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) return key==null?"":Json.Str(key.GetValue("CodexUsageBar")); }
    public static bool AutoStartEnabled() { return ReadStartupCommand().EndsWith(" --watch-codex",StringComparison.OrdinalIgnoreCase); }
    public static void SetAutoStart(bool enabled) {
        using(var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) {
            if(enabled) key.SetValue("CodexUsageBar",StartupCommand);
            else key.DeleteValue("CodexUsageBar",false);
        }
        if(enabled) using(Process watcher=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--watch-codex") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})) {}
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length>0&&args[0]=="--enable-autostart") { SetAutoStart(true); return 0; }
        if(args.Length>0&&args[0]=="--disable-autostart") { SetAutoStart(false); return 0; }
        if(args.Length>0&&args[0]=="--watch-codex") { Native.SetProcessDPIAware(); return CodexFollower.Run(); }
        if(args.Length>1&&args[0]=="--capture") { Directory.CreateDirectory(State); File.WriteAllText(CaptureFile,Path.GetFullPath(args[1])); return 0; }
        if(args.Length>1&&args[0]=="--window-check") {
            List<object> windows=new List<object>();
            Native.EnumWindows(delegate(IntPtr h,IntPtr d){
                uint pid; Native.GetWindowThreadProcessId(h,out pid); Native.Rect rect;
                try { using(Process p=Process.GetProcessById((int)pid)) {
                    if(p.ProcessName.Equals("ChatGPT",StringComparison.OrdinalIgnoreCase)||p.ProcessName.Equals("Codex",StringComparison.OrdinalIgnoreCase)) {
                        Native.GetWindowRect(h,out rect); windows.Add(new{hwnd=h.ToInt64(),pid=pid,name=p.ProcessName,visible=Native.IsWindowVisible(h),minimized=Native.IsIconic(h),width=rect.Right-rect.Left,height=rect.Bottom-rect.Top});
                    }
                }} catch{} return true;
            },IntPtr.Zero);
            File.WriteAllText(args[1],Json.Encode(new{foreground=Native.GetForegroundWindow().ToInt64(),app=Native.FindApp(Native.GetForegroundWindow()).ToInt64(),windows=windows})); return 0;
        }
        if(args.Length>0&&args[0]=="--stop") { Directory.CreateDirectory(Path.GetDirectoryName(StopFile)); SignalStop(); return 0; }
        if(args.Length>1&&args[0]=="--quota-check") {
            try {
                using(Rpc rpc=new Rpc()) {
                    DateTimeOffset start=DateTimeOffset.UtcNow; Quota value=rpc.ReadQuota().GetAwaiter().GetResult();
                    File.WriteAllText(args[1],Json.Encode(new{success=value.Five.HasValue&&value.Week.HasValue,version=Version,
                        elapsedSeconds=(DateTimeOffset.UtcNow-start).TotalSeconds,networkSource=rpc.NetworkSource,proxyEndpoint=rpc.ProxyEndpoint,
                        fiveHourRemaining=value.Five,weeklyRemaining=value.Week,fiveHourResetsAt=value.FiveReset,weeklyResetsAt=value.WeekReset,error=value.Error}),new UTF8Encoding(false));
                    return value.Five.HasValue&&value.Week.HasValue?0:1;
                }
            } catch(Exception e) { File.WriteAllText(args[1],Json.Encode(new{success=false,version=Version,errorType=e.GetType().Name}),new UTF8Encoding(false)); return 1; }
        }
        if(args.Length>0&&args[0]=="--self-test") { return Tests.Run(args.Length>1?args[1]:Path.Combine(Root,"state","tests.json")); }
        Native.SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length>1&&args[0]=="--click-test-host") return InteractionTests.Host(args[1]);
        if(args.Length>1&&args[0]=="--layer-test-host") return InteractionTests.LayerHost(args[1]);
        if(args.Length>1&&args[0]=="--layout-test") {
            try { using(UsageForm demo=new UsageForm(true)) demo.VerifyCacheLayout();
                File.WriteAllText(args[1],Json.Encode(new{success=true,widths=10,dpiValues=4,quotaValues=5})); return 0;
            } catch(Exception e) { File.WriteAllText(args[1],Json.Encode(new{success=false,error=e.Message})); return 1; }
        }
        if(args.Length>1&&args[0]=="--interaction-test") return InteractionTests.Run(args[1]);
        if(args.Length>1&&args[0]=="--follower-test") return FollowerTests.Run(args[1]);
        if(args.Length>1&&args[0]=="--preview") { using(UsageForm demo=new UsageForm(true)) demo.MakePreview(args[1],args.Length>2?Int32.Parse(args[2]):96,args.Length>3?Int32.Parse(args[3]):630); return 0; }
        bool created;
        using(Mutex mutex=new Mutex(true,Name,out created)) {
            if(!created) return 0;
            Directory.CreateDirectory(Path.GetDirectoryName(StopFile));
            if(File.Exists(StopFile)) File.Delete(StopFile);
            using(UsageForm form=new UsageForm(false)) {
                System.Windows.Forms.Timer stop=new System.Windows.Forms.Timer{Interval=1000};
                stop.Tick+=delegate{
                    if(File.Exists(StopFile)) form.Close();
                    if(File.Exists(CaptureFile)) {
                        try {
                            string path=File.ReadAllText(CaptureFile); File.Delete(CaptureFile);
                            form.CaptureLive(path);
                        } catch{}
                    }
                }; stop.Start();
                Application.Run(form); stop.Dispose();
            }
        }
        return 0;
    }
}
// Login starts only this lightweight watcher. Quota RPC and log scans start with
// the bar after a Codex window appears, and stop when its last window closes.
sealed class FollowSession {
    string current="";
    readonly Action start,stop;
    public FollowSession(Action launch,Action end) { start=launch; stop=end; }
    public void Observe(string session) {
        if(session==current) return;
        if(session.Length==0) { if(current.Length>0) stop(); }
        else start();
        current=session;
    }
}
static class CodexFollower {
    const string Name="Local\\HP.CodexUsageBar.Follower.v1";
    static bool BarRunning() {
        Mutex ui;
        if(!Mutex.TryOpenExisting(Program.Name,out ui)) return false;
        using(ui) {
            bool owned=false;
            try { try { owned=ui.WaitOne(0); } catch(AbandonedMutexException) { owned=true; } return !owned; }
            finally { if(owned) ui.ReleaseMutex(); }
        }
    }
    static void StartBar() {
        using(Process bar=Process.Start(new ProcessStartInfo(Application.ExecutablePath){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Program.Root})) {
            Stopwatch clock=Stopwatch.StartNew();
            while(clock.ElapsedMilliseconds<3000&&!BarRunning()&&!bar.HasExited) Thread.Sleep(50);
            if(!BarRunning()) throw new Exception("额度条尚未启动");
        }
    }
    static void StopBar() {
        Program.SignalStop();
        Stopwatch clock=Stopwatch.StartNew(); while(clock.ElapsedMilliseconds<3500&&BarRunning()) Thread.Sleep(50);
    }
    public static int Run() {
        Directory.CreateDirectory(Program.State);
        string state=Path.Combine(Program.State,"follower-status.json");
        bool owned=false;
        using(Mutex mutex=new Mutex(false,Name)) {
            try {
                try { owned=mutex.WaitOne(5000); } catch(AbandonedMutexException) { owned=true; }
                if(!owned) return 0;
                FollowSession session=new FollowSession(StartBar,StopBar); IntPtr last=IntPtr.Zero;
                while(Program.ReadStartupCommand().Equals(Program.StartupCommand,StringComparison.OrdinalIgnoreCase)) {
                    string error=""; uint pid=0;
                    try {
                        last=Native.FindApp(last,true);
                        if(last!=IntPtr.Zero) Native.GetWindowThreadProcessId(last,out pid);
                        session.Observe(last==IntPtr.Zero?"":pid.ToString()+":"+last.ToInt64());
                    } catch(Exception e) { error=e.GetType().Name; }
                    try { File.WriteAllText(state,Json.Encode(new{version=Program.Version,enabled=true,watcherProcessId=Process.GetCurrentProcess().Id,
                        appPresent=last!=IntPtr.Zero,appWindow=last.ToInt64(),appProcessId=pid,updatedUtc=DateTimeOffset.UtcNow.ToString("o"),error=error}),new UTF8Encoding(false)); } catch {}
                    Thread.Sleep(1250);
                }
                try { File.WriteAllText(state,Json.Encode(new{version=Program.Version,enabled=false,updatedUtc=DateTimeOffset.UtcNow.ToString("o")}),new UTF8Encoding(false)); } catch {}
                return 0;
            } finally { if(owned) mutex.ReleaseMutex(); }
        }
    }
}
static class FollowerTests {
    public static int Run(string report) {
        List<string> passed=new List<string>();
        IntPtr originalForeground=Native.GetForegroundWindow();
        try {
            int starts=0,stops=0;
            FollowSession session=new FollowSession(()=>{starts++;},()=>{stops++;});
            session.Observe(""); session.Observe(""); if(starts!=0||stops!=0) throw new Exception("无 Codex 时启动了额度查询");
            session.Observe("app-a"); session.Observe("app-a"); if(starts!=1||stops!=0) throw new Exception("同一次 Codex 启动重复创建额度条");
            passed.Add("Idle without Codex; one launch per app session, preserving manual quit");
            session.Observe(""); session.Observe(""); session.Observe("app-b");
            if(starts!=2||stops!=1) throw new Exception("Codex 退出或重启未跟随");
            passed.Add("App closure stops bar; next app session launches it again");
            using(Form host=new Form{Text="跟随启动测试",ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-3000,-3000),ClientSize=new Size(520,350)}) {
                host.Show(); Application.DoEvents();
                if(!Native.IsAppWindow(host.Handle,true)) throw new Exception("普通应用窗口未识别");
                Native.ShowWindow(host.Handle,6); Application.DoEvents();
                if(!Native.IsIconic(host.Handle)||!Native.IsAppWindow(host.Handle,true)||Native.IsAppWindow(host.Handle,false)) throw new Exception("最小化被误当作退出");
                host.Hide(); if(Native.IsAppWindow(host.Handle,true)) throw new Exception("关闭显示后未排除窗口"); host.Close();
            }
            passed.Add("Actual minimized window remains an app session while overlay hides");
            File.WriteAllText(report,Json.Encode(new{success=true,passed=passed}),new UTF8Encoding(false)); return 0;
        } catch(Exception e) { File.WriteAllText(report,Json.Encode(new{success=false,error=e.Message,passed=passed}),new UTF8Encoding(false)); return 1; }
        finally { if(originalForeground!=IntPtr.Zero&&Native.IsWindowVisible(originalForeground)) Native.SetForegroundWindow(originalForeground); }
    }
}
static class Tests {
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    static string Event(string time,long i,long o,long cache) { return Json.Encode(new {timestamp=time,type="event_msg",payload=new{type="token_count",info=new{total_token_usage=new{input_tokens=i,output_tokens=o,cached_input_tokens=cache}}}})+"\n"; }
    static string Meta(string time,string id,object source) { return Json.Encode(new{timestamp=time,type="session_meta",payload=new{id=id,originator="Codex Desktop",source=source}})+"\n"; }
    static string Lifecycle(string time,string type,string turn) { return Json.Encode(new{timestamp=time,type="event_msg",payload=new{type=type,turn_id=turn}})+"\n"; }
    static void NetworkTests(List<string> passed) {
        string before=Environment.GetEnvironmentVariable("HTTPS_PROXY");
        QuotaNetwork plan=QuotaNetwork.FromCommandLine("\"App With Spaces\\ChatGPT.exe\" --proxy-server=\"http://127.0.0.1:23456\" --proxy-bypass-list=\"<local>;localhost;127.0.0.1;[::1]\"");
        Check(plan!=null&&plan.Proxy=="http://127.0.0.1:23456"&&plan.Source=="codex_launch_argument","未识别 Codex 独立代理参数");
        ProcessStartInfo child=new ProcessStartInfo("test.exe"){UseShellExecute=false};
        child.EnvironmentVariables["HTTPS_PROXY"]="http://127.0.0.1:65000"; child.EnvironmentVariables["NO_PROXY"]="*";
        plan.Apply(child);
        Check(child.EnvironmentVariables["HTTP_PROXY"]==plan.Proxy&&child.EnvironmentVariables["HTTPS_PROXY"]==plan.Proxy&&child.EnvironmentVariables["ALL_PROXY"]==plan.Proxy,"旧登录环境覆盖了当前 Codex 代理");
        Check(child.EnvironmentVariables["NO_PROXY"]=="localhost,127.0.0.1,::1"&&Environment.GetEnvironmentVariable("HTTPS_PROXY")==before,"代理绕过设置错误或修改了父进程环境");
        Check(QuotaNetwork.FromCommandLine("ChatGPT.exe --proxy-server=http://127.0.0.1:34567").Proxy.EndsWith(":34567",StringComparison.Ordinal),"代理端口被硬编码");
        Check(QuotaNetwork.FromCommandLine("ChatGPT.exe --proxy-server https=127.0.0.1:23456;http=127.0.0.1:23457").Proxy=="http://127.0.0.1:23456","分协议代理解析失败");
        Check(QuotaNetwork.FromCommandLine("ChatGPT.exe --proxy-server=http://sample:secret@127.0.0.1:23456").Endpoint=="http://127.0.0.1:23456","诊断暴露了代理认证信息");
        QuotaNetwork.FromCommandLine("ChatGPT.exe --no-proxy-server").Apply(child);
        Check(!child.EnvironmentVariables.ContainsKey("HTTPS_PROXY")&&!child.EnvironmentVariables.ContainsKey("ALL_PROXY"),"明确直连没有清除子进程遗留代理");
        Check(QuotaNetwork.FromCommandLine("ChatGPT.exe")==null,"无代理参数时误造了代理");
        Check(QuotaNetwork.RetrySeconds(1)==5&&QuotaNetwork.RetrySeconds(2)==10&&QuotaNetwork.RetrySeconds(3)==20&&QuotaNetwork.RetrySeconds(20)==30,"启动失败未进行有上限的自动重试");
        passed.Add("Codex proxy discovery, stale environment override, variable ports, direct mode, credential-free diagnostics and bounded startup retries");
    }
    static void RateTests(string root,List<string> passed) {
        string sessions=Path.Combine(root,"rate","sessions"); Directory.CreateDirectory(sessions);
        DateTimeOffset now=new DateTimeOffset(2026,10,4,12,0,10,TimeSpan.Zero);
        string alpha=Path.Combine(sessions,"alpha.jsonl"),beta=Path.Combine(sessions,"beta.jsonl");
        string history=Event("2026-10-04T11:59:01Z",1000,100,800);
        string current=Event("2026-10-04T12:00:02Z",1200,120,850);
        File.WriteAllText(alpha,Meta("2026-10-04T11:59:00Z","alpha","vscode")+history+
            Lifecycle("2026-10-04T12:00:00Z","task_started","turn-a")+
            Lifecycle("2026-10-04T12:00:01Z","task_started","turn-a")+current+current+
            Event("2026-10-04T12:00:03Z",1200,120,850),new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(alpha,now.UtcDateTime);
        TokenLedger ledger=new TokenLedger(Path.Combine(root,"rate")); Tokens t=ledger.Scan(now);
        ConversationRate a=TokenLedger.Select(t,"");
        Check(a.Id=="alpha"&&a.Running&&a.Total==220&&a.Cached==50&&a.PerSecond==22&&a.OutputPerSecond==2,"本轮速率包含历史、重复、缓存或起点错误");
        passed.Add("Turn rate excludes previous turns and duplicate snapshots; cached tokens counted once");
        File.WriteAllText(beta,Meta("2026-10-04T12:00:04Z","beta","vscode")+
            Lifecycle("2026-10-04T12:00:05Z","task_started","turn-b")+Event("2026-10-04T12:00:06Z",9000,100,5000),new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(beta,now.UtcDateTime);
        string child=Path.Combine(sessions,"child.jsonl"),fork=Path.Combine(sessions,"fork.jsonl");
        File.WriteAllText(child,Meta("2026-10-04T12:00:07Z","child",new{sub_agent="task"})+
            Lifecycle("2026-10-04T12:00:08Z","task_started","turn-child")+Event("2026-10-04T12:00:09Z",99999,999,0),new UTF8Encoding(false));
        File.WriteAllText(fork,Meta("2026-10-04T12:00:09Z","fork","vscode")+history+
            Lifecycle("2026-10-04T12:00:00Z","task_started","turn-a")+current,new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(child,now.UtcDateTime); File.SetLastWriteTimeUtc(fork,now.UtcDateTime);
        t=ledger.Scan(now.AddSeconds(5));
        Check(t.Conversations.Length==2&&TokenLedger.Select(t,"").Id=="beta","自动对话选择或子代理/分叉历史隔离失败");
        Check(TokenLedger.Select(t,"alpha").Total==220&&TokenLedger.Select(t,"beta").Total==9100,"多个会话的速率被相加");
        Check(TokenLedger.Select(t,"missing")==null,"指定对话丢失后错误地跟踪另一个对话");
        passed.Add("Concurrent conversations stay separate; latest-started desktop turn or explicit selection; no subagent or fork history");
        t=ledger.Scan(now.AddSeconds(6));
        Check(Math.Abs(TokenLedger.Select(t,"alpha").PerSecond.Value-220d/16)<0.000001,"无新记录时速率没有随经过时间更新");
        File.AppendAllText(beta,Lifecycle("2026-10-04T12:00:17Z","task_complete","old-turn"),new UTF8Encoding(false));
        t=ledger.Scan(now.AddSeconds(7)); Check(TokenLedger.Select(t,"beta").Running,"旧轮次完成事件结束了当前轮次");
        File.AppendAllText(beta,Lifecycle("2026-10-04T12:00:18Z","task_complete","turn-b"),new UTF8Encoding(false));
        t=ledger.Scan(now.AddSeconds(8)); Check(!TokenLedger.Select(t,"beta").Running&&TokenLedger.Select(t,"beta").PerSecond==0&&TokenLedger.Select(t,"beta").OutputPerSecond==0,"完成后未显示空闲零速率");
        string start=Lifecycle("2026-10-04T12:00:20Z","task_started","turn-a2");
        File.AppendAllText(alpha,start.Substring(0,start.Length-2),new UTF8Encoding(false));
        t=ledger.Scan(now.AddSeconds(11)); Check(TokenLedger.Select(t,"alpha").Total==220,"半条开始记录被提前应用");
        File.AppendAllText(alpha,start.Substring(start.Length-2)+Event("2026-10-04T12:00:22Z",1300,130,900),new UTF8Encoding(false));
        t=ledger.Scan(now.AddSeconds(15)); a=TokenLedger.Select(t,"alpha"); Check(a.Total==110&&a.PerSecond==22&&a.OutputPerSecond==2,"新一轮未清零或未正确使用增量");
        t=ledger.Scan(now.AddMinutes(16)); Check(TokenLedger.Select(t,"alpha").Stale&&TokenLedger.Select(t,"alpha").PerSecond==null&&TokenLedger.Select(t,"alpha").OutputPerSecond==null,"停止更新的记录被当成实时速率");
        File.AppendAllText(alpha,Lifecycle("2026-10-04T12:16:11Z","turn_aborted","turn-a2"),new UTF8Encoding(false));
        t=ledger.Scan(now.AddMinutes(17)); Check(!TokenLedger.Select(t,"alpha").Running,"中止未切换为空闲");
        Check(TokenLedger.Select(t,"alpha").OutputPerSecond==0,"中止后输出均速未清零");
        ConversationRate example=new ConversationRate{Running=true,Seconds=40,Input=800000,Output=2000,Cached=700000};
        Check(example.PerSecond==20050&&example.OutputPerSecond==50,"输入或缓存混入输出均速");
        example.Seconds=0; Check(example.OutputPerSecond==null,"零耗时应显示未知");
        passed.Add("Output-only turn average excludes input/cache; completion, abort, reset and stale states match consumption rate");
        passed.Add("Every-second elapsed-time update, matching completion, partial writes, new-turn reset, stale unknown and abort");
    }
    static void TimeZoneTests(string root,List<string> passed) {
        string home=Path.Combine(root,"zones"); Directory.CreateDirectory(Path.Combine(home,"sessions"));
        string log=Path.Combine(home,"sessions","local-day.jsonl");
        DateTimeOffset now=new DateTimeOffset(2026,10,4,12,0,0,TimeSpan.Zero);
        File.WriteAllText(log,Event("2026-10-03T23:59:59Z",100,10,50)+Event("2026-10-04T00:00:00Z",200,20,100)+Event("2026-10-04T08:00:00Z",300,30,150),new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(log,now.UtcDateTime);
        TimeZoneInfo west=TimeZoneInfo.CreateCustomTimeZone("Test -07",TimeSpan.FromHours(-7),"Test -07","Test -07");
        TimeZoneInfo east=TimeZoneInfo.CreateCustomTimeZone("Test +05:30",TimeSpan.FromMinutes(330),"Test +05:30","Test +05:30");
        TokenLedger ledger=new TokenLedger(home);
        Check(ledger.Scan(now,TimeZoneInfo.Utc).Total==220,"UTC 本日边界错误");
        Tokens shifted=ledger.Scan(now,west);
        Check(shifted.Day=="2026-10-04"&&shifted.Total==110&&shifted.Cached==50,"时区切换后同一日期没有重算用量和缓存");
        Check(UserTime.Day(new DateTimeOffset(2026,10,4,20,0,0,TimeSpan.Zero),east)=="2026-10-05","半小时时区或跨日处理失败");
        long reset=(long)(now-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds;
        Check(UsageForm.ResetLabelText(reset,false,false,now,west)=="重置 05:00","负时区重置时间错误");
        Check(UsageForm.ResetLabelText(reset,false,false,now,east)=="重置 17:30","半小时时区重置时间错误");
        DateTimeOffset nearMidnight=new DateTimeOffset(2026,10,5,6,30,0,TimeSpan.Zero);
        long nextReset=(long)(nearMidnight.AddHours(1)-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds;
        Check(UsageForm.ResetLabelText(nextReset,false,false,nearMidnight,west)=="重置 10/5 00:30","本地日期跨午夜遗漏日期");
        passed.Add("UTC, negative and fractional zones; same-date timezone changes rescan daily tokens/cache; reset labels follow local dates");
        TimeZoneInfo pacific=TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        DateTimeOffset spring=new DateTimeOffset(2026,3,8,20,0,0,TimeSpan.Zero),springNext=spring.AddDays(1);
        DateTimeOffset autumn=new DateTimeOffset(2026,11,1,20,0,0,TimeSpan.Zero),autumnNext=autumn.AddDays(1);
        Check((UserTime.DayStartUtc(springNext,pacific)-UserTime.DayStartUtc(spring,pacific)).TotalHours==23,"夏令时开始当日不为 23 小时");
        Check((UserTime.DayStartUtc(autumnNext,pacific)-UserTime.DayStartUtc(autumn,pacific)).TotalHours==25,"夏令时结束当日不为 25 小时");
        long winterReset=(long)(new DateTimeOffset(2026,1,15,12,0,0,TimeSpan.Zero)-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds;
        long summerReset=(long)(new DateTimeOffset(2026,7,15,12,0,0,TimeSpan.Zero)-new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero)).TotalSeconds;
        Check(UsageForm.ResetDate(winterReset,pacific).Value.Offset==TimeSpan.FromHours(-8)&&UsageForm.ResetDate(summerReset,pacific).Value.Offset==TimeSpan.FromHours(-7),"重置时间没有按对应日期应用夏令时");
        passed.Add("DST-aware day boundaries: 23/25-hour days and per-reset winter/summer offsets");
    }
    public static int Run(string report) {
        string root=Path.Combine(Path.GetTempPath(),"codex-usage-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"sessions"));
        List<string> passed=new List<string>();
        try {
            TimeZoneInfo testZone=TimeZoneInfo.CreateCustomTimeZone("Test +08",TimeSpan.FromHours(8),"Test +08","Test +08");
            string log=Path.Combine(root,"sessions","a.jsonl");
            string before=Event("2026-10-03T15:59:59Z",1000,100,800);
            string first=Event("2026-10-03T16:00:00Z",1100,110,850);
            File.WriteAllText(log,before+first+first,new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(log,new DateTime(2026,10,4,12,0,0));
            TokenLedger ledger=new TokenLedger(root);
            DateTimeOffset now=new DateTimeOffset(2026,10,4,12,0,0,TimeSpan.Zero);
            Tokens t=ledger.Scan(now,testZone);
            Check(t.Total==110&&t.Cached==50,"测试时区午夜增量或重复去重失败"); passed.Add("Local-zone midnight, cumulative deltas and duplicate snapshots");
            File.WriteAllText(Path.Combine(root,"sessions","fork.jsonl"),before+first,new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(Path.Combine(root,"sessions","fork.jsonl"),now.UtcDateTime);
            t=ledger.Scan(now.AddSeconds(5),testZone); Check(t.Total==110,"复制或分叉日志重复计数"); passed.Add("Copied history deduplication");
            string append=Event("2026-10-04T12:01:00Z",1300,150,950);
            File.AppendAllText(log,append.Substring(0,append.Length-3),new UTF8Encoding(false));
            t=ledger.Scan(now,testZone); Check(t.Total==110,"不完整记录被提前计数");
            File.AppendAllText(log,append.Substring(append.Length-3),new UTF8Encoding(false));
            t=ledger.Scan(now,testZone); Check(t.Total==350&&t.Input==300&&t.Output==50&&t.Cached==150,"追加增量或缓存口径错误"); passed.Add("Partial writes and cached input not double counted");
            File.AppendAllText(log,Event("2026-10-04T16:00:00Z",1400,170,1000),new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(log,new DateTime(2026,10,5,1,0,0));
            t=ledger.Scan(new DateTimeOffset(2026,10,5,1,0,0,TimeSpan.Zero),testZone); Check(t.Total==120,"跨天未重算"); passed.Add("Automatic new-day rollover");
            Quota q=Quota.From(Json.Parse("{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":31.5,\"windowDurationMins\":300,\"resetsAt\":1},\"secondary\":{\"usedPercent\":8,\"windowDurationMins\":10080,\"resetsAt\":2}}}}"));
            Check(q.Five==68.5&&q.Week==92,"usedPercent 被误当成剩余");
            Check(q.FiveReset==1&&q.WeekReset==2,"两项重置时间未对应各自窗口");
            Check(Quota.From(Json.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":15}}}")).Five==null,"不匹配窗口被误用"); passed.Add("Remaining quota and exact window selection");
            DateTimeOffset resetNow=new DateTimeOffset(2026,10,7,23,0,0,TimeSpan.FromHours(8));
            DateTimeOffset resetEpoch=new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero);
            long sameDay=(long)(resetNow.AddMinutes(20)-resetEpoch).TotalSeconds,nextDay=(long)(resetNow.AddHours(2)-resetEpoch).TotalSeconds;
            Check(UsageForm.ResetLabelText(sameDay,false,false,resetNow,testZone)=="重置 23:20","五小时重置时间未转为指定时区");
            Check(UsageForm.ResetLabelText(nextDay,false,false,resetNow,testZone)=="重置 10/8 01:00","跨日五小时重置遗漏日期");
            Check(UsageForm.ResetLabelText(sameDay,true,false,resetNow,testZone)=="重置 10/7 23:20","周重置遗漏日期");
            Check(UsageForm.ResetLabelText(nextDay,true,true,resetNow,testZone)=="10/8 01:00","窄窗口丢失周重置日期和时间");
            Check(UsageForm.ResetLabelText(0,false,false,resetNow,testZone)=="重置 未知"&&UsageForm.ResetLabelText(long.MaxValue,true,true,resetNow,testZone)=="未知","未知或异常时间戳未安全处理");
            passed.Add("Independent reset timestamps, local-zone time, midnight rollover and missing/invalid values");
            Check(UsageForm.CacheHitPercent(10000,8000)==80&&UsageForm.CacheHitPercent(10000,0)==0&&UsageForm.CacheHitPercent(10000,10000)==100,"缓存命中率未按输入 Token 计算");
            Check(UsageForm.CacheHitPercent(0,0)==null,"没有输入 Token 时错误显示缓存命中率为零");
            passed.Add("Token-weighted cached input ratio; zero input remains unknown; cached tokens not added twice");
            Rectangle area=new Rectangle(0,0,1920,1080);
            Rectangle large=UsageForm.PlaceBar(new Native.Rect{Left=100,Top=80,Right=1300,Bottom=980},area,1f);
            Check(large.Width==630&&large.Left+large.Width/2==700&&large.Top<120,"大窗顶部居中失败");
            Rectangle small=UsageForm.PlaceBar(new Native.Rect{Left=100,Top=80,Right=600,Bottom=700},area,1f);
            Check(small.Width<large.Width&&small.Right<=600-144&&small.Left>=100+64,"小窗未缩窄或遮住窗口按钮");
            passed.Add("Top placement and responsive width reserve window controls");
            RateTests(root,passed);
            TimeZoneTests(root,passed);
            NetworkTests(passed);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report)));
            File.WriteAllText(report,Json.Encode(new{success=true,passed=passed}),new UTF8Encoding(false)); return 0;
        } catch(Exception e) {
            File.WriteAllText(report,Json.Encode(new{success=false,error=e.Message,passed=passed}),new UTF8Encoding(false)); return 1;
        } finally { // Delete only this test's verified unique temporary directory.
            if(Path.GetFileName(root).StartsWith("codex-usage-test-",StringComparison.Ordinal)&&Directory.Exists(root)) Directory.Delete(root,true);
        }
    }
}
static class InteractionTests {
    static void Pump(int ms) { Stopwatch clock=Stopwatch.StartNew(); while(clock.ElapsedMilliseconds<ms) { Application.DoEvents(); Thread.Sleep(5); } }
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    public static int LayerHost(string state) {
        using(Form host=new Form{Text="额度条层级测试宿主",ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(350,220),ClientSize=new Size(520,270)}) {
            host.Shown+=delegate{File.WriteAllText(state,Json.Encode(new{window=host.Handle.ToInt64()}));};
            using(System.Windows.Forms.Timer timeout=new System.Windows.Forms.Timer{Interval=30000}) {
                timeout.Tick+=delegate{host.Close();}; timeout.Start(); Application.Run(host);
            }
        }
        return 0;
    }
    static void CoverBar(IntPtr bar,IntPtr owner) {
        // Restore an ownership relation after the host has been placed in front,
        // without a Z-order refresh. This is the live failure state: a valid
        // foreign owner, WS_VISIBLE set, but the host covers the entire bar.
        Native.SetWindowLongPtr(bar,-8,IntPtr.Zero);
        Native.SetWindowPos(bar,new IntPtr(1),0,0,0,0,0x10|0x1|0x2|0x200);
        Native.SetWindowLongPtr(bar,-8,owner);
    }
    static void LayerTests(string report,List<string> passed) {
        string state=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)),"layer-host-"+Guid.NewGuid().ToString("N")+".json");
        using(Process host=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--layer-test-host \""+state+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})) {
            try {
                Stopwatch startup=Stopwatch.StartNew(); while(!File.Exists(state)&&startup.ElapsedMilliseconds<3000) Pump(20);
                Check(File.Exists(state),"独立层级宿主未就绪");
                IntPtr owner=new IntPtr(Json.Num(Json.Get(Json.Parse(File.ReadAllText(state)),"window")));
                Native.SetWindowPos(owner,IntPtr.Zero,0,0,0,0,0x10|0x1|0x2|0x200); Pump(60);
                Native.Rect rect; Check(Native.GetWindowRect(owner,out rect),"无法取得层级宿主位置");
                using(UsageForm bar=new UsageForm(true)) {
                    bar.Bounds=new Rectangle(rect.Left+40,rect.Top+40,430,38); bar.Show(); bar.BindOwner(owner); Pump(80);
                    Point center=new Point(bar.Left+bar.Width/2,bar.Top+bar.Height/2);
                    // Reproduce a visible but covered cross-process overlay.
                    CoverBar(bar.Handle,owner); Pump(60);
                    Check(!Native.IsAboveWindow(bar.Handle,owner)&&Native.GetAncestor(Native.WindowFromPoint(center),2)==owner,"未复现窗口已显示但被宿主遮挡 (above="+Native.IsAboveWindow(bar.Handle,owner)+", ownerVisible="+Native.IsWindowVisible(owner)+", root="+Native.GetAncestor(Native.WindowFromPoint(center),2)+", owner="+owner+", bar="+bar.Handle+")");
                    Check(Native.EnsureAboveOwner(bar.Handle,owner),"未修复跨进程宿主遮挡"); Pump(60);
                    Check(Native.GetAncestor(Native.WindowFromPoint(center),2)==bar.Handle,"修复后额度条仍被遮挡");
                    Check((Native.GetWindowLongPtr(bar.Handle,-20).ToInt64()&8)==0&&Native.GetWindowLongPtr(bar.Handle,-8)==owner,"修复破坏了普通窗口层级或宿主关系");
                    Check(!Native.EnsureAboveOwner(bar.Handle,owner),"正常层级仍被反复提升");
                    passed.Add("Visible overlay hidden by a separate host is restored without topmost or repeated raises");
                    using(Form candidate=new Form{FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Bounds=bar.Bounds}) {
                        candidate.Show(); Native.SetWindowPos(candidate.Handle,IntPtr.Zero,0,0,0,0,0x10|0x1|0x2); Pump(60);
                        CoverBar(bar.Handle,owner);
                        Check(Native.EnsureAboveOwner(bar.Handle,owner),"普通候选窗存在时未修复宿主遮挡"); Pump(60);
                        Check(Native.GetAncestor(Native.WindowFromPoint(center),2)==candidate.Handle&&Native.IsAboveWindow(bar.Handle,owner),"修复宿主遮挡时盖住普通候选窗");
                        candidate.TopMost=true; Pump(60);
                        CoverBar(bar.Handle,owner);
                        Check(Native.EnsureAboveOwner(bar.Handle,owner),"置顶候选窗存在时未修复宿主遮挡"); Pump(60);
                        Check(Native.GetAncestor(Native.WindowFromPoint(center),2)==candidate.Handle&&(Native.GetWindowLongPtr(bar.Handle,-20).ToInt64()&8)==0,"修复把额度条变成置顶或盖住输入法");
                        passed.Add("Occlusion recovery preserves both ordinary and topmost candidate windows above the bar");
                    }
                }
            } finally {
                if(!host.HasExited) { host.CloseMainWindow(); if(!host.WaitForExit(500)) host.Kill(); }
                if(File.Exists(state)) File.Delete(state);
            }
        }
    }
    public static int Host(string state) {
        using(Form host=new Form{FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(375,490),ClientSize=new Size(190,60),TopMost=true})
        using(Button button=new Button{Text="测试点击",Location=new Point(25,14),Size=new Size(130,32)}) {
            int clicks=0; host.Controls.Add(button);
            Action save=delegate { Point point=button.PointToScreen(new Point(button.Width/2,button.Height/2)); File.WriteAllText(state,Json.Encode(new{clicks=clicks,button=button.Handle.ToInt64(),x=point.X,y=point.Y})); };
            button.Click+=delegate{clicks++;save();}; host.Shown+=delegate{save();};
            System.Windows.Forms.Timer timeout=new System.Windows.Forms.Timer{Interval=10000}; timeout.Tick+=delegate{host.Close();}; timeout.Start();
            Application.Run(host); timeout.Dispose();
        }
        return 0;
    }
    public static int Run(string report) {
        List<string> passed=new List<string>();
        Point originalCursor=Cursor.Position; IntPtr originalForeground=Native.GetForegroundWindow();
        try {
            using(UsageForm cacheLayout=new UsageForm(true)) cacheLayout.VerifyCacheLayout();
            passed.Add("Cache amount and hit percentage fit 190/292/420/630 widths at 96/144 DPI without overlapping daily tokens or turn rate");
            LayerTests(report,passed);
            using(Form host=new Form{Text="Codex 额度条交互测试",StartPosition=FormStartPosition.Manual,Location=new Point(350,220),ClientSize=new Size(520,270),ShowInTaskbar=false})
            using(Button target=new Button{Text="测试点击",Location=new Point(25,195),Size=new Size(130,32)})
            using(UsageForm bar=new UsageForm(true)) {
                bool clicked=false; target.Click+=delegate{clicked=true;}; host.Controls.Add(target); host.Show();
                bar.Location=host.PointToScreen(new Point(20,20)); bar.ClientSize=new Size(430,34); bar.Show(); bar.BindOwner(host.Handle); Pump(80);
                Check((Native.GetWindowLongPtr(bar.Handle,-20).ToInt64()&8)==0,"悬浮条仍是系统置顶窗口");
                Check(Native.GetWindowLongPtr(bar.Handle,-8)==host.Handle,"未绑定 Codex 同类宿主窗口");
                passed.Add("Ordinary non-topmost window owned by host");
                // Disable framework dismissal so this exercises the native outside-click watcher.
                bar.TestMenu.AutoClose=false; bar.TestMenu.Show(bar,new Point(340,bar.Height+4)); Pump(80);
                Check(bar.TestMenu.Visible&&bar.TestMouseWatcherActive,"菜单未安装外部点击检测");
                // Codex is a different process. WinForms' in-process menu filter has
                // different input routing, so exercise an actual separate test host.
                string hostState=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)),"click-host-"+Guid.NewGuid().ToString("N")+".json");
                using(Process clickHost=new Process{StartInfo=new ProcessStartInfo{FileName=Application.ExecutablePath,Arguments="--click-test-host \""+hostState+"\"",UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}}) {
                    clickHost.Start();
                    try {
                    Stopwatch startup=Stopwatch.StartNew(); while(!File.Exists(hostState)&&startup.ElapsedMilliseconds<2000) Pump(20);
                    Check(File.Exists(hostState),"独立点击测试窗口未就绪"); Pump(80);
                    object state=Json.Parse(File.ReadAllText(hostState));
                    Point point=new Point((int)Json.Num(Json.Get(state,"x")),(int)Json.Num(Json.Get(state,"y")));
                    Check(Native.WindowFromPoint(point)==new IntPtr(Json.Num(Json.Get(state,"button"))),"测试按钮被其他窗口覆盖，未发送点击");
                    OutsideMenuClick watcher=bar.TestMouseWatcher;
                    Task injection=Task.Run(()=>{Native.SetCursorPos(point.X,point.Y); Native.mouse_event(2,0,0,0,UIntPtr.Zero); Native.mouse_event(4,0,0,0,UIntPtr.Zero);}); Pump(450);
                    clicked=Json.Num(Json.Get(Json.Parse(File.ReadAllText(hostState)),"clicks"))>0;
                    Check(!bar.TestMenu.Visible,"点击菜单外没有关闭菜单 (seen="+watcher.Seen+", down="+watcher.Downs+", outside="+watcher.Outside+", errors="+watcher.Errors+", injected="+injection.IsCompleted+", clicked="+clicked+")");
                    } finally { if(!clickHost.HasExited) { clickHost.CloseMainWindow(); if(!clickHost.WaitForExit(500)) clickHost.Kill(); } if(File.Exists(hostState)) File.Delete(hostState); }
                }
                Check(clicked,"关闭菜单吞掉了目标按钮的点击");
                Check(!bar.TestMouseWatcherActive,"菜单关闭后仍在监听鼠标");
                passed.Add("Outside click dismisses menu and reaches target; watcher released");
                bar.TestMenu.AutoClose=true; bar.TestMenu.Show(bar,new Point(100,bar.Height+4)); Pump(80);
                ToolStripMenuItem tracking=(ToolStripMenuItem)bar.TestMenu.Items[1]; tracking.ShowDropDown(); Pump(80);
                ToolStripItem choice=tracking.DropDownItems[0]; bool selected=false; choice.Click+=delegate{selected=true;};
                Point choicePoint=tracking.DropDown.PointToScreen(new Point(choice.Bounds.Left+choice.Bounds.Width/2,choice.Bounds.Top+choice.Bounds.Height/2));
                OutsideMenuClick submenuWatcher=bar.TestMouseWatcher;
                Task.Run(()=>{Native.SetCursorPos(choicePoint.X,choicePoint.Y); Native.mouse_event(2,0,0,0,UIntPtr.Zero); Native.mouse_event(4,0,0,0,UIntPtr.Zero);}); Pump(400);
                Check(selected&&submenuWatcher.Outside==0,"速率跟踪子菜单的点击被误当作菜单外点击");
                Check(!bar.TestMenu.Visible&&!bar.TestMouseWatcherActive,"选择速率选项后未释放菜单监听");
                passed.Add("Rate tracking submenu receives clicks without outside-dismissal interference");
                using(Form candidate=new Form{FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Bounds=bar.Bounds,BackColor=Color.CornflowerBlue}) {
                    candidate.Show(host); Pump(80);
                    for(int i=0;i<3;i++) Native.SetWindowPos(bar.Handle,IntPtr.Zero,bar.Left,bar.Top,bar.Width,bar.Height,0x10|0x4|0x200);
                    Point center=new Point(bar.Left+bar.Width/2,bar.Top+bar.Height/2);
                    Check(Native.WindowFromPoint(center)==candidate.Handle,"位置刷新把普通弹窗压到后面");
                    candidate.TopMost=true; Pump(80);
                    Native.SetWindowPos(bar.Handle,IntPtr.Zero,bar.Left,bar.Top,bar.Width,bar.Height,0x10|0x4|0x200);
                    Check(Native.WindowFromPoint(center)==candidate.Handle,"位置刷新遮住系统置顶候选窗");
                    passed.Add("Position refresh stays below normal and topmost candidate windows");
                    candidate.Close();
                }
                bar.Close(); host.Close();
            }
            File.WriteAllText(report,Json.Encode(new{success=true,passed=passed}),new UTF8Encoding(false)); return 0;
        } catch(Exception e) {
            File.WriteAllText(report,Json.Encode(new{success=false,error=e.Message,passed=passed}),new UTF8Encoding(false)); return 1;
        } finally {
            Native.SetCursorPos(originalCursor.X,originalCursor.Y);
            if(originalForeground!=IntPtr.Zero&&Native.IsWindowVisible(originalForeground)) Native.SetForegroundWindow(originalForeground);
        }
    }
}
}



