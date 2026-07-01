// <copyright file="Input.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridge
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    ///     Shared synthetic-input helper for plugins. Key/mouse events are sent via
    ///     user32 on a single dedicated background worker so the overlay render thread
    ///     is never blocked by the inter-event sleeps (down/up gaps, holds).
    ///     Pure Win32 + concrete helper, in the spirit of <see cref="Draw" />.
    /// </summary>
    public static class Input
    {
        /// <summary>Virtual-key code: Left mouse button.</summary>
        public const int VkLButton = 0x01;

        /// <summary>Virtual-key code: Right mouse button.</summary>
        public const int VkRButton = 0x02;

        /// <summary>Virtual-key code: Control.</summary>
        public const int VkControl = 0x11;

        /// <summary>Virtual-key code: Shift.</summary>
        public const int VkShift = 0x10;

        /// <summary>Virtual-key code: Alt.</summary>
        public const int VkAlt = 0x12;

        /// <summary>Virtual-key code: Escape.</summary>
        public const int VkEscape = 0x1B;

        /// <summary>Virtual-key code: Enter.</summary>
        public const int VkReturn = 0x0D;

        private const byte VkA = 0x41;
        private const byte VkV = 0x56;
        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;

        private const uint KeyeventfKeydown = 0x0000;
        private const uint KeyeventfKeyup = 0x0002;
        private const uint MouseeventfLeftdown = 0x0002;
        private const uint MouseeventfLeftup = 0x0004;
        private const uint MouseeventfRightdown = 0x0008;
        private const uint MouseeventfRightup = 0x0010;
        private const uint MouseeventfMiddledown = 0x0020;
        private const uint MouseeventfMiddleup = 0x0040;

        private static readonly BlockingCollection<Action> Queue = new(new ConcurrentQueue<Action>());
        private static readonly object HeldLock = new();
        private static readonly HashSet<int> HeldKeys = new();
        private static readonly HashSet<MouseButton> HeldMouse = new();
        private static int pending;

        static Input()
        {
            var worker = new Thread(Loop)
            {
                IsBackground = true,
                Name = "ExileBridge-Input",
            };
            worker.Start();

            // Safety net: if the process exits while a key is still held down
            // (e.g. a movement key), release everything synchronously so it does
            // not stay stuck in the game after the overlay is gone.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseAllHeld();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => ReleaseAllHeld();
        }

        /// <summary>Mouse button selector.</summary>
        public enum MouseButton
        {
            /// <summary>Left mouse button.</summary>
            Left,

            /// <summary>Right mouse button.</summary>
            Right,

            /// <summary>Middle mouse button.</summary>
            Middle,
        }

        /// <summary>Gets the number of input actions still queued/in-flight.</summary>
        public static int Pending => Volatile.Read(ref pending);

        /// <summary>Queues an arbitrary input action to run on the background worker.</summary>
        /// <param name="action">the action to run.</param>
        public static void Enqueue(Action action)
        {
            if (action == null || Queue.IsAddingCompleted)
            {
                return;
            }

            Interlocked.Increment(ref pending);
            try
            {
                Queue.Add(action);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Decrement(ref pending);
            }
        }

        /// <summary>Returns whether a key is currently physically held down.</summary>
        /// <param name="vk">virtual-key code.</param>
        /// <returns>true if down.</returns>
        public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>
        ///     Scans for the first non-modifier key currently held and returns it
        ///     (for keybind capture UIs); returns 0 when nothing relevant is pressed.
        /// </summary>
        /// <param name="vk">the captured virtual-key code, when returning true.</param>
        /// <returns>true if a key was captured.</returns>
        public static bool TryCaptureKey(out int vk)
        {
            for (var i = 1; i <= 254; i++)
            {
                if (i is VkShift or VkControl or VkAlt or VkEscape or 91 or 92 or 93
                    or 160 or 161 or 162 or 163 or 164 or 165)
                {
                    continue;
                }

                if ((GetAsyncKeyState(i) & 0x8000) != 0)
                {
                    vk = i;
                    return true;
                }
            }

            vk = 0;
            return false;
        }

        /// <summary>Queues a press+release of a key with optional modifiers.</summary>
        /// <param name="vk">main virtual-key code.</param>
        /// <param name="ctrl">hold Ctrl.</param>
        /// <param name="shift">hold Shift.</param>
        /// <param name="alt">hold Alt.</param>
        /// <param name="holdMs">milliseconds between down and up.</param>
        public static void PressKey(int vk, bool ctrl = false, bool shift = false, bool alt = false, int holdMs = 30)
        {
            Enqueue(() =>
            {
                if (ctrl) keybd_event((byte)VkControl, 0, KeyeventfKeydown, UIntPtr.Zero);
                if (shift) keybd_event((byte)VkShift, 0, KeyeventfKeydown, UIntPtr.Zero);
                if (alt) keybd_event((byte)VkAlt, 0, KeyeventfKeydown, UIntPtr.Zero);
                keybd_event((byte)vk, 0, KeyeventfKeydown, UIntPtr.Zero);
                Thread.Sleep(Math.Clamp(holdMs, 1, 10000));
                keybd_event((byte)vk, 0, KeyeventfKeyup, UIntPtr.Zero);
                if (alt) keybd_event((byte)VkAlt, 0, KeyeventfKeyup, UIntPtr.Zero);
                if (shift) keybd_event((byte)VkShift, 0, KeyeventfKeyup, UIntPtr.Zero);
                if (ctrl) keybd_event((byte)VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
            });
        }

        /// <summary>Queues a key-down (no release). Tracks the key as held so it can
        /// be released on shutdown if no matching key-up is sent.</summary>
        /// <param name="vk">virtual-key code.</param>
        public static void KeyDown(int vk)
        {
            lock (HeldLock)
            {
                HeldKeys.Add(vk);
            }

            Enqueue(() => keybd_event((byte)vk, 0, KeyeventfKeydown, UIntPtr.Zero));
        }

        /// <summary>Queues a key-up and clears the held-key tracking.</summary>
        /// <param name="vk">virtual-key code.</param>
        public static void KeyUp(int vk)
        {
            lock (HeldLock)
            {
                HeldKeys.Remove(vk);
            }

            Enqueue(() => keybd_event((byte)vk, 0, KeyeventfKeyup, UIntPtr.Zero));
        }

        /// <summary>
        ///     Immediately (synchronously) releases every key currently tracked as
        ///     held — used as a shutdown / panic safety net so a held movement key is
        ///     never left stuck in the game.
        /// </summary>
        public static void ReleaseAllHeld()
        {
            int[] keys;
            MouseButton[] mice;
            lock (HeldLock)
            {
                if (HeldKeys.Count == 0 && HeldMouse.Count == 0)
                {
                    return;
                }

                keys = new int[HeldKeys.Count];
                HeldKeys.CopyTo(keys);
                HeldKeys.Clear();

                mice = new MouseButton[HeldMouse.Count];
                HeldMouse.CopyTo(mice);
                HeldMouse.Clear();
            }

            foreach (var vk in keys)
            {
                try
                {
                    keybd_event((byte)vk, 0, KeyeventfKeyup, UIntPtr.Zero);
                }
                catch
                {
                    // best-effort release during teardown
                }
            }

            foreach (var b in mice)
            {
                try
                {
                    var up = b switch
                    {
                        MouseButton.Right => MouseeventfRightup,
                        MouseButton.Middle => MouseeventfMiddleup,
                        _ => MouseeventfLeftup,
                    };
                    mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                }
                catch
                {
                    // best-effort release during teardown
                }
            }
        }

        /// <summary>Blocks until the input queue drains or the timeout elapses.</summary>
        /// <param name="timeoutMs">maximum time to wait.</param>
        public static void Flush(int timeoutMs = 500)
        {
            var spins = Math.Max(1, timeoutMs / 5);
            for (var i = 0; i < spins && Volatile.Read(ref pending) > 0; i++)
            {
                Thread.Sleep(5);
            }
        }

        /// <summary>Queues a mouse click of the given button.</summary>
        /// <param name="button">which button.</param>
        /// <param name="holdMs">milliseconds between down and up.</param>
        public static void Click(MouseButton button = MouseButton.Left, int holdMs = 30)
        {
            var (down, up) = button switch
            {
                MouseButton.Right => (MouseeventfRightdown, MouseeventfRightup),
                MouseButton.Middle => (MouseeventfMiddledown, MouseeventfMiddleup),
                _ => (MouseeventfLeftdown, MouseeventfLeftup),
            };

            Enqueue(() =>
            {
                mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(Math.Clamp(holdMs, 1, 10000));
                mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            });
        }

        /// <summary>Queues a mouse-button press (no release) — pair with <see cref="MouseUp" />.</summary>
        /// <param name="button">which button.</param>
        public static void MouseDown(MouseButton button = MouseButton.Left)
        {
            var down = button switch
            {
                MouseButton.Right => MouseeventfRightdown,
                MouseButton.Middle => MouseeventfMiddledown,
                _ => MouseeventfLeftdown,
            };

            lock (HeldLock)
            {
                HeldMouse.Add(button);
            }

            Enqueue(() => mouse_event(down, 0, 0, 0, UIntPtr.Zero));
        }

        /// <summary>Queues a mouse-button release. Pair with <see cref="MouseDown" />.</summary>
        /// <param name="button">which button.</param>
        public static void MouseUp(MouseButton button = MouseButton.Left)
        {
            var up = button switch
            {
                MouseButton.Right => MouseeventfRightup,
                MouseButton.Middle => MouseeventfMiddleup,
                _ => MouseeventfLeftup,
            };

            lock (HeldLock)
            {
                HeldMouse.Remove(button);
            }

            Enqueue(() => mouse_event(up, 0, 0, 0, UIntPtr.Zero));
        }

        /// <summary>Moves the cursor to an absolute desktop position (runs immediately).</summary>
        /// <param name="x">absolute X.</param>
        /// <param name="y">absolute Y.</param>
        public static void MoveMouse(int x, int y) => SetCursorPos(x, y);

        /// <summary>
        ///     Queues sending a chat message to the game: opens chat (Enter), pastes
        ///     <paramref name="message" /> via the clipboard (reliable for long whisper
        ///     text with special characters), then sends it (Enter). Overwrites the
        ///     system clipboard. Use for trade whispers and chat commands.
        /// </summary>
        /// <param name="message">the full message to send (e.g. an "@Account ..." whisper).</param>
        public static void SendChat(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            Enqueue(() =>
            {
                if (!SetClipboardText(message))
                {
                    return;
                }

                Thread.Sleep(25);
                Tap(VkReturn);          // open chat
                Thread.Sleep(70);
                CtrlTap(VkA);           // select any existing draft
                Thread.Sleep(25);
                CtrlTap(VkV);           // paste the message
                Thread.Sleep(70);
                Tap(VkReturn);          // send
            });
        }

        private static void Tap(int vk)
        {
            keybd_event((byte)vk, 0, KeyeventfKeydown, UIntPtr.Zero);
            Thread.Sleep(15);
            keybd_event((byte)vk, 0, KeyeventfKeyup, UIntPtr.Zero);
        }

        private static void CtrlTap(byte vk)
        {
            keybd_event((byte)VkControl, 0, KeyeventfKeydown, UIntPtr.Zero);
            keybd_event(vk, 0, KeyeventfKeydown, UIntPtr.Zero);
            Thread.Sleep(15);
            keybd_event(vk, 0, KeyeventfKeyup, UIntPtr.Zero);
            keybd_event((byte)VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
        }

        private static bool SetClipboardText(string text)
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    return false;
                }

                try
                {
                    EmptyClipboard();
                    var bytes = (text.Length + 1) * 2;
                    var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                    if (hGlobal == IntPtr.Zero)
                    {
                        return false;
                    }

                    var target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0);
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }

                    if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    // Ownership transferred to the clipboard on success; do not free.
                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Returns a friendly name for a virtual-key code.</summary>
        /// <param name="vk">virtual-key code.</param>
        /// <returns>display name.</returns>
        public static string KeyName(int vk) => vk switch
        {
            0 => "None",
            VkLButton => "LMouse",
            VkRButton => "RMouse",
            0x04 => "MMouse",
            0x05 => "Mouse4",
            0x06 => "Mouse5",
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x20 => "Space",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
            _ => $"Key{vk}",
        };

        private static void Loop()
        {
            foreach (var action in Queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch
                {
                    // Never let a bad input action kill the worker.
                }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);
    }
}
