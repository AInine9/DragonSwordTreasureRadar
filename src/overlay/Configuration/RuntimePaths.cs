using System;
namespace DragonSwordTreasureRadar {
    internal static class RuntimePaths {
#if INPROCESS
        public const string CatalogFolder = "cache";
#else
        public const string CatalogFolder = "scripts";
#endif
        public static string BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
    }
}
