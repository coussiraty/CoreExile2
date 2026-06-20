// <copyright file="OverlayLocalization.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Localization
{
    /// <summary>
    ///     Compatibility shim for plugins ported from a localized fork.
    ///     There is no localization logic in this build: <see cref="L"/> always
    ///     returns the English string. The German parameter is ignored.
    /// </summary>
    public static class OverlayLocalization
    {
        /// <summary>
        ///     Gets a value indicating whether the overlay language is German.
        ///     Always <c>false</c> in this build.
        /// </summary>
        public static bool IsGerman => false;

        /// <summary>
        ///     Returns the localized string. This build is English-only, so the
        ///     <paramref name="english"/> value is always returned.
        /// </summary>
        /// <param name="english">The English string (always returned).</param>
        /// <param name="german">The German string (ignored).</param>
        /// <returns>The <paramref name="english"/> string.</returns>
        public static string L(string english, string german) => english;
    }
}
