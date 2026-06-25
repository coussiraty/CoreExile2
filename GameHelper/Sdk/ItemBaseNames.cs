// <copyright file="ItemBaseNames.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Sdk
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    ///     Resolves an item's language-independent metadata base name (what the overlay reads from
    ///     an entity, e.g. <c>CurrencyUpgradeMagicToRare2</c> or <c>RuneColdGreater</c>) to the
    ///     game's own LOCALIZED display name (e.g. "Greater Regal Orb", "Greater Glacial Rune").
    ///
    ///     The display name is what poe.ninja / poe2scout key their prices on, so this removes the
    ///     need for hand-maintained metadata→name tables. Built once per session by reading the
    ///     game's <c>BaseItemTypes</c> table — reached through the loaded <c>Expedition2Recipes</c>
    ///     dat handle (a recipe row's reward foreign key points at the shared BaseItemTypes table).
    ///     Offsets verified live by the RunecraftHelper plugin (PoE2 0.5.x).
    /// </summary>
    internal static class ItemBaseNames
    {
        private const int DatPathOffset = 0x08;          // dat-file handle / table object → path string ptr
        private const int TableRowsVectorOffset = 0x28;  // table object → ptr to {begin,end} rows vector
        private const int RecipeStride = 0xBA;
        private const int RecipeRewardTableOffset = 0x34; // recipe row → BaseItemTypes table object
        private const int BaseItemTypeStride = 0x168;
        private const int BaseItemTypeIdOffset = 0x00;    // → meta-path "Metadata/Items/.../<Id>"
        private const int BaseItemTypeNameOffset = 0x20;  // → localized display-name buffer

        private static Dictionary<string, string> metaIdToName = new(StringComparer.OrdinalIgnoreCase);
        private static bool built;

        /// <summary>Gets the number of resolved base names (0 until the table is found).</summary>
        internal static int Count => metaIdToName.Count;

        /// <summary>Gets a value indicating whether the BaseItemTypes table has been read.</summary>
        internal static bool Built => built;

        /// <summary>Resolves a metadata base name to its localized display name.</summary>
        /// <param name="metaId">item metadata base name (last path segment).</param>
        /// <param name="name">resolved localized display name.</param>
        /// <returns>true when a non-empty display name was found.</returns>
        internal static bool TryGetName(string metaId, out string name)
        {
            name = string.Empty;
            if (string.IsNullOrEmpty(metaId))
            {
                return false;
            }

            return metaIdToName.TryGetValue(metaId, out name) && !string.IsNullOrEmpty(name);
        }

        /// <summary>
        ///     Builds the metadata→display-name map once, by BFS-walking each anchor's pointer graph
        ///     to the loaded Expedition2Recipes dat handle and reading the shared BaseItemTypes table.
        /// </summary>
        /// <param name="anchors">root addresses to search from (e.g. open stash/inventory panels).</param>
        internal static void EnsureBuilt(params IntPtr[] anchors)
        {
            if (built || anchors == null)
            {
                return;
            }

            try
            {
                foreach (var anchor in anchors)
                {
                    if (anchor == IntPtr.Zero)
                    {
                        continue;
                    }

                    // Preferred: the stash/inventory references the BaseItemTypes table directly to
                    // render item names, so its rows can be read straight from that dat handle (+0x28).
                    var bitHandle = FindDatHandle(anchor, "BaseItemTypes");
                    if (bitHandle != IntPtr.Zero && BuildFromRowsVector(bitHandle + TableRowsVectorOffset))
                    {
                        built = true;
                        return;
                    }

                    // Fallback: reach BaseItemTypes through the Expedition2Recipes reward foreign key.
                    var recipeHandle = FindDatHandle(anchor, "Expedition2Recipes");
                    if (recipeHandle != IntPtr.Zero && BuildFromRecipeTable(recipeHandle))
                    {
                        built = true;
                        return;
                    }
                }
            }
            catch
            {
                // Leave unbuilt; a later open panel can retry.
            }
        }

        // Reads BaseItemType rows from a {begin,end} rows-vector pointer and builds the map.
        private static bool BuildFromRowsVector(IntPtr rowsVecPtr)
        {
            var h = Core.Process.Handle;
            var vec = h.ReadMemory<IntPtr>(rowsVecPtr, false);
            var begin = h.ReadMemory<IntPtr>(vec, false);
            var end = h.ReadMemory<IntPtr>(vec + 8, false);
            if (begin == IntPtr.Zero || (long)end <= (long)begin)
            {
                return false;
            }

            var count = ((long)end - (long)begin) / BaseItemTypeStride;
            if (count <= 0 || count > 200000)
            {
                return false;
            }

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (long j = 0; j < count; j++)
            {
                var row = begin + (nint)(j * BaseItemTypeStride);
                var name = ReadUtf16Z(h.ReadMemory<IntPtr>(row + BaseItemTypeNameOffset, false), 64);
                if (name.Length < 2)
                {
                    continue;
                }

                var metaId = LastSegment(ReadUtf16Z(h.ReadMemory<IntPtr>(row + BaseItemTypeIdOffset, false), 128));
                if (metaId.Length == 0)
                {
                    continue;
                }

                dict[metaId] = name.Trim();
            }

            if (dict.Count == 0)
            {
                return false;
            }

            metaIdToName = dict;
            return true;
        }

        private static bool BuildFromRecipeTable(IntPtr recipeHandle)
        {
            var h = Core.Process.Handle;

            var recVec = h.ReadMemory<IntPtr>(recipeHandle + TableRowsVectorOffset, false);
            var recBegin = h.ReadMemory<IntPtr>(recVec, false);
            var recEnd = h.ReadMemory<IntPtr>(recVec + 8, false);
            if (recBegin == IntPtr.Zero || (long)recEnd <= (long)recBegin)
            {
                return false;
            }

            var recCount = ((long)recEnd - (long)recBegin) / RecipeStride;
            if (recCount <= 0 || recCount > 5000)
            {
                return false;
            }

            // BaseItemTypes table object = first recipe's reward-FK table ptr (shared by all rows).
            var bitTable = IntPtr.Zero;
            for (long k = 0; k < recCount && bitTable == IntPtr.Zero; k++)
            {
                bitTable = h.ReadMemory<IntPtr>(recBegin + (nint)(k * RecipeStride) + RecipeRewardTableOffset, false);
            }

            if (bitTable == IntPtr.Zero)
            {
                return false;
            }

            return BuildFromRowsVector(bitTable + TableRowsVectorOffset);
        }

        // BFS the anchor's pointer graph for a dat-file handle: an object whose +0x00 is an in-module
        // vtable and whose +0x08 path string contains <paramref name="pathContains" />.
        private static IntPtr FindDatHandle(IntPtr anchor, string pathContains)
        {
            var h = Core.Process.Handle;
            var seen = new HashSet<long> { (long)anchor };
            var queue = new Queue<(IntPtr addr, int depth)>();
            queue.Enqueue((anchor, 0));
            var visited = 0;

            while (queue.Count > 0 && visited < 40000)
            {
                var (addr, depth) = queue.Dequeue();
                visited++;

                if (IsExeAddr(h.ReadMemory<IntPtr>(addr, false)))
                {
                    var pathPtr = h.ReadMemory<IntPtr>(addr + DatPathOffset, false);
                    if (pathPtr != IntPtr.Zero)
                    {
                        var s = ReadUtf16Z(pathPtr, 80);
                        if (s.Contains(pathContains, StringComparison.Ordinal))
                        {
                            return addr;
                        }
                    }
                }

                if (depth >= 7)
                {
                    continue;
                }

                byte[] buf;
                try
                {
                    buf = h.ReadMemoryArray<byte>(addr, 0x180, false);
                }
                catch
                {
                    continue;
                }

                if (buf == null)
                {
                    continue;
                }

                for (var o = 0; o + 8 <= buf.Length; o += 8)
                {
                    var v = BitConverter.ToInt64(buf, o);
                    if ((ulong)v < 0x10000 || (ulong)v > 0x7FFFFFFFFFFF)
                    {
                        continue;
                    }

                    if (seen.Add(v))
                    {
                        queue.Enqueue(((IntPtr)v, depth + 1));
                    }
                }
            }

            return IntPtr.Zero;
        }

        private static bool IsExeAddr(IntPtr p) => (ulong)p >= 0x7FF000000000ul && (ulong)p < 0x800000000000ul;

        // Reads a NUL-terminated UTF-16 string from a raw char* (the dat string-heap layout).
        private static string ReadUtf16Z(IntPtr ptr, int maxChars)
        {
            if (ptr == IntPtr.Zero)
            {
                return string.Empty;
            }

            var u = (ulong)ptr;
            if (u < 0x10000 || u > 0x7FFFFFFFFFFF)
            {
                return string.Empty;
            }

            byte[] buf;
            try
            {
                buf = Core.Process.Handle.ReadMemoryArray<byte>(ptr, maxChars * 2, false);
            }
            catch
            {
                return string.Empty;
            }

            if (buf == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(maxChars);
            for (var i = 0; i + 1 < buf.Length; i += 2)
            {
                var c = (char)BitConverter.ToUInt16(buf, i);
                if (c == '\0')
                {
                    break;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var idx = path.LastIndexOf('/');
            return idx >= 0 && idx < path.Length - 1 ? path[(idx + 1)..] : path;
        }
    }
}
