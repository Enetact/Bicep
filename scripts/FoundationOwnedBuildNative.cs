using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PlatformStudio.FoundationOwnedBuild {
public static class Native {
 [StructLayout(LayoutKind.Sequential)] struct Security { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
 [StructLayout(LayoutKind.Sequential)] struct Startup { public int Size; public IntPtr Reserved,Desktop,Title; public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags; public ushort Show,ReservedSize; public IntPtr ReservedBytes,Input,Output,Error; }
 [StructLayout(LayoutKind.Sequential)] struct StartupEx { public Startup Startup; public IntPtr Attributes; }
 [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread; public int ProcessId,ThreadId; }
 [StructLayout(LayoutKind.Sequential)] struct BasicLimit { public long ProcessTime,JobTime; public uint Flags; public UIntPtr MinimumWorkingSet,MaximumWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority,Scheduling; }
 [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes; }
 [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory; }
 [StructLayout(LayoutKind.Sequential)] struct Accounting { public long UserTime,KernelTime,PeriodUserTime,PeriodKernelTime; public uint PageFaults,TotalProcesses,ActiveProcesses,TerminatedProcesses; }
 [StructLayout(LayoutKind.Sequential)] struct FileInfo { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written; public uint Volume,SizeHigh,SizeLow,Links,IdHigh,IdLow; }
 [StructLayout(LayoutKind.Sequential)] struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
 internal sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid { public JobHandle() : base(true) {} protected override bool ReleaseHandle() { return CloseHandle(handle); } }
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern JobHandle CreateJobObjectW(IntPtr security,string name);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(JobHandle job,int kind,ref ExtendedLimit value,int length);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(JobHandle job,int kind,out Accounting value,int length,IntPtr returned);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(JobHandle job,SafeProcessHandle process);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateJobObject(JobHandle job,uint code);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(SafeProcessHandle process,uint code);
 [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
 [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(SafeProcessHandle process,out uint code);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,int flags,StringBuilder name,ref int length);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool CreatePipe(out IntPtr read,out IntPtr write,ref Security security,int size);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr bytes);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr bytes,IntPtr previous,IntPtr returned);
 [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFileW(string path,uint access,uint share,ref Security security,uint disposition,uint flags,IntPtr template);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,[MarshalAs(UnmanagedType.Bool)] bool inherit,uint flags,IntPtr environment,string directory,ref StartupEx startup,out ProcessInfo information);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileInfo information);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,StringBuilder path,uint length,uint flags);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetFileInformationByHandle(SafeFileHandle file,int kind,ref Disposition value,int length);
 static IOException Failure(string code) { return new IOException("OwnedBuild"+code); }
 static string Quote(string value) { var result=new StringBuilder("\"");int slashes=0;foreach(var c in value){if(c=='\\'){slashes++;continue;}if(c=='\"'){result.Append('\\',slashes*2+1).Append(c);slashes=0;continue;}result.Append('\\',slashes).Append(c);slashes=0;}return result.Append('\\',slashes*2).Append('"').ToString(); }
 internal sealed class Capture : IDisposable {
  readonly FileStream input; readonly MemoryStream bytes=new MemoryStream(); readonly int maximum; readonly object gate=new object();
  public Task Work { get; private set; }
  public Capture(SafeFileHandle handle,int maximum){input=new FileStream(handle,FileAccess.Read,4096,false);this.maximum=maximum;Work=Task.CompletedTask;}
  public void Start(){Work=Task.Run(()=>{try{var buffer=new byte[8192];int count;while((count=input.Read(buffer,0,buffer.Length))>0){lock(gate){if(bytes.Length+count>maximum)throw Failure("OutputLimit");bytes.Write(buffer,0,count);}}}finally{input.Dispose();}});}
  public byte[] Copy(){lock(gate){return bytes.ToArray();}}
  public void Dispose(){input.Dispose();try{Work.Wait(5000);}catch{}lock(gate){var array=bytes.GetBuffer();Array.Clear(array,0,array.Length);bytes.Dispose();}}
 }
 public sealed class Observation {
  public int ProcessId { get; internal set; } public long ProcessStartTicks { get; internal set; } public string Executable { get; internal set; }="";
  public int ExitCode { get; internal set; } public int TotalProcesses { get; internal set; } public int ActiveProcesses { get; internal set; }
  public long FinishedUnixMilliseconds { get; internal set; }
 }
 public sealed class Session : IDisposable {
  readonly JobHandle job; readonly SafeProcessHandle process; readonly Capture output,error; readonly uint maximumActive,maximumTotal; bool completed,disposed,stopped;
  public int ProcessId { get; private set; } public long ProcessStartTicks { get; private set; } public string Executable { get; private set; }
  internal Session(JobHandle job,SafeProcessHandle process,Capture output,Capture error,int id,long ticks,string executable,uint maximumActive,uint maximumTotal){this.job=job;this.process=process;this.output=output;this.error=error;this.maximumActive=maximumActive;this.maximumTotal=maximumTotal;ProcessId=id;ProcessStartTicks=ticks;Executable=executable;}
  public byte[] StandardOutput(){return output.Copy();} public byte[] StandardError(){return error.Copy();}
  public Observation Wait(int timeoutMilliseconds,CancellationToken token){
   if(disposed||completed||stopped||timeoutMilliseconds<50||timeoutMilliseconds>900000)throw Failure("WaitRejected");var elapsed=Stopwatch.StartNew();
   while(true){token.ThrowIfCancellationRequested();if(elapsed.ElapsedMilliseconds>=timeoutMilliseconds)throw Failure("Timeout");
    if(output.Work.IsFaulted){output.Work.GetAwaiter().GetResult();}if(error.Work.IsFaulted){error.Work.GetAwaiter().GetResult();}
    if(!QueryInformationJobObject(job,1,out var accounting,Marshal.SizeOf<Accounting>(),IntPtr.Zero))throw Failure("JobQueryRejected");
    if(accounting.TotalProcesses>maximumTotal||accounting.ActiveProcesses>maximumActive)throw Failure("ProcessLimit");
    var wait=WaitForSingleObject(process,0);if(wait!=0&&wait!=258)throw Failure("ProcessQueryRejected");
    if(wait==0){if(!GetExitCodeProcess(process,out var code))throw Failure("ProcessQueryRejected");if(code!=0)throw Failure("ExitNonzero");
     if(accounting.ActiveProcesses==0&&output.Work.IsCompleted&&error.Work.IsCompleted){output.Work.GetAwaiter().GetResult();error.Work.GetAwaiter().GetResult();completed=true;return new Observation{ProcessId=ProcessId,ProcessStartTicks=ProcessStartTicks,Executable=Executable,ExitCode=0,TotalProcesses=(int)accounting.TotalProcesses,ActiveProcesses=0,FinishedUnixMilliseconds=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()};}}
    Thread.Sleep(10);
   }
  }
  // Stop the held job before copying diagnostics. Pipe EOF is a separate condition
  // from parent exit: descendants may retain inherited writers or no writers at all.
  public Observation Stop(){
   if(disposed)throw Failure("WaitRejected");
   if(!completed&&!stopped&&!TerminateJobObject(job,2))throw Failure("JobStopRejected");
   var clock=Stopwatch.StartNew();Accounting state;
   while(true){if(!QueryInformationJobObject(job,1,out state,Marshal.SizeOf<Accounting>(),IntPtr.Zero))throw Failure("JobQueryRejected");if(state.ActiveProcesses==0)break;if(clock.ElapsedMilliseconds>5000)throw Failure("JobStopUncertain");Thread.Sleep(10);}
   // Job active accounting may reach zero just before the original process
   // object is signaled; retain its handle and use the same bounded stop budget.
   while(true){var wait=WaitForSingleObject(process,10);if(wait==0)break;if(wait!=258)throw Failure("ProcessQueryRejected");if(clock.ElapsedMilliseconds>5000)throw Failure("JobStopUncertain");}
   while(!output.Work.IsCompleted||!error.Work.IsCompleted){if(clock.ElapsedMilliseconds>5000)throw Failure("CaptureStopUncertain");Thread.Sleep(10);}
   if(WaitForSingleObject(process,0)!=0||!GetExitCodeProcess(process,out var code))throw Failure("ProcessQueryRejected");stopped=true;
   return new Observation{ProcessId=ProcessId,ProcessStartTicks=ProcessStartTicks,Executable=Executable,ExitCode=unchecked((int)code),TotalProcesses=(int)state.TotalProcesses,ActiveProcesses=0,FinishedUnixMilliseconds=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()};
  }
  public void Dispose(){if(disposed)return;try{if(!completed&&!stopped)Stop();}finally{disposed=true;process.Dispose();job.Dispose();output.Dispose();error.Dispose();}}
 }
 public static Session Start(string executable,string[] arguments,string directory,IDictionary variables,int outputLimit=4194304,int maximumActiveProcesses=16,int maximumTotalProcesses=128){
  if(!OperatingSystem.IsWindows()||outputLimit<1024||outputLimit>4194304||maximumActiveProcesses<1||maximumActiveProcesses>128||maximumTotalProcesses<maximumActiveProcesses||maximumTotalProcesses>32768)throw Failure("ConfigurationRejected");
  var security=new Security{Length=Marshal.SizeOf<Security>(),Inherit=true};
  IntPtr readOut=IntPtr.Zero,writeOut=IntPtr.Zero,readError=IntPtr.Zero,writeError=IntPtr.Zero,attributes=IntPtr.Zero,handles=IntPtr.Zero,environment=IntPtr.Zero;
  var information=new ProcessInfo();bool initialized=false;JobHandle job=null;SafeProcessHandle process=null;Capture output=null,error=null;
  try {
   job=CreateJobObjectW(IntPtr.Zero,null);if(job.IsInvalid)throw Failure("JobCreationRejected");
   var limits=new ExtendedLimit{Basic=new BasicLimit{Flags=0x2000|0x8,ActiveProcessLimit=(uint)maximumActiveProcesses}};
   if(!SetInformationJobObject(job,9,ref limits,Marshal.SizeOf<ExtendedLimit>()))throw Failure("JobLimitRejected");
   if(!CreatePipe(out readOut,out writeOut,ref security,4096)||!CreatePipe(out readError,out writeError,ref security,4096)||!SetHandleInformation(readOut,1,0)||!SetHandleInformation(readError,1,0))throw Failure("PipeRejected");
   output=new Capture(new SafeFileHandle(readOut,true),outputLimit);readOut=IntPtr.Zero;error=new Capture(new SafeFileHandle(readError,true),outputLimit);readError=IntPtr.Zero;
   using(var input=CreateFileW("NUL",0x80000000,3,ref security,3,0x80,IntPtr.Zero)){
    if(input.IsInvalid)throw Failure("InputRejected");IntPtr size=IntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);if(size==IntPtr.Zero)throw Failure("HandleListRejected");
    attributes=Marshal.AllocHGlobal(size);if(!InitializeProcThreadAttributeList(attributes,1,0,ref size))throw Failure("HandleListRejected");initialized=true;handles=Marshal.AllocHGlobal(3*IntPtr.Size);
    Marshal.WriteIntPtr(handles,0,input.DangerousGetHandle());Marshal.WriteIntPtr(handles,IntPtr.Size,writeOut);Marshal.WriteIntPtr(handles,2*IntPtr.Size,writeError);
    if(!UpdateProcThreadAttribute(attributes,0,new IntPtr(0x20002),handles,new IntPtr(3*IntPtr.Size),IntPtr.Zero,IntPtr.Zero))throw Failure("HandleListRejected");
    var values=variables.Cast<DictionaryEntry>().OrderBy(p=>Convert.ToString(p.Key),StringComparer.OrdinalIgnoreCase).Select(p=>Convert.ToString(p.Key)+"="+Convert.ToString(p.Value));environment=Marshal.StringToHGlobalUni(string.Join("\0",values)+"\0\0");
    var startup=new StartupEx{Attributes=attributes,Startup=new Startup{Size=Marshal.SizeOf<StartupEx>(),Flags=0x101,Input=input.DangerousGetHandle(),Output=writeOut,Error=writeError}};
    var command=new StringBuilder(Quote(executable)+" "+string.Join(" ",arguments.Select(Quote)));
    if(!CreateProcessW(executable,command,IntPtr.Zero,IntPtr.Zero,true,0x08080404,environment,directory,ref startup,out information))throw Failure("ProcessCreationRejected");
   }
   process=new SafeProcessHandle(information.Process,true);information.Process=IntPtr.Zero;
   if(!AssignProcessToJobObject(job,process))throw Failure("JobAssignmentRejected");
   if(!GetProcessTimes(process,out var created,out _,out _,out _))throw Failure("ProcessIdentityRejected");var name=new StringBuilder(32768);var length=name.Capacity;
   if(!QueryFullProcessImageNameW(process,0,name,ref length)||!string.Equals(name.ToString(),executable,StringComparison.OrdinalIgnoreCase))throw Failure("ProcessIdentityRejected");
   output.Start();error.Start();if(ResumeThread(information.Thread)==uint.MaxValue)throw Failure("ProcessResumeRejected");
   var session=new Session(job,process,output,error,information.ProcessId,DateTime.FromFileTimeUtc(created).Ticks,name.ToString(),(uint)maximumActiveProcesses,(uint)maximumTotalProcesses);job=null;process=null;output=null;error=null;return session;
  } finally {
   // Only the original newly-created process/job can be stopped. A suspended assignment failure never runs its code.
   if(process!=null){try{TerminateProcess(process,2);WaitForSingleObject(process,5000);}finally{process.Dispose();}}
   if(job!=null){TerminateJobObject(job,2);job.Dispose();}
   if(information.Thread!=IntPtr.Zero)CloseHandle(information.Thread);if(information.Process!=IntPtr.Zero)CloseHandle(information.Process);
   foreach(var handle in new[]{readOut,writeOut,readError,writeError})if(handle!=IntPtr.Zero)CloseHandle(handle);
   output?.Dispose();error?.Dispose();if(environment!=IntPtr.Zero)Marshal.FreeHGlobal(environment);if(handles!=IntPtr.Zero)Marshal.FreeHGlobal(handles);
   if(initialized)DeleteProcThreadAttributeList(attributes);if(attributes!=IntPtr.Zero)Marshal.FreeHGlobal(attributes);
  }
 }

 public sealed class FileLease : IDisposable {
  sealed class Entry { public string Path; public bool Directory; public SafeFileHandle Handle; public FileInfo Identity; }
  readonly List<Entry> entries=new List<Entry>();readonly List<SafeFileHandle> anchors=new List<SafeFileHandle>();readonly string root;bool disposed,mutated;
  internal FileLease(string root){this.root=Path.GetFullPath(root);try{
   var parent=Path.GetDirectoryName(this.root);var paths=new List<string>();while(parent!=null){paths.Add(parent);parent=Path.GetDirectoryName(parent);}paths.Reverse();foreach(var path in paths)anchors.Add(Open(path,true,false));
   Walk(this.root);
   AssertCurrent();
  }catch{Dispose();throw;}
   void Walk(string path){if(entries.Count>=8192)throw Failure("FileLeaseLimit");var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw Failure("FileLeaseRejected");var directory=(attributes&FileAttributes.Directory)!=0;
    var handle=Open(path,directory,true);try{if(!GetFileInformationByHandle(handle,out var info)||!directory&&info.Links!=1)throw Failure("FileLeaseRejected");entries.Add(new Entry{Path=path,Directory=directory,Handle=handle,Identity=info});handle=null;}finally{handle?.Dispose();}
    if(directory)foreach(var child in Directory.EnumerateFileSystemEntries(path))Walk(child);
   }
  }
  static SafeFileHandle Open(string path,bool directory,bool delete){var security=new Security{Length=Marshal.SizeOf<Security>()};var handle=CreateFileW("\\\\?\\"+Path.GetFullPath(path),0x80U|(delete?0x10000U:0),directory?3U:0U,ref security,3,0x00200000U|(directory?0x02000000U:0),IntPtr.Zero);try{
   if(handle.IsInvalid||!GetFileInformationByHandle(handle,out var info)||(info.Attributes&0x400)!=0||((info.Attributes&0x10)!=0)!=directory)throw Failure("FileLeaseRejected");
   var name=new StringBuilder(32768);var size=GetFinalPathNameByHandleW(handle,name,(uint)name.Capacity,0);var actual=name.ToString();if(actual.StartsWith("\\\\?\\",StringComparison.Ordinal))actual=actual.Substring(4);
   if(size==0||size>=name.Capacity||!string.Equals(actual.TrimEnd('\\'),Path.GetFullPath(path).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw Failure("FileLeaseBindingRejected");var result=handle;handle=null;return result;
  }finally{handle?.Dispose();}}
  public void AssertCurrent(){if(disposed||mutated)throw Failure("FileLeaseConsumed");var names=new List<string>();var expected=new HashSet<string>(entries.Select(e=>e.Path),StringComparer.OrdinalIgnoreCase);Collect(root);var observed=names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
   if(observed.Length!=entries.Count||!observed.SequenceEqual(entries.Select(e=>e.Path).Order(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase))throw Failure("FileLeaseTreeChanged");
   foreach(var entry in entries){if(!GetFileInformationByHandle(entry.Handle,out var current)||current.Volume!=entry.Identity.Volume||current.IdHigh!=entry.Identity.IdHigh||current.IdLow!=entry.Identity.IdLow||(current.Attributes&0x400)!=0)throw Failure("FileLeaseBindingRejected");}
   void Collect(string path){if(names.Count>=8192||!expected.Contains(path))throw Failure("FileLeaseTreeChanged");names.Add(path);var entry=entries.First(e=>string.Equals(e.Path,path,StringComparison.OrdinalIgnoreCase));if(entry.Directory)foreach(var child in Directory.EnumerateFileSystemEntries(path))Collect(child);}
  }
  public void Delete(){AssertCurrent();mutated=true;foreach(var entry in entries.OrderBy(e=>e.Directory?1:0).ThenByDescending(e=>e.Path.Length)){
   var value=new Disposition{Delete=true};if(!SetFileInformationByHandle(entry.Handle,4,ref value,Marshal.SizeOf<Disposition>()))throw Failure("FileRemovalUncertain");entry.Handle.Dispose();
  }if(Directory.Exists(root)||File.Exists(root))throw Failure("FileRemovalUncertain");}
  public void Dispose(){if(disposed)return;disposed=true;foreach(var entry in entries)entry.Handle.Dispose();foreach(var handle in anchors)handle.Dispose();}
 }
 public static FileLease AcquireFiles(string root){return new FileLease(root);}
}
}
