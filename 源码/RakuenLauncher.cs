using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

// Launches the original game with a narrowly scoped, memory-only empty-glyph fix.
// The on-disk game executable and resource archives are never rewritten.
static class RakuenLauncher
{
    const uint Continue = 0x10002, NotHandled = 0x80010001;
    const string ExpectedHash = "2715DDD3435D19E980CA09B9EEF54A0C7B0879C715AFD0D4820CE90BD49ED0DA";
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Startup {
        public uint cb; public string reserved, desktop, title;
        public uint x,y,xsize,ysize,xcount,ycount,fill,flags;
        public ushort show,cb2; public IntPtr reserved2,stdin,stdout,stderr;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr process,thread; public uint pid,tid; }
    // Native x64 DEBUG_EVENT: the union starts at offset 16.
    [StructLayout(LayoutKind.Explicit, Size=176)]
    struct DebugEvent {
        [FieldOffset(0)] public uint code;
        [FieldOffset(4)] public uint pid;
        [FieldOffset(8)] public uint tid;
        [FieldOffset(16)] public uint exceptionCode;
        [FieldOffset(16)] public IntPtr file;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool CreateProcessW(string app, StringBuilder command, IntPtr pa, IntPtr ta,
        bool inherit,uint flags,IntPtr env,string cwd,ref Startup si,out ProcessInfo pi);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool WaitForDebugEvent(out DebugEvent e,uint timeout);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ContinueDebugEvent(uint pid,uint tid,uint status);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool DebugActiveProcessStop(uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr read);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool WriteProcessMemory(IntPtr p,IntPtr a,byte[] b,UIntPtr n,out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool VirtualProtectEx(IntPtr p,IntPtr a,UIntPtr n,uint protection,out uint old);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool FlushInstructionCache(IntPtr p,IntPtr a,UIntPtr n);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h,uint exitCode);
    static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    static string ResolveGame(string[] args) {
        if(args.Length>1) throw new ArgumentException("只接受一个参数：原游戏 EXE 的完整路径。");
        if(args.Length==1) {
            string selected=Path.GetFullPath(args[0]);
            if(!File.Exists(selected)) throw new FileNotFoundException("找不到指定的游戏程序：",selected);
            return selected;
        }
        string adjacent=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"rakuen_cn_160916.exe");
        if(File.Exists(adjacent)) return adjacent;
        using(var dialog=new OpenFileDialog()) {
            dialog.Title="请选择游戏根目录中的 rakuen_cn_160916.exe（不是修正补丁）";
            dialog.Filter="游戏程序 (*.exe)|*.exe";
            dialog.FileName="rakuen_cn_160916.exe";
            dialog.CheckFileExists=true;
            return dialog.ShowDialog()==DialogResult.OK ? dialog.FileName : null;
        }
    }
    static byte[] Read(IntPtr process, int address, int size) {
        byte[] bytes=new byte[size]; UIntPtr n;
        if (!ReadProcessMemory(process,new IntPtr(address),bytes,(UIntPtr)size,out n) || n.ToUInt64()!=(ulong)size) return null;
        return bytes;
    }
    static bool Apply(IntPtr process) {
        byte[] signature = {0xff,0x74,0x24,0x1c,0xff,0x74,0x24,0x1c};
        byte[] found=Read(process,0x434800,signature.Length);
        if (found==null || !found.SequenceEqual(signature)) return false;
        byte[] original={0x8b,0x4c,0x24,0x18,0x85,0xc9,0x75,0x03,0xc2,0x1c,0x00,0xc6,0x01,0x00,0xc2,0x1c,0x00,0x00,0x00};
        found=Read(process,0x434829,original.Length);
        if (found==null || !found.SequenceEqual(original)) throw new InvalidOperationException("字形函数与验证过的版本不符，已停止启动。");
        // This branch is entered only when GetGlyphOutline returns zero (empty glyph).
        // EAX is zero: initialize all five DWORDs of GLYPHMETRICS, then return normally.
        byte[] patch={0x8b,0x4c,0x24,0x10,0x89,0x01,0x89,0x41,0x04,0x89,0x41,0x08,0x89,0x41,0x0c,0x89,0x41,0x10,0xc2,0x1c,0x00};
        uint old,discard; UIntPtr written;
        IntPtr address=new IntPtr(0x434829);
        Check(VirtualProtectEx(process,address,(UIntPtr)patch.Length,0x40,out old));
        try {
            Check(WriteProcessMemory(process,address,patch,(UIntPtr)patch.Length,out written));
            if (written.ToUInt64()!=(ulong)patch.Length) throw new IOException("未能完整应用字形修复。");
            Check(FlushInstructionCache(process,address,(UIntPtr)patch.Length));
        } finally { Check(VirtualProtectEx(process,address,(UIntPtr)patch.Length,old,out discard)); }
        byte[] verified=Read(process,0x434829,patch.Length);
        if (verified==null || !verified.SequenceEqual(patch)) throw new IOException("字形修复验证失败。");
        return true;
    }
    [STAThread]
    static void Main(string[] args) {
        ProcessInfo pi=new ProcessInfo(); bool started=false,detached=false;
        try {
            string exe=ResolveGame(args);
            if(exe==null) return;
            string folder=Path.GetDirectoryName(Path.GetFullPath(exe));
            using (var sha=SHA256.Create()) using (var stream=File.OpenRead(exe)) {
                string hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
                if (hash!=ExpectedHash) throw new InvalidOperationException("原程序版本或校验值已改变，未应用修复。");
            }
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) {
                using(process) {
                    bool sameGame=false;
                    try { sameGame=String.Equals(process.MainModule.FileName,exe,StringComparison.OrdinalIgnoreCase); }
                    catch(Win32Exception) {} catch(InvalidOperationException) {}
                    if(sameGame && process.MainWindowHandle!=IntPtr.Zero) {
                    MessageBox.Show("游戏已在运行，请先关闭现有游戏窗口。","乐园兼容启动"); return;
                }}
            }
            // Limit the compatibility layer to this launch and its game child.
            Environment.SetEnvironmentVariable("__COMPAT_LAYER","HIGHDPIAWARE");
            Startup si=new Startup(); si.cb=(uint)Marshal.SizeOf(typeof(Startup));
            Check(CreateProcessW(exe,new StringBuilder("\""+exe+"\""),IntPtr.Zero,IntPtr.Zero,false,2,IntPtr.Zero,folder,ref si,out pi));
            started=true; Check(DebugSetProcessKillOnExit(false));
            var timer=Stopwatch.StartNew(); bool patched=false;
            while (timer.Elapsed.TotalSeconds<30) {
                DebugEvent e;
                if (!WaitForDebugEvent(out e,1000)) continue;
                uint status=Continue;
                if(e.code==1 && e.exceptionCode!=0x80000003 && e.exceptionCode!=0x4000001f) status=NotHandled;
                try {
                    if(e.code==5) throw new InvalidOperationException("游戏在修复前退出，退出码：0x"+e.exceptionCode.ToString("X8"));
                    if(e.code==3 || e.code==6) { if(e.file!=IntPtr.Zero && e.file!=new IntPtr(-1)) CloseHandle(e.file); }
                    patched=Apply(pi.process);
                } finally { Check(ContinueDebugEvent(e.pid,e.tid,status)); }
                if(patched) break;
            }
            if(!patched) throw new TimeoutException("未能定位已验证的字形函数。");
            Check(DebugActiveProcessStop(pi.pid)); detached=true;
        } catch(Exception error) {
            if(started && !detached) TerminateProcess(pi.process,1);
            MessageBox.Show(error.Message,"乐园兼容启动",MessageBoxButtons.OK,MessageBoxIcon.Error);
        } finally {
            if(pi.thread!=IntPtr.Zero) CloseHandle(pi.thread);
            if(pi.process!=IntPtr.Zero) CloseHandle(pi.process);
        }
    }
}
