using Microsoft.Xna.Framework;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
using StardewValley;
using StardewValley.GameData.Crops;
using SObject = StardewValley.Object;
using DroneWarehouseMod.Core;
using System.Reflection;
using MD = DroneWarehouseMod.Core.ModDataKeys;

namespace DroneWarehouseMod.Game.Drones
{
    // ---------- Сборщик ----------
    internal sealed class HarvestDrone : CargoDroneBase
    {
        public override DroneKind Kind => DroneKind.Harvest;
        private Bush? _targetBush;
        private readonly float _speed;
        public override float SpeedPxPerTick => _speed;
        private readonly ModConfig _managerConfig;

        private struct ExtraDropInfo
            {
                public string Qid;
                public int Min;
                public int Max;
                public double Chance;
            }

        public HarvestDrone(Building home, DroneAnimSet anim, int capacity, float speed, ModConfig managerConfig = null)
            : base(home, anim, capacity)
        {
            _speed = Math.Max(0.1f, speed);
            _managerConfig = managerConfig ?? new ModConfig();
        }

        protected override bool TryAcquireWork(Farm farm, DroneManager mgr, out Point tile, out WorkKind kind)
        {
            tile = default; kind = WorkKind.None;
            double bestDist = double.MaxValue;
            Point best = default; WorkKind bestKind = WorkKind.None;
            _targetBush = null; // сбрасываем прошлую цель-куст

            // --- 1) спелые грядки (не цветы) ---
            foreach (var pair in farm.terrainFeatures.Pairs)
            {
                if (pair.Value is not HoeDirt hd || hd.crop is null) continue;
                if (!hd.readyForHarvest()) continue;

                try
                {
                    bool isForageCrop = hd.crop.forageCrop?.Value ?? false;

                    // менеджер отдаёт значение из конфига
                    if (mgr.SkipFlowerCrops && !isForageCrop)
                    {
                        try
                        {
                            string qid = DroneWarehouseMod.Core.Qid.Qualify(hd.crop.indexOfHarvest.Value?.ToString() ?? "");
                            if (ItemRegistry.Create(qid, 1) is SObject so && so.Category == SObject.flowersCategory)
                                continue;
                        }
                        catch { /* молча */ }
                    }
                }
                catch { }

                Point p = new((int)pair.Key.X, (int)pair.Key.Y);
                if (mgr.IsTileReserved(p)) continue;

                double dist = Vector2.Distance(this.Position, DroneManager.TileCenter(p));
                if (dist < bestDist) { bestDist = dist; best = p; bestKind = WorkKind.HarvestCrop; }
            }

            // --- 2) дары природы на земле (IsSpawnedObject) ---
            foreach (var pair in farm.objects.Pairs)
            {
                if (pair.Value is not SObject o || !o.IsSpawnedObject) continue;
                Point p = new((int)pair.Key.X, (int)pair.Key.Y);
                if (mgr.IsTileReserved(p)) continue;

                double dist = Vector2.Distance(this.Position, DroneManager.TileCenter(p));
                if (dist < bestDist) { bestDist = dist; best = p; bestKind = WorkKind.PickupForage; }
            }

            // --- 3) ягодные кусты с ягодами ---
            IEnumerable<Bush> BushStream()
            {
                foreach (var ltf in farm.largeTerrainFeatures)
                    if (ltf is Bush b) yield return b;
                foreach (var tf in farm.terrainFeatures.Pairs)
                    if (tf.Value is Bush b2) yield return b2;
            }

            foreach (var bush in BushStream())
            {
                if (!BushHasBerries(bush, farm))
                    continue;

                // Ключевая строка: берём в работу только кусты с ВАНИЛЬНЫМ дропом
                string dropQid = GetBushDropQid(farm, bush);
                if (string.IsNullOrEmpty(dropQid))
                    continue; // модовый куст => не кандидат, дрон не полетит

                Point p = BushTile(bush);
                if (mgr.IsTileReserved(p)) continue;

                double dist = Vector2.Distance(this.Position, DroneManager.TileCenter(p));
                if (dist < bestDist)
                {
                    bestDist = dist; best = p;
                    bestKind = WorkKind.PickupForage;   // «сбор»
                    _targetBush = bush;                 // запоминаем цель
                }
            }

            if (bestKind != WorkKind.None)
            {
                mgr.ReserveTile(best, this);
                tile = best;
                kind = bestKind;
                return true;
            }

            return false;
        }

        private static bool TryGetExtraDropFromData(CropData data, out ExtraDropInfo info)
        {
            info = default;
            if (data == null) return false;

            try
            {
                var t = data.GetType();

                // Вариант A: одиночные поля (если конкретный мод/сборка их добавляет)
                var pId     = t.GetProperty("ExtraHarvestItemId");
                var pMin    = t.GetProperty("ExtraHarvestMinStack");
                var pMax    = t.GetProperty("ExtraHarvestMaxStack");
                var pChance = t.GetProperty("ExtraHarvestItemChance") ?? t.GetProperty("ExtraHarvestChance");

                if (pId != null)
                {
                    string id = pId.GetValue(data) as string ?? string.Empty;
                    if (!string.IsNullOrEmpty(id))
                    {
                        info.Qid = Qid.Qualify(id);
                        info.Min = Math.Max(1, (int?)pMin?.GetValue(data) ?? 1);
                        info.Max = Math.Max(info.Min, (int?)pMax?.GetValue(data) ?? info.Min);

                        object chObj = pChance?.GetValue(data);
                        double ch = chObj is float f ? f
                                : chObj is double d ? d
                                : chObj is int i ? i
                                : 0.0;
                        info.Chance = Math.Clamp(ch, 0.0, 1.0);
                        return true;
                    }
                }

                // Вариант B: список записей (часто формат наподобие GenericSpawnItemData)
                var pList = t.GetProperty("ExtraHarvestItems")
                        ?? t.GetProperty("AdditionalHarvestItems")
                        ?? t.GetProperty("ExtraItems");
                var listObj = pList?.GetValue(data) as System.Collections.IEnumerable;

                if (listObj != null)
                {
                    foreach (var entry in listObj)
                    {
                        var te = entry.GetType();
                        string id = te.GetProperty("ItemId")?.GetValue(entry) as string ?? "";
                        if (string.IsNullOrEmpty(id)) continue;

                        info.Qid = Qid.Qualify(id);
                        info.Min = Math.Max(1, (int?)te.GetProperty("MinStack")?.GetValue(entry) ?? 1);
                        info.Max = Math.Max(info.Min, (int?)te.GetProperty("MaxStack")?.GetValue(entry) ?? info.Min);

                        object ch = te.GetProperty("Chance")?.GetValue(entry);
                        double chD = ch is float f ? f
                                    : ch is double d ? d
                                    : ch is int i ? i
                                    : 1.0; // по умолчанию 100%
                        info.Chance = Math.Clamp(chD, 0.0, 1.0);
                        return true; // берём первую подходящую запись
                    }
                }
            }
            catch { /* молча */ }

            return false;
        }


        // --- есть ли ягоды на кусте прямо сейчас ---
        private static bool BushHasBerries(Bush b, GameLocation loc)
        {
            try
            {
                if (b is null) return false;

                if (b.modData != null
                    && b.modData.TryGetValue(MD.BushPickedDay, out var s)
                    && int.TryParse(s, out var day)
                    && day == Game1.Date.TotalDays)
                    return false;

                int offs = b.tileSheetOffset?.Value ?? 0;
                return offs > 0;
            }
            catch { return false; }
        }

        private static bool IsStrictVanillaTeaOrBerryBush(Bush b, GameLocation loc)
            => !string.IsNullOrEmpty(GetBushDropQid(loc, b));

        private static Point BushTile(Bush b)
        {
            // В вашей сборке доступен вариант без аргументов.
            Rectangle bb = b.getBoundingBox();
            return new Point(bb.Center.X / Game1.tileSize, bb.Center.Y / Game1.tileSize);
        }

        private static string GetBushDropQid(GameLocation loc, Bush bush)
        {
            try
            {
                if (bush is null) return string.Empty;

                // Строго только базовый vanilla-тип (не наследники модов)
                if (bush.GetType() != typeof(Bush))
                    return string.Empty;

                // Любой чужой ключ в modData => считаем модовым кустом и игнорируем
                var md = bush.modData;
                if (md != null)
                {
                    foreach (var kv in md.Pairs)
                    {
                        if (kv.Key == MD.BushPickedDay) continue; // наш служебный ключ
                        return string.Empty;
                    }
                }

                // Ваниль: чай
                if (bush.size?.Value == Bush.greenTeaBush)
                    return "(O)815"; // Tea Leaves

                // Ваниль: сезонные ягоды (в мире, не на ферме)
                Season s = Game1.GetSeasonForLocation(loc);
                if (s == Season.Spring && Game1.dayOfMonth is >= 15 and <= 18) return "(O)296"; // Salmonberry
                if (s == Season.Fall   && Game1.dayOfMonth is >=  8 and <= 11) return "(O)410"; // Blackberry
            }
            catch { /* молча */ }

            // Всё остальное — модовое/неподходящее: игнор
            return string.Empty;
        }

        private void TryAddBonusHay(string harvestId)
        {
            string id = Qid.Qualify(harvestId);

            // Wheat = (O)262, Amaranth = (O)300, Hay = (O)178
            if (id == "(O)262")
            {
                if (Game1.random.NextDouble() < 0.40)
                {
                    if (ItemRegistry.Create("(O)178", 1) is SObject hay)
                        _cargo.Add(hay); // пойдёт в сундук склада
                }
            }
        }

        private static int ComputeBushHarvestCount(Farmer who, Bush bush, GameLocation loc)
        {
            // Чайный куст всегда даёт 1 лист
            if (bush.size.Value == Bush.greenTeaBush)
                return 1;

            // Ягодные кусты: приблизим ваниль — 1..3 за уровень, с небольшой шансом на +1
            int forLevel = Math.Max(0, (who?.ForagingLevel ?? 0));
            int n = 1;
            if (forLevel >= 4) n++;   // ~ур. 4
            if (forLevel >= 8) n++;   // ~ур. 8

            // маленький шанс на бонус (+1) от скилла/удачи, но не более +1
            double bonusChance = Math.Clamp(forLevel * 0.03 + Game1.player.team.sharedDailyLuck.Value * 0.5, 0.0, 0.5);
            if (Game1.random.NextDouble() < bonusChance)
                n++;

            return Math.Max(1, n);
        }

        private static string ToQid(object? value)
        {
            if (value is null) return string.Empty;
            var s = value.ToString() ?? string.Empty;
            return Qid.Qualify(s);
        }

        private static void MarkBushPickedToday(Bush bush)
        {
            try
            {
                // визуально убрать ягоды
                bush.tileSheetOffset.Value = 0;

                // мгновенно перестроить sourceRect (иначе может остаться старый спрайт до перерисовки)
                var mi = bush.GetType().GetMethod("setUpSourceRect",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                mi?.Invoke(bush, null);
            }
            catch { /* молча */ }

            try
            {
                // пометить «собран сегодня» (1.6: у TerrainFeature есть modData)
                bush.modData[MD.BushPickedDay] = Game1.Date.TotalDays.ToString();
            }
            catch { /* молча */ }
        }

        // === DroneWarehouseMod.Game.Drones.HarvestDrone ===
        protected override void DoWorkAt(Farm farm, DroneManager mgr, Point p, WorkKind kind)
        {
            // Если груз полон — ничего не трогаем (не «съедаем» ресурс)
            if (CargoFull())
                return;

            Vector2 v = new(p.X, p.Y);

            // --- A) Дары природы на земле ---
            if (kind == WorkKind.PickupForage && _targetBush == null)
            {
                if (farm.objects.TryGetValue(v, out var obj) && obj is SObject drop && drop.IsSpawnedObject)
                {
                    var one = (SObject)drop.getOne();
                    one.Stack = drop.Stack;

                    // Ботаник (Foraging 10): всегда иридиевое качество
                    if (Game1.player.professions.Contains(Farmer.botanist))
                        one.Quality = SObject.bestQuality;

                    _cargo.Add(one);

                    Game1.player.gainExperience(Farmer.foragingSkill, 7);
                    farm.objects.Remove(v);

                    // звук только если игрок на ферме
                    Audio.PlayFarmOnly(farm, "harvest");
                }
                return;
            }

            // --- B) Сбор с ягодного/чайного куста ---
            if (kind == WorkKind.PickupForage && _targetBush != null)
            {
                var bush = _targetBush;
                _targetBush = null;

                // Новое: если внезапно оказался не-ванильный — выходим
                if (!IsStrictVanillaTeaOrBerryBush(bush, farm))
                    return;

                if (!BushHasBerries(bush, farm))
                    return;

                string dropQid = GetBushDropQid(farm, bush);
                if (string.IsNullOrEmpty(dropQid))
                    return;

                // Сколько дать (ванильная логика), без спавна debris
                int qty = ComputeBushHarvestCount(Game1.player, bush, farm);
                if (qty <= 0) qty = 1;

                int given = 0;
                for (int i = 0; i < qty && !CargoFull(); i++)
                {
                    if (ItemRegistry.Create(dropQid, 1) is SObject so)
                    {
                        // Ботаник (Foraging 10): всегда иридиевая
                        if (Game1.player.professions.Contains(Farmer.botanist))
                            so.Quality = SObject.bestQuality;

                        _cargo.Add(so);
                        given++;
                    }
                }

                // Помечаем куст «собран сегодня» и звуки/XP — только если реально взяли
                if (given > 0)
                {
                    MarkBushPickedToday(bush);
                    Audio.PlayFarmOnly(farm, "harvest");
                    Game1.player.gainExperience(Farmer.foragingSkill, 7);
                }
                return;
            }

            // --- C) Урожай с грядки ---
            if (kind != WorkKind.HarvestCrop)
                return;

            if (!farm.terrainFeatures.TryGetValue(v, out var tf) || tf is not HoeDirt hd || hd.crop is null)
                return;

            var crop = hd.crop;

            try
            {
                string harvestQid = Qid.Qualify(crop.indexOfHarvest.Value?.ToString() ?? "");

                // 1.6: прямой доступ к CropData
                CropData? data = null;
                try { data = crop.GetData(); } catch { /* fallback */ }

                // Базовые поля с безопасными дефолтами
                int min = 1;
                int max = 1;
                int regrowDays = -1;
                float extraChance = 0f;

                if (data != null)
                {
                    min = Math.Max(1, data.HarvestMinStack);
                    max = Math.Max(min, data.HarvestMaxStack);
                    // жёстко уводим вычисление в float: MathF + явное (float)
                    extraChance = MathF.Max(0f, (float)data.ExtraHarvestChance);
                    regrowDays = data.RegrowDays;
                }

                // Количество основного урожая
                int qty = Game1.random.Next(min, max + 1);

                // сравниваем double с double, чтобы не было даункаста
                while (Game1.random.NextDouble() < (double)extraChance)
                    qty++;

                bool isForageCrop = crop.forageCrop?.Value ?? false;
                int quality = DroneWarehouseMod.Game.DroneManager.RollCropQuality(hd, Game1.player);

                // ---------- (1) ОСНОВНОЙ УРОЖАЙ ----------
                int addedMain = 0;
                for (int i = 0; i < qty && !CargoFull(); i++)
                {
                    if (ItemRegistry.Create(harvestQid, 1) is SObject so)
                    {
                        so.Quality = quality;
                        _cargo.Add(so);
                        addedMain++;
                    }
                }

                // ---------- (2) ДОПОЛНИТЕЛЬНЫЙ ПРЕДМЕТ ИЗ CropData (если задан) ----------
                int addedExtra = 0;
                if (data != null && TryGetExtraDropFromData(data, out var extra))
                {
                    if (Game1.random.NextDouble() < extra.Chance)
                    {
                        int eQty = Game1.random.Next(extra.Min, extra.Max + 1);
                        for (int i = 0; i < eQty && !CargoFull(); i++)
                        {
                            if (ItemRegistry.Create(extra.Qid, 1) is SObject soExtra)
                            {
                                // Обычно к доп-предмету качество не применяется.
                                _cargo.Add(soExtra);
                                addedExtra++;
                            }
                        }
                    }
                }

                // Бонусное сено для пшеницы — как было.
                TryAddBonusHay(harvestQid);

                // ---------- (3) ФИНАЛ: XP/звук/регроу — только если ЧТО‑ТО реально взяли ----------
                int addedTotal = addedMain + addedExtra;
                if (addedTotal > 0)
                {
                    // XP
                    if (isForageCrop)
                    {
                        Game1.player.gainExperience(Farmer.foragingSkill, 3 * addedTotal);
                    }
                    else
                    {
                        var tmp = (SObject)ItemRegistry.Create(harvestQid, 1);
                        int oldQ = tmp.Quality;
                        tmp.Quality = SObject.lowQuality;
                        int basePrice = tmp.sellToStorePrice();
                        tmp.Quality = oldQ;

                        int xp = DroneWarehouseMod.Game.DroneManager
                            .ComputeFarmingXpFromBasePrice(basePrice) * addedTotal;
                        Game1.player.gainExperience(Farmer.farmingSkill, xp);
                    }

                    Audio.PlayFarmOnly(farm, "harvest");

                    if (regrowDays >= 0)
                    {
                        crop.fullyGrown.Value = true;
                        crop.currentPhase.Value = Math.Max(0, crop.phaseDays.Count - 1);
                        crop.dayOfCurrentPhase.Value = Math.Max(1, regrowDays);
                    }
                    else
                    {
                        hd.crop = null;
                    }
                }
                // иначе — ничего не удаляем с грядки (груз мог забиться)
            }
            catch
            {
                // молча
            }
        }

        private static Item? GetDebrisItem(object debris)
        {
            try
            {
                var t  = debris.GetType();
                var fi = t.GetField("item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var pi = t.GetProperty("item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? t.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                object val = fi?.GetValue(debris) ?? pi?.GetValue(debris);
                if (val is Item it) return it;

                // NetRef<T> (Value)
                if (val != null)
                {
                    var vt = val.GetType();
                    var pv = vt.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var v2 = pv?.GetValue(val);
                    return v2 as Item;
                }
            }
            catch { /* тихо */ }
            return null;
        }
                


        protected override DroneAnimMode WorkAnimMode() =>
            IsLoadedVisual ? DroneAnimMode.WorkLoaded : DroneAnimMode.WorkEmpty;

        protected override int WorkDurationTicks()
        {
            int frames = IsLoadedVisual ? Anim.WorkLoaded.Length : Anim.WorkEmpty.Length;
            return System.Math.Max(1, frames * ANIM_WORK_TPF);
        }
    }
}
