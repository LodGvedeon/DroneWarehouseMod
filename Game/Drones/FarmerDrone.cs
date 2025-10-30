using Microsoft.Xna.Framework;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
using StardewValley.Objects;
using StardewValley;
using SObject = StardewValley.Object;
using DroneWarehouseMod.Core;

namespace DroneWarehouseMod.Game.Drones
{
    // Фермер
    internal sealed class FarmerDrone : DroneBase
    {
        public override DroneKind Kind => DroneKind.Farmer;
        public override int DrawPixelSize => 150;
        private const int TICKS_PER_SECOND = 60;
        private readonly float _speed;
        public override float SpeedPxPerTick => _speed;
        private readonly int _workPlantTicks;
        private readonly int _workClearTicks;

        private readonly List<Point> _tiles = new();
        private int _index = 0;
        private string _seedQid = ""; // "(O)472" и т.п.
        private enum BlockKind { None, SmallJunk, HardBlock }

        public bool HasPendingWork => _index < _tiles.Count;
        public bool HasJob => _tiles.Count > 0;
        private bool _tilledPhaseDone = false;

        public FarmerDrone(Building home, DroneAnimSet anim, float speed, int plantSeconds, int clearSeconds)
            : base(home, anim)
        {
            _speed          = Math.Max(0.1f, speed);
            _workPlantTicks = Math.Max(1, plantSeconds * TICKS_PER_SECOND);
            _workClearTicks = Math.Max(1, clearSeconds * TICKS_PER_SECOND);
        }

        public void SetJob(string seedQid, List<Point> tiles, int startIndex = 0)
        {
            _seedQid = seedQid ?? "";
            _tiles.Clear();
            _tiles.AddRange(tiles);
            _index = Math.Clamp(startIndex, 0, _tiles.Count);
        }

        // Кодирование состояния в modData
        public string EncodeJob()
        {
            if (!HasJob) return string.Empty;
            var sb = new System.Text.StringBuilder();
            sb.Append(_seedQid).Append("|").Append(_index).Append("|");
            for (int i = 0; i < _tiles.Count; i++)
            {
                if (i > 0) sb.Append(";");
                sb.Append(_tiles[i].X).Append(",").Append(_tiles[i].Y);
            }
            return sb.ToString();
        }

        public void DecodeJob(string data)
        {
            _tiles.Clear(); _index = 0; _seedQid = "";
            if (string.IsNullOrEmpty(data)) return;
            var p = data.Split('|');
            if (p.Length < 3) return;
            _seedQid = p[0];
            _index = int.TryParse(p[1], out var t) ? t : 0;
            foreach (var s in p[2].Split(';'))
            {
                var xy = s.Split(',');
                if (xy.Length == 2 && int.TryParse(xy[0], out var x) && int.TryParse(xy[1], out var y))
                    _tiles.Add(new Point(x, y));
            }
            _index = Math.Clamp(_index, 0, _tiles.Count);
        }

        protected override bool TryAcquireWork(Farm farm, DroneManager mgr, out Point tile, out WorkKind kind)
        {
            tile = default; kind = WorkKind.None;
            if (!HasPendingWork) return false;
            tile = _tiles[_index];
            switch (ClassifyBlock(farm, tile))
            {
                case BlockKind.None:
                    kind = WorkKind.TillAndPlant; break;
                case BlockKind.SmallJunk:
                    kind = WorkKind.ClearSmall; break;
                default:
                    kind = WorkKind.Fail; break;
            }
            return true;
        }

        private static BlockKind ClassifyBlock(Farm farm, Point p)
        {
            if (!CanTillHere(farm, p)) return BlockKind.HardBlock;
            if (farm.isWaterTile(p.X, p.Y)) return BlockKind.HardBlock;
            if (farm.getBuildingAt(new Vector2(p.X, p.Y)) != null) return BlockKind.HardBlock;

            var v = new Vector2(p.X, p.Y);

            // крупные препятствия
            try
            {
                if (farm.resourceClumps?.Any(rc => rc.occupiesTile(p.X, p.Y)) == true)
                    return BlockKind.HardBlock;
            }
            catch { }

            // TerrainFeatures
            if (farm.terrainFeatures.TryGetValue(v, out var tf))
            {
                if (tf is HoeDirt h)
                    return (h.crop != null) ? BlockKind.HardBlock : BlockKind.None;

                if (tf is Grass)
                    return BlockKind.SmallJunk;

                return BlockKind.HardBlock;
            }

            // объекты
            if (farm.objects.TryGetValue(v, out var o) && o is SObject obj)
            {
                if (obj.bigCraftable.Value) return BlockKind.HardBlock;

                string nm = (obj.Name ?? "").ToLowerInvariant();
                if (nm == "stone" || nm == "twig" || nm == "stick" || nm.StartsWith("weeds"))
                    return BlockKind.SmallJunk;

                return BlockKind.HardBlock;
            }

            if (!CanTillHere(farm, p))
                return BlockKind.HardBlock;

            return BlockKind.None;
        }

        // Очистка мелкого мусора с лутом в сундук
        private bool ClearSmallJunkIntoChest(Farm farm, DroneManager mgr, Point p)
        {
            var v = new Vector2(p.X, p.Y);
            var chest = mgr.GetChestFor(Home);

            // трава
            if (farm.terrainFeatures.TryGetValue(v, out var tf) && tf is Grass)
            {
                farm.terrainFeatures.Remove(v);
                Audio.PlayFarmOnly(farm, "cut");
                return true;
            }

            // камни/ветки/сорняк (обычные объекты)
            if (farm.objects.TryGetValue(v, out var o) && o is SObject so && !so.bigCraftable.Value)
            {
                var before = farm.debris.ToList();

                // стандартный дроп
                PlantCompat.TryPerformRemoveActionCompat(so, v, farm);

                farm.objects.Remove(v);

                var created = farm.debris.Except(before).ToList();
                int deposited = 0;
                foreach (var d in created)
                {
                    if (d?.item != null)
                    {
                        var leftover = chest.addItem(d.item);
                        d.item = leftover as SObject;
                        if (leftover == null) deposited++;
                    }
                    farm.debris.Remove(d);
                }

                // если debris не появился — даём безопасный фолбэк
                if (deposited == 0)
                {
                    string nm = (so.Name ?? "").ToLowerInvariant();
                    SObject? fallback = nm switch
                    {
                        var s when s.Contains("stone") => (SObject)ItemRegistry.Create("(O)390", 1),
                        var s when s.Contains("twig") || s.Contains("stick") => (SObject)ItemRegistry.Create("(O)388", 1),
                        var s when s.StartsWith("weeds") => (SObject)ItemRegistry.Create("(O)771", Game1.random.Next(1, 3)),
                        _ => null
                    };

                    if (fallback != null)
                    {
                        var leftover = chest.addItem(fallback);
                        if (leftover is SObject rest)
                            Game1.createItemDebris(rest, DroneManager.TileCenter(p), -1, farm);
                        else
                            deposited++;
                    }
                }

                string sfx = ((so.Name ?? "").ToLowerInvariant()) switch
                {
                    var s when s.Contains("stone") => "stoneCrack",
                    var s when s.Contains("twig") || s.Contains("stick") => "axchop",
                    var s when s.StartsWith("weeds") => "cut",
                    _ => "cut"
                };
                Audio.PlayFarmOnly(farm, sfx);
                return true;
            }

            return false;
        }

        private static string Unqualify(string qid)
        {
            if (string.IsNullOrEmpty(qid)) return qid;
            return (qid.Length > 3 && qid[0] == '(' && qid[2] == ')') ? qid.Substring(3) : qid;
        }

        private static bool IsSeasonOkForSeed(string seedQid, GameLocation loc)
            => DataCache.IsSeedSeasonOk(seedQid, loc);

        private static bool ChestHasSeed(Chest chest, string seedQid)
        {
            if (string.IsNullOrEmpty(seedQid)) return false;
            foreach (var it in chest.Items)
                if (it is SObject o && o.QualifiedItemId == seedQid && o.Stack > 0)
                    return true;
            return false;
        }

        private static bool TryConsumeOneSeed(Chest chest, string seedQid)
        {
            if (string.IsNullOrEmpty(seedQid)) return false;
            for (int i = 0; i < chest.Items.Count; i++)
            {
                if (chest.Items[i] is SObject o && o.QualifiedItemId == seedQid && o.Stack > 0)
                {
                    o.Stack -= 1;
                    if (o.Stack <= 0) chest.Items[i] = null;
                    return true;
                }
            }
            return false;
        }

        private static bool IsBlocked(Farm farm, Point p)
        {
            if (farm.isWaterTile(p.X, p.Y)) return true;

            Vector2 v = new(p.X, p.Y);

            if (farm.getBuildingAt(v) != null) return true;
            if (farm.objects.ContainsKey(v)) return true;

            if (farm.terrainFeatures.TryGetValue(v, out var tf))
            {
                if (tf is HoeDirt h) return h.crop != null;
                return true;
            }

            return false;
        }

        protected override void OnWorkProgress(Farm farm, DroneManager mgr)
        {
            if (_workKind == WorkKind.TillAndPlant && IsBlocked(farm, _targetTile))
            {
                _waitTicks = 0;
                _animTick = _workTotalTicks;
                return;
            }
            if (_workKind != WorkKind.TillAndPlant || _tilledPhaseDone || _workTotalTicks <= 0) return;

            // на середине анимации: создаём/смачиваем вспашку
            if (_animTick >= _workTotalTicks / 2)
            {
                if (!IsBlocked(farm, _targetTile) && CanTillHere(farm, _targetTile))
                {
                    var tv = new Vector2(_targetTile.X, _targetTile.Y);
                    if (!farm.terrainFeatures.TryGetValue(tv, out var tf) || tf is not HoeDirt)
                    {
                        var newHd = new HoeDirt();
                        farm.terrainFeatures[tv] = newHd;
                        try { Audio.PlayFarmOnly(farm, "hoeHit"); } catch { }

                        if (IsRainingNow(farm))
                        {
                            try { newHd.state.Value = HoeDirt.watered; } catch { newHd.state.Value = 1; }
                        }
                    }
                    else
                    {
                        if (IsRainingNow(farm) && tf is HoeDirt existingHd)
                        {
                            try { existingHd.state.Value = HoeDirt.watered; } catch { existingHd.state.Value = 1; }
                        }
                    }
                }
                _tilledPhaseDone = true;
            }
        }

        protected override void DoWorkAt(Farm farm, DroneManager mgr, Point tile, WorkKind kind)
        {
            if (kind == WorkKind.ClearSmall)
            {
                // очищаем мусор, индекс не двигаем
                bool ok = ClearSmallJunkIntoChest(farm, mgr, tile);
                if (!ok) _index++;
                return;
            }

            if (kind == WorkKind.Fail) { _index++; return; }
            if (kind != WorkKind.TillAndPlant) return;

            if (IsBlocked(farm, tile)) { _index++; return; }

            var tv = new Vector2(tile.X, tile.Y);

            // гарантируем HoeDirt
            HoeDirt hd;
            if (farm.terrainFeatures.TryGetValue(tv, out var tf))
            {
                if (tf is HoeDirt h) hd = h;
                else { _index++; return; }
            }
            else if (CanTillHere(farm, tile))
            {
                hd = new HoeDirt();
                farm.terrainFeatures[tv] = hd;
            }
            else { _index++; return; }

            // посадка
            bool planted = false;
            try
            {
                var chest = mgr.GetChestFor(Home);

                string seedToUse = _seedQid;
                if (!string.IsNullOrEmpty(seedToUse))
                {
                    if (!ChestHasSeed(chest, seedToUse) || !IsSeasonOkForSeed(seedToUse, farm))
                        seedToUse = "";
                }
                if (string.IsNullOrEmpty(seedToUse))
                    seedToUse = DroneManager.PickFirstSeasonSeedFromChest(chest, farm);

                if (string.IsNullOrEmpty(seedToUse))
                {
                    _index++;
                    return;
                }

                try
                {
                    planted = PlantCompat.TryPlantCompat(hd, seedToUse, farm, tile.X, tile.Y, Game1.player, false);
                }
                catch { planted = false; }

                if (planted) TryConsumeOneSeed(chest, seedToUse);
            }
            catch { planted = false; }

            _index++;
        }

        public int EnqueueTiles(IEnumerable<Point> newTiles)
        {
            int added = 0;
            var planned = new HashSet<Point>(_tiles);
            foreach (var t in newTiles)
            {
                if (planned.Contains(t)) continue;
                _tiles.Add(t);
                planned.Add(t);
                added++;
            }
            return added;
        }

        protected override void OnAfterWork(Farm farm, DroneManager mgr)
        {
            State = DroneState.Idle;
            _tilledPhaseDone = false;
        }

        protected override DroneAnimMode WorkAnimMode() =>
            _workKind == WorkKind.Fail
                ? DroneAnimMode.WorkFarmerFail
                : (_workKind == WorkKind.ClearSmall
                    ? DroneAnimMode.WorkFarmerClear
                    : DroneAnimMode.WorkFarmerPlant);

        protected override int WorkDurationTicks() =>
            (_workKind == WorkKind.ClearSmall) ? _workClearTicks : _workPlantTicks;

        private static bool CanTillHere(Farm farm, Point p)
        {
            try
            {
                string noHoe = farm.doesTileHaveProperty(p.X, p.Y, "NoHoe", "Back");
                if (!string.IsNullOrEmpty(noHoe)) return false;

                string dig = farm.doesTileHaveProperty(p.X, p.Y, "Diggable", "Back");
                if (!string.IsNullOrEmpty(dig))
                    return string.Equals(dig, "T", System.StringComparison.OrdinalIgnoreCase);

                if (farm.terrainFeatures.TryGetValue(new Vector2(p.X, p.Y), out var tf) && tf is HoeDirt)
                    return true;
                return false;
            }
            catch { return false; }
        }

        private static bool IsRainingNow(GameLocation loc)
        {
            try
            {
                var mi = typeof(Game1).GetMethod("IsRainingHere", new[] { typeof(GameLocation) });
                if (mi != null)
                {
                    var res = mi.Invoke(null, new object[] { loc });
                    if (res is bool b) return b;
                }
            }
            catch { }

            return Game1.isRaining;
        }
    }
}
