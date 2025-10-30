using StardewValley;
using StardewValley.GameData.Crops;

namespace DroneWarehouseMod.Core
{
    // Кэш Data/Crops
    internal static class DataCache
    {
        // ключ = неквалифицированный ID семян, например "472"
        private static Dictionary<string, CropData>? _crops;

        public static Dictionary<string, CropData> Crops
        {
            get
            {
                if (_crops == null) LoadCrops();
                return _crops!;
            }
        }

        public static void Refresh() => LoadCrops();
        public static void Invalidate() => _crops = null;

        private static void LoadCrops()
        {
            try
            {
                _crops = Game1.content.Load<Dictionary<string, CropData>>("Data/Crops");
            }
            catch
            {
                _crops = new Dictionary<string, CropData>();
            }
        }

        // Проверка сезонности семян для локации
        public static bool IsSeedSeasonOk(string qualifiedSeedId, GameLocation loc)
        {
            try
            {
                string key = Qid.Unqualify(qualifiedSeedId);
                if (Crops.TryGetValue(key, out var cd) && cd?.Seasons?.Count > 0)
                    return cd.Seasons.Contains(Game1.GetSeasonForLocation(loc));
            }
            catch { /* молча */ }

            return true; // нет данных — считаем допустимым
        }
    }
}
