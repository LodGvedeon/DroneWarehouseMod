using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;
using StardewValley.Objects;
using SObject = StardewValley.Object;
using DroneWarehouseMod.Core;
using MD = DroneWarehouseMod.Core.ModDataKeys;
using DroneWarehouseMod.UI;
using DroneWarehouseMod.Game;
using DroneWarehouseMod.Game.Drones;

namespace DroneWarehouseMod
{
    public class ModEntry : Mod
    {
        private ITranslationHelper I18n => this.Helper.Translation;

        // Иконки/текстуры UI
        private Texture2D _harvestIcon = null!;
        private Texture2D _waterIcon = null!;
        private Texture2D _petIcon = null!;
        private Texture2D _farmerIcon = null!;
        private Texture2D? _texFrame, _texButton, _texScreen, _ledGreen, _ledRed;

        private GameDataPatcher? _patcher;

        // Наборы анимаций
        private DroneAnimSet _harvestAnim = new();
        private DroneAnimSet _waterAnim = new();
        private DroneAnimSet _farmerAnim = new();
        private DroneAnimSet _petAnim = new();

        private ModConfig _config = null!;

        // Менеджер / выделение зон
        private DroneManager _manager = null!;
        private Building? _selectionOwner;

        // Отложенная пометка новых «маяков»
        private int _pendingBeaconCount = 0;
        private string? _pendingBeaconOwner = null;
        private int _pendingBeaconSize = 0;

        private int _deferredRebuildTicks = 0;

        public override void Entry(IModHelper helper)
        {
            // Иконки
            _harvestIcon = helper.ModContent.Load<Texture2D>("assets/ui/harvest_drone/harvest_drone_base.png");
            _waterIcon   = helper.ModContent.Load<Texture2D>("assets/ui/water_drone/water_drone_base.png");
            _petIcon     = helper.ModContent.Load<Texture2D>("assets/ui/pet_drone/pet_drone_base.png");

            // Иконка фермера (fallback — сборщик)
            Texture2D? tmpFarmer = null;
            TryLoad(ref tmpFarmer, "assets/ui/farmer_drone/farmer_drone_base.png");
            _farmerIcon = tmpFarmer ?? _harvestIcon;

            _config = helper.ReadConfig<ModConfig>();

            // Анимации: сборщик
            _harvestAnim = new DroneAnimSet
            {
                FlyEmpty  = LoadFramesSeq("assets/ui/harvest_drone/fly/harvest_drone_base{0}.png", 4, _harvestIcon),
                FlyLoaded = LoadFramesSeq("assets/ui/harvest_drone/fly/harvest_drone_base_crops{0}.png", 4, _harvestIcon),
                Launch    = LoadFramesSeq("assets/ui/harvest_drone/start/harvest_drone_start{0}.png", 5, _harvestIcon),
                Land      = LoadFramesSeq("assets/ui/harvest_drone/land/harvest_drone_landing{0}.png", 5, _harvestIcon),
                WorkEmpty = LoadFramesSeq("assets/ui/harvest_drone/harvest/harvest_drone_harvesting_empty{0}.png", 5, _harvestIcon),
                WorkLoaded= LoadFramesSeq("assets/ui/harvest_drone/harvest/harvest_drone_harvesting_full{0}.png", 6, _harvestIcon),
            };

            // Анимации: поливальщик
            _waterAnim = new DroneAnimSet
            {
                FlyEmpty  = LoadFramesSeq("assets/ui/water_drone/fly/water_drone_base_empty{0}.png", 5, _waterIcon),
                FlyLoaded = LoadFramesSeq("assets/ui/water_drone/fly/water_drone_base_full{0}.png", 5, _waterIcon),
                Launch    = LoadFramesSeq("assets/ui/water_drone/start/water_drone_start{0}.png", 5, _waterIcon),
                Land      = LoadFramesSeq("assets/ui/water_drone/land/water_drone_landing{0}.png", 5, _waterIcon),
                WorkEmpty = LoadFramesSeq("assets/ui/water_drone/water/water_drone_watering{0}.png", 5, _waterIcon),
                WorkLoaded= LoadFramesSeq("assets/ui/water_drone/refill/water_drone_refill{0}.png", 8, _waterIcon),
            };

            // Анимации: «гладильщик»
            _petAnim = new DroneAnimSet
            {
                FlyLoaded = LoadFramesSeq("assets/ui/pet_drone/fly/pet_drone_happy{0}.png", 5, _petIcon),
                FlyEmpty  = LoadFramesSeq("assets/ui/pet_drone/fly/pet_drone_sad{0}.png", 5, _petIcon),
                Launch    = LoadFramesSeq("assets/ui/pet_drone/start/pet_drone_start{0}.png", 5, _petIcon),
                Land      = LoadFramesSeq("assets/ui/pet_drone/land/pet_drone_landing{0}.png", 5, _petIcon),
                WorkPetSmall = LoadFramesSeq("assets/ui/pet_drone/pet/pet_drone_pet_small{0}.png", 5, _petIcon),
                WorkPetBig   = LoadFramesSeq("assets/ui/pet_drone/pet/pet_drone_pet_big{0}.png", 5, _petIcon),
                Refill       = LoadFramesSeq("assets/ui/pet_drone/refill/pet_drone_refill{0}.png", 5, _petIcon),
            };

            // Анимации: фермер
            _farmerAnim = new DroneAnimSet
            {
                FlyEmpty  = LoadFramesSeq("assets/ui/farmer_drone/fly/farmer_drone_dry{0}.png", 6, _farmerIcon),
                FlyLoaded = LoadFramesSeq("assets/ui/farmer_drone/fly/farmer_drone_dry{0}.png", 6, _farmerIcon),
                Launch    = LoadFramesSeq("assets/ui/farmer_drone/start/farmer_drone_start{0}.png", 5, _farmerIcon),
                Land      = LoadFramesSeq("assets/ui/farmer_drone/land/farmer_drone_landing{0}.png", 5, _farmerIcon),
                FarmerWork = LoadFramesSeq("assets/ui/farmer_drone/rip_and_tear/farmer_drone_rip_and_tear{0}.png", 17, _farmerIcon),
                FarmerFail = LoadFramesSeq("assets/ui/farmer_drone/fail/farmer_drone_fail{0}.png", 9, _farmerIcon),
                FarmerClear= LoadFramesSeq("assets/ui/farmer_drone/destroy/farmer_drone_destroy{0}.png", 7, _farmerIcon),
            };

            // Консольные текстуры
            TryLoad(ref _texFrame,  "assets/ui/console/console_frame_60.png");
            TryLoad(ref _texButton, "assets/ui/console/console_button_60.png");
            TryLoad(ref _texScreen, "assets/ui/console/console_screen_16.png");
            TryLoad(ref _ledGreen,  "assets/ui/console/led_green_8.png");
            TryLoad(ref _ledRed,    "assets/ui/console/led_red_8.png");

            // События
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.TimeChanged += this.OnTimeChanged;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
            helper.Events.GameLoop.Saving += this.OnSaving;
            helper.Events.World.BuildingListChanged += this.OnBuildingListChanged;
            helper.Events.Player.InventoryChanged += this.OnInventoryChanged;
            helper.Events.World.ObjectListChanged += this.OnObjectListChanged;

            // Локальные загрузчики кадров/текстур
            Texture2D[] LoadFramesSeq(string pattern, int count, Texture2D fallback)
            {
                var list = new List<Texture2D>(count);
                for (int i = 1; i <= count; i++)
                {
                    string path = string.Format(pattern, i);
                    try { list.Add(helper.ModContent.Load<Texture2D>(path)); }
                    catch { Monitor.Log($"[Drone] Не найден кадр '{path}' — пропускаю.", LogLevel.Trace); }
                }
                if (list.Count == 0) list.Add(fallback);
                return list.ToArray();
            }
            void TryLoad(ref Texture2D? slot, string path)
            {
                try { slot = helper.ModContent.Load<Texture2D>(path); }
                catch { slot = null; Monitor.Log($"[UI] Не найден ассет {path} — будет ваниль.", LogLevel.Trace); }
            }

            _patcher = new GameDataPatcher(this.Helper, this.Monitor, () => _manager?.WarehouseLidOpen == true);
            _patcher.Hook();

            Core.Audio.Init(this.Helper, this.Monitor, _config);
            helper.Events.Display.MenuChanged += this.OnMenuChanged;
        }

        // Открыть сундук склада
        private void OpenWarehouseChestMenu(Building b)
        {
            Chest chest = _manager.GetChestFor(b);
            chest.playerChest.Value = true;

            Game1.activeClickableMenu = new ItemGrabMenu(
                inventory: chest.Items,
                reverseGrab: false,
                showReceivingMenu: true,
                highlightFunction: null,
                behaviorOnItemSelectFunction: chest.grabItemFromInventory,
                message: I18n.Get("menu.chest.title"),
                behaviorOnItemGrab: chest.grabItemFromChest,
                snapToBottom: false,
                canBeExitedWithKey: true,
                playRightClickSound: true,
                allowRightClick: true,
                showOrganizeButton: true,
                source: ItemGrabMenu.source_chest,
                sourceItem: chest,
                context: chest
            );
        }

        private static void PlayFarmOnly(GameLocation loc, string cue) => Audio.PlayFarmOnly(loc, cue);

        private void OnMenuChanged(object? s, MenuChangedEventArgs e)
        {
            // если закрыли наше меню — опустим крышку (менеджер сам синхронизирует)
            bool wasOurMenu =
                e.OldMenu is ItemGrabMenu
                || e.OldMenu is DroneConsoleMenu;
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            DataCache.Refresh();
            _manager = new DroneManager(_config, this.Monitor, this.Helper,
                _harvestAnim, _waterAnim, _petAnim, _farmerAnim, _harvestIcon);
            _manager.SyncWithBuildings();
            if (Game1.getFarm() is Farm farm)
            {
                _manager.RebuildDryList(farm);
                _manager.RebuildNoFly(farm);
            }
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            DataCache.Refresh();
            _manager?.SyncWithBuildings();

            _manager?.OnNewDay();
            _manager?.ResetVisualProxies();

            _deferredRebuildTicks = 20;

            _manager?.CancelBeaconSelection();
            _selectionOwner = null;
        }

        private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
        {
            if (!Context.IsWorldReady) return;
            if (Game1.getFarm() is Farm farm)
            {
                _manager?.RebuildDryList(farm);
                if (e.NewTime % 100 == 0)
                    _manager?.RefreshPetReservations(farm);
            }
        }

        private void OnSaving(object? sender, SavingEventArgs e)
        {
            if (Game1.getFarm() is Farm farm)
            {
                _manager?.PersistChests(farm);
                _manager?.PersistFarmerJobs(farm);
            }
        }

        private void OnBuildingListChanged(object? sender, BuildingListChangedEventArgs e)
        {
            if (e.Location is not Farm farm) return;

            foreach (var removed in e.Removed)
                if (removed.buildingType?.Value == "DroneWarehouse")
                    _manager?.CleanupWarehouseOnRemoved(farm, removed);

            _manager?.SyncWithBuildings();
            _manager?.RebuildNoFly(farm);
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            if (_deferredRebuildTicks > 0)
            {
                _deferredRebuildTicks--;
                if (_deferredRebuildTicks == 0 && Game1.getFarm() is Farm f)
                {
                    _manager?.RebuildDryList(f);
                    _manager?.RebuildNoFly(f);
                }
            }

            if (_manager?.IsSelectionActive == true && Game1.currentLocation is not Farm)
            {
                _manager.CancelBeaconSelection();
                _selectionOwner = null;
            }

            if (Game1.activeClickableMenu != null || Game1.paused || Game1.eventUp || !Context.IsPlayerFree) return;
            if (!Game1.game1.IsActive) return;

            if (Game1.currentLocation is Farm farm)
                _manager?.Update(farm, e);

            if (_config.WorkOffFarm && Game1.currentLocation is not Farm)
                _manager?.Update(Game1.getFarm(), e);
        }

        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            if (Game1.currentLocation is Farm)
                _manager?.Draw(e.SpriteBatch);

            // Оверлей выделения зон
            if (_manager?.IsSelectionActive == true && Game1.currentLocation is Farm farm)
            {
                var cursor = this.Helper.Input.GetCursorPosition().Tile;
                var hover = new Point((int)cursor.X, (int)cursor.Y);
                int size = _manager.SelectionSize;

                void FillTile(Point t, Color c)
                {
                    Vector2 topLeft = Game1.GlobalToLocal(Game1.viewport,
                        new Vector2(t.X * Game1.tileSize, t.Y * Game1.tileSize));
                    e.SpriteBatch.Draw(Game1.staminaRect,
                        new Rectangle((int)topLeft.X, (int)topLeft.Y, Game1.tileSize, Game1.tileSize),
                        c);
                }

                int r = size / 2;
                for (int y = hover.Y - r; y <= hover.Y + r; y++)
                    for (int x = hover.X - r; x <= hover.X + r; x++)
                        FillTile(new Point(x, y), new Color(0, 255, 120, 70));

                if (_manager.SelectionBuilding is Building sb)
                {
                    foreach (var (center, s) in _manager.GetVirtualBeaconsSnapshot(sb))
                    {
                        int rr = s / 2;
                        for (int y = center.Y - rr; y <= center.Y + rr; y++)
                            for (int x = center.X - rr; x <= center.X + rr; x++)
                                FillTile(new Point(x, y), new Color(0, 255, 120, 20));
                    }
                }
            }
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            // Глобальный хоткей выбора зон
            if (e.Button == SButton.F6 && Game1.currentLocation is Farm farmF6)
            {
                HandleGlobalHotkeyF6(farmF6, e);
                return;
            }

            // Управление режимом выделения
            if (_manager?.IsSelectionActive == true && Game1.currentLocation is Farm farmSel)
            {
                if (HandleSelectionModeInput(farmSel, e)) return;
            }

            // Клик по зданию: правая половина — консоль, левая — сундук
            if (!e.Button.IsActionButton()) return;
            if (Game1.currentLocation is not Farm farm) return;

            HandleWarehouseInteraction(farm, Game1.player.GetGrabTile(), e);
        }

        private void HandleGlobalHotkeyF6(Farm farmF6, ButtonPressedEventArgs e)
        {
            if (e.Button == SButton.F6 && Game1.currentLocation is Farm _)
            {
                const string KeyHasFarmer = MD.HasFarmer;

                Building? PickWarehouseForF6(Farm farm)
                {
                    var underGrab = farm.getBuildingAt(Game1.player.GetGrabTile());
                    if (underGrab != null && underGrab.buildingType?.Value == "DroneWarehouse")
                        return underGrab;

                    var list = farm.buildings.Where(b => b?.buildingType?.Value == "DroneWarehouse").ToList();
                    if (list.Count == 0) return null;

                    Vector2 me = Game1.player.Tile;

                    float Dist(Building b)
                    {
                        float cx = b.tileX.Value + b.tilesWide.Value / 2f;
                        float cy = b.tileY.Value + b.tilesHigh.Value / 2f;
                        return Vector2.Distance(me, new Vector2(cx, cy));
                    }

                    var withFarmer = list
                        .Where(b => b.modData.TryGetValue(MD.HasFarmer, out var v) && v == "1")
                        .OrderBy(Dist)
                        .FirstOrDefault();

                    return withFarmer ?? list.OrderBy(Dist).First();
                }

                var wh = PickWarehouseForF6(farmF6);
                if (wh == null)
                {
                    farmF6.localSound("cancel");
                    Game1.addHUDMessage(new HUDMessage(I18n.Get("hud.noWarehouse"), HUDMessage.error_type));
                    this.Helper.Input.Suppress(e.Button);
                    return;
                }

                bool hasFarmer = wh.modData.TryGetValue(KeyHasFarmer, out var hf) && hf == "1";
                if (!hasFarmer)
                {
                    farmF6.localSound("cancel");
                    Game1.addHUDMessage(new HUDMessage(I18n.Get("hud.noFarmer"), HUDMessage.error_type));
                    this.Helper.Input.Suppress(e.Button);
                    return;
                }

                if (_manager.IsSelectionActive && _selectionOwner == wh)
                {
                    _manager.CancelBeaconSelection();
                    _selectionOwner = null;
                    Game1.currentLocation?.localSound("cancel");
                }
                else
                {
                    int startSize = _manager.SelectionSize > 0 ? _manager.SelectionSize : 3;
                    _manager.BeginBeaconSelection(wh, startSize);
                    _selectionOwner = wh;

                    farmF6.localSound("smallSelect");
                    Game1.addHUDMessage(new HUDMessage(I18n.Get("hud.plant.instructions"), HUDMessage.newQuest_type));
                }

                this.Helper.Input.Suppress(e.Button);
                return;
            }
        }

        // Хоткеи режима выделения (Q/ЛКМ/ПКМ/Enter/Esc)
        private bool HandleSelectionModeInput(Farm farmSel, ButtonPressedEventArgs e)
        {
            switch (e.Button)
            {
                case SButton.Q:
                    _manager.CycleSelectionSize();
                    this.Helper.Input.Suppress(e.Button);
                    return true;
                case SButton.MouseLeft:
                    var pos = this.Helper.Input.GetCursorPosition().Tile;
                    _manager.TryAddVirtualBeacon(new Point((int)pos.X, (int)pos.Y));
                    Game1.currentLocation?.localSound("coin");
                    this.Helper.Input.Suppress(e.Button);
                    return true;
                case SButton.MouseRight:
                    _manager.RemoveLastVirtualBeacon();
                    Game1.currentLocation?.localSound("cancel");
                    this.Helper.Input.Suppress(e.Button);
                    return true;
                case SButton.Escape:
                    _manager.CancelBeaconSelection();
                    _selectionOwner = null;
                    Game1.currentLocation?.localSound("cancel");
                    this.Helper.Input.Suppress(e.Button);
                    return true;
                case SButton.Enter:
                    if (_manager.SelectionBuilding is Building sb)
                    {
                        string msg;
                        bool ok = _manager.TryStartFarmerFromBeacons(sb, farmSel, out msg);
                        Game1.playSound(ok ? "smallSelect" : "cancel");
                        if (!string.IsNullOrEmpty(msg))
                            Game1.addHUDMessage(new HUDMessage(msg, ok ? HUDMessage.newQuest_type : HUDMessage.error_type));
                    }
                    _manager.CancelBeaconSelection();
                    _selectionOwner = null;
                    this.Helper.Input.Suppress(e.Button);
                    return true;
            }
            return false;
        }

        private void HandleWarehouseInteraction(Farm farm, Vector2 grabTile, ButtonPressedEventArgs e)
        {
            Building? warehouse = farm.getBuildingAt(grabTile);
            if (warehouse == null || warehouse.buildingType.Value != "DroneWarehouse")
                return;

            int leftX = warehouse.tileX.Value;
            int rightX = warehouse.tileX.Value + warehouse.tilesWide.Value - 1;
            int topY  = warehouse.tileY.Value;
            int botY  = warehouse.tileY.Value + warehouse.tilesHigh.Value - 1;

            int gx = Math.Clamp((int)grabTile.X, leftX, rightX);
            int gy = Math.Clamp((int)grabTile.Y, topY, botY);
            bool rightHalf = (gx == rightX);

            if (rightHalf)
            {
                Game1.activeClickableMenu = new DroneConsoleMenu(
                    this.Helper, this.Monitor, _manager, warehouse,
                    _harvestIcon, _waterIcon, _petIcon, _farmerIcon,
                    _texFrame, _texButton, _texScreen, _ledGreen, _ledRed
                );
            }
            else
            {
                OpenWarehouseChestMenu(warehouse);
            }
            this.Helper.Input.Suppress(e.Button);
        }

        private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
        {
            if (!Context.IsWorldReady || !e.IsLocalPlayer) return;

            // Учёт размещённых «маяков» (факелы с modData)
            foreach (var item in e.Removed)
            {
                if (item is SObject o && !o.bigCraftable.Value && o.ParentSheetIndex == 93 && o.modData.ContainsKey(MD.Beacon))
                {
                    _pendingBeaconOwner ??= (o.modData.TryGetValue(MD.BeaconOwner, out var owner) ? owner : null);
                    if (_pendingBeaconSize == 0 && o.modData.TryGetValue(MD.BeaconSize, out var s) && int.TryParse(s, out var z))
                        _pendingBeaconSize = z;

                    _pendingBeaconCount += Math.Max(1, o.Stack);
                }
            }
        }

        private void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
        {
            if (_pendingBeaconCount <= 0 || _pendingBeaconSize <= 0 || string.IsNullOrEmpty(_pendingBeaconOwner))
                return;

            if (e.Location is not Farm) return;

            // Помечаем новые факелы как наши маяки (в точном количестве)
            foreach (var pair in e.Added)
            {
                if (pair.Value is SObject o && !o.bigCraftable.Value && o.ParentSheetIndex == 93 && !o.modData.ContainsKey(MD.Beacon))
                {
                    o.modData[MD.Beacon] = "1";
                    o.modData[MD.BeaconOwner] = _pendingBeaconOwner!;
                    o.modData[MD.BeaconSize] = _pendingBeaconSize.ToString();
                    if (--_pendingBeaconCount <= 0) break;
                }
            }

            if (_pendingBeaconCount <= 0)
            {
                _pendingBeaconOwner = null;
                _pendingBeaconSize = 0;
                _pendingBeaconCount = 0;
            }
        }
    }
}
