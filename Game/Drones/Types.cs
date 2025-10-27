using Microsoft.Xna.Framework.Graphics;

namespace DroneWarehouseMod.Game.Drones
{
    internal enum DroneKind { Harvest, Water, Pet, Farmer }

    internal enum DroneState
    {
        Docked,            // в складе
        Launching,         // вылет
        Idle,              // в воздухе без задачи
        MovingToTarget,    // к цели
        WaitingAtTarget,   // «работает»
        ReturningUnload,   // к разгрузке
        ReturningDock,     // к доку (через очередь)
        Landing            // посадка
    }

    internal enum DroneAnimMode
    {
        Fly,
        Launch,
        Land,
        WorkEmpty,
        WorkLoaded,
        // доп. режимы
        WorkRefill,
        WorkPetSmall,
        WorkPetBig,
        WorkFarmerPlant,
        WorkFarmerFail,
        WorkFarmerClear
    }

    internal enum WorkKind
    {
        None, HarvestCrop, PickupForage, WaterSoil, Refill,
        PetSmall, PetBig,
        TillAndPlant, ClearSmall, Fail
    }

    internal sealed class DroneAnimSet
    {
        public Texture2D[] FlyEmpty = System.Array.Empty<Texture2D>();
        public Texture2D[] FlyLoaded = System.Array.Empty<Texture2D>();
        public Texture2D[] Launch = System.Array.Empty<Texture2D>();
        public Texture2D[] Land = System.Array.Empty<Texture2D>();
        public Texture2D[] WorkEmpty = System.Array.Empty<Texture2D>();
        public Texture2D[] WorkLoaded = System.Array.Empty<Texture2D>();

        // спец. дорожки
        public Texture2D[] WorkPetSmall = System.Array.Empty<Texture2D>();
        public Texture2D[] WorkPetBig = System.Array.Empty<Texture2D>();
        public Texture2D[] Refill = System.Array.Empty<Texture2D>();
        public Texture2D[] FarmerWork = System.Array.Empty<Texture2D>();
        public Texture2D[] FarmerFail = System.Array.Empty<Texture2D>();
        public Texture2D[] FarmerClear = System.Array.Empty<Texture2D>();
    }
}
