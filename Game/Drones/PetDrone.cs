using Microsoft.Xna.Framework;
using StardewValley.Buildings;
using DroneWarehouseMod.Core;
using StardewValley;
using Netcode;
using StardewValley.Characters;

namespace DroneWarehouseMod.Game.Drones
{
    // Гладильщик
    internal sealed class PetDrone : DroneBase
    {
        private int _charges;
        private readonly int _maxCharges;
        private readonly float _speed;
        public override float SpeedPxPerTick => _speed;

        private object? _target;     // FarmAnimal или Pet
        private bool _targetIsBig;   // для выбора анимации

        public override DroneKind Kind => DroneKind.Pet;

        public PetDrone(Building home, DroneAnimSet anim, int maxCharges, float speed) : base(home, anim)
        {
            _maxCharges = Math.Max(1, maxCharges);
            _charges    = _maxCharges;
            _speed      = Math.Max(0.1f, speed);
        }

        private static void BumpFriend(NetInt value, int delta)
        {
            int clamp = Math.Min(1000, Math.Max(0, value.Value + delta));
            value.Value = clamp;
        }

        protected override bool TryAcquireWork(Farm farm, DroneManager mgr, out Point tile, out WorkKind kind)
        {
            tile = default; kind = WorkKind.None;

            if (_charges <= 0)
            {
                if (TryGetRefillPoint(farm, mgr, out var v))
                {
                    tile = new Point((int)(v.X / Game1.tileSize), (int)(v.Y / Game1.tileSize));
                    kind = WorkKind.Refill;
                    return true;
                }
                return false;
            }

            _target = null;
            _targetIsBig = false;
            double best = double.MaxValue;
            Point bestTile = default;
            WorkKind bestKind = WorkKind.None;

            // 1) животные: на улице и не поглажены
            foreach (var a in farm.animals.Values)
            {
                if (a is null) continue;
                if (a.wasPet.Value) continue;
                if (a.currentLocation != farm) continue;
                if (!mgr.TryReservePetTarget(a, this)) continue;

                var t = new Point((int)(a.Position.X / Game1.tileSize), (int)(a.Position.Y / Game1.tileSize));
                double d = Vector2.Distance(this.Position, DroneManager.TileCenter(t));
                if (d < best)
                {
                    if (_target is not null) mgr.ReleasePetReservation(_target);

                    best = d; bestTile = t;
                    _target = a;

                    bool isCoop = a.home?.buildingType?.Value?.IndexOf("Coop", StringComparison.OrdinalIgnoreCase) >= 0;
                    _targetIsBig = !isCoop;
                    bestKind = _targetIsBig ? WorkKind.PetBig : WorkKind.PetSmall;
                }
                else
                {
                    mgr.ReleasePetReservation(a);
                }

                if (a is null || a.wasPet.Value || a.currentLocation != farm || mgr.IsPetDone(a)) continue;
            }

            // 2) питомцы: не поглажены сегодня
            foreach (var p in farm.characters.OfType<Pet>())
            {
                if (p is null || DroneManager.WasPetPettedToday(p) || mgr.IsPetDone(p)) continue;
                if (!mgr.TryReservePetTarget(p, this)) continue;

                var tp = p.Tile;
                var t  = new Point((int)tp.X, (int)tp.Y);
                double d = Vector2.Distance(this.Position, DroneManager.TileCenter(t));
                if (d < best)
                {
                    if (_target is not null) mgr.ReleasePetReservation(_target);
                    best = d; bestTile = t; _target = p;
                    _targetIsBig = false; bestKind = WorkKind.PetSmall;
                }
                else
                {
                    mgr.ReleasePetReservation(p);
                }
            }

            if (_target != null)
            {
                tile = bestTile;
                kind = bestKind;
                return true;
            }

            return false;
        }

        protected override Vector2? GetDynamicMoveTarget(GameLocation loc)
        {
            if (_workKind != WorkKind.PetSmall && _workKind != WorkKind.PetBig)
                return null;

            Point ToTile(Vector2 pos) => new((int)(pos.X / Game1.tileSize), (int)(pos.Y / Game1.tileSize));

            if (_target is FarmAnimal a && a.currentLocation == loc)
                return DroneManager.TileCenter(ToTile(a.Position));

            if (_target is Pet p && p.currentLocation == loc)
            {
                var tp = p.Tile;
                return DroneManager.TileCenter(new Point((int)tp.X, (int)tp.Y));
            }

            return null;
        }

        protected override void DoWorkAt(Farm farm, DroneManager mgr, Point tile, WorkKind kind)
        {
            switch (kind)
            {
                case WorkKind.Refill:
                    _charges = _maxCharges;
                    Audio.PlayFarmOnly(farm, "dwop");
                    break;

                case WorkKind.PetSmall:
                case WorkKind.PetBig:
                    if (_target is FarmAnimal a)
                    {
                        if (a.currentLocation == farm && !a.wasPet.Value)
                        {
                            a.wasPet.Value = true;
                            BumpFriend(a.friendshipTowardFarmer, 10);
                            Audio.PlayFarmOnly(farm, "dwop");
                        }
                        mgr.MarkPetDone(a);
                    }
                    else if (_target is Pet p)
                    {
                        if (p.currentLocation == farm && !DroneManager.WasPetPettedToday(p))
                        {
                            bool done = false;

                            try
                            {
                                var mi = p.GetType().GetMethod("checkAction", new[] { typeof(Farmer), typeof(GameLocation) });
                                if (mi != null)
                                {
                                    object? ret = mi.Invoke(p, new object[] { Game1.player, farm });
                                    done = ret is bool b && b;
                                }
                            }
                            catch { }

                            if (!done)
                            {
                                try
                                {
                                    var miPet = p.GetType().GetMethod("pet", new[] { typeof(Farmer) });
                                    if (miPet != null) { miPet.Invoke(p, new object[] { Game1.player }); done = true; }
                                }
                                catch { }
                            }

                            if (!done) { DroneManager.SetPetPettedToday(p); try { p.doEmote(20); } catch { } Audio.PlayFarmOnly(farm, "dwop"); }
                            mgr.MarkPetDone(p);
                        }
                    }

                    if (_target != null) mgr.ReleasePetReservation(_target);
                    _target = null;
                    _charges = Math.Max(0, _charges - 1);
                    break;
            }
        }

        protected override void OnAfterWork(Farm farm, DroneManager mgr) => State = DroneState.Idle;

        protected override DroneAnimMode WorkAnimMode() =>
            _workKind switch
            {
                WorkKind.Refill => DroneAnimMode.WorkRefill,
                WorkKind.PetBig => DroneAnimMode.WorkPetBig,
                WorkKind.PetSmall => DroneAnimMode.WorkPetSmall,
                _ => DroneAnimMode.WorkEmpty,
            };

        protected override int WorkDurationTicks()
        {
            int frames = _workKind switch
            {
                WorkKind.Refill => Anim.Refill.Length,
                WorkKind.PetBig => Anim.WorkPetBig.Length,
                WorkKind.PetSmall => Anim.WorkPetSmall.Length,
                _ => Anim.WorkEmpty.Length
            };
            return Math.Max(1, frames * ANIM_WORK_TPF);
        }

        protected override bool NeedsRefill() => _charges <= 0;

        protected override bool TryGetRefillPoint(Farm farm, DroneManager mgr, out Vector2 dest)
        {
            // к ближайшему питомцу, иначе — к люку
            dest = default;
            Pet? best = null;
            double bestD = double.MaxValue;

            foreach (var p in farm.characters.OfType<Pet>())
            {
                var tp = p.Tile;
                double d = Vector2.Distance(this.Position, DroneManager.TileCenter(new Point((int)tp.X, (int)tp.Y)));
                if (d < bestD) { bestD = d; best = p; }
            }

            if (best != null)
            {
                var tp = best.Tile;
                dest = DroneManager.TileCenter(new Point((int)tp.X, (int)tp.Y));
                return true;
            }

            dest = DroneManager.HatchCenter(Home);
            return true;
        }

        protected override bool IsLoadedVisual => _charges > 0;
    }
}
