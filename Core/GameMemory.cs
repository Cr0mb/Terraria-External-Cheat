using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TerrariaTrainer
{
    /// <summary>
    /// Thin external memory accessor (ReadProcessMemory / WriteProcessMemory).
    /// Target is 32-bit, and this app is built x86, so a 32-bit IntPtr/uint
    /// address maps 1:1 onto the target's pointer width.
    /// </summary>
    public sealed class GameMemory : IDisposable
    {
        const uint PROCESS_VM_READ = 0x0010;
        const uint PROCESS_VM_WRITE = 0x0020;
        const uint PROCESS_VM_OPERATION = 0x0008;
        const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr h);

        // Address is UIntPtr: a uint->UIntPtr cast is lossless on both x86 and x64.
        // (On x86, casting a >2GB address to a signed IntPtr throws OverflowException,
        //  and Terraria is LargeAddressAware so heap objects can live above 2GB.)
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr h, UIntPtr addr, byte[] buf, int size, out int read);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteProcessMemory(IntPtr h, UIntPtr addr, byte[] buf, int size, out int written);

        IntPtr _handle;
        public Process Process { get; private set; }
        // Note: deliberately does NOT call Process.HasExited — that can throw (Access denied /
        // Win32Exception) and must never be in a hot loop condition. A dead process just makes
        // RPM/WPM return false, which callers handle.
        public bool IsOpen => _handle != IntPtr.Zero;

        public bool Open(Process p)
        {
            Close();
            Process = p;
            _handle = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION, false, p.Id);
            return _handle != IntPtr.Zero;
        }

        public void Close()
        {
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
            Process = null;
        }

        readonly byte[] _b4 = new byte[4];

        public bool ReadBytes(uint addr, byte[] buf)
        {
            return ReadProcessMemory(_handle, (UIntPtr)addr, buf, buf.Length, out int got) && got == buf.Length;
        }

        // Read exactly `count` bytes into the start of buf (buf may be larger).
        public bool ReadBytes(uint addr, byte[] buf, int count)
        {
            return ReadProcessMemory(_handle, (UIntPtr)addr, buf, count, out int got) && got == count;
        }

        public bool WriteBytes(uint addr, byte[] buf)
        {
            return WriteProcessMemory(_handle, (UIntPtr)addr, buf, buf.Length, out int wrote) && wrote == buf.Length;
        }

        public uint ReadU32(uint addr)
        {
            return ReadBytes(addr, _b4) ? BitConverter.ToUInt32(_b4, 0) : 0u;
        }

        public int ReadI32(uint addr)
        {
            return ReadBytes(addr, _b4) ? BitConverter.ToInt32(_b4, 0) : 0;
        }

        public float ReadF32(uint addr)
        {
            return ReadBytes(addr, _b4) ? BitConverter.ToSingle(_b4, 0) : 0f;
        }

        readonly byte[] _b8 = new byte[8];
        public double ReadF64(uint addr)
        {
            return ReadBytes(addr, _b8) ? BitConverter.ToDouble(_b8, 0) : 0d;
        }

        public bool WriteF64(uint addr, double value)
        {
            return WriteProcessMemory(_handle, (UIntPtr)addr, BitConverter.GetBytes(value), 8, out _);
        }

        public bool WriteI32(uint addr, int value)
        {
            return WriteProcessMemory(_handle, (UIntPtr)addr, BitConverter.GetBytes(value), 4, out _);
        }

        public bool WriteF32(uint addr, float value)
        {
            return WriteProcessMemory(_handle, (UIntPtr)addr, BitConverter.GetBytes(value), 4, out _);
        }

        public bool WriteBool(uint addr, bool value)
        {
            return WriteProcessMemory(_handle, (UIntPtr)addr, new byte[] { (byte)(value ? 1 : 0) }, 1, out _);
        }

        public void Dispose() => Close();
    }
}
