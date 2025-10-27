using Microsoft.Xna.Framework;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
using DroneWarehouseMod.Game;            // DroneManager
using DroneWarehouseMod.Game.Drones;
using DroneWarehouseMod.Core;
using System.Collections.Generic;
using StardewValley.Objects;
using StardewValley;
using SObject = StardewValley.Object;

namespace DroneWarehouseMod.Game.Drones
{
    // Поливальщик
    internal sealed class WaterDrone : DroneBase
    {
        private int _charges;
        private readonly int _maxCharges;
        private readonly float _speed;
        public override float SpeedPxPerTick => _speed;
        public void RefillToMax() => _charges = _maxCharges;

        public override DroneKind Kind => DroneKind.Water;

        public WaterDrone(Building home, DroneAnimSet anim, int maxCharges, float speed) : base(home, anim)
        {
            _maxCharges = Math.Max(1, maxCharges);
            _charges    = _maxCharges;
            _speed      = Math.Max(0.1f, speed);
        }

        protected override bool TryAcquireWork(Farm farm, DroneManager mgr, out Point tile, out WorkKind kind)
        {
            tile = default; kind = WorkKind.None;

            if (_charges > 0)
            {
                if (mgr.TryPopNearestDry(new Point((int)(Position.X / Game1.tileSize), (int)(Position.Y / Game1.tileSize)), this, out tile))
                {
                    kind = WorkKind.WaterSoil;
                    return true;
                }
                // есть заряд, но нет целей → домой
                return false;
            }

            // нет заряда — ищем воду
            if (mgr.TryFindNearestWaterSource(Position, farm, Home, out var to))
            {
                tile = new Point((int)(to.X / Game1.tileSize), (int)(to.Y / Game1.tileSize));
                kind = WorkKind.Refill;
                return true;
            }

            return false;
        }

        protected override void OnAfterWork(Farm farm, DroneManager mgr)
        {
            State = DroneState.Idle;
        }

        protected override void DoWorkAt(Farm farm, DroneManager mgr, Point tile, WorkKind kind)
        {
            switch (kind)
            {
                case WorkKind.WaterSoil:
                {
                    var v = new Vector2(tile.X, tile.Y);
                    if (farm.terrainFeatures.TryGetValue(v, out var tf) && tf is HoeDirt hd)
                    {
                        bool alreadyWatered = false;
                        try { alreadyWatered = (hd.state?.Value == HoeDirt.watered); } catch { alreadyWatered = false; }

                        if (!alreadyWatered)
                        {
                            try { hd.state.Value = HoeDirt.watered; } catch { hd.state.Value = 1; }
                            mgr.MarkWatered(tile);
                            _charges = Math.Max(0, _charges - 1);
                            Audio.PlayFarmOnly(farm, "wateringCan");
                        }
                        else
                        {
                            mgr.ReleaseWaterReservation(tile);
                        }
                    }
                    else
                    {
                        mgr.ReleaseWaterReservation(tile);
                    }
                    break;
                }

                case WorkKind.Refill:
                    _charges = _maxCharges;
                    Audio.PlayFarmOnly(farm, "Ship");
                    break;
            }
        }

        protected override DroneAnimMode WorkAnimMode() =>
            _workKind == WorkKind.Refill ? DroneAnimMode.WorkLoaded : DroneAnimMode.WorkEmpty;

        protected override int WorkDurationTicks()
        {
            int frames = _workKind == WorkKind.Refill ? Anim.WorkLoaded.Length : Anim.WorkEmpty.Length;
            return Math.Max(1, frames * ANIM_WORK_TPF);
        }

        protected override bool NeedsRefill() => _charges <= 0;

        protected override bool TryGetRefillPoint(Farm farm, DroneManager mgr, out Vector2 dest)
            => mgr.TryFindNearestWaterSource(Position, farm, Home, out dest);

        protected override bool IsLoadedVisual => _charges > 0;
    }
}
