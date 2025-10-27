using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace DroneWarehouseMod.Core
{
    // Совместимость: удаление/посадка с разными сигнатурами
    internal static class PlantCompat
    {
        // Пытаемся вызвать любую доступную performRemoveAction(...)
        public static bool TryPerformRemoveActionCompat(SObject obj, Vector2 tile, GameLocation loc)
        {
            try
            {
                var t = obj.GetType();
                var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               .Where(m => m.Name == "performRemoveAction");
                foreach (var mi in methods)
                {
                    var ps = mi.GetParameters();
                    var args = new object[ps.Length];
                    bool ok = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType;
                        if (pt == typeof(Vector2)) args[i] = tile;
                        else if (typeof(GameLocation).IsAssignableFrom(pt)) args[i] = loc;
                        else if (pt == typeof(Farmer)) args[i] = Game1.player;
                        else if (pt == typeof(bool)) args[i] = true;
                        else { ok = false; break; }
                    }
                    if (!ok) continue;

                    mi.Invoke(obj, args);
                    return true;
                }
            }
            catch { }
            return false;
        }

        // Перебор перегрузок HoeDirt.plant(...); иначе — прямой конструктор Crop
        public static bool TryPlantCompat(HoeDirt hd, string seedQid, GameLocation loc, int x, int y, Farmer who, bool isFertilizer = false)
        {
            if (hd is null || hd.crop != null) return false;

            string unq = Unqualify(seedQid);
            int intId = ParseObjectId(seedQid);

            // (int, int, int, Farmer)
            var mi = typeof(HoeDirt).GetMethod("plant", new[] { typeof(int), typeof(int), typeof(int), typeof(Farmer) });
            if (mi != null && intId >= 0 && mi.Invoke(hd, new object[] { intId, x, y, who }) is true)
                return true;

            // (string, int, int, Farmer)
            mi = typeof(HoeDirt).GetMethod("plant", new[] { typeof(string), typeof(int), typeof(int), typeof(Farmer) });
            if (mi != null && mi.Invoke(hd, new object[] { unq, x, y, who }) is true)
                return true;

            // Item‑версия
            mi = typeof(HoeDirt).GetMethod("plant", new[] { typeof(Item), typeof(int), typeof(int), typeof(Farmer), typeof(bool), typeof(GameLocation) });
            if (mi != null)
            {
                var seedItem = ItemRegistry.Create(seedQid);
                if (mi.Invoke(hd, new object[] { seedItem, x, y, who, isFertilizer, loc }) is true)
                    return true;
            }

            // string + loc
            mi = typeof(HoeDirt).GetMethod("plant", new[] { typeof(string), typeof(int), typeof(int), typeof(Farmer), typeof(bool), typeof(GameLocation) });
            if (mi != null)
            {
                if (mi.Invoke(hd, new object[] { unq, x, y, who, isFertilizer, loc }) is true) return true;
                if (mi.Invoke(hd, new object[] { seedQid, x, y, who, isFertilizer, loc }) is true) return true;
            }

            // int + loc
            mi = typeof(HoeDirt).GetMethod("plant", new[] { typeof(int), typeof(int), typeof(int), typeof(Farmer), typeof(bool), typeof(GameLocation) });
            if (mi != null && intId >= 0 && mi.Invoke(hd, new object[] { intId, x, y, who, isFertilizer, loc }) is true)
                return true;

            // форс: создать Crop руками
            return ForcePlant(hd, seedQid, x, y, loc);

            static bool ForcePlant(HoeDirt hd, string seedQid, int x, int y, GameLocation loc)
            {
                try
                {
                    if (hd.crop != null) return false;

                    string unq = Unqualify(seedQid);
                    int intId = ParseObjectId(seedQid);
                    object? crop = null;
                    var cropType = typeof(Crop);

                    foreach (var ctor in cropType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var ps = ctor.GetParameters();

                        if (ps.Length == 3 && ps[0].ParameterType == typeof(string))
                            crop = ctor.Invoke(new object[] { unq, x, y });
                        else if (ps.Length == 4 && ps[0].ParameterType == typeof(string) && ps[3].ParameterType == typeof(GameLocation))
                            crop = ctor.Invoke(new object[] { unq, x, y, loc });
                        else if (ps.Length == 3 && ps[0].ParameterType == typeof(int) && intId >= 0)
                            crop = ctor.Invoke(new object[] { intId, x, y });
                        else if (ps.Length == 4 && ps[0].ParameterType == typeof(int) && intId >= 0 && ps[3].ParameterType == typeof(GameLocation))
                            crop = ctor.Invoke(new object[] { intId, x, y, loc });

                        if (crop is Crop) break;
                    }

                    if (crop is Crop c) { hd.crop = c; return true; }
                }
                catch { }
                return false;
            }

            static string Unqualify(string qid)
            {
                if (string.IsNullOrEmpty(qid)) return qid;
                if (qid.Length > 3 && qid[0] == '(' && qid[2] == ')') return qid.Substring(3);
                return qid;
            }
            static int ParseObjectId(string qid)
            {
                if (string.IsNullOrEmpty(qid)) return -1;
                if (qid.StartsWith("(O)") && int.TryParse(qid.AsSpan(3), out var id)) return id;
                if (int.TryParse(qid, out var id2)) return id2;
                return -1;
            }
        }
    }
}
