using System;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley.GameData.Buildings;

namespace DroneWarehouseMod.Core
{
    // Подмена текстуры склада и запись в Data/Buildings
    internal sealed class GameDataPatcher
    {
        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private readonly Func<bool> _isLidOpen;

        public GameDataPatcher(IModHelper helper, IMonitor monitor, Func<bool> isLidOpen)
        {
            _helper = helper;
            _monitor = monitor;
            _isLidOpen = isLidOpen;
        }

        public void Hook() => _helper.Events.Content.AssetRequested += OnAssetRequested;

        private void OnAssetRequested(object? s, AssetRequestedEventArgs e)
        {
            // Текстура склада: открытая/закрытая
            if (e.Name.IsEquivalentTo(Keys.Asset_BuildingTexture))
            {
                e.LoadFrom(
                    () => _helper.ModContent.Load<Texture2D>(
                        _isLidOpen()
                            ? "assets/hub/drone_warehouse_sheet_opened.png"
                            : "assets/hub/drone_warehouse_sheet.png"
                    ),
                    AssetLoadPriority.Exclusive
                );
                return;
            }

            // Регистрация "DroneWarehouse" в Data/Buildings
            if (e.Name.IsEquivalentTo("Data/Buildings"))
            {
                e.Edit(asset =>
                {
                    var dict = asset.AsDictionary<string, BuildingData>().Data;

                    var data = _helper.ModContent.Load<BuildingData>("assets/data/drone_warehouse.json");

                    // локализация
                    var i18n = _helper.Translation;
                    data.Name = i18n.Get("building.name");
                    data.Description = i18n.Get("building.description");

                    dict["DroneWarehouse"] = data;
                }, AssetEditPriority.Late);
            }
        }
    }
}
