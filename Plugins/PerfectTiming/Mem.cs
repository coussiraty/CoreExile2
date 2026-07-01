using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PerfectTiming
{
    // Own cross-process reader (the SDK doesn't expose the Actor component or host memory layouts).
    // The plugin feeds the game pid each frame via Mem.Pid (from Ctx.Game.Pid).
    internal static class Mem
    {
        public static uint Pid;

        private static IntPtr handle = IntPtr.Zero;
        private static int handlePid;

        private static void EnsureHandle()
        {
            int pid = (int)Pid;
            if (handle != IntPtr.Zero && handlePid == pid)
                return;
            Close();
            handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                ProcessMemoryUtilities.Native.ProcessAccessFlags.Read, pid);
            handlePid = pid;
        }

        public static void Close()
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
            handlePid = 0;
        }

        public static T Read<T>(IntPtr address) where T : unmanaged
        {
            if (address == IntPtr.Zero)
                return default;
            EnsureHandle();
            T result = default;
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemory(handle, address, ref result);
            return result;
        }

        public static byte[] ReadBytes(IntPtr address, int count)
        {
            if (address == IntPtr.Zero || count <= 0 || count > 8192)
                return Array.Empty<byte>();
            EnsureHandle();
            var buf = new byte[count];
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(handle, address, buf);
            return buf;
        }

        public static IntPtr Ptr(IntPtr address) => Read<IntPtr>(address);

        public static long I64(IntPtr address) => Read<long>(address);

        public static int I32(IntPtr address) => Read<int>(address);

        public static string ReadAscii(IntPtr address, int maxChars)
        {
            if (address == IntPtr.Zero || maxChars <= 0)
                return string.Empty;
            var bytes = ReadBytes(address, maxChars);
            if (bytes.Length == 0)
                return string.Empty;
            int z = Array.IndexOf(bytes, (byte)0);
            return z >= 0 ? Encoding.ASCII.GetString(bytes, 0, z) : Encoding.ASCII.GetString(bytes);
        }

        public static string ReadUnicode(IntPtr address, int maxChars)
        {
            if (address == IntPtr.Zero || maxChars <= 0)
                return string.Empty;
            var bytes = ReadBytes(address, maxChars * 2);
            if (bytes.Length == 0)
                return string.Empty;
            var s = Encoding.Unicode.GetString(bytes);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
