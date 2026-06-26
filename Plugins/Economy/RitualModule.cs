namespace Economy
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;
    using ExileBridge;
    using ImGuiNET;

    /// <summary>
    ///     Prices the rewards in the Ritual Tribute Shop (Favours window) and draws the value on each
    ///     reward tile. A self-contained reader (signature BFS + raw memory) that reuses the Economy
    ///     <see cref="PoeNinjaPriceFetcher" />. Uniques are priced by their RenderItem art path
    ///     (RenderItem.ResourcePath @ +0x28 → e.g. "…/DarkDefiler.dds" → poe.ninja icon id), which is
    ///     language-independent; currency / bases fall back to the metadata path. Adapted from
    ///     AlexanderHel's RitualHelper.
    /// </summary>
    internal sealed class RitualModule
    {
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint VisibleMask = 0x800;       // flags bit 0x0B
        private const uint PositionModMask = 0x400;   // flags bit 0x0A

        private const int ParentOffset = 0xB8;
        private const int ChildrenFirstOffset = 0x10;
        private const int ChildrenLastOffset = 0x18;
        private const int FlagsOffset = 0x180;
        private const int TextWStringOffset = 0x390;
        private const int ItemPointerOffset = 0x4F8;
        private const int EntityDetailsOffset = 0x08;
        private const int RenderItemResourcePathOffset = 0x28; // found via the RE probe
        private const int ScanIntervalMs = 200;
        private const int PriceIntervalMs = 250;

        private readonly Stopwatch sigThrottle = Stopwatch.StartNew();
        private readonly Stopwatch priceThrottle = Stopwatch.StartNew();
        private readonly List<CachedTile> cachedTiles = new();

        private IntPtr handle = IntPtr.Zero;
        private int handlePid;
        private IntPtr cachedSigEl = IntPtr.Zero;
        private IntPtr lastUiRoot = IntPtr.Zero;

        private static readonly int UiBaseSize = System.Runtime.CompilerServices.Unsafe.SizeOf<UiElementBaseOffset>();
        private readonly byte[] uiBaseBuf = new byte[UiBaseSize];
        private readonly Dictionary<long, UiElementBaseOffset> frameBaseCache = new();

        private readonly struct CachedTile
        {
            public CachedTile(float x, float y, float w, float h, string label)
            {
                this.X = x;
                this.Y = y;
                this.W = w;
                this.H = h;
                this.Label = label;
            }

            public float X { get; }

            public float Y { get; }

            public float W { get; }

            public float H { get; }

            public string Label { get; }
        }

        internal void DrawUI(IContext ctx, EconomySettings settings)
        {
            if (!settings.ShowRitualPrices)
            {
                return;
            }

            try
            {
                var gameUi = ctx.Ui.GameUiAddress;
                if (gameUi == IntPtr.Zero || !this.EnsureHandle((int)ctx.Game.Pid))
                {
                    this.cachedTiles.Clear();
                    return;
                }

                if (gameUi != this.lastUiRoot)
                {
                    this.cachedSigEl = IntPtr.Zero;
                    this.lastUiRoot = gameUi;
                }

                if (!this.IsSignatureValid(this.cachedSigEl))
                {
                    this.cachedSigEl = IntPtr.Zero;
                }

                if (this.cachedSigEl == IntPtr.Zero && this.sigThrottle.ElapsedMilliseconds >= ScanIntervalMs)
                {
                    this.sigThrottle.Restart();
                    this.cachedSigEl = this.FindSignatureElement(gameUi);
                }

                if (this.cachedSigEl == IntPtr.Zero)
                {
                    this.cachedTiles.Clear();
                    return;
                }

                if (this.priceThrottle.ElapsedMilliseconds >= PriceIntervalMs)
                {
                    this.priceThrottle.Restart();
                    this.RebuildTiles(settings);
                }

                this.DrawTiles(settings);
            }
            catch
            {
                // best-effort overlay; never throw out of DrawUI
            }
        }

        private void RebuildTiles(EconomySettings settings)
        {
            this.cachedTiles.Clear();

            var cur = this.cachedSigEl;
            var grid = IntPtr.Zero;
            for (var up = 0; up < 8 && grid == IntPtr.Zero; up++)
            {
                grid = this.FindRewardGrid(cur);
                var parent = this.Ptr(cur + ParentOffset);
                if (parent == IntPtr.Zero)
                {
                    break;
                }

                cur = parent;
            }

            if (grid == IntPtr.Zero || !this.ChildSpan(grid, out var gf, out var gn))
            {
                return;
            }

            this.frameBaseCache.Clear();

            for (long i = 0; i < gn; i++)
            {
                var tile = this.Ptr(gf + (int)(i * 8));
                var item = this.TileItem(tile);
                if (item == IntPtr.Zero)
                {
                    continue;
                }

                if (!this.TryTileRect(tile, out var tx, out var ty, out var tw, out var th))
                {
                    continue;
                }

                var path = this.ReadMetadata(item);
                var art = this.ReadItemArt(item);

                var price = PoeNinjaPriceFetcher.GetPrice(art, null, art, path, art);
                if (price == null)
                {
                    if (settings.RitualDebugLog)
                    {
                        Console.WriteLine($"[Ritual] unpriced: art='{art}' path='{path}'");
                    }

                    continue;
                }

                var (value, currency) = PoeNinjaPriceFetcher.GetDisplayPrice(price, settings.DisplayCurrency);
                if (settings.MinValueEx > 0f && value < settings.MinValueEx)
                {
                    continue;
                }

                this.cachedTiles.Add(new CachedTile(tx, ty, tw, th, FormatValue(value, currency)));
            }
        }

        private void DrawTiles(EconomySettings settings)
        {
            if (this.cachedTiles.Count == 0)
            {
                return;
            }

            var fg = ImGui.GetForegroundDrawList();
            var font = ImGui.GetFont();
            var fontSize = ImGui.GetFontSize() * settings.PriceFontScale;
            var textColor = ImGui.ColorConvertFloat4ToU32(settings.TextColor);

            foreach (var tile in this.cachedTiles)
            {
                if (string.IsNullOrEmpty(tile.Label))
                {
                    continue;
                }

                var textW = ImGui.CalcTextSize(tile.Label).X * settings.PriceFontScale;
                var textPos = new Vector2(
                    tile.X + ((tile.W - textW) / 2f),
                    tile.Y + tile.H - fontSize - 3f);
                PriceLabel.Draw(fg, font, fontSize, textPos, tile.Label, textColor);
            }
        }

        private static string FormatValue(double value, string currency) => currency switch
        {
            "divine" => value.ToString("0.00", CultureInfo.InvariantCulture) + " div",
            "chaos" => value.ToString("0.#", CultureInfo.InvariantCulture) + " c",
            _ => value.ToString("0.#", CultureInfo.InvariantCulture) + " ex",
        };

        private string ReadItemArt(IntPtr item)
        {
            var render = this.ResolveComponent(item, "RenderItem");
            if (render == IntPtr.Zero)
            {
                return string.Empty;
            }

            var path = this.ReadStdWString(render + RenderItemResourcePathOffset);
            return ArtBasename(path);
        }

        private static string ArtBasename(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var slash = path.LastIndexOf('/');
            var name = slash >= 0 ? path[(slash + 1)..] : path;
            var dot = name.IndexOf('.');
            return dot >= 0 ? name[..dot] : name;
        }

        private bool IsSignatureValid(IntPtr el)
        {
            if (el == IntPtr.Zero)
            {
                return false;
            }

            var u = (ulong)el;
            if (u < 0x10000 || u > 0x7FFFFFFFFFFF)
            {
                return false;
            }

            if ((this.U32(el + FlagsOffset) & VisibleMask) == 0)
            {
                return false;
            }

            var t = this.ReadStdWString(el + TextWStringOffset);
            return !string.IsNullOrEmpty(t) &&
                   (t.Contains("Rituals Remaining", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("tribute to the king", StringComparison.OrdinalIgnoreCase));
        }

        private IntPtr FindSignatureElement(IntPtr uiRoot)
        {
            var queue = new Queue<IntPtr>();
            queue.Enqueue(uiRoot);
            var visited = new HashSet<IntPtr>();
            while (queue.Count > 0 && visited.Count < 20000)
            {
                var el = queue.Dequeue();
                if (el == IntPtr.Zero || !visited.Add(el))
                {
                    continue;
                }

                var flags = this.U32(el + FlagsOffset);
                if ((flags & VisibleMask) == 0 && el != uiRoot)
                {
                    continue;
                }

                if (this.ChildSpan(el, out var f, out var nn))
                {
                    for (long k = 0; k < nn; k++)
                    {
                        queue.Enqueue(this.Ptr(f + (int)(k * 8)));
                    }
                }

                var t = this.ReadStdWString(el + TextWStringOffset);
                if (!string.IsNullOrEmpty(t) && t.Length >= 6 &&
                    (t.Contains("Rituals Remaining", StringComparison.OrdinalIgnoreCase) ||
                     t.Contains("tribute to the king", StringComparison.OrdinalIgnoreCase)))
                {
                    return el;
                }
            }

            return IntPtr.Zero;
        }

        private IntPtr FindRewardGrid(IntPtr parent)
        {
            if (!this.ChildSpan(parent, out var first, out var n))
            {
                return IntPtr.Zero;
            }

            var best = IntPtr.Zero;
            var bestItems = 0;
            for (long i = 0; i < n; i++)
            {
                var c = this.Ptr(first + (int)(i * 8));
                if (!this.ChildSpan(c, out var cf, out var cn) || cn < 1 || cn > 120)
                {
                    continue;
                }

                var items = 0;
                for (long k = 0; k < cn; k++)
                {
                    if (this.TileItem(this.Ptr(cf + (int)(k * 8))) != IntPtr.Zero)
                    {
                        items++;
                    }
                }

                if (items >= 2 && items > bestItems)
                {
                    best = c;
                    bestItems = items;
                }
            }

            return best;
        }

        private IntPtr TileItem(IntPtr tile)
        {
            if (tile == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var item = this.Ptr(tile + ItemPointerOffset);
            return item != IntPtr.Zero && this.ResolveComponent(item, "RenderItem") != IntPtr.Zero ? item : IntPtr.Zero;
        }

        private bool ChildSpan(IntPtr el, out IntPtr first, out long n)
        {
            first = this.Ptr(el + ChildrenFirstOffset);
            n = 0;
            if (first == IntPtr.Zero)
            {
                return false;
            }

            var last = this.I64(el + ChildrenLastOffset);
            if (last == 0)
            {
                return false;
            }

            n = (last - first.ToInt64()) / 8;
            return n > 0 && n <= 4000;
        }

        private string ReadMetadata(IntPtr entity)
        {
            var details = this.Ptr(entity + EntityDetailsOffset);
            return details == IntPtr.Zero ? string.Empty : this.ReadStdWString(details + 0x08);
        }

        private IntPtr ResolveComponent(IntPtr entity, string name)
        {
            var details = this.Ptr(entity + EntityDetailsOffset);
            if (details == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var lookup = this.Ptr(details + 0x28);
            if (lookup == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var clFirst = this.I64(entity + 0x10);
            var clLast = this.I64(entity + 0x18);
            var compCount = (clLast - clFirst) / 8;
            if (compCount <= 0 || compCount > 256)
            {
                return IntPtr.Zero;
            }

            var bFirst = this.I64(lookup + 0x28);
            var bLast = this.I64(lookup + 0x30);
            if (bFirst == 0 || bLast == 0)
            {
                return IntPtr.Zero;
            }

            var entries = (bLast - bFirst) / 16;
            if (entries <= 0 || entries > 256)
            {
                return IntPtr.Zero;
            }

            for (long i = 0; i < entries; i++)
            {
                var e = (IntPtr)(bFirst + (i * 16));
                var namePtr = this.I64(e);
                var index = this.I32(e + 8);
                if (index < 0 || index >= compCount || namePtr == 0)
                {
                    continue;
                }

                if (this.ReadAscii((IntPtr)namePtr, 64) != name)
                {
                    continue;
                }

                return this.Ptr((IntPtr)(clFirst + (index * 8)));
            }

            return IntPtr.Zero;
        }

        // ── UI element screen-rect math (mirrors GameHelper.UiElementBase — the scale-aware
        //    parent-chain walk, same as the Runecraft module; reads UiElementBaseOffset) ───────

        private bool TryTileRect(IntPtr tile, out float x, out float y, out float w, out float h)
        {
            x = y = w = h = 0f;
            if (!this.TryReadBaseCached(tile, out var el) || (el.Flags & VisibleMask) == 0)
            {
                return false;
            }

            if (!this.TryGetUnscaledPosition(in el, 0, out var p))
            {
                return false;
            }

            var (sw, sh) = ScaleValue(el.ScaleIndex, el.LocalScaleMultiplier);
            x = p.X * sw;
            y = p.Y * sh;
            w = el.UnscaledSize.X * sw;
            h = el.UnscaledSize.Y * sh;
            return w > 1f && h > 1f;
        }

        private static (float W, float H) ScaleValue(byte index, float multiplier)
        {
            var io = ImGui.GetIO();
            var v1 = io.DisplaySize.X / (float)UiElementBaseFuncs.BaseResolution.X;
            var v2 = io.DisplaySize.Y / (float)UiElementBaseFuncs.BaseResolution.Y;
            float w = multiplier, h = multiplier;
            switch (index)
            {
                case 1: w *= v1; h *= v1; break;
                case 2: w *= v2; h *= v2; break;
                case 3: w *= v1; h *= v2; break;
            }

            return (w, h);
        }

        private bool TryGetUnscaledPosition(in UiElementBaseOffset el, int depth, out Vector2 pos)
        {
            var local = new Vector2(el.RelativePosition.X, el.RelativePosition.Y);
            if (el.ParentPtr == IntPtr.Zero || depth >= 64)
            {
                pos = local;
                return true;
            }

            if (!this.TryReadBaseCached(el.ParentPtr, out var parent) ||
                !this.TryGetUnscaledPosition(in parent, depth + 1, out var parentPos))
            {
                pos = local;
                return false;
            }

            if (UiElementBaseFuncs.ShouldModifyPos(el.Flags))
            {
                parentPos += new Vector2(parent.PositionModifier.X, parent.PositionModifier.Y);
            }

            if (parent.ScaleIndex == el.ScaleIndex && parent.LocalScaleMultiplier.Equals(el.LocalScaleMultiplier))
            {
                pos = parentPos + local;
                return true;
            }

            var (psw, psh) = ScaleValue(parent.ScaleIndex, parent.LocalScaleMultiplier);
            var (msw, msh) = ScaleValue(el.ScaleIndex, el.LocalScaleMultiplier);
            pos = new Vector2((parentPos.X * psw / msw) + local.X, (parentPos.Y * psh / msh) + local.Y);
            return true;
        }

        private bool TryReadBaseCached(IntPtr addr, out UiElementBaseOffset ui)
        {
            if (this.frameBaseCache.TryGetValue((long)addr, out ui))
            {
                return true;
            }

            if (!this.TryReadUiBase(addr, out ui))
            {
                return false;
            }

            this.frameBaseCache[(long)addr] = ui;
            return true;
        }

        private bool TryReadUiBase(IntPtr addr, out UiElementBaseOffset ui)
        {
            ui = default;
            var u = (ulong)addr;
            if (u < 0x10000 || u > 0x7FFFFFFFFFFF)
            {
                return false;
            }

            if (!ReadProcessMemory(this.handle, addr, this.uiBaseBuf, (uint)UiBaseSize, out var got) || got < UiBaseSize)
            {
                return false;
            }

            ui = MemoryMarshal.Read<UiElementBaseOffset>(this.uiBaseBuf);
            return true;
        }

        // ── Raw memory helpers ────────────────────────────────────────────────

        private bool EnsureHandle(int pid)
        {
            if (this.handle != IntPtr.Zero && this.handlePid == pid)
            {
                return true;
            }

            if (this.handle != IntPtr.Zero)
            {
                CloseHandle(this.handle);
                this.handle = IntPtr.Zero;
            }

            this.handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, pid);
            this.handlePid = pid;
            return this.handle != IntPtr.Zero;
        }

        private byte[] ReadBytes(IntPtr addr, int count)
        {
            if (addr == IntPtr.Zero || count <= 0 || count > 8192)
            {
                return Array.Empty<byte>();
            }

            var buf = new byte[count];
            return ReadProcessMemory(this.handle, addr, buf, (uint)count, out var got) && got > 0
                ? buf
                : Array.Empty<byte>();
        }

        private IntPtr Ptr(IntPtr addr)
        {
            var v = this.I64(addr);
            var u = (ulong)v;
            return u < 0x10000 || u > 0x7FFFFFFFFFFF ? IntPtr.Zero : (IntPtr)v;
        }

        private long I64(IntPtr addr)
        {
            var b = this.ReadBytes(addr, 8);
            return b.Length >= 8 ? BitConverter.ToInt64(b, 0) : 0;
        }

        private int I32(IntPtr addr)
        {
            var b = this.ReadBytes(addr, 4);
            return b.Length >= 4 ? BitConverter.ToInt32(b, 0) : 0;
        }

        private uint U32(IntPtr addr)
        {
            var b = this.ReadBytes(addr, 4);
            return b.Length >= 4 ? BitConverter.ToUInt32(b, 0) : 0;
        }

        private float Flt(IntPtr addr)
        {
            var b = this.ReadBytes(addr, 4);
            return b.Length >= 4 ? BitConverter.ToSingle(b, 0) : 0f;
        }

        private byte ReadByte(IntPtr addr)
        {
            var b = this.ReadBytes(addr, 1);
            return b.Length >= 1 ? b[0] : (byte)0;
        }

        private Vector2 Vec2(IntPtr addr)
        {
            var b = this.ReadBytes(addr, 8);
            return b.Length >= 8 ? new Vector2(BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4)) : Vector2.Zero;
        }

        private string ReadAscii(IntPtr addr, int maxChars)
        {
            var b = this.ReadBytes(addr, maxChars);
            if (b.Length == 0)
            {
                return string.Empty;
            }

            var z = Array.IndexOf(b, (byte)0);
            return z >= 0 ? Encoding.ASCII.GetString(b, 0, z) : Encoding.ASCII.GetString(b);
        }

        private string ReadStdWString(IntPtr addr)
        {
            var head = this.ReadBytes(addr, 0x20);
            if (head.Length < 0x20)
            {
                return string.Empty;
            }

            var len = BitConverter.ToInt64(head, 0x10);
            var cap = BitConverter.ToInt64(head, 0x18);
            if (len <= 0 || len > 1000)
            {
                return string.Empty;
            }

            var byteLen = (int)len * 2;
            byte[] data;
            if (cap <= 8)
            {
                data = this.ReadBytes(addr, byteLen);
            }
            else
            {
                var buf = BitConverter.ToInt64(head, 0);
                if (buf == 0)
                {
                    return string.Empty;
                }

                data = this.ReadBytes((IntPtr)buf, byteLen);
            }

            return data.Length == 0 ? string.Empty : Encoding.Unicode.GetString(data).TrimEnd('\0');
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
