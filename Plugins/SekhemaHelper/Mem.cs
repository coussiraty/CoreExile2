using System;
using System.Runtime.InteropServices;

namespace SekhemaHelper
{
    // Local mirror of GameOffsets.Natives.StdVector (the SDK does not expose host
    // memory layouts). Two pointers (begin/end) + capacity end; Pack = 1.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct StdVector
    {
        public IntPtr First;
        public IntPtr Last;
        public IntPtr End;
    }

    // Own cross-process reader (GameHelper's SafeMemoryHandle methods are internal to plugins),
    // mirroring the proven pattern from the Atlas plugin. The plugin feeds the game pid each
    // frame via Mem.Pid (from Ctx.Game.Pid), so this stays decoupled from the host.
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
            if (address == IntPtr.Zero || count <= 0 || count > 4096)
                return Array.Empty<byte>();
            EnsureHandle();
            var buf = new byte[count];
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(handle, address, buf);
            return buf;
        }

        public static string ReadWideString(IntPtr address, int maxChars)
        {
            if (address == IntPtr.Zero || maxChars <= 0)
                return string.Empty;
            var bytes = ReadBytes(address, maxChars * 2);
            if (bytes.Length == 0)
                return string.Empty;
            var s = System.Text.Encoding.Unicode.GetString(bytes);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
