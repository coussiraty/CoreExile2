using ExileBridge;
using System.Collections.Generic;
using System.Numerics;

namespace SekhemaHelper
{
    public sealed class SekhemaHelperSettings : IPluginSettings
    {
        public string CurrentProfile = "Default";
        public Dictionary<string, ProfileContent> Profiles = new()
        {
            ["Default"] = ProfileContent.CreateDefaultProfile(),
            ["No-Hit"] = ProfileContent.CreateNoHitProfile(),
        };

        public Vector4 BestPathColor = new(0.2f, 1f, 0.2f, 1f);
        public Vector4 TextColor = new(1f, 1f, 1f, 1f);
        public Vector4 BackgroundColor = new(0f, 0f, 0f, 0.75f);
        public float FrameThickness = 4f;

        public bool DrawBestPath = true;
        public bool DebugEnable = false;

        // Crystal Path (Death Crystal Run) — a walkable pickup route through the lethal Hourglass
        // crystals in your current escape room, drawn on the large area map.
        public bool ShowCrystalPath = true;
        public bool CrystalWalkablePath = true;
        public Vector4 CrystalRouteColor = new(1f, 0.85f, 0.2f, 1f);
        public Vector4 CrystalMarkerColor = new(1f, 0.3f, 0.3f, 1f);
        public float CrystalRouteThickness = 2f;
        public float CrystalMarkerRadius = 9f;
        public int CrystalIdGroupGap = 10;
        public float CrystalRoomMargin = 30f;
    }
}
