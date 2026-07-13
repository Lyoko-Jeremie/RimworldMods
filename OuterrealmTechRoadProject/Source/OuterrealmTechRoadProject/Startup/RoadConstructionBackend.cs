using Verse;

namespace OuterrealmTechRoadProject.Startup
{
    /// <summary>
    /// 检测当前启用的世界道路施工后端。
    /// RoadsOfTheRim 与 RailsAndRoadsOfTheRim 是相似的替代实现，不能假设两者的类型可以互换。
    /// </summary>
    public static class RoadConstructionBackend
    {
        public const string RoadsPackageId = "Mlie.RoadsOfTheRim";
        public const string RailsPackageId = "Mlie.RailsAndRoadsOfTheRim";

        private static bool warningLogged;

        public static bool RoadsActive
        {
            get
            {
                return ModLister.GetActiveModWithIdentifier(RoadsPackageId, true) != null;
            }
        }

        public static bool RailsActive
        {
            get
            {
                return ModLister.GetActiveModWithIdentifier(RailsPackageId, true) != null;
            }
        }

        public static RoadConstructionBackendKind Selected
        {
            get
            {
                if (RailsActive)
                {
                    return RoadConstructionBackendKind.RailsAndRoadsOfTheRim;
                }

                if (RoadsActive)
                {
                    return RoadConstructionBackendKind.RoadsOfTheRim;
                }

                return RoadConstructionBackendKind.None;
            }
        }

        public static void LogDetectedBackend()
        {
            if (warningLogged)
            {
                return;
            }

            warningLogged = true;
            if (RoadsActive && RailsActive)
            {
                Log.Warning("[OuterrealmTechRoadProject] RoadsOfTheRim and RailsAndRoadsOfTheRim are both active. They are replacement-style road construction systems; OuterrealmTechRoadProject will prefer RailsAndRoadsOfTheRim compatibility data.");
            }
            else if (!RoadsActive && !RailsActive)
            {
                Log.Warning("[OuterrealmTechRoadProject] No supported road construction backend is active. Enable Roads of the Rim or Rails and Roads of the Rim to build outerrealm links from caravans.");
            }
        }
    }

    public enum RoadConstructionBackendKind
    {
        None,
        RoadsOfTheRim,
        RailsAndRoadsOfTheRim
    }
}
