namespace DroneWarehouseMod
{
    // Настройки мода
    internal sealed class ModConfig
    {
        public bool WorkOffFarm { get; set; } = true;

        public bool  DrawUnderTrees       { get; set; } = true;
        public bool  DrawInFrontNearHatch { get; set; } = true;
        public float NearHatchRadius      { get; set; } = 140f;

        public float HatchXOffset { get; set; } = -30f;
        public float HatchYOffset { get; set; } = -60f;

        public int   ScanIntervalTicks { get; set; } = 6;

        public bool AllowRefillAtHatchIfNoWater { get; set; } = true;
        public bool HarvesterSkipFlowerCrops { get; set; } = true;
        public bool HarvesterSkipFruitTrees { get; set; } = false;

        // Pathing
        public int NoFlyPadTiles   { get; set; } = 1;
        public int LineOfSightPadPx { get; set; } = 12;

        // Capacities / charges
        public int[] WarehouseCapacityByLevel { get; set; } = new[] { 3, 6, 9 };
        public int HarvestCapacity { get; set; } = 10;
        public int WaterMaxCharges { get; set; } = 10;
        public int PetMaxCharges   { get; set; } = 10;

        // Speeds (px per tick)
        public float HarvestSpeed  { get; set; } = 2.6f;
        public float WaterSpeed    { get; set; } = 1.3f;
        public float PetSpeed      { get; set; } = 1.3f;
        public float FarmerSpeed   { get; set; } = 2f;

        // Farmer timings (s)
        public int FarmerWorkSeconds  { get; set; } = 2;
        public int FarmerClearSeconds { get; set; } = 1;

        // Audio
        public bool   EnableCustomSfx { get; set; } = true;
        public float  CustomSfxVolume { get; set; } = 1.0f;
        public string LidOpenSfx      { get; set; } = "assets/audio/lid_open.wav";
        public string LidCloseSfx     { get; set; } = "assets/audio/lid_close.wav";
    }
}
