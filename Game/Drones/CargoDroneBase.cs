using System.Linq;
using System.Collections.Generic;
using StardewValley;
using StardewValley.Buildings;
using DroneWarehouseMod.Game;

namespace DroneWarehouseMod.Game.Drones
{
    internal abstract class CargoDroneBase : DroneBase
    {
        protected readonly List<Item> _cargo = new();
        public List<Item> CargoList => _cargo; // для выгрузки
        protected readonly int Capacity;

        protected CargoDroneBase(Building home, DroneAnimSet anim, int capacity) : base(home, anim)
        {
            Capacity = System.Math.Max(1, capacity);
        }

        protected int  CargoCount() => _cargo.Sum(i => i is StardewValley.Object o ? o.Stack : 1);
        protected bool CargoFull()  => CargoCount() >= Capacity;

        protected override bool IsLoadedVisual => CargoCount() > 0;

        protected override void OnAfterWork(Farm farm, DroneManager mgr)
        {
            State = CargoFull() ? DroneState.ReturningUnload : DroneState.Idle;
        }

        protected override void OnReachedUnloadPoint(Farm farm, DroneManager mgr)
        {
            mgr.DepositToChest(this, _cargo);
        }

        protected override void OnDocked(Farm farm, DroneManager mgr)
        {
            if (_cargo.Count > 0)
                mgr.DepositToChest(this, _cargo);
        }
    }
}
