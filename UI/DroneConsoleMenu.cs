// UI/DroneConsoleMenu.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Buildings;
using DroneWarehouseMod.Game.Drones;
using DroneWarehouseMod.Core;
using DroneWarehouseMod.Game;
using MD = DroneWarehouseMod.Core.ModDataKeys;
using SObject = StardewValley.Object;

namespace DroneWarehouseMod.UI
{
    internal class DroneConsoleMenu : IClickableMenu
    {
        private ITranslationHelper I18n => _helper.Translation;
        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private readonly DroneManager _manager;
        private readonly Building _building;

        // Иконки/текстуры
        private readonly Texture2D _iconHarvest;
        private readonly Texture2D _iconWater;
        private readonly Texture2D _iconPet;
        private readonly Texture2D _iconFarmer;
        private readonly Texture2D? _texFrame, _texButton, _texScreen, _ledGreen, _ledRed;

        // Кнопки
        private Rectangle _btnUpgrade;
        private Rectangle _btnScrapHarvest, _btnScrapWater, _btnScrapPet;
        private Rectangle _btnCreateHarvest, _btnCreateWater, _btnCreatePet;

        private bool _hoverUpgrade, _hoverScrapH, _hoverScrapW, _hoverScrapP;
        private bool _hoverCreateH, _hoverCreateW, _hoverCreateP;

        private Rectangle _btnCreateFarmer;
        private Rectangle _btnBeaconSelect;
        private Rectangle _btnBeaconCreate;
        private bool _hoverCreateFarmer, _hoverBeaconSelect, _hoverBeaconCreate;

        private static readonly int[] BEACON_SIZES = new[] { 3, 5, 7 };
        private int _beaconIndex = 0;

        private const int LedSize = 16;
        private const int GOLD = -999;

        private static readonly Color TxtMain   = new Color(168, 255, 138);
        private static readonly Color TxtShadow = new Color(28, 58, 28);

        // Вместимость по уровню
        private static int CapacityByLevel(int level) => level switch
        {
            >= 3 => 9,
            2 => 6,
            _ => 3,
        };

        // Стоимость апгрейдов
        private static readonly Dictionary<int, int> COST_1_TO_2 = new()
        {
            { GOLD, 30000 },
            { 336, 10 },   // Gold Bar
            { 787, 5 },    // Battery Pack
            { 82,  3 },    // Fire Quartz
        };
        private static readonly Dictionary<int, int> COST_2_TO_3 = new()
        {
            { GOLD, 50000 },
            { 337, 10 },   // Iridium Bar
            { 787, 8 },    // Battery Pack
            { 74,  1 },    // Prismatic Shard
        };
        private static Dictionary<int, int>? GetUpgradeCost(int level) => level switch
        {
            1 => COST_1_TO_2,
            2 => COST_2_TO_3,
            _ => null
        };

        // Стоимость создания по типам
        private static readonly Dictionary<DroneKind, Dictionary<int, int>> CreateCostByKind = new()
        {
            [DroneKind.Harvest] = new()
            {
                { 334, 5 },   // Copper Bar
                { 709, 10 },  // Hardwood
                { 60,  1 },   // Emerald
            },
            [DroneKind.Water] = new()
            {
                { 335, 5 },   // Iron Bar
                { 709, 15 },  // Hardwood
                { 72,  1 },   // Diamond
            },
            [DroneKind.Pet] = new()
            {
                { 336, 5 },   // Gold Bar
                { 709, 20 },  // Hardwood
                { 446, 1 },   // Rabbit's Foot
            },
        };

        // Фермер — отдельная цена
        private static readonly Dictionary<int, int> COST_FARMER_CREATE = new()
        {
            { 337, 5 },   // Iridium Bar
            { 848, 10 },  // Cinder Shard
            { 74,  1 },   // Prismatic Shard
        };

        private static void DrawConsoleText(SpriteBatch b, SpriteFont font, string text, Vector2 pos)
        {
            b.DrawString(font, text, pos + new Vector2(2f, 2f), TxtShadow);
            b.DrawString(font, text, pos, TxtMain);
        }

        internal DroneConsoleMenu(
            IModHelper helper, IMonitor monitor, DroneManager manager, Building building,
            Texture2D iconHarvest, Texture2D iconWater, Texture2D iconPet, Texture2D iconFarmer,
            Texture2D? texFrame, Texture2D? texButton, Texture2D? texScreen,
            Texture2D? ledGreen, Texture2D? ledRed
        ) : base(0, 0, 0, 0, true)
        {
            _helper = helper;
            _monitor = monitor;
            _manager = manager;
            _building = building;

            _iconHarvest = iconHarvest;
            _iconWater = iconWater;
            _iconPet = iconPet;
            _iconFarmer = iconFarmer;
            _texFrame = texFrame;
            _texButton = texButton;
            _texScreen = texScreen;
            _ledGreen = ledGreen;
            _ledRed = ledRed;

            width = 760;
            height = 470;
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            // Верхняя панель
            _btnUpgrade = new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + 58, 260, 78);
            _btnCreateFarmer = new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + 48, 320, 110);

            // Ряд управления маяками
            int rowWidth = 150 + 10 + 120;
            int safeRight = xPositionOnScreen + width - 24;
            int topRowX = Math.Min(_btnCreateFarmer.Right + 12, safeRight - rowWidth);
            int topRowY = _btnCreateFarmer.Y + 12;
            _btnBeaconSelect = new Rectangle(topRowX, topRowY, 150, 42);
            _btnBeaconCreate = new Rectangle(_btnBeaconSelect.Right + 10, topRowY, 120, 42);

            // Разборка
            int rightColX = xPositionOnScreen + width - 250;
            int rowY = yPositionOnScreen + 150;
            _btnScrapHarvest = new Rectangle(rightColX + 0, rowY + 24, 68, 68);
            _btnScrapWater = new Rectangle(_btnScrapHarvest.Right + 10, rowY + 24, 68, 68);
            _btnScrapPet = new Rectangle(_btnScrapWater.Right + 10, rowY + 24, 68, 68);

            // Создание
            int createY = yPositionOnScreen + height - 140;
            _btnCreateHarvest = new Rectangle(xPositionOnScreen + 24, createY, 220, 70);
            _btnCreateWater = new Rectangle(_btnCreateHarvest.Right + 22, createY, 220, 70);
            _btnCreatePet = new Rectangle(_btnCreateWater.Right + 22, createY, 220, 70);

            // Значения по умолчанию в modData
            if (!_building.modData.ContainsKey(MD.HasFarmer)) _building.modData[MD.HasFarmer] = "0";
            var md = _building.modData;
            if (!md.ContainsKey(MD.Level)) md[MD.Level] = "1";
            if (!md.ContainsKey(MD.CountHarvest)) md[MD.CountHarvest] = "0";
            if (!md.ContainsKey(MD.CountWater)) md[MD.CountWater] = "0";
            if (!md.ContainsKey(MD.CountPet))
            {
                if (md.TryGetValue("Jenya.DroneWarehouseMod/Count.Iron", out var old))
                    md[MD.CountPet] = old;
                else
                    md[MD.CountPet] = "0";
            }
        }

        public override void performHoverAction(int x, int y)
        {
            int level = int.TryParse(_building.modData.TryGetValue(MD.Level, out var sLvl) ? sLvl : "1", out var lv) ? lv : 1;
            bool atMaxLevel = level >= 3;
            bool hasFarmer = _building.modData.TryGetValue(MD.HasFarmer, out var hf) && hf == "1";

            _hoverUpgrade      = !atMaxLevel && _btnUpgrade.Contains(x, y);

            _hoverScrapH       = _btnScrapHarvest.Contains(x, y);
            _hoverScrapW       = _btnScrapWater.Contains(x, y);
            _hoverScrapP       = _btnScrapPet.Contains(x, y);

            _hoverCreateH      = _btnCreateHarvest.Contains(x, y);
            _hoverCreateW      = _btnCreateWater.Contains(x, y);
            _hoverCreateP      = _btnCreatePet.Contains(x, y);

            _hoverCreateFarmer =  atMaxLevel && !hasFarmer && _btnCreateFarmer.Contains(x, y);
            _hoverBeaconSelect =  atMaxLevel &&  hasFarmer && _btnBeaconSelect.Contains(x, y);
            _hoverBeaconCreate =  atMaxLevel &&  hasFarmer && _btnBeaconCreate.Contains(x, y);

            base.performHoverAction(x, y);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            int level  = int.Parse(_building.modData[MD.Level]);
            int countH = int.Parse(_building.modData[MD.CountHarvest]);
            int countW = int.Parse(_building.modData[MD.CountWater]);
            int countP = int.Parse(_building.modData[MD.CountPet]);
            int total  = countH + countW + countP;

            bool levelOkH = level >= 1;
            bool levelOkW = level >= 2;
            bool levelOkP = level >= 3;

            int capacity = CapacityByLevel(level);
            bool atMaxLevel = level >= 3;
            bool blockedByMax = total >= capacity;

            // Upgrade
            if (!atMaxLevel && _btnUpgrade.Contains(x, y))
            {
                var cost = GetUpgradeCost(level);
                if (cost != null && TryConsumeItems(Game1.player, cost))
                {
                    _building.modData[MD.Level] = Math.Min(level + 1, 3).ToString();
                    Game1.currentLocation?.localSound("purchase");
                }
                else Game1.currentLocation?.localSound("cancel");
                return;
            }

            // Scrap
            if (_btnScrapHarvest.Contains(x, y))
            {
                if (countH > 0)
                {
                    _building.modData[MD.CountHarvest] = (countH - 1).ToString();
                    _manager.SyncWithBuildings();
                    Game1.currentLocation?.localSound("trashcan");
                }
                else Game1.currentLocation?.localSound("cancel");
                return;
            }
            if (_btnScrapWater.Contains(x, y))
            {
                if (countW > 0)
                {
                    _building.modData[MD.CountWater] = (countW - 1).ToString();
                    _manager.SyncWithBuildings();
                    Game1.currentLocation?.localSound("trashcan");
                }
                else Game1.currentLocation?.localSound("cancel");
                return;
            }
            if (_btnScrapPet.Contains(x, y))
            {
                if (countP > 0)
                {
                    _building.modData[MD.CountPet] = (countP - 1).ToString();
                    _manager.SyncWithBuildings();
                    Game1.currentLocation?.localSound("trashcan");
                }
                else Game1.currentLocation?.localSound("cancel");
                return;
            }

            // Create
            if (_btnCreateHarvest.Contains(x, y))
            {
                if (levelOkH && !blockedByMax && HasAllItems(Game1.player, CreateCostByKind[DroneKind.Harvest]) &&
                    TryConsumeItems(Game1.player, CreateCostByKind[DroneKind.Harvest]))
                {
                    countH++;
                    _building.modData[MD.CountHarvest] = countH.ToString();
                    _manager.SyncWithBuildings();
                    Game1.currentLocation?.localSound("smallSelect");
                }
                else Game1.currentLocation?.localSound("cancel");
                return;
            }

            if (_btnCreateWater.Contains(x, y))
            {
                if (levelOkW && !blockedByMax && HasAllItems(Game1.player, CreateCostByKind[DroneKind.Water]) &&
                    TryConsumeItems(Game1.player, CreateCostByKind[DroneKind.Water]))
                {
                    countW++;
                    _building.modData[MD.CountWater] = countW.ToString();
                    _manager.SyncWithBuildings();
                    Game1.currentLocation?.localSound("smallSelect");
                }
                else Game1.currentLocation?.localSound("cancel");
                return;
            }

            if (_btnCreatePet.Contains(x, y))
            {
                if (levelOkP && !blockedByMax && HasAllItems(Game1.player, CreateCostByKind[DroneKind.Pet]) &&
                    TryConsumeItems(Game1.player, CreateCostByKind[DroneKind.Pet]))
                {
                    countP++;
                    _building.modData[MD.CountPet] = countP.ToString();
                    _manager.SyncWithBuildings();
                    Game1.currentLocation?.localSound("smallSelect");
                }
                else Game1.currentLocation?.localSound("cancel");
                return;
            }

            // Фермер/маяки при 3 уровне
            bool hasFarmer = _building.modData.TryGetValue(MD.HasFarmer, out var _hf) && _hf == "1";
            if (level >= 3)
            {
                if (!hasFarmer && _btnCreateFarmer.Contains(x, y))
                {
                    if (HasAllItems(Game1.player, COST_FARMER_CREATE) && TryConsumeItems(Game1.player, COST_FARMER_CREATE))
                    {
                        _building.modData[MD.HasFarmer] = "1";
                        _manager.SyncWithBuildings();
                        Game1.currentLocation?.localSound("purchase");
                    }
                    else Game1.currentLocation?.localSound("cancel");
                    return;
                }

                if (hasFarmer)
                {
                    if (_btnBeaconSelect.Contains(x, y))
                    {
                        _beaconIndex = (_beaconIndex + 1) % BEACON_SIZES.Length;
                        Game1.currentLocation?.localSound("smallSelect");
                        return;
                    }
                    if (_btnBeaconCreate.Contains(x, y))
                    {
                        int size = BEACON_SIZES[_beaconIndex];
                        _manager.BeginBeaconSelection(_building, size);
                        Game1.currentLocation?.localSound("smallSelect");
                        Game1.addHUDMessage(new HUDMessage(I18n.Get("hud.selection.instructions"), HUDMessage.newQuest_type));
                        this.exitThisMenu();
                        return;
                    }
                }
            }

            base.receiveLeftClick(x, y, playSound);
        }

        public override void draw(SpriteBatch b)
        {
            var frameTex = _texFrame ?? Game1.menuTexture;
            var frameSrc = (_texFrame != null) ? new Rectangle(0, 0, 60, 60) : new Rectangle(0, 256, 60, 60);
            IClickableMenu.drawTextureBox(b, frameTex, frameSrc, xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, false);

            DrawConsoleText(b, Game1.smallFont, I18n.Get("ui.console.title"), new Vector2(xPositionOnScreen + 24, yPositionOnScreen + 16));

            int level  = int.Parse(_building.modData[MD.Level]);
            int countH = int.Parse(_building.modData[MD.CountHarvest]);
            int countW = int.Parse(_building.modData[MD.CountWater]);
            int countP = int.Parse(_building.modData[MD.CountPet]);
            int total  = countH + countW + countP;

            bool levelOkH = level >= 1;
            bool levelOkW = level >= 2;
            bool levelOkP = level >= 3;

            int capacity = CapacityByLevel(level);
            bool atMaxLevel = level >= 3;

            // Уровень
            DrawConsoleText(b, Game1.smallFont, I18n.Get("ui.level", new { level = Math.Min(level, 3) }),
                new Vector2(xPositionOnScreen + width - 130, yPositionOnScreen + 20));

            // Верхняя панель: апгрейд или фермер/маяки
            if (!atMaxLevel)
            {
                var cost = GetUpgradeCost(level);
                if (cost != null)
                {
                    bool canUpgrade = HasAllItems(Game1.player, cost);
                    DrawButton(b, _btnUpgrade, I18n.Get("ui.upgrade"), _hoverUpgrade, disabled: !canUpgrade);
                    DrawLed(b, canUpgrade, _btnUpgrade.Right - LedSize - 6, _btnUpgrade.Y + (_btnUpgrade.Height - LedSize) / 2);
                    DrawCostSmart(b, cost, _btnUpgrade.Right + 20, _btnUpgrade.Y + 18, scale: 0.60f);
                }
            }
            else
            {
                bool hasFarmer = _building.modData.TryGetValue(MD.HasFarmer, out var _hf) && _hf == "1";
                if (!hasFarmer)
                {
                    DrawButton(b, _btnCreateFarmer, I18n.Get("ui.createFarmer"), _hoverCreateFarmer, icon: _iconFarmer, iconSize: 36);
                    int costX = _btnCreateFarmer.Right + 12;
                    int costY = _btnCreateFarmer.Y + (_btnCreateFarmer.Height - (int)(40 * 0.60f)) / 2;
                    DrawCostSmart(b, COST_FARMER_CREATE, costX, costY, scale: 0.60f, pad: 4);
                }
                else
                {
                    b.Draw(_iconFarmer, new Rectangle(_btnCreateFarmer.X + 12, _btnCreateFarmer.Y + 10, 72, 72), Color.White);
                    DrawButton(b, _btnBeaconSelect, I18n.Get("ui.beacon.label", new { size = BEACON_SIZES[_beaconIndex] }), _hoverBeaconSelect);
                    DrawButton(b, _btnBeaconCreate, I18n.Get("ui.beacon.create"), _hoverBeaconCreate);
                }
            }

            // Текущие дроны + счётчик
            int startX = xPositionOnScreen + 24;
            int rowY = yPositionOnScreen + 150;
            DrawConsoleText(b, Game1.smallFont, I18n.Get("ui.currentDrones"), new Vector2(startX, rowY));
            DrawConsoleText(b, Game1.smallFont, $"{total}/{capacity}", new Vector2(startX + 180, rowY));

            int iconsY = rowY + 34;
            DrawIconRow(b, _iconHarvest, countH, startX, iconsY);
            iconsY += 30;
            DrawIconRow(b, _iconWater, countW, startX, iconsY);
            iconsY += 30;
            DrawIconRow(b, _iconPet, countP, startX, iconsY);

            // Разобрать
            int rightColX = xPositionOnScreen + width - 250;
            DrawConsoleText(b, Game1.smallFont, I18n.Get("ui.scrap.header"), new Vector2(rightColX, rowY));
            DrawButton(b, _btnScrapHarvest, "", _hoverScrapH, disabled: countH <= 0, icon: _iconHarvest, iconSize: 22);
            DrawButton(b, _btnScrapWater, "", _hoverScrapW, disabled: countW <= 0, icon: _iconWater, iconSize: 22);
            DrawButton(b, _btnScrapPet, "", _hoverScrapP, disabled: countP <= 0, icon: _iconPet, iconSize: 22);

            // Создать
            DrawConsoleText(b, Game1.smallFont, I18n.Get("ui.create.header"), new Vector2(xPositionOnScreen + 24, _btnCreateHarvest.Y - 26));
            bool canCreateH = levelOkH && total < capacity && HasAllItems(Game1.player, CreateCostByKind[DroneKind.Harvest]);
            bool canCreateW = levelOkW && total < capacity && HasAllItems(Game1.player, CreateCostByKind[DroneKind.Water]);
            bool canCreateP = levelOkP && total < capacity && HasAllItems(Game1.player, CreateCostByKind[DroneKind.Pet]);

            DrawButton(b, _btnCreateHarvest, I18n.Get("ui.create.harvest"), _hoverCreateH, disabled: !canCreateH, icon: _iconHarvest, iconSize: 20);
            DrawLed(b, canCreateH, _btnCreateHarvest.Right - LedSize - 6, _btnCreateHarvest.Y + (_btnCreateHarvest.Height - LedSize) / 2);
            DrawCostSmart(b, CreateCostByKind[DroneKind.Harvest], _btnCreateHarvest.X + 8, _btnCreateHarvest.Bottom + 4, scale: 0.55f, pad: 3);

            DrawButton(b, _btnCreateWater, I18n.Get("ui.create.water"), _hoverCreateW, disabled: !canCreateW, icon: _iconWater, iconSize: 20);
            DrawLed(b, canCreateW, _btnCreateWater.Right - LedSize - 6, _btnCreateWater.Y + (_btnCreateWater.Height - LedSize) / 2);
            DrawCostSmart(b, CreateCostByKind[DroneKind.Water], _btnCreateWater.X + 8, _btnCreateWater.Bottom + 4, scale: 0.55f, pad: 3);

            DrawButton(b, _btnCreatePet, I18n.Get("ui.create.pet"), _hoverCreateP, disabled: !canCreateP, icon: _iconPet, iconSize: 20);
            DrawLed(b, canCreateP, _btnCreatePet.Right - LedSize - 6, _btnCreatePet.Y + (_btnCreatePet.Height - LedSize) / 2);
            DrawCostSmart(b, CreateCostByKind[DroneKind.Pet], _btnCreatePet.X + 8, _btnCreatePet.Bottom + 4, scale: 0.55f, pad: 3);

            base.draw(b);

            // Подсказки
            string? tip = null;
            if      (_hoverCreateH && !levelOkH) tip = I18n.Get("tooltip.requiresLevel", new { level = 1 });
            else if (_hoverCreateW && !levelOkW) tip = I18n.Get("tooltip.requiresLevel", new { level = 2 });
            else if (_hoverCreateP && !levelOkP) tip = I18n.Get("tooltip.requiresLevel", new { level = 3 });
            else if (_hoverCreateH)              tip = I18n.Get("tooltip.harvest");
            else if (_hoverCreateW)              tip = I18n.Get("tooltip.water");
            else if (_hoverCreateP)              tip = I18n.Get("tooltip.pet");
            else if (_hoverCreateFarmer)         tip = I18n.Get("tooltip.farmer");

            if (!string.IsNullOrEmpty(tip))
                drawHoverText(b, tip, Game1.smallFont);

            drawMouse(b);

            // Локальная отрисовка ряда иконок
            void DrawIconRow(SpriteBatch sb, Texture2D icon, int n, int x, int y)
            {
                int perRow = 20, size = 24, pad = 6;
                for (int i = 0; i < Math.Min(n, perRow); i++)
                    sb.Draw(icon, new Rectangle(x + i * (size + pad), y, size, size), Color.White);
            }
        }

        // Кнопка
        private void DrawButton(SpriteBatch b, Rectangle r, string text, bool hovered, bool disabled = false, Texture2D? icon = null, int iconSize = 0)
        {
            var tex = _texButton ?? Game1.menuTexture;
            var src = (_texButton != null) ? new Rectangle(0, 0, 60, 60) : new Rectangle(0, 256, 60, 60);
            var tint = disabled ? Color.White * 0.6f : (hovered ? Color.White : Color.White * 0.95f);
            IClickableMenu.drawTextureBox(b, tex, src, r.X, r.Y, r.Width, r.Height, tint, 1f, false);

            int textOffsetX = 0;

            if (icon != null && iconSize > 0)
            {
                int iconX = string.IsNullOrEmpty(text) ? r.X + (r.Width - iconSize) / 2 : r.X + 10;
                var dst = new Rectangle(iconX, r.Y + (r.Height - iconSize) / 2, iconSize, iconSize);
                b.Draw(icon, dst, Color.White * (disabled ? 0.6f : 1f));
                if (!string.IsNullOrEmpty(text)) textOffsetX = iconSize + 14;
            }

            if (!string.IsNullOrEmpty(text))
            {
                var size = Game1.smallFont.MeasureString(text);
                var pos = new Vector2(r.X + (r.Width - size.X) / 2f + textOffsetX / 2f, r.Y + (r.Height - size.Y) / 2f);
                var color = disabled ? Color.Lerp(new Color(120, 255, 170), Color.Black, 0.4f) : new Color(120, 255, 170);
                Utility.drawTextWithShadow(b, text, Game1.smallFont, pos, color);
            }
        }

        // LED
        private void DrawLed(SpriteBatch sb, bool on, int x, int y)
        {
            var tex = on ? _ledGreen : _ledRed;
            if (tex != null)
                sb.Draw(tex, new Rectangle(x, y, LedSize, LedSize), Color.White);
        }

        // Проверка наличия ресурсов
        private static bool HasAllItems(Farmer who, Dictionary<int, int> req)
        {
            foreach (var (id, need) in req)
            {
                if (id == GOLD)
                {
                    if (who.Money < need) return false;
                    continue;
                }
                int have = who.Items.Sum(it => it is SObject o && !o.bigCraftable.Value && o.ParentSheetIndex == id ? o.Stack : 0);
                if (have < need) return false;
            }
            return true;
        }

        // Списание ресурсов
        private static bool TryConsumeItems(Farmer who, Dictionary<int, int> req)
        {
            if (!HasAllItems(who, req)) return false;
            foreach (var (id, needInit) in req)
            {
                if (id == GOLD) { who.Money -= needInit; continue; }

                int need = needInit;
                for (int i = 0; i < who.Items.Count && need > 0; i++)
                {
                    if (who.Items[i] is SObject o && !o.bigCraftable.Value && o.ParentSheetIndex == id)
                    {
                        int take = Math.Min(o.Stack, need);
                        o.Stack -= take;
                        need -= take;
                        if (o.Stack <= 0) who.Items[i] = null;
                    }
                }
            }
            return true;
        }

        // Отрисовка цены (GOLD = «g»)
        private static void DrawCostSmart(SpriteBatch b, Dictionary<int, int> cost, int startX, int y, float scale = 1f, int pad = 6)
        {
            int iconSize = (int)(40 * scale);
            int textDx = (int)(46 * scale);
            int textDy = (int)(10 * scale);

            int x = startX;
            foreach (var (psi, amount) in cost)
            {
                if (psi == GOLD)
                {
                    string g = $"{amount:n0}g";
                    var color = (Game1.player.Money >= amount) ? new Color(120, 255, 170) : Color.Red;
                    Utility.drawTextWithShadow(b, g, Game1.smallFont, new Vector2(x, y + textDy), color);
                    int w = (int)Math.Ceiling(Game1.smallFont.MeasureString(g).X);
                    x += w + pad;
                    continue;
                }

                Rectangle src = Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, psi, 16, 16);
                b.Draw(Game1.objectSpriteSheet, new Rectangle(x, y, iconSize, iconSize), src, Color.White);

                string txt = $"x{amount}";
                int have = Game1.player.Items.Sum(it => it is SObject o && !o.bigCraftable.Value && o.ParentSheetIndex == psi ? o.Stack : 0);
                var color2 = (have >= amount) ? new Color(120, 255, 170) : Color.Red;

                Utility.drawTextWithShadow(b, txt, Game1.smallFont, new Vector2(x + textDx, y + textDy), color2);

                int textW = (int)Math.Ceiling(Game1.smallFont.MeasureString(txt).X);
                x += iconSize + textDx + textW + pad;
            }
        }
    }
}
