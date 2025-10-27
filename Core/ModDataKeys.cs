namespace DroneWarehouseMod.Core
{
    internal static class ModDataKeys
    {
        public const string Prefix = "Jenya.DroneWarehouseMod/";

        public const string Id           = Prefix + "Id";
        public const string Chest        = Prefix + "Chest";

        public const string Level        = Prefix + "Level";
        public const string CountHarvest = Prefix + "Count.Harvest";
        public const string CountWater   = Prefix + "Count.Water";
        public const string CountPet     = Prefix + "Count.Pet";

        public const string HasFarmer    = Prefix + "HasFarmer";
        public const string FarmerJob    = Prefix + "FarmerJob";

        // День (TotalDays), когда куст был собран
        public const string BushPickedDay = Prefix + "Bush.PickedDay";

        public const string Beacon      = Prefix + "Beacon";
        public const string BeaconSize  = Prefix + "Beacon.Size";
        public const string BeaconOwner = Prefix + "Beacon.Owner";
    }
}
