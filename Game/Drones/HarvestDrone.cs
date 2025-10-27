using Microsoft.Xna.Framework;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
using DroneWarehouseMod.Game;            // DroneManager
using DroneWarehouseMod.Game.Drones;
using System.Collections.Generic;
using StardewValley.Objects;
using StardewValley;
using StardewValley.GameData.Crops;
using SObject = StardewValley.Object;
using DroneWarehouseMod.Core;
using System.Linq;
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

        public HarvestDrone(Building home, DroneAnimSet anim, int capacity, float speed)
            : base(home, anim, capacity)
        {
            _speed = Math.Max(0.1f, speed);
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
                    Item harvestItem = ItemRegistry.Create(hd.crop!.indexOfHarvest.Value);
                    if (harvestItem is SObject so && so.Category == SObject.flowersCategory)
                        continue;
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
                if (!BushHasBerries(bush, farm)) continue;   // есть ли ягоды на этом кусте

                Point p = BushTile(bush);
                if (mgr.IsTileReserved(p)) continue;

                double dist = Vector2.Distance(this.Position, DroneManager.TileCenter(p));
                if (dist < bestDist)
                {
                    bestDist = dist; best = p;
                    bestKind = WorkKind.PickupForage;         // используем ту же анимацию «сбор»
                    _targetBush = bush;                       // запомним цель-куст
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


        // --- есть ли ягоды на кусте прямо сейчас ---
        private static bool BushHasBerries(Bush b, GameLocation loc)
        {
            try
            {
                // уже собран сегодня нами/игроком?
                if (b?.modData != null
                    && b.modData.TryGetValue(MD.BushPickedDay, out var s)
                    && int.TryParse(s, out var day)
                    && day == Game1.Date.TotalDays)
                    return false;

                // чайный куст: последняя неделя сезона; в теплице — тоже; зимой на улице — нет
                if (b.size.Value == Bush.greenTeaBush)
                {
                    bool lastWeek = Game1.dayOfMonth is >= 22 and <= 28;
                    if (!lastWeek) return false;
                    var season = Game1.GetSeasonForLocation(loc);
                    return season != Season.Winter || (loc?.IsGreenhouse == true);
                }

                // сезонные ягоды + на спрайте видны ягоды
                bool window =
                    (Game1.GetSeasonForLocation(loc) == Season.Spring && Game1.dayOfMonth is >= 15 and <= 18) ||
                    (Game1.GetSeasonForLocation(loc) == Season.Fall   && Game1.dayOfMonth is >= 8  and <= 11);

                return window && (b.tileSheetOffset?.Value ?? 0) > 0;
            }
            catch { return false; }
        }

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
                if (bush.size.Value == Bush.greenTeaBush) return "(O)815";   // Tea Leaves
                Season s = Game1.GetSeasonForLocation(loc);
                if (s == Season.Spring && Game1.dayOfMonth is >= 15 and <= 18) return "(O)296"; // Salmonberry
                if (s == Season.Fall   && Game1.dayOfMonth is >= 8  and <= 11) return "(O)410"; // Blackberry
            }
            catch { }
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

                    // ВАЖНО: звук только на ферме (через общий сервис Audio)
                    Audio.PlayFarmOnly(farm, "harvest");
                }
                return;
            }

            // --- B) сбор с куста ---
            if (kind == WorkKind.PickupForage && _targetBush != null)
            {
                var bush = _targetBush;
                _targetBush = null;

                // Куст уже без ягод — выходим
                if (!BushHasBerries(bush, farm))
                    return;

                // Что падает с куста (чай / морошка / ежевика)
                string dropQid = GetBushDropQid(farm, bush);
                if (string.IsNullOrEmpty(dropQid))
                    return;

                // Сколько ягод дать (приближение ванили), БЕЗ спавна debris
                int qty = ComputeBushHarvestCount(Game1.player, bush, farm);
                if (qty <= 0) qty = 1;

                int given = 0;
                for (int i = 0; i < qty && !CargoFull(); i++)
                {
                    if (ItemRegistry.Create(dropQid, 1) is SObject so)
                    {
                        if (Game1.player.professions.Contains(Farmer.botanist))
                            so.Quality = SObject.bestQuality;
                        _cargo.Add(so);
                        given++;
                    }
                }

                // === ВАЖНО: корректно «обнуляем» куст и помечаем «собран сегодня» ===
                MarkBushPickedToday(bush);

                if (given > 0)
                {
                    Audio.PlayFarmOnly(farm, "harvest");               // звук только если игрок на ферме
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

                StardewValley.GameData.Crops.CropData? data = null;
                try
                {
                    var db = Game1.content.Load<Dictionary<string, StardewValley.GameData.Crops.CropData>>("Data/Crops");
                    string target = harvestQid;
                    data = db.Values.FirstOrDefault(cd =>
                        !string.IsNullOrEmpty(cd.HarvestItemId)
                        && string.Equals(ToQid(cd.HarvestItemId), target, System.StringComparison.OrdinalIgnoreCase));
                }
                catch { }

                int min = Math.Max(1, data?.HarvestMinStack ?? 1);
                int max = Math.Max(min, data?.HarvestMaxStack ?? min);
                int qty = Game1.random.Next(min, max + 1);

                float extraChance = (float)System.Math.Max(0.0, (double)(data?.ExtraHarvestChance ?? 0f));
                while (Game1.random.NextDouble() < extraChance)
                    qty++;

                bool isForageCrop = crop.forageCrop?.Value ?? false;

                int quality = DroneManager.RollCropQuality(hd, Game1.player);

                int added = 0;
                for (int i = 0; i < qty && !CargoFull(); i++)
                {
                    if (ItemRegistry.Create(harvestQid, 1) is SObject so)
                    {
                        so.Quality = quality;
                        _cargo.Add(so);
                        added++;
                    }
                }

                TryAddBonusHay(harvestQid);

                // XP
                if (isForageCrop)
                {
                    Game1.player.gainExperience(Farmer.foragingSkill, 3 * System.Math.Max(1, added));
                }
                else
                {
                    var tmp = (SObject)ItemRegistry.Create(harvestQid, 1);
                    int oldQ = tmp.Quality;
                    tmp.Quality = SObject.lowQuality;
                    int basePrice = tmp.sellToStorePrice();
                    tmp.Quality = oldQ;

                    int xp = DroneWarehouseMod.Game.DroneManager
                        .ComputeFarmingXpFromBasePrice(basePrice) * System.Math.Max(1, added);
                    Game1.player.gainExperience(Farmer.farmingSkill, xp);
                }

                if (added > 0)
                    Audio.PlayFarmOnly(farm, "harvest"); // звук только на ферме

                int regrowDays = data?.RegrowDays ?? -1;
                if (regrowDays >= 0)
                {
                    crop.fullyGrown.Value = true;
                    crop.currentPhase.Value = System.Math.Max(0, crop.phaseDays.Count - 1);
                    crop.dayOfCurrentPhase.Value = System.Math.Max(1, regrowDays);
                }
                else
                {
                    hd.crop = null;
                }
            }
            catch
            {
                // молча
            }
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
