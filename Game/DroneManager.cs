using System.Reflection;
using Netcode;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using MD = DroneWarehouseMod.Core.ModDataKeys;
using DroneWarehouseMod.Core;
using DroneWarehouseMod.Game.Drones;
using SObject = StardewValley.Object;

namespace DroneWarehouseMod.Game
{
    internal sealed class DroneManager
    {
        private ITranslationHelper I18n => _helper.Translation;
        private readonly IMonitor _mon;
        private readonly IModHelper _helper;
        internal readonly ModConfig _cfg;

        public bool WarehouseLidOpen { get; private set; }

        // Анимации/иконки
        private readonly DroneAnimSet _harvestAnim;
        private readonly DroneAnimSet _waterAnim;
        private readonly DroneAnimSet _petAnim;
        private readonly DroneAnimSet _farmerAnim;
        private readonly Texture2D _fallbackFrame;

        // Резервации
        private readonly Dictionary<Point, DroneBase> _waterReserved = new();
        private readonly Dictionary<object, DroneBase> _petReserved = new();
        private readonly Dictionary<Point, DroneBase> _reserved = new();

        // Отрисовка
        private static bool  DRAW_UNDER_TREES = true;
        private static bool  FRONT_OF_WAREHOUSE_NEAR_HATCH = true;
        private static float NEAR_HATCH_RADIUS = 140f;
        private readonly Dictionary<DroneBase, TemporaryAnimatedSprite> _proxies = new();

        // Люк/тикер
        private readonly Dictionary<string, int>  _hatchFreeUntil = new(); // guid → tick
        private readonly Dictionary<string, bool> _hatchOpen      = new(); // guid → открыт
        private int _tick = 0;
        private const int HatchSlotTicks = 60; // ~1 c
        private static float HATCH_X_OFFSET = -30f;
        private static float HATCH_Y_OFFSET = -60f;

        // Очередь посадки
        private readonly Dictionary<string, List<DroneBase>> _landingQueue = new();
        private static readonly Vector2[] LANDING_QUEUE_OFFSETS =
        {
            new Vector2(-44, -12), new Vector2(-60, 6), new Vector2(-44, 22), new Vector2(-28, 8),
        };

        // Состав
        private readonly List<DroneBase> _drones = new();
        private readonly Dictionary<string, Chest> _chests = new(); // guid → сундук

        // Кэш задач
        private readonly HashSet<Point> _dryHoed = new();
        private readonly HashSet<object> _petDoneToday = new();
        private readonly HashSet<object> _bushDoneToday = new();

        public bool SkipFlowerCrops => _cfg?.HarvesterSkipFlowerCrops ?? true;

        public bool IsPetDone(object e) => e != null && _petDoneToday.Contains(e);
        public void MarkPetDone(object e) { if (e != null) _petDoneToday.Add(e); }

        // Виртуальные маяки
        private readonly Dictionary<string, List<(Point tile, int size)>> _virtualBeacons = new();
        private Building? _selBuilding;
        private int _selSize = 3;
        private static readonly int[] _sizesCycle = new[] { 3, 5, 7 };
        public bool IsSelectionActive => _selBuilding != null;
        public int SelectionSize => _selSize;
        public Building? SelectionBuilding => _selBuilding;

        private const int MAX_FARMERS = 3;

        private readonly Dictionary<string, Vector2> _caProxyTiles = new(); // guid → тайл
        private static readonly Vector2 CA_PROXY_BASE = new(-9000, -9000);

        // Сетка no‑fly
        private readonly HashSet<Point> _noFly = new();
        private static int NOFLY_PAD_TILES = 1;
        private static int LOS_PAD_PX = 12;
        private static readonly Point[] _nbr =
        {
            new( 1, 0), new(-1, 0), new(0,  1), new(0, -1),
            new( 1, 1), new(-1, 1), new(1, -1), new(-1,-1)
        };

        private static bool IsWarehouse(Building b) => b?.buildingType?.Value == "DroneWarehouse";

        public void OnNewDay()
        {
            if (Game1.getFarm() is Farm farm)
            {
                RecallAllDronesHome(farm);
                CleanupRegrowFlags(farm); // <— НОВОЕ

                foreach (var b in farm.buildings)
                if (b?.buildingType?.Value == "DroneWarehouse")
                    EnsureChestAnywhereProxy(farm, b);
            }

            foreach (var d in _drones)
                if (d is WaterDrone w) w.RefillToMax();
            

            _petDoneToday.Clear();
            _bushDoneToday.Clear();
        }

        public bool IsBushDone(object b) => b != null && _bushDoneToday.Contains(b);
        public void MarkBushDone(object b) { if (b != null) _bushDoneToday.Add(b); }

        internal bool TryReservePetTarget(object entity, DroneBase owner)
        {
            if (_petReserved.ContainsKey(entity)) return false;
            _petReserved[entity] = owner;
            return true;
        }
        internal void ReleasePetReservation(object entity) => _petReserved.Remove(entity);

        private void ReleaseAllPetFor(DroneBase d)
        {
            foreach (var k in _petReserved.Where(kv => ReferenceEquals(kv.Value, d)).Select(kv => kv.Key).ToList())
                _petReserved.Remove(k);
        }

        internal List<FarmerDrone> GetFarmers(Building b)
            => _drones.Where(d => d.Home == b && d is FarmerDrone)
                .Cast<FarmerDrone>()
                .OrderBy(fd => fd.Slot)
                .ToList();

        private static int ParseInt(string v) => int.TryParse(v, out var n) ? n : 0;

        private static int CountSeasonSeeds(Chest chest, GameLocation loc)
        {
            int sum = 0;
            try
            {
                var crops = DataCache.Crops;
                Season season = Game1.GetSeasonForLocation(loc);
                foreach (var it in chest.Items)
                {
                    if (it is not SObject o) continue;
                    if (o.Category != SObject.SeedsCategory || o.Stack <= 0) continue;
                    string key = (o.QualifiedItemId.Length > 3 && o.QualifiedItemId[0] == '(' && o.QualifiedItemId[2] == ')')
                        ? o.QualifiedItemId.Substring(3) : o.QualifiedItemId;
                    if (crops != null && crops.TryGetValue(key, out var cd) && cd?.Seasons?.Contains(season) == true)
                        sum += o.Stack;
                }
            }
            catch { }
            return sum;
        }

        public void BeginBeaconSelection(Building b, int size)
        {
            _selBuilding = b;
            _selSize = size;
            string id = EnsureGuid(b);
            if (!_virtualBeacons.ContainsKey(id))
                _virtualBeacons[id] = new List<(Point, int)>();
        }

        public void SetWarehouseLidOpen(bool open) => WarehouseLidOpen = open;

        public void ResetVisualProxies()
        {
            try
            {
                if (Game1.getFarm() is not Farm farm) return;
                foreach (var tas in _proxies.Values)
                    farm.temporarySprites.Remove(tas);
                _proxies.Clear();
            }
            catch { }
        }

        public void CycleSelectionSize()
        {
            if (_selBuilding == null) return;
            int idx = Array.IndexOf(_sizesCycle, _selSize);
            _selSize = _sizesCycle[(idx + 1 + _sizesCycle.Length) % _sizesCycle.Length];
            Game1.currentLocation?.localSound("smallSelect");
        }

        public string CurrentSelectionSizeText() => $"{_selSize}x{_selSize}";
        public void CancelBeaconSelection() => _selBuilding = null;

        public bool TryAddVirtualBeacon(Point center)
        {
            if (_selBuilding == null) return false;
            string id = EnsureGuid(_selBuilding);
            var list = _virtualBeacons[id];
            if (list.Any(t => t.tile == center && t.size == _selSize)) return false;
            list.Add((center, _selSize));
            return true;
        }

        public bool RemoveLastVirtualBeacon()
        {
            if (_selBuilding == null) return false;
            string id = EnsureGuid(_selBuilding);
            if (!_virtualBeacons.TryGetValue(id, out var list) || list.Count == 0) return false;
            list.RemoveAt(list.Count - 1);
            return true;
        }

        public List<(Point tile, int size)> GetVirtualBeaconsSnapshot(Building b)
        {
            string id = EnsureGuid(b);
            return _virtualBeacons.TryGetValue(id, out var list) ? list.ToList() : new List<(Point, int)>();
        }
        public void ClearVirtualBeacons(Building b)
        {
            string id = EnsureGuid(b);
            if (_virtualBeacons.TryGetValue(id, out var list)) list.Clear();
        }

        private static List<Point> BuildTilesFromBeaconsSimple(IEnumerable<(Point tile, int size)> beacons)
        {
            var hs = new HashSet<Point>();
            foreach (var (p, size) in beacons)
            {
                int r = size / 2;
                for (int y = p.Y - r; y <= p.Y + r; y++)
                    for (int x = p.X - r; x <= p.X + r; x++)
                        hs.Add(new Point(x, y));
            }
            return hs.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        }

        private static List<Point> SortTilesHilbert(List<Point> tiles)
        {
            if (tiles == null || tiles.Count == 0) return tiles ?? new List<Point>();
            int minX = tiles.Min(t => t.X), minY = tiles.Min(t => t.Y);
            int maxX = tiles.Max(t => t.X), maxY = tiles.Max(t => t.Y);
            int w = Math.Max(1, maxX - minX + 1);
            int h = Math.Max(1, maxY - minY + 1);
            int n = NextPow2(Math.Max(w, h)); // 2^k
            return tiles.OrderBy(p => HilbertXYToIndex(n, p.X - minX, p.Y - minY)).ToList();
        }
        private static int NextPow2(int v) { int n = 1; while (n < v) n <<= 1; return n; }

        // Классический xy→d (см. вики Hilbert curve): n — степень двойки (сторона), 0<=x,y<n
        private static int HilbertXYToIndex(int n, int x, int y)
        {
            int d = 0;
            for (int s = n / 2; s > 0; s /= 2)
            {
                int rx = ((x & s) != 0) ? 1 : 0;
                int ry = ((y & s) != 0) ? 1 : 0;
                d += s * s * ((3 * rx) ^ ry);
                // rot
                if (ry == 0)
                {
                    if (rx == 1) { x = n - 1 - x; y = n - 1 - y; }
                    // swap
                    int t = x; x = y; y = t;
                }
            }
            return d;
        }

        private static List<List<Point>> ClusterTilesKMeans(List<Point> tiles, int k, int iters = 5)
        {
            k = Math.Clamp(k, 1, Math.Min(3, Math.Max(1, tiles.Count)));
            var centers = new List<Vector2>();

            // farthest-first инициализация
            centers.Add(new Vector2(tiles[0].X, tiles[0].Y));
            for (int c = 1; c < k; c++)
            {
                Point best = tiles[0]; double bestMin = -1;
                foreach (var t in tiles)
                {
                    double min = centers.Min(ct => Vector2.Distance(ct, new Vector2(t.X, t.Y)));
                    if (min > bestMin) { bestMin = min; best = t; }
                }
                centers.Add(new Vector2(best.X, best.Y));
            }

            var groups = new List<List<Point>>(k);
            for (int i = 0; i < k; i++) groups.Add(new List<Point>());

            for (int it = 0; it < iters; it++)
            {
                foreach (var g in groups) g.Clear();
                foreach (var t in tiles)
                {
                    int bestI = 0; double bestD = double.MaxValue;
                    for (int i = 0; i < centers.Count; i++)
                    {
                        double d = Vector2.Distance(centers[i], new Vector2(t.X, t.Y));
                        if (d < bestD) { bestD = d; bestI = i; }
                    }
                    groups[bestI].Add(t);
                }
                for (int i = 0; i < centers.Count; i++)
                {
                    if (groups[i].Count == 0) continue;
                    float cx = (float)groups[i].Average(p => p.X);
                    float cy = (float)groups[i].Average(p => p.Y);
                    centers[i] = new Vector2(cx, cy);
                }
            }

            // убрать пустые
            groups = groups.Where(g => g.Count > 0).ToList();

            // упорядочить внутри каждого кластера
            for (int i = 0; i < groups.Count; i++)
                groups[i] = SortTilesHilbert(groups[i]);

            return groups;
        }

        // Сопоставление кластеров фермерам: жадно по минимальной «стоимости»
        // cost = dist_in_tiles + 4 * pendingCount. Излишки кластеров — тоже жадно.
        private static Dictionary<FarmerDrone, List<Point>> AssignClustersToFarmers(List<FarmerDrone> farmers, List<List<Point>> clusters)
        {
            var result = farmers.ToDictionary(fd => fd, _ => new List<Point>());
            if (clusters.Count == 0) return result;

            var freeF = new HashSet<int>(Enumerable.Range(0, farmers.Count));
            var freeC = new HashSet<int>(Enumerable.Range(0, clusters.Count));

            const float W_LOAD = 4f;

            (double cost, int fi, int ci) BestPair()
            {
                double best = double.MaxValue; int bfi = -1, bci = -1;
                foreach (int fi in freeF)
                foreach (int ci in freeC)
                {
                    var fd = farmers[fi];
                    var cl = clusters[ci];
                    // центр кластера
                    float cx = (float)cl.Average(p => p.X) * Game1.tileSize + Game1.tileSize / 2f;
                    float cy = (float)cl.Average(p => p.Y) * Game1.tileSize + Game1.tileSize / 2f;
                    float distTiles = Vector2.Distance(fd.Position, new Vector2(cx, cy)) / Game1.tileSize;
                    double cost = distTiles + W_LOAD * Math.Max(0, fd.PendingCount);
                    if (cost < best) { best = cost; bfi = fi; bci = ci; }
                }
                return (best, bfi, bci);
            }

            // 1) пока есть свободные фермеры — выдаём по одному кластеру
            while (freeF.Count > 0 && freeC.Count > 0)
            {
                var (_, fi, ci) = BestPair();
                if (fi < 0 || ci < 0) break;
                result[farmers[fi]].AddRange(clusters[ci]);
                freeF.Remove(fi); freeC.Remove(ci);
            }

            // 2) оставшиеся кластеры (если их больше, чем фермеров) — жадно к ближайшему/наименее загруженному
            foreach (int ci in freeC.ToList())
            {
                int bestFi = 0; double best = double.MaxValue;
                for (int fi = 0; fi < farmers.Count; fi++)
                {
                    var fd = farmers[fi];
                    var cl = clusters[ci];
                    float cx = (float)cl.Average(p => p.X) * Game1.tileSize + Game1.tileSize / 2f;
                    float cy = (float)cl.Average(p => p.Y) * Game1.tileSize + Game1.tileSize / 2f;
                    float distTiles = Vector2.Distance(fd.Position, new Vector2(cx, cy)) / Game1.tileSize;
                    // учитываем уже назначенные в result
                    int extra = result[fd].Count;
                    double cost = distTiles + W_LOAD * (fd.PendingCount + extra);
                    if (cost < best) { best = cost; bestFi = fi; }
                }
                result[farmers[bestFi]].AddRange(clusters[ci]);
            }

            return result;
        }

        public void RebuildNoFly(Farm farm)
        {
            _noFly.Clear();
            if (farm == null) return;

            foreach (var b in farm.buildings)
            {
                if (IsWarehouse(b)) continue;

                int pad = NOFLY_PAD_TILES;
                int x0 = b.tileX.Value - pad;
                int y0 = b.tileY.Value - pad;
                int x1 = b.tileX.Value + b.tilesWide.Value + pad - 1;
                int y1 = b.tileY.Value + b.tilesHigh.Value + pad - 1;

                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        if (farm.isTileOnMap(x, y))
                            _noFly.Add(new Point(x, y));
            }

            foreach (var b in farm.buildings)
            {
                if (b?.buildingType?.Value != "DroneWarehouse") continue;
                var front = HatchFrontStandPoint(b);
                var t = new Point((int)(front.X / Game1.tileSize), (int)(front.Y / Game1.tileSize));
                _noFly.Remove(t);
            }
        }

        private bool HasLineOfSight(Farm farm, Vector2 a, Vector2 b, Building? except)
        {
            foreach (var bu in farm.buildings)
            {
                if (IsWarehouse(bu)) continue;
                if (except != null && ReferenceEquals(bu, except)) continue;

                var r = BuildingRectPx(bu);
                r.Inflate(LOS_PAD_PX, LOS_PAD_PX);
                if (SegmentIntersectsRect(r, a, b))
                    return false;
            }
            return true;
        }

        private bool IsTileBlocked(Farm farm, Point t, Building? except)
        {
            if (!farm.isTileOnMap(t.X, t.Y))
                return true;

            if (_noFly.Contains(t))
            {
                if (except != null)
                {
                    int pad = NOFLY_PAD_TILES;
                    if (t.X >= except.tileX.Value - pad && t.X < except.tileX.Value + except.tilesWide.Value + pad
                    && t.Y >= except.tileY.Value - pad && t.Y < except.tileY.Value + except.tilesHigh.Value + pad)
                        return false;
                }
                return true;
            }
            return false;
        }

        internal bool FindPath(Farm farm, Vector2 startPx, Vector2 destPx, Building? except, out List<Vector2> waypoints)
        {
            waypoints = new List<Vector2>();
            if (farm == null) return false;

            // Прямая
            if (HasLineOfSight(farm, startPx, destPx, except))
            {
                waypoints.Add(destPx);
                return true;
            }

            // A*
            Point start = new((int)(startPx.X / Game1.tileSize), (int)(startPx.Y / Game1.tileSize));
            Point goal  = new((int)(destPx.X  / Game1.tileSize), (int)(destPx.Y  / Game1.tileSize));
            var pathTiles = AStar(farm, start, goal, except);
            if (pathTiles.Count == 0) return false;

            // Центры
            var centers = new List<Vector2>(pathTiles.Count);
            foreach (var t in pathTiles) centers.Add(TileCenter(t));

            // Сглаживание
            var sm = new List<Vector2>();
            Vector2 cur = startPx;
            int i = 0;
            while (i < centers.Count)
            {
                Vector2 cand = centers[i];
                if (HasLineOfSight(farm, cur, cand, except))
                {
                    int j = i + 1;
                    Vector2 last = cand;
                    while (j < centers.Count && HasLineOfSight(farm, cur, centers[j], except))
                    {
                        last = centers[j];
                        j++;
                    }
                    sm.Add(last);
                    cur = last;
                    i = j;
                }
                else
                {
                    sm.Add(cand);
                    cur = cand;
                    i++;
                }
            }

            // Хвост
            if (HasLineOfSight(farm, cur, destPx, except))
                sm.Add(destPx);
            else
                sm.Add(centers[^1]);

            waypoints = sm;
            return true;
        }

        private static int HeurOctile(Point a, Point b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);
            return 10 * (dx + dy) + (14 - 20) * Math.Min(dx, dy);
        }

        private List<Point> AStar(Farm farm, Point start, Point goal, Building? except)
        {
            var closed = new HashSet<Point>();
            var open   = new HashSet<Point> { start };
            var came   = new Dictionary<Point, Point>();
            var gScore = new Dictionary<Point, int> { [start] = 0 };
            var fScore = new Dictionary<Point, int> { [start] = HeurOctile(start, goal) };

            while (open.Count > 0)
            {
                Point current = default;
                int bestF = int.MaxValue;
                foreach (var p in open)
                {
                    int f = fScore.TryGetValue(p, out var v) ? v : int.MaxValue;
                    if (f < bestF) { bestF = f; current = p; }
                }

                if (current.Equals(goal))
                    return Reconstruct(came, current);

                open.Remove(current);
                closed.Add(current);

                foreach (var d in _nbr)
                {
                    var nb = new Point(current.X + d.X, current.Y + d.Y);
                    if (!farm.isTileOnMap(nb.X, nb.Y)) continue;

                    // Без «срезания углов»
                    if (d.X != 0 && d.Y != 0)
                    {
                        var n1 = new Point(current.X + d.X, current.Y);
                        var n2 = new Point(current.X, current.Y + d.Y);
                        if (IsTileBlocked(farm, n1, except) || IsTileBlocked(farm, n2, except))
                            continue;
                    }

                    bool blocked = IsTileBlocked(farm, nb, except);
                    if (blocked && !nb.Equals(goal)) continue;
                    if (closed.Contains(nb)) continue;

                    int step = (d.X == 0 || d.Y == 0) ? 10 : 14;
                    int tentative = (gScore.TryGetValue(current, out var g) ? g : int.MaxValue) + step;

                    bool inOpen = open.Contains(nb);
                    if (!inOpen || tentative < (gScore.TryGetValue(nb, out var gOld) ? gOld : int.MaxValue))
                    {
                        came[nb] = current;
                        gScore[nb] = tentative;
                        fScore[nb] = tentative + HeurOctile(nb, goal);
                        if (!inOpen) open.Add(nb);
                    }
                }
            }
            return new List<Point>();
        }

        private static List<Point> Reconstruct(Dictionary<Point, Point> came, Point cur)
        {
            var list = new List<Point> { cur };
            while (came.TryGetValue(cur, out var prev))
            {
                cur = prev;
                list.Add(cur);
            }
            list.Reverse();
            return list;
        }

        public void RecallAllDronesHome(Farm farm)
        {
            _hatchOpen.Clear();
            _hatchFreeUntil.Clear();

            foreach (var d in _drones.ToList())
            {
                ReleaseAllFor(d);
                ReleaseAllWaterFor(d);
                ReleaseAllPetFor(d);

                d.Position = HatchCenter(d.Home);
                d.State = DroneState.Docked;

                RemoveFromLandingQueue(d);
            }

            WarehouseLidOpen = false;
            _helper.GameContent.InvalidateCache(Keys.Asset_BuildingTexture);
            ForceReloadWarehouseTextures(farm);
        }

        private static Rectangle BuildingRectPx(Building b) =>
            new Rectangle(
                b.tileX.Value * Game1.tileSize,
                b.tileY.Value * Game1.tileSize,
                b.tilesWide.Value * Game1.tileSize,
                b.tilesHigh.Value * Game1.tileSize
            );

        private static Vector2 ClosestPoint(Rectangle r, Vector2 p) =>
            new(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));

        private static bool SegmentIntersectsRect(Rectangle r, Vector2 a, Vector2 b)
        {
            if (r.Contains((int)a.X, (int)a.Y) || r.Contains((int)b.X, (int)b.Y)) return true;

            bool Intersects(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
            {
                float o1 = MathF.Sign((p2.X - p1.X) * (q1.Y - p1.Y) - (p2.Y - p1.Y) * (q1.X - p1.X));
                float o2 = MathF.Sign((p2.X - p1.X) * (q2.Y - p1.Y) - (p2.Y - p1.Y) * (q2.X - p1.X));
                float o3 = MathF.Sign((q2.X - q1.X) * (p1.Y - q1.Y) - (q2.Y - q1.Y) * (p1.X - q1.X));
                float o4 = MathF.Sign((q2.X - q1.X) * (p2.Y - q1.Y) - (q2.Y - q1.Y) * (p2.X - q1.X));
                return o1 != o2 && o3 != o4;
            }

            Vector2 a1 = new(r.Left, r.Top), a2 = new(r.Right, r.Top);
            Vector2 b1 = new(r.Right, r.Top), b2 = new(r.Right, r.Bottom);
            Vector2 c1 = new(r.Right, r.Bottom), c2 = new(r.Left, r.Bottom);
            Vector2 d1 = new(r.Left, r.Bottom), d2 = new(r.Left, r.Top);

            return Intersects(a, b, a1, a2) || Intersects(a, b, b1, b2)
                || Intersects(a, b, c1, c2) || Intersects(a, b, d1, d2);
        }

        private static void TrySetNetBool(object obj, string memberName, bool value)
        {
            try
            {
                var t = obj?.GetType();
                if (t == null) return;
                var p = t.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object net = p != null ? p.GetValue(obj) : t.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);
                net?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(net, value);
            }
            catch { /* тихо для 1.5 */ }
        }
        private static void TrySetNetString(object obj, string memberName, string value)
        {
            try
            {
                var t = obj?.GetType();
                if (t == null) return;
                var p = t.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object net = p != null ? p.GetValue(obj) : t.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);
                net?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(net, value);
            }
            catch { /* тихо для 1.5 */ }
        }

        private static void SetChestDisplayName(Chest chest, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            // 1) ванильное 1.6+ поле (NetString)
            TrySetNetString(chest, "playerChestName", name);

            // 2) базовое имя объекта (SObject.name) — на всякий случай
            try { chest.Name = name; } catch { /* тихо */ }

            // 3) ключи, которые понимает Chests Anywhere для пользовательского имени
            try
            {
                chest.modData["Pathoschild.ChestsAnywhere/Name"] = name;
                chest.modData["Pathoschild.ChestsAnywhere/CustomName"] = "1";
            }
            catch { /* тихо */ }
        }

        private static void TrySetNetEnum(object obj, string memberName, string enumName)
        {
            try
            {
                var t = obj?.GetType();
                if (t == null) return;

                // 1) редкий случай: член — прямое enum‑свойство
                var propDirect = t.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (propDirect != null && propDirect.PropertyType.IsEnum)
                {
                    var val = Enum.Parse(propDirect.PropertyType, enumName, ignoreCase: true);
                    propDirect.SetValue(obj, val);
                    return;
                }

                // 2) обычный случай: NetEnum<T> поле/свойство — ставим .Value
                object net = propDirect != null
                    ? propDirect.GetValue(obj)
                    : t.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);

                var valProp = net?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (valProp != null)
                {
                    var enumType = valProp.PropertyType;
                    var val = Enum.Parse(enumType, enumName, ignoreCase: true);
                    valProp.SetValue(net, val);
                }
            }
            catch { /* тихо для 1.5 */ }
        }

        private Vector2 AllocateProxyTile(Farm farm)
        {
            Vector2 t = CA_PROXY_BASE;
            while (farm.objects.ContainsKey(t))
                t.X -= 1;
            return t;
        }

        internal void EnsureChestAnywhereProxy(Farm farm, Building b)
        {
            // создаём только если установлен Chest Anywhere
            if (!_helper.ModRegistry.IsLoaded("Pathoschild.ChestsAnywhere"))
                return;

            string id = EnsureGuid(b);
            Chest chest = GetChestFor(b);

            if (!_caProxyTiles.TryGetValue(id, out var tile))
            {
                tile = AllocateProxyTile(farm);
                _caProxyTiles[id] = tile;
            }

            // очистим возможный старый объект в том же месте
            farm.objects.Remove(tile);

            chest.TileLocation = tile;
            chest.playerChest.Value = true;
            TrySetNetBool(chest, "fridge",   false);
            TrySetNetBool(chest, "movable",  false);
            TrySetNetString(chest, "playerChestName", _helper.Translation.Get("menu.chest.title").ToString());

            string nice = _helper.Translation.Get("menu.chest.title").ToString();
            if (string.IsNullOrWhiteSpace(nice)) nice = "Склад дронов — Сундук";
            SetChestDisplayName(chest, nice);

            // пометим чей это прокси, чтобы безопасно удалять
            chest.modData[MD.CeProxy] = id; // ключ уже был в проекте; если его убрали — верните в ModDataKeys

            // важное: это ТОТ ЖЕ экземпляр Chest, который открывается меню
            farm.objects[tile] = chest;
        }

        internal void RemoveChestAnywhereProxy(Farm farm, Building b)
        {
            string id = EnsureGuid(b);
            if (_caProxyTiles.TryGetValue(id, out var tile))
            {
                if (farm.objects.TryGetValue(tile, out var obj) && obj is Chest c &&
                    c.modData.TryGetValue(MD.CeProxy, out var wid) && wid == id)
                    farm.objects.Remove(tile);
                _caProxyTiles.Remove(id);
            }
        }

        internal void RemoveAllChestAnywhereProxies(Farm farm)
        {
            foreach (var (wid, tile) in _caProxyTiles.ToList())
            {
                if (farm.objects.TryGetValue(tile, out var obj) && obj is Chest c &&
                    c.modData.TryGetValue(MD.CeProxy, out var id) && id == wid)
                    farm.objects.Remove(tile);
            }
            _caProxyTiles.Clear();
        }

        public bool IsNearHatch(Building b, Vector2 pos, float radiusMul = 1f)
        {
            float r = Math.Max(1f, NEAR_HATCH_RADIUS * radiusMul);
            return Vector2.Distance(pos, HatchCenter(b)) < r;
        }

        private void ReprioritizeLandingQueues(Farm farm)
        {
            foreach (var b in farm.buildings)
            {
                if (b?.buildingType?.Value != "DroneWarehouse") continue;

                var q = GetQueue(b);
                if (q.Count <= 1) continue;

                Vector2 hatch = HatchCenter(b);
                q.Sort((d1, d2) =>
                    Vector2.Distance(d1.Position, hatch).CompareTo(
                    Vector2.Distance(d2.Position, hatch)));
            }
        }

        private static bool NearBuildingRect(Building b, Vector2 pos, float padPx)
        {
            var r = BuildingRectPx(b);
            r.Inflate((int)padPx, (int)padPx);
            return r.Contains((int)pos.X, (int)pos.Y);
        }

        private static bool IsInFrontOfBuilding(Building b, Vector2 pos)
        {
            float yBottom = (b.tileY.Value + b.tilesHigh.Value) * Game1.tileSize;
            return pos.Y >= yBottom - 2;
        }

        internal static bool WasPetPettedToday(Pet p)
        {
            try
            {
                int today = Game1.Date.TotalDays;
                object lastObj = p.lastPetDay;

                if (lastObj is NetInt ni)
                    return ni.Value == today;

                var t = lastObj?.GetType();
                if (t != null && t.Name.Contains("NetLongDictionary"))
                {
                    long id = Game1.player.UniqueMultiplayerID;

                    var indexer = t.GetProperty("Item", new[] { typeof(long) });
                    if (indexer?.CanRead == true)
                    {
                        var val = indexer.GetValue(lastObj, new object[] { id });
                        if (val is NetInt net) return net.Value == today;
                        if (val is int iv) return iv == today;
                    }

                    foreach (var mi in t.GetMethods().Where(m => m.Name == "TryGetValue"))
                    {
                        var ps = mi.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(long) && ps[1].IsOut)
                        {
                            object[] args = new object[] { id, null! };
                            bool ok = (bool)mi.Invoke(lastObj, args)!;
                            if (ok)
                            {
                                var val = args[1];
                                if (val is NetInt net) return net.Value == today;
                                if (val is int iv) return iv == today;
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void CleanupRegrowFlags(Farm farm)
        {
            try
            {
                foreach (var kv in farm.terrainFeatures.Pairs)
                {
                    if (kv.Value is not HoeDirt hd || hd.modData == null) continue;

                    if (hd.crop == null)
                    {
                        // Грядка пустая — стираем наши следы
                        hd.modData.Remove(MD.PlantEpoch);
                        hd.modData.Remove(MD.RegrowXpSig);
                        hd.modData.Remove(MD.RegrowXpGiven);
                        continue;
                    }

                    // Для одноразовых культур блокировка XP не нужна
                    int regrowDays = -1;
                    try { regrowDays = hd.crop.GetData()?.RegrowDays ?? -1; } catch { }
                    if (regrowDays < 0)
                    {
                        hd.modData.Remove(MD.RegrowXpSig);
                        hd.modData.Remove(MD.RegrowXpGiven);
                        // PlantEpoch можно оставить — не мешает
                    }
                }
            }
            catch { /* тихо */ }
        }
        internal static void EnsurePlantEpoch(HoeDirt hd)
        {
            try
            {
                if (hd?.crop == null || hd.modData == null) return;

                bool justPlantedToday =
                    hd.crop.currentPhase.Value == 0 &&
                    hd.crop.dayOfCurrentPhase.Value == 0 &&
                    !hd.crop.fullyGrown.Value;

                if (justPlantedToday)
                {
                    // новая "эпоха" и сброс блокировок XP
                    hd.modData[MD.PlantEpoch] = Guid.NewGuid().ToString("N");
                    hd.modData.Remove(MD.RegrowXpSig);
                    hd.modData.Remove(MD.RegrowXpGiven);
                }
                else if (!hd.modData.ContainsKey(MD.PlantEpoch))
                {
                    // лениво создаём epoch для старых сейвов/уже выросших растений
                    hd.modData[MD.PlantEpoch] = Guid.NewGuid().ToString("N");
                }
            }
            catch { /* тихо */ }
        }

        public void TrimAllFarmerQueues(Farm farm)
        {
            int removed = 0;
            foreach (var fd in _drones.OfType<FarmerDrone>())
                removed += fd.TrimCompleted();
            if (removed > 0)
                PersistFarmerJobs(farm);
        }
        public string PickFirstSeasonSeed(Building b, GameLocation loc)
            => PickFirstSeasonSeedFromChest(GetChestFor(b), loc);

        public bool TryStartFarmerFromBeacons(Building b, Farm farm, out string reason)
        {
            var virt = GetVirtualBeaconsSnapshot(b);
            if (virt.Count == 0) { reason = I18n.Get("farmer.reason.noBeacons"); return false; }

            var tiles = BuildTilesFromBeaconsSimple(virt);
            if (tiles.Count == 0) { reason = I18n.Get("farmer.reason.noTiles"); return false; }

            int totalSeeds = CountSeasonSeeds(GetChestFor(b), farm);
            if (totalSeeds < tiles.Count)
            {
                reason = I18n.Get("farmer.reason.notEnoughSeeds", new { need = tiles.Count, have = totalSeeds });
                return false;
            }

            var farmers = GetFarmers(b);
            if (farmers.Count == 0) { reason = I18n.Get("farmer.reason.noFarmer"); return false; }

            // 0) Подчищаем завершённое, чтобы нагрузка считалась честно
            foreach (var fd in farmers) fd.TrimCompleted();

            // 1) Если есть драматичный перекос нагрузок — отдать всё самому «пустому»
            var loads = farmers.Select(fd => fd.PendingCount).ToArray();
            int minLoad = loads.Min();
            int maxLoad = loads.Max();
            bool hugeImbalance = (minLoad == 0) ? (maxLoad >= 10) : (maxLoad / (float)minLoad >= 10f);
            if (hugeImbalance)
            {
                int idxMin = Array.IndexOf(loads, minLoad);
                farmers[idxMin].EnqueueTiles(tiles);
                PersistFarmerJobs(farm);
                ClearVirtualBeacons(b);
                farm.localSound("stoneCrack");
                reason = I18n.Get("farmer.reason.scheduled.split", new { total = tiles.Count, workers = farmers.Count });
                return true;
            }

            // 2) Кластеризация выбранных тайлов по количеству фермеров
            int k = Math.Clamp(farmers.Count, 1, tiles.Count);
            var clusters = ClusterTilesKMeans(tiles, k);              // см. хелпер ниже (k-means с farthest-first)
            if (clusters.Count == 0) { reason = I18n.Get("farmer.reason.noTiles"); return false; }

            // 3) Сопоставление «кластер → фермер» с учётом расстояния и текущей очереди
            var mapping = AssignClustersToFarmers(farmers, clusters); // см. хелпер ниже

            // 4) Выдать работу
            int totalScheduled = 0;
            foreach (var (fd, chunk) in mapping)
            {
                if (chunk.Count == 0) continue;
                if (fd.HasJob) totalScheduled += fd.EnqueueTiles(chunk);
                else { fd.SetJob("", chunk); totalScheduled += chunk.Count; }
            }

            // 5) Сохранить/очистить
            PersistFarmerJobs(farm);
            ClearVirtualBeacons(b);
            farm.localSound("stoneCrack");
            reason = I18n.Get("farmer.reason.scheduled.split", new { total = tiles.Count, workers = farmers.Count });
            return true;
        }

        internal static string PickFirstSeasonSeedFromChest(Chest chest, GameLocation loc)
        {
            var crops = DataCache.Crops;
            var season = Game1.GetSeasonForLocation(loc);

            foreach (var it in chest.Items)
            {
                if (it is not SObject o) continue;
                if (o.Category != SObject.SeedsCategory || o.Stack <= 0) continue;

                string key = Qid.Unqualify(o.QualifiedItemId);
                if (crops != null && crops.TryGetValue(key, out var cd) && cd?.Seasons?.Count > 0
                    && !cd.Seasons.Contains(season))
                    continue;

                return o.QualifiedItemId;
            }
            return "";
        }

        public int CapacityByLevel(int level)
        {
            try
            {
                var caps = _cfg?.WarehouseCapacityByLevel;
                if (caps is { Length: >= 1 })
                {
                    int idx = Math.Clamp(level - 1, 0, caps.Length - 1);
                    // защита: неотрицательно
                    return Math.Max(0, caps[idx]);
                }
            }
            catch { }
            // запасной вариант как раньше
            return level >= 3 ? 9 : (level == 2 ? 6 : 3);
        }

        internal static void SetPetPettedToday(Pet p)
        {
            try
            {
                int today = Game1.Date.TotalDays;
                object lastObj = p.lastPetDay;

                if (lastObj is NetInt ni) { ni.Value = today; return; }

                var t = lastObj?.GetType();
                if (t != null && t.Name.Contains("NetLongDictionary"))
                {
                    long id = Game1.player.UniqueMultiplayerID;

                    var indexer = t.GetProperty("Item", new[] { typeof(long) });
                    if (indexer?.CanWrite == true)
                    {
                        Type valType = indexer.PropertyType;
                        object value = (valType == typeof(int)) ? (object)today :
                                       (valType == typeof(NetInt)) ? (object)new NetInt(today) :
                                       null!;
                        if (value != null) { indexer.SetValue(lastObj, value, new object[] { id }); return; }
                    }

                    var add = t.GetMethods().FirstOrDefault(m =>
                    {
                        if (m.Name != "Add") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 2 && ps[0].ParameterType == typeof(long);
                    });
                    if (add != null)
                    {
                        Type valType = add.GetParameters()[1].ParameterType;
                        object value = (valType == typeof(int)) ? (object)today :
                                       (valType == typeof(NetInt)) ? (object)new NetInt(today) :
                                       null!;
                        if (value != null) { add.Invoke(lastObj, new object[] { id, value }); return; }
                    }
                }
            }
            catch { }
        }

        private static bool IsAnimalOutdoors(FarmAnimal a, Farm farm) => a?.currentLocation == farm;

        private int CountPetTargets(Farm farm)
        {
            int count = 0;

            foreach (var a in farm.animals.Values)
                if (a != null && IsAnimalOutdoors(a, farm) && !a.wasPet.Value && !_petReserved.ContainsKey(a) && !_petDoneToday.Contains(a))
                    count++;

            foreach (var p in farm.characters.OfType<Pet>())
                if (p != null && !WasPetPettedToday(p) && !_petReserved.ContainsKey(p) && !_petDoneToday.Contains(p))
                    count++;

            return count;
        }

        public void RefreshPetReservations(Farm farm)
        {
            foreach (var key in _petReserved.Keys.ToList())
            {
                if (key is FarmAnimal a)
                {
                    if (a.currentLocation != farm || !IsAnimalOutdoors(a, farm) || a.wasPet.Value)
                        _petReserved.Remove(key);
                }
                else if (key is Pet p)
                {
                    if (p.currentLocation != farm || WasPetPettedToday(p))
                        _petReserved.Remove(key);
                }
                else
                {
                    _petReserved.Remove(key);
                }
            }
        }

        public void CleanupWarehouseOnRemoved(Farm farm, Building b)
        {
            foreach (var d in _drones.Where(d => d.Home == b).ToList())
                ScrapDrone(d);

            string id = EnsureGuid(b);
            if (_chests.TryGetValue(id, out var chest))
            {
                Vector2 drop = TileCenter(new Point(
                    b.tileX.Value + b.tilesWide.Value / 2,
                    b.tileY.Value + b.tilesHigh.Value / 2));

                foreach (var item in chest.Items.Where(i => i != null).ToList())
                {
                    if (item == null) continue;
                    Game1.createItemDebris(item, drop, -1, farm);
                }
                chest.Items.Clear();
                _chests.Remove(id);
            }

            _landingQueue.Remove(id);
            _hatchOpen.Remove(id);
            _hatchFreeUntil.Remove(id);

            foreach (var p in _waterReserved.Where(kv => kv.Value.Home == b).Select(kv => kv.Key).ToList())
                _waterReserved.Remove(p);

            foreach (var key in _petReserved.Where(kv => ReferenceEquals(kv.Value.Home, b)).Select(kv => kv.Key).ToList())
                _petReserved.Remove(key);

            b.modData.Remove(MD.FarmerJob);
            b.modData.Remove(MD.HasFarmer);

            RemoveChestAnywhereProxy(farm, b);

        }

        private readonly int _scanInterval;

        public DroneManager(ModConfig cfg, IMonitor mon, IModHelper helper, DroneAnimSet harvestAnim, DroneAnimSet waterAnim, DroneAnimSet petAnim, DroneAnimSet farmerAnim, Texture2D fallbackFrame)
        {
            _cfg = cfg ?? new ModConfig();
            _mon = mon; _helper = helper;
            _harvestAnim = EnsureNotEmpty(harvestAnim, fallbackFrame);
            _waterAnim   = EnsureNotEmpty(waterAnim,   fallbackFrame);
            _petAnim     = EnsureNotEmpty(petAnim,     fallbackFrame);
            _farmerAnim  = EnsureNotEmpty(farmerAnim,  fallbackFrame);
            _fallbackFrame = fallbackFrame;

            DRAW_UNDER_TREES = _cfg.DrawUnderTrees;
            FRONT_OF_WAREHOUSE_NEAR_HATCH = _cfg.DrawInFrontNearHatch;
            NEAR_HATCH_RADIUS = _cfg.NearHatchRadius;
            HATCH_X_OFFSET = _cfg.HatchXOffset;
            HATCH_Y_OFFSET = _cfg.HatchYOffset;
            _scanInterval = Math.Max(1, _cfg.ScanIntervalTicks);
            NOFLY_PAD_TILES = Math.Max(0, _cfg.NoFlyPadTiles);
            LOS_PAD_PX = Math.Max(0, _cfg.LineOfSightPadPx);
            
            _helper.Events.GameLoop.Saving += OnSaving;
            _helper.Events.GameLoop.Saved  += OnSaved;

            static DroneAnimSet EnsureNotEmpty(DroneAnimSet set, Texture2D fb)
            {
                Texture2D[] NonEmpty(Texture2D[] a) => (a is { Length: > 0 }) ? a : new[] { fb };
                return new DroneAnimSet
                {
                    FlyEmpty   = NonEmpty(set.FlyEmpty),
                    FlyLoaded  = NonEmpty(set.FlyLoaded),
                    Launch     = NonEmpty(set.Launch),
                    Land       = NonEmpty(set.Land),
                    WorkEmpty  = NonEmpty(set.WorkEmpty),
                    WorkLoaded = NonEmpty(set.WorkLoaded),

                    WorkPetSmall = (set.WorkPetSmall?.Length > 0) ? set.WorkPetSmall : NonEmpty(set.WorkEmpty),
                    WorkPetBig   = (set.WorkPetBig?.Length > 0)   ? set.WorkPetBig   : NonEmpty(set.WorkLoaded),
                    Refill       = (set.Refill?.Length > 0)       ? set.Refill       : NonEmpty(set.WorkLoaded),
                    FarmerWork   = (set.FarmerWork?.Length > 0)   ? set.FarmerWork   : NonEmpty(set.WorkEmpty),
                    FarmerFail   = (set.FarmerFail?.Length > 0)   ? set.FarmerFail   : NonEmpty(set.WorkEmpty),
                    FarmerClear  = (set.FarmerClear?.Length > 0)  ? set.FarmerClear  : NonEmpty(set.WorkEmpty),
                };
            }
        }

        // --------- Публичные вызовы ---------

        public void Update(Farm farm, UpdateTickedEventArgs e)
        {
            _tick++;

            // 1) Запуск из люков
            bool shouldScan = (_tick % _scanInterval) == 0;
            int waterWork = _dryHoed.Count;
            int harvestWork = shouldScan ? CountHarvestTargets(farm) : 0;
            int petWork = shouldScan ? CountPetTargets(farm) : 0;
            bool anyFarmerPending = _drones.Any(d => d is FarmerDrone fd && fd.HasPendingWork);

            if (waterWork > 0 || harvestWork > 0 || petWork > 0 || anyFarmerPending)
            {
                foreach (var b in farm.buildings)
                {
                    if (b.buildingType.Value != "DroneWarehouse")
                        continue;

                    bool farmerWork = _drones.Any(d => d.Home == b && d is FarmerDrone fd && fd.HasPendingWork);

                    DroneBase? candidate =
                        _drones.FirstOrDefault(d => d.Home == b && d.IsDocked && d is WaterDrone   && waterWork   > 0)
                    ?? _drones.FirstOrDefault(d => d.Home == b && d.IsDocked && d is HarvestDrone && harvestWork > 0)
                    ?? _drones.FirstOrDefault(d => d.Home == b && d.IsDocked && d is PetDrone     && petWork     > 0)
                    ?? _drones
                            .Where(d => d.Home == b && d.IsDocked && d is FarmerDrone fd && fd.HasPendingWork)
                            .OrderByDescending(d => ((FarmerDrone)d).PendingCount) // опционально: сначала с самой длинной очередью
                            .FirstOrDefault();

                    if (candidate == null)
                        continue;

                    if (candidate is PetDrone && petWork > 0) petWork--;

                    EnsureGuid(b);
                    string id = b.modData[MD.Id];
                    int free = _hatchFreeUntil.TryGetValue(id, out var t) ? t : 0;
                    bool open = _hatchOpen.TryGetValue(id, out var isOpen) && isOpen;

                    if (!open)
                    {
                        if (_tick >= free)
                        {
                            _hatchFreeUntil[id] = _tick + HatchSlotTicks;
                            _hatchOpen[id] = true;
                            Core.Audio.LidOpen(farm);
                        }
                        continue;
                    }

                    if (_tick < free) continue;

                    int launchTicks = LaunchDurationTicks();
                    _hatchFreeUntil[id] = _tick + launchTicks;
                    candidate.BeginLaunching(HatchCenter(b), launchTicks);

                    if (candidate is WaterDrone && waterWork > 0) waterWork--;
                    if (candidate is HarvestDrone && harvestWork > 0) harvestWork--;
                    if (candidate is PetDrone && petWork > 0) petWork--;
                }
            }

            // 2) Апдейт дронов
            foreach (var d in _drones.ToList())
                d.Update(farm, this);

            // 2.1) Приоритет посадки — ближе к люку раньше
            ReprioritizeLandingQueues(farm);

            // 2.2) Чистим TAS у пристыкованных
            if (DRAW_UNDER_TREES)
            {
                foreach (var d in _drones)
                    if (d.State == DroneState.Docked && _proxies.Remove(d, out var tas))
                        farm.temporarySprites.Remove(tas);
            }

            // 3) Закрытие люков и перерисовка текстуры
            foreach (var b in farm.buildings)
            {
                if (b.buildingType.Value != "DroneWarehouse") continue;
                EnsureGuid(b);
                string id = b.modData[MD.Id];

                bool anyAirborne = _drones.Any(d => d.Home == b && d.State != DroneState.Docked);
                if (!anyAirborne && (_hatchOpen.TryGetValue(id, out var open) && open))
                {
                    int free = _hatchFreeUntil.TryGetValue(id, out var t) ? t : 0;
                    if (_tick >= free)
                    {
                        _hatchOpen[id] = false;
                        _hatchFreeUntil[id] = _tick + HatchSlotTicks;
                        Core.Audio.LidClose(farm);
                    }
                }

                bool openWanted =
                    _hatchOpen.Any(kv => kv.Value) ||
                    _drones.Any(d => d.State != DroneState.Docked);

                if (openWanted != WarehouseLidOpen)
                {
                    WarehouseLidOpen = openWanted;
                    _helper.GameContent.InvalidateCache(Keys.Asset_BuildingTexture);
                    ForceReloadWarehouseTextures(farm);
                }
            }
        }

        private void OnSaving(object? sender, SavingEventArgs e)
        {
            try
            {
                if (Game1.getFarm() is not Farm farm)
                    return;

                // 1) Выгрузить весь непустой карго у ВСЕХ карго-дронов (Сборщик / Ороситель, если когда-то будет лут и т.д.)
                foreach (var d in _drones.ToList())
                {
                    if (d is DroneWarehouseMod.Game.Drones.CargoDroneBase cargo && cargo.CargoList.Count > 0)
                    {
                        DepositToChest(cargo, cargo.CargoList);
                        // DepositToChest уже вызывает SaveChestToModData(...) для конкретного здания.
                    }
                }

                // 2) На всякий случай сериализуем все сундуки складов в modData (глобальный снимок).
                PersistChests(farm);

                RemoveAllChestAnywhereProxies(farm);
            }
            catch (Exception ex)
            {
                _mon.Log($"[Saving] unload/persist failed: {ex}", LogLevel.Trace);
            }
        }
        
        private void OnSaved(object? sender, SavedEventArgs e)
        {
            try
            {
                if (Game1.getFarm() is not Farm farm) return;
            }
            catch { }
        }

        public void PersistFarmerJobs(Farm farm)
        {
            foreach (var b in farm.buildings)
            {
                if (b.buildingType.Value != "DroneWarehouse") continue;
                var farmers = GetFarmers(b);
                // чистим все ключи
                b.modData.Remove(MD.FarmerJob);
                b.modData.Remove(MD.FarmerJob0);
                b.modData.Remove(MD.FarmerJob1);
                b.modData.Remove(MD.FarmerJob2);
                for (int slot = 0; slot < farmers.Count; slot++)
                {
                    string key = slot switch { 0 => MD.FarmerJob0, 1 => MD.FarmerJob1, 2 => MD.FarmerJob2, _ => "" };
                    if (!string.IsNullOrEmpty(key) && farmers[slot].HasJob)
                        b.modData[key] = farmers[slot].EncodeJob();
                }
            }
        }

        public Vector2 HatchFrontStandPoint(Building b)
        {
            Vector2 hatch = HatchCenter(b);
            float yBottom = (b.tileY.Value + b.tilesHigh.Value) * Game1.tileSize;
            return new Vector2(hatch.X, yBottom + 2);
        }

        public void Draw(SpriteBatch b)
        {
            if (!DRAW_UNDER_TREES)
            {
                foreach (var d in _drones)
                {
                    if (d.State == DroneState.Docked) continue;
                    Texture2D tex = d.GetCurrentFrameTexture();
                    d.DrawManual(b, tex);
                }
                return;
            }

            if (Game1.currentLocation is not Farm farm) return;

            foreach (var d in _drones)
            {
                if (d.State == DroneState.Docked)
                {
                    if (_proxies.Remove(d, out var oldTas))
                        farm.temporarySprites.Remove(oldTas);
                    continue;
                }

                Texture2D tex = d.GetCurrentFrameTexture();

                int drawW = d.DrawPixelSize;
                float scale = drawW / (float)tex.Width;
                int drawH = (int)Math.Round(drawW * (tex.Height / (float)tex.Width));
                Vector2 topLeft = new Vector2(d.Position.X - drawW / 2f, d.Position.Y - drawH);

                if (!_proxies.TryGetValue(d, out var tas))
                {
                    tas = new TemporaryAnimatedSprite
                    {
                        texture = tex,
                        sourceRect = new Rectangle(0, 0, tex.Width, tex.Height),
                        sourceRectStartingPos = new Vector2(0, 0),
                        interval = 999999f,
                        animationLength = 1,
                        totalNumberOfLoops = 999999,
                        position = topLeft,
                        flicker = false,
                        flipped = false,
                        scale = scale,
                        color = Color.White
                    };

                    _proxies[d] = tas;
                    farm.temporarySprites.Add(tas);
                }

                tas.texture = tex;
                tas.sourceRect = new Rectangle(0, 0, tex.Width, tex.Height);
                tas.sourceRectStartingPos = new Vector2(0, 0);
                tas.position = topLeft;
                tas.scale = scale;

                float depth = Math.Clamp((d.Position.Y + 8) / 10000f, 0.0001f, 0.9999f);

                bool nearHatch = Vector2.Distance(d.Position, HatchCenter(d.Home)) < NEAR_HATCH_RADIUS;

                if (FRONT_OF_WAREHOUSE_NEAR_HATCH &&
                    (d.State == DroneState.Launching || d.State == DroneState.Landing || nearHatch))
                {
                    depth = Math.Max(depth, Math.Min(BuildingFrontLayer(d.Home) + 0.0015f, 0.9999f));
                }

                tas.layerDepth = depth;
            }

            foreach (var kv in _proxies.Where(kv => !_drones.Contains(kv.Key)).ToList())
            {
                farm.temporarySprites.Remove(kv.Value);
                _proxies.Remove(kv.Key);
            }
        }

        public Chest GetChestFor(Building b)
        {
            string id = EnsureGuid(b);
            if (!_chests.TryGetValue(id, out var chest))
            {
                chest = new Chest(true) { playerChest = { Value = true } };

                try
                {
                    // 1.6+: реально переключаем тип сундука на BigChest (70 слотов)
                    TrySetNetEnum(chest, "specialChestType", "BigChest");

                    // подстраховка: довести список до 70, чтобы не было null‑range ошибок
                    if (chest.Items != null)
                        while (chest.Items.Count < 70)
                            chest.Items.Add(null);

                    string nice = _helper.Translation.Get("menu.chest.title").ToString();
                    if (string.IsNullOrWhiteSpace(nice)) nice = "Склад дронов — Сундук";
                    SetChestDisplayName(chest, nice);

                    // на 1.6 запретим переносить (чисто косметика для удалённых контейнеров)
                    TrySetNetBool(chest, "movable", false);
                    TrySetNetBool(chest, "fridge",  false);
                }
                catch { /* тихо */ }

                _chests[id] = chest;

                if (b.modData.TryGetValue(MD.Chest, out var data) && !string.IsNullOrEmpty(data))
                    LoadChestFromString(chest, data);
            }
            return chest;
        }

        public void PersistChests(Farm farm)
        {
            foreach (var b in farm.buildings)
            {
                if (b.buildingType.Value != "DroneWarehouse") continue;
                SaveChestToModData(b, GetChestFor(b));
            }
        }

        private void SaveChestToModData(Building b, Chest chest) =>
            b.modData[MD.Chest] = EncodeItems(chest.Items);

        private void LoadChestFromString(Chest chest, string data)
        {
            chest.Items.Clear();
            foreach (var item in DecodeItems(data))
                chest.Items.Add(item);
        }

        private static string EncodeItems(IList<Item> items)
        {
            if (items == null || items.Count == 0) return string.Empty;
            var parts = new List<string>(items.Count);
            foreach (var it in items)
            {
                if (it is null) continue;
                string id = it.QualifiedItemId;
                int stack = Math.Max(1, it.Stack);
                int q = (it as SObject)?.Quality ?? -1;
                parts.Add($"{id}*{stack}*{q}");
            }
            return string.Join("|", parts);
        }

        private static IEnumerable<Item> DecodeItems(string data)
        {
            if (string.IsNullOrEmpty(data)) yield break;
            foreach (var token in data.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                var f = token.Split('*');
                if (f.Length < 3) continue;

                string id = f[0];
                int n = int.TryParse(f[1], out var st) ? st : 1;
                int qual = int.TryParse(f[2], out var qq) ? qq : -1;

                Item item;
                try { item = ItemRegistry.Create(id, n); }
                catch { continue; }
                if (item is SObject o && qual >= 0) o.Quality = qual;
                yield return item;
            }
        }

        public string EnsureGuid(Building b)
        {
            if (!b.modData.TryGetValue(MD.Id, out var id) || string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                b.modData[MD.Id] = id;
            }
            return id;
        }

        private void ForceReloadWarehouseTextures(Farm farm)
        {
            foreach (var b in farm.buildings)
            {
                if (b.buildingType.Value != "DroneWarehouse") continue;
                b.texture = new Lazy<Texture2D>(() => Game1.content.Load<Texture2D>(Keys.Asset_BuildingTexture));
            }
        }

        public void SyncWithBuildings()
        {
            if (!Context.IsWorldReady || Game1.getFarm() is not Farm farm) return;

            foreach (var b in farm.buildings)
            {
                if (b.buildingType.Value != "DroneWarehouse") continue;

                EnsureGuid(b);
                GetChestFor(b);
                EnsureChestAnywhereProxy(farm, b);

                int wantH = ParseInt(b.modData.TryGetValue(MD.CountHarvest, out var sH) ? sH : "0");
                int wantW = ParseInt(b.modData.TryGetValue(MD.CountWater,  out var sW) ? sW : "0");
                int wantP = ParseInt(b.modData.TryGetValue(MD.CountPet,    out var sP) ? sP : "0");

                var have = _drones.Where(d => d.Home == b).ToList();
                int haveH = have.Count(d => d is HarvestDrone);
                int haveW = have.Count(d => d is WaterDrone);
                int haveP = have.Count(d => d is PetDrone);

                RemoveExcess(b, DroneKind.Water,   haveW - wantW);
                RemoveExcess(b, DroneKind.Harvest, haveH - wantH);
                RemoveExcess(b, DroneKind.Pet,     haveP - wantP);

                for (int i = haveH; i < wantH; i++)
                    _drones.Add(new HarvestDrone(b, _harvestAnim, _cfg.HarvestCapacity, _cfg.HarvestSpeed, _cfg));
                for (int i = haveW; i < wantW; i++)
                    _drones.Add(new WaterDrone(b, _waterAnim, _cfg.WaterMaxCharges, _cfg.WaterSpeed));
                for (int i = haveP; i < wantP; i++)
                    _drones.Add(new PetDrone(b, _petAnim, _cfg.PetMaxCharges, _cfg.PetSpeed));

                void RemoveExcess(Building bb, DroneKind kind, int excess)
                {
                    if (excess <= 0) return;
                    var list = _drones.Where(d => d.Home == bb && d.Kind == kind)
                                      .OrderBy(d => d.State == DroneState.Docked ? 0 : 1).ToList();
                    for (int k = 0; k < excess && k < list.Count; k++)
                        ScrapDrone(list[k]);
                }

                int wantFarmers = ParseInt(
                    b.modData.TryGetValue(MD.FarmerCount, out var sCnt) ? sCnt
                    : (b.modData.TryGetValue(MD.HasFarmer, out var wf) && wf == "1") ? "1" : "0"
                );
                wantFarmers = Math.Clamp(wantFarmers, 0, MAX_FARMERS);

                var haveFarmers = have.Where(d => d is FarmerDrone).Cast<FarmerDrone>().OrderBy(fd => fd.Slot).ToList();

                // удалить лишних (с конца)var fd = _drones.FirstOrDefault(d => d.Home == b && d is 
                for (int i = haveFarmers.Count - 1; i >= wantFarmers; i--)
                    ScrapDrone(haveFarmers[i]);

                // добрать недостающих
                haveFarmers = have.Where(d => d is FarmerDrone).Cast<FarmerDrone>().OrderBy(fd => fd.Slot).ToList();
                for (int slot = haveFarmers.Count; slot < wantFarmers; slot++)
                {
                    var fd = new FarmerDrone(b, _farmerAnim, _cfg.FarmerSpeed, _cfg.FarmerWorkSeconds, _cfg.FarmerClearSeconds, slot);
                    _drones.Add(fd);
                }

                // восстановить очереди для каждого слота
                haveFarmers = GetFarmers(b);
                for (int slot = 0; slot < haveFarmers.Count; slot++)
                {
                    string key = slot switch { 0 => MD.FarmerJob0, 1 => MD.FarmerJob1, 2 => MD.FarmerJob2, _ => "" };
                    if (!string.IsNullOrEmpty(key) && b.modData.TryGetValue(key, out var data) && !string.IsNullOrEmpty(data))
                        haveFarmers[slot].DecodeJob(data);
                    else if (slot == 0 && b.modData.TryGetValue(MD.FarmerJob, out var legacy) && !string.IsNullOrEmpty(legacy))
                       haveFarmers[0].DecodeJob(legacy); // fallback со старого сейва
                }
            }

            _drones.RemoveAll(d => !farm.buildings.Contains(d.Home));

            static int ParseInt(string v) => int.TryParse(v, out var n) ? n : 0;
        }

        private int CountHarvestTargets(Farm farm)
        {
            int count = 0;
            const int CAP = 256;

            // A) Урожай (не цветы)
            foreach (var pair in farm.terrainFeatures.Pairs)
            {
                if (pair.Value is not HoeDirt hd || hd.crop is null) continue;
                if (!hd.readyForHarvest()) continue;

                try
                {
                    bool isForageCrop = hd.crop.forageCrop?.Value ?? false;

                    // пропускаем обычные цветочные грядки только если включён флаг
                    if (_cfg.HarvesterSkipFlowerCrops && !isForageCrop)
                    {
                        try
                        {
                            string qid = DroneWarehouseMod.Core.Qid.Qualify(hd.crop.indexOfHarvest.Value?.ToString() ?? "");
                            if (ItemRegistry.Create(qid, 1) is SObject so && so.Category == SObject.flowersCategory)
                                continue; // пропустить цветок
                        }
                        catch { /* молча */ }
                    }
                }
                catch { }

                Point p = new((int)pair.Key.X, (int)pair.Key.Y);
                if (!_reserved.ContainsKey(p)) count++;
                if (count >= CAP) return count;
            }

            // B) Дары природы
            foreach (var pair in farm.objects.Pairs)
            {
                if (pair.Value is not SObject o || !o.IsSpawnedObject) continue;
                Point p = new((int)pair.Key.X, (int)pair.Key.Y);
                if (!_reserved.ContainsKey(p)) count++;
                if (count >= CAP) return count;
            }

            // C) Ягодные кусты (ваниль)
            foreach (var bush in farm.largeTerrainFeatures.OfType<Bush>())
            {
                if (!BushHasBerries_Manager(bush, farm)) continue;
                if (!IsStrictVanillaTeaOrBerryBush_Manager(bush, farm)) continue;

                Point p = BushTile_Manager(bush);
                if (!_reserved.ContainsKey(p)) count++;
                if (count >= CAP) return count;
            }
            foreach (var tf in farm.terrainFeatures.Pairs)
            {
                if (tf.Value is not Bush bush) continue;
                if (!BushHasBerries_Manager(bush, farm)) continue;
                if (!IsStrictVanillaTeaOrBerryBush_Manager(bush, farm)) continue;

                Point p = BushTile_Manager(bush);
                if (!_reserved.ContainsKey(p)) count++;
                if (count >= CAP) return count;
            }

            // D) Плодовые деревья (1.6+): берём, если на дереве есть ХОТЯ БЫ 1 предмет в списке fruit.
            if (!_cfg.HarvesterSkipFruitTrees)
            {
                foreach (var tf in farm.terrainFeatures.Pairs)
                {
                    if (tf.Value is not FruitTree ft) continue;

                    // в 1.6 плоды лежат в списке ft.fruit; он уже содержит предметы с нужным качеством
                    int n = 0;
                    try { n = ft.fruit?.Count ?? 0; } catch { n = 0; }
                    if (n <= 0) continue;

                    // (опционально можно пропускать «грозовые» дни; по умолчанию собираем и уголь тоже)
                    // if ((ft.struckByLightningCountdown?.Value ?? 0) > 0) continue;

                    Point p = new((int)tf.Key.X, (int)tf.Key.Y);
                    if (!_reserved.ContainsKey(p))
                    {
                        count++;
                        _mon?.Log($"[DroneWarehouse] Fruit tree ready at {tf.Key} (fruit: {n})", LogLevel.Trace);
                    }
                    if (count >= CAP) return count;
                }
            }

            return count;
        }
        private static bool BushHasBerries_Manager(Bush b, GameLocation loc)
        {
            try
            {
                if (b is null) return false;

                // уже собран сегодня?
                if (b.modData != null
                    && b.modData.TryGetValue(MD.BushPickedDay, out var s)
                    && int.TryParse(s, out var day)
                    && day == Game1.Date.TotalDays)
                    return false;

                // авторитетный признак готовности — на спрайте есть «ягоды/листья»
                int offs = b.tileSheetOffset?.Value ?? 0;
                return offs > 0;
            }
            catch { return false; }
        }

        private static bool IsStrictVanillaTeaOrBerryBush_Manager(Bush b, GameLocation loc)
        {
            try
            {
                if (b is null) return false;

                // только точный базовый тип (никаких наследников модов)
                if (b.GetType() != typeof(Bush))
                    return false;

                // любые посторонние ключи в modData → считаем модовым кустом
                var md = b.modData;
                if (md != null)
                {
                    foreach (var kv in md.Pairs)
                    {
                        if (kv.Key == MD.BushPickedDay) continue; // наш служебный ключ
                        return false;
                    }
                }

                // ваниль: чай
                if (b.size?.Value == Bush.greenTeaBush)
                    return true;

                // ваниль: сезонные ягоды
                Season s = Game1.GetSeasonForLocation(loc);
                bool berryWindow =
                    (s == Season.Spring && Game1.dayOfMonth >= 15 && Game1.dayOfMonth <= 18) ||
                    (s == Season.Fall   && Game1.dayOfMonth >=  8 && Game1.dayOfMonth <= 11);

                return berryWindow;
            }
            catch { return false; }
        }

        private static Point BushTile_Manager(Bush b)
        {
            Rectangle bb = b.getBoundingBox();
            return new Point(bb.Center.X / Game1.tileSize, bb.Center.Y / Game1.tileSize);
        }

        public bool IsTileReserved(Point p) => _reserved.ContainsKey(p);
        internal bool ReserveTile(Point p, DroneBase owner)
        {
            if (_reserved.ContainsKey(p)) return false;
            _reserved[p] = owner;
            return true;
        }
        public void ReleaseTarget(Point p) => _reserved.Remove(p);

        private void ReleaseAllFor(DroneBase d)
        {
            foreach (var k in _reserved.Where(kv => ReferenceEquals(kv.Value, d)).Select(kv => kv.Key).ToList())
                _reserved.Remove(k);
        }
        private void ReleaseAllWaterFor(DroneBase d)
        {
            if (Game1.getFarm() is not Farm farm) { _waterReserved.Clear(); return; }
            foreach (var k in _waterReserved.Where(kv => ReferenceEquals(kv.Value, d)).Select(kv => kv.Key).ToList())
            {
                _waterReserved.Remove(k);
                if (farm.terrainFeatures.TryGetValue(new Vector2(k.X, k.Y), out var tf) && tf is HoeDirt hd && !TryIsWatered(hd))
                    _dryHoed.Add(k);
            }
        }

        public void RebuildDryList(Farm farm)
        {
            foreach (var kv in _waterReserved.ToList())
            {
                Point p = kv.Key;
                if (!farm.terrainFeatures.TryGetValue(new Vector2(p.X, p.Y), out var tf) || tf is not HoeDirt hd || TryIsWatered(hd))
                    _waterReserved.Remove(p);
            }

            _dryHoed.Clear();
            foreach (var tf in farm.terrainFeatures.Pairs)
            {
                if (tf.Value is HoeDirt hd)
                {
                    bool watered = TryIsWatered(hd);
                    if (!watered)
                    {
                        Point p = new((int)tf.Key.X, (int)tf.Key.Y);
                        if (!_waterReserved.ContainsKey(p))
                            _dryHoed.Add(p);
                    }
                }
            }
        }

        private static bool TryIsWatered(HoeDirt hd)
        {
            try { return hd.state?.Value == HoeDirt.watered; } catch { return false; }
        }

        internal void MarkWatered(Point p)
        {
            _dryHoed.Remove(p);
            _waterReserved.Remove(p);
        }
        internal void ReleaseWaterReservation(Point p) => _waterReserved.Remove(p);

        internal bool TryPopNearestDry(Point from, DroneBase owner, out Point tile)
        {
            tile = default;
            if (_dryHoed.Count == 0) return false;

            double best = double.MaxValue;
            foreach (var t in _dryHoed)
            {
                double d = Vector2.Distance(TileCenter(from), TileCenter(t));
                if (d < best) { best = d; tile = t; }
            }

            if (best < double.MaxValue)
            {
                _dryHoed.Remove(tile);
                _waterReserved[tile] = owner;
                return true;
            }
            return false;
        }

        internal bool TryFindNearestWaterSource(Vector2 from, Farm farm, Building home, out Vector2 dest)
        {
            dest = default;
            Point pf = new((int)(from.X / Game1.tileSize), (int)(from.Y / Game1.tileSize));
            Point best = pf; double bestDist = double.MaxValue;

            const int R = 14;
            for (int dx = -R; dx <= R; dx++)
                for (int dy = -R; dy <= R; dy++)
                {
                    int x = pf.X + dx, y = pf.Y + dy;
                    if (farm.isWaterTile(x, y))
                    {
                        double d = Vector2.Distance(from, TileCenter(new Point(x, y)));
                        if (d < bestDist) { bestDist = d; best = new Point(x, y); }
                    }
                }
            if (bestDist < double.MaxValue)
            {
                int sx = Math.Sign(best.X - pf.X);
                int sy = Math.Sign(best.Y - pf.Y);
                Point deeper = new(best.X + sx, best.Y + sy);

                if (farm.isWaterTile(deeper.X, deeper.Y))
                    best = deeper;

                dest = TileCenter(best);
                return true;
            }

            var well = farm.buildings.FirstOrDefault(b => b.buildingType.Value == "Well");
            if (well != null)
            {
                dest = TileCenter(new Point(well.tileX.Value + well.tilesWide.Value / 2, well.tileY.Value + well.tilesHigh.Value / 2));
                return true;
            }

            if (_cfg.AllowRefillAtHatchIfNoWater)
            {
                dest = HatchCenter(home);
                return true;
            }
            dest = default;
            return false;
        }

        // Посадка/очередь

        internal int EnqueueLanding(DroneBase d)
        {
            var q = GetQueue(d.Home);
            if (!q.Contains(d)) q.Add(d);
            return q.IndexOf(d);
        }
        internal void RemoveFromLandingQueue(DroneBase d) => GetQueue(d.Home).Remove(d);
        internal bool IsInLandingQueue(DroneBase d) => GetQueue(d.Home).Contains(d);
        internal bool IsFirstInLandingQueue(DroneBase d)
        {
            var q = GetQueue(d.Home);
            return q.Count > 0 && q[0] == d;
        }

        private List<DroneBase> GetQueue(Building b)
        {
            string id = EnsureGuid(b);
            if (!_landingQueue.TryGetValue(id, out var list))
            {
                list = new List<DroneBase>();
                _landingQueue[id] = list;
            }
            return list;
        }

        internal Vector2 GetHoldPoint(Building b, DroneBase d)
        {
            var q = GetQueue(b);
            int idx = q.IndexOf(d);
            if (idx < 0) idx = 0;
            Vector2 basePt = HatchCenter(b);
            Vector2 offset = LANDING_QUEUE_OFFSETS[idx % LANDING_QUEUE_OFFSETS.Length];
            return basePt + offset;
        }

        public static Vector2 TileCenter(Point p) =>
            new Vector2(p.X * Game1.tileSize + Game1.tileSize / 2, p.Y * Game1.tileSize + Game1.tileSize / 2);

        public static Vector2 HatchCenter(Building b)
        {
            Vector2 basePoint = TileCenter(new Point(b.tileX.Value + 1, b.tileY.Value));
            return basePoint + new Vector2(HATCH_X_OFFSET, HATCH_Y_OFFSET);
        }

        public int LaunchDurationTicks() => Math.Max(1, _harvestAnim.Launch.Length * DroneBase.ANIM_LAUNCH_TPF);
        public int LandDurationTicks()   => Math.Max(1, _harvestAnim.Land.Length   * DroneBase.ANIM_LAND_TPF);

        private static float BuildingFrontLayer(Building b)
        {
            float yBottom = (b.tileY.Value + b.tilesHigh.Value) * Game1.tileSize;
            return Math.Clamp((yBottom + 1) / 10000f, 0.0001f, 0.9999f);
        }

        internal bool TryStartLanding(Farm farm, DroneBase d)
        {
            var q = GetQueue(d.Home);
            if (q.Count > 0 && !ReferenceEquals(q[0], d))
            {
                Vector2 hatch = HatchCenter(d.Home);
                float me   = Vector2.Distance(d.Position, hatch);
                float head = Vector2.Distance(q[0].Position, hatch);
                if (me + 8f < head) { q.Remove(d); q.Insert(0, d); }
                else return false;
            }

            string id = EnsureGuid(d.Home);
            int free = _hatchFreeUntil.TryGetValue(id, out var t) ? t : 0;
            if (_tick < free) return false;

            const float LAND_START_RADIUS = 32f;
            if (Vector2.Distance(d.Position, HatchCenter(d.Home)) > LAND_START_RADIUS)
                return false;

            int landTicks = LandDurationTicks();
            _hatchFreeUntil[id] = _tick + landTicks;
            d.BeginLanding(HatchCenter(d.Home), landTicks);

            if (q.Count > 0 && q[0] == d) q.RemoveAt(0);
            return true;
        }

        internal void ScrapDrone(DroneBase d)
        {
            if (d is CargoDroneBase cargo)
                DepositToChest(cargo, cargo.CargoList);

            ReleaseAllFor(d);
            ReleaseAllWaterFor(d);
            ReleaseAllPetFor(d);

            _drones.Remove(d);
            Game1.currentLocation?.localSound("trashcan");
        }

        internal void DepositToChest(CargoDroneBase d, List<Item> cargo)
        {
            if (cargo == null || cargo.Count == 0) return;

            // 1) базовый сундук склада
            Chest baseChest = GetChestFor(d.Home);
            for (int i = 0; i < cargo.Count; i++)
            {
                var item = cargo[i];
                if (item is null) continue;

                Item? left = baseChest.addItem(item);
                cargo[i] = (left is SObject lo && lo.Stack > 0) ? lo : null!;
            }
            SaveChestToModData(d.Home, baseChest);
            cargo.RemoveAll(it => it is null);
        }   


        public void ForceUnloadAllCargoForTests()
        {
            if (Game1.getFarm() is not Farm farm) return;

            foreach (var d in _drones.ToList())
                if (d is CargoDroneBase cargo && cargo.CargoList.Count > 0)
                    DepositToChest(cargo, cargo.CargoList);

            PersistChests(farm);
        }

        // Качество/XP

        private static int GetFertilizerQualityLevel(HoeDirt soil)
        {
            string f = soil?.fertilizer?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(f)) return 0;
            if (!f.StartsWith("(O)") && int.TryParse(f, out _)) f = "(O)" + f;

            return f switch
            {
                "(O)368" => 1,
                "(O)369" => 2,
                "(O)919" => 3,
                _ => 0
            };
        }

        internal static int RollCropQuality(HoeDirt soil, Farmer who)
        {
            if (soil is null) return SObject.lowQuality;

            int lvl = Math.Clamp(who?.FarmingLevel ?? Game1.player.FarmingLevel, 0, 10);
            int fertLvl = GetFertilizerQualityLevel(soil);

            double gold = 0.2 * (lvl / 10.0) + 0.2 * fertLvl * ((lvl + 2) / 12.0) + 0.01;

            if (fertLvl >= 3)
            {
                double iridium = gold / 2.0;
                if (Game1.random.NextDouble() < iridium) return SObject.bestQuality;
                if (Game1.random.NextDouble() < gold)    return SObject.highQuality;
                return SObject.medQuality;
            }
            else
            {
                if (Game1.random.NextDouble() < gold) return SObject.highQuality;

                double silverLocal = Math.Min(0.75, 2.0 * gold);
                if (Game1.random.NextDouble() < silverLocal) return SObject.medQuality;

                return SObject.lowQuality;
            }
        }

        internal static int ComputeFarmingXpFromBasePrice(int basePrice)
        {
            double xp = 16.0 * Math.Log(0.018 * Math.Max(1, basePrice) + 1.0);
            return Math.Max(1, (int)Math.Round(xp));
        }
    }
}
