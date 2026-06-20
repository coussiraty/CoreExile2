namespace RunecraftHelper
{
    using System;
    using System.Runtime.InteropServices;

    // Local mirrors of the GameOffsets native layouts the geometry code reads via
    // MemoryMarshal.Read. The ExileBridge SDK exposes UI element ADDRESSES (escape hatch)
    // but not the host's memory layouts, so the few structs this plugin marshals from the
    // game's address space are replicated here, byte-for-byte, to stay self-contained.

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct StdTuple2D<T>
        where T : unmanaged
    {
        public T X;
        public T Y;
    }

    // MSVC std::wstring layout (only its footprint matters inside UiElementBaseOffset; the
    // plugin reads strings manually via ReadStdWString, not through this struct).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct StdWString
    {
        public IntPtr Buffer;
        public IntPtr ReservedBytes;
        public int Length;
        public int PAD_14;
        public int Capacity;
        public int PAD_1C;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct StdVector
    {
        public IntPtr First;
        public IntPtr Last;
        public IntPtr End;
    }

    // Byte-identical mirror of GameOffsets.Objects.UiElement.UiElementBaseOffset — the field
    // offsets MUST match the host exactly (read straight from game memory).
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    internal struct UiElementBaseOffset
    {
        [FieldOffset(0x000)] public IntPtr Vtable;
        [FieldOffset(0x008)] public IntPtr Self;
        [FieldOffset(0x010)] public StdVector ChildrensPtr;
        [FieldOffset(0x0B8)] public IntPtr ParentPtr;
        [FieldOffset(0x0F0)] public StdTuple2D<float> PositionModifier;
        [FieldOffset(0x118)] public StdTuple2D<float> RelativePosition;
        [FieldOffset(0x130)] public float LocalScaleMultiplier;
        [FieldOffset(0x140)] public StdWString StringIdPtr;
        [FieldOffset(0x180)] public uint Flags;
        [FieldOffset(0x18A)] public byte ScaleIndex;
        [FieldOffset(0x288)] public StdTuple2D<float> UnscaledSize;
        [FieldOffset(0x2A4)] public uint BackgroundColor;
    }

    internal static class UiElementBaseFuncs
    {
        private const int SHOULD_MODIFY_BINARY_POS = 0x0A;

        // From GGPK Metadata/UI/UISettings.xml (the base UI resolution the game scales against).
        public static readonly StdTuple2D<double> BaseResolution = new() { X = 2560, Y = 1600 };

        public static bool ShouldModifyPos(uint flags) => (flags & (1u << SHOULD_MODIFY_BINARY_POS)) != 0;
    }
}
