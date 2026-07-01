namespace SekhemaHelper
{
    using System;
    using System.Numerics;

    /// <summary>
    /// Large/mini-map world-to-pixel projection (copied from the Radar plugin's Helper). Set
    /// <see cref="DiagonalLength" /> and <see cref="Scale" /> from the live map element, then call
    /// <see cref="DeltaInWorldToMapDelta" /> with the grid delta (entity - player) and height delta.
    /// </summary>
    internal static class MapProjection
    {
        public static readonly double CameraAngle = 38.7 * Math.PI / 180;
        private static double diagonalLength;
        private static float scale = 0.5f;
        private static float cos;
        private static float sin;

        /// <summary>Sets the diagonal length of the visible map element.</summary>
        public static double DiagonalLength
        {
            set
            {
                if (value > 0 && value != diagonalLength)
                {
                    diagonalLength = value;
                    UpdateCosSin();
                }
            }
        }

        /// <summary>Sets the scale of the visible map element.</summary>
        public static float Scale
        {
            set
            {
                if (value > 0 && value != scale)
                {
                    scale = value;
                    UpdateCosSin();
                }
            }
        }

        /// <summary>
        /// Converts an entity→player grid delta (+ terrain-height delta) to a map-pixel delta from
        /// the map center. The Z divisor (10.86957) is the world/grid distance ratio read from game data.
        /// </summary>
        public static Vector2 DeltaInWorldToMapDelta(Vector2 delta, float deltaZ)
        {
            deltaZ /= 10.86957f;
            return new Vector2((delta.X - delta.Y) * cos, (deltaZ - (delta.X + delta.Y)) * sin);
        }

        private static void UpdateCosSin()
        {
            float mapScale = 240f / scale;
            cos = (float)(diagonalLength * Math.Cos(CameraAngle) / mapScale);
            sin = (float)(diagonalLength * Math.Sin(CameraAngle) / mapScale);
        }
    }
}
