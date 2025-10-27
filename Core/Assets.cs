using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace DroneWarehouseMod.Core
{
    // Утилиты загрузки текстур
    internal static class Assets
    {
        // Загружает кадры по шаблону; при отсутствии — возвращает fallback
        public static Texture2D[] LoadFramesSeq(IModHelper helper, string pattern, int count, Texture2D fallback, IMonitor mon)
        {
            var list = new List<Texture2D>(count);
            for (int i = 1; i <= count; i++)
            {
                string path = string.Format(pattern, i);
                try { list.Add(helper.ModContent.Load<Texture2D>(path)); }
                catch { mon.Log($"[UI] Нет кадра '{path}', пропускаю.", LogLevel.Trace); }
            }
            if (list.Count == 0) list.Add(fallback);
            return list.ToArray();
        }

        // Пытается загрузить текстуру; при неудаче — null и лог
        public static void TryLoad(IModHelper helper, IMonitor mon, ref Texture2D? slot, string path)
        {
            try { slot = helper.ModContent.Load<Texture2D>(path); }
            catch { slot = null; mon.Log($"[UI] Нет ассета {path} — ваниль.", LogLevel.Trace); }
        }
    }
}
