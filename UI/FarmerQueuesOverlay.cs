// UI/FarmerQueuesOverlay.cs
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewModdingAPI;
using StardewValley.Buildings;
using StardewValley.Menus;
using DroneWarehouseMod.Game;
using DroneWarehouseMod.Game.Drones;
using System.Linq;

namespace DroneWarehouseMod.UI
{
    internal sealed class FarmerQueuesOverlay
    {
        private readonly ITranslationHelper _i18n;
        private readonly IMonitor _mon;
        private readonly DroneManager _mgr;
        private readonly Building _bld;

        public FarmerQueuesOverlay(IModHelper helper, IMonitor mon, DroneManager mgr, Building building)
        {
            _i18n = helper.Translation;
            _mon  = mon;
            _mgr  = mgr;
            _bld  = building;
        }

        public void Draw(SpriteBatch b)
        {
            var farmers = _mgr.GetFarmers(_bld);
            int n = farmers.Count;
            if (n <= 0) return;

            // —— layout (шире колонки + увеличенные паддинги)
            const int colW = 240;
            const int colH = 120;
            const int colPad = 16;      // расстояние между колонками
            const int inPadX = 16;      // внутренний паддинг колонки (лево/право)
            const int inPadY = 10;      // внутренний верхний паддинг
            const int topPad = 56;      // от заголовка до колонок
            const int sidePad = 24;     // от краёв панели
            const int bottomPad = 56;   // ↑ больше вертикальный паддинг под колонками

            int blocks = Math.Min(3, Math.Max(1, n));
            int panelW = sidePad * 2 + blocks * colW + (blocks - 1) * colPad;
            int panelH = topPad + colH + bottomPad;

            var vp = Game1.uiViewport;
            var panel = new Rectangle((vp.Width - panelW) / 2, 32, panelW, panelH);

            // Рамка панели
            IClickableMenu.drawTextureBox(
                b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                panel.X, panel.Y, panel.Width, panel.Height, Color.White, 1f, false
            );

            // Заголовок
            var title = _i18n.Get("overlay.farmers.title", new { count = n });
            Utility.drawTextWithShadow(
                b, title, Game1.smallFont,
                new Vector2(panel.X + sidePad, panel.Y + 16),
                new Color(120, 255, 170)
            );

            // Колонки
            int x = panel.X + sidePad;
            int y = panel.Y + topPad;

            for (int i = 0; i < blocks; i++)
            {
                var rect = new Rectangle(x, y, colW, colH);
                IClickableMenu.drawTextureBox(
                    b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                    rect.X, rect.Y, rect.Width, rect.Height, Color.White * 0.85f, 1f, false
                );

                // «Фермер i»
                var label = _i18n.Get("overlay.farmer.block", new { idx = (i + 1) });
                Utility.drawTextWithShadow(
                    b, label, Game1.smallFont,
                    new Vector2(rect.X + inPadX, rect.Y + inPadY),
                    new Color(200, 255, 200)
                );

                // Содержимое
                FarmerDrone? fd = (i < farmers.Count) ? farmers[i] : null;
                if (fd == null)
                {
                    var txt = _i18n.Get("overlay.empty");
                    Utility.drawTextWithShadow(
                        b, txt, Game1.smallFont,
                        new Vector2(rect.X + inPadX, rect.Y + inPadY + 24),
                        Color.Silver
                    );
                }
                else
                {
                    var (done, total) = fd.GetProgress();
                    string line = (total > 0)
                        ? _i18n.Get("overlay.progress", new { done, total })
                        : _i18n.Get("overlay.queue.empty");

                    Utility.drawTextWithShadow(
                        b, line, Game1.smallFont,
                        new Vector2(rect.X + inPadX, rect.Y + inPadY + 24),
                        new Color(168, 255, 138)
                    );

                    // Полоска прогресса
                    if (total > 0)
                    {
                        float pct = Math.Clamp(done / (float)total, 0f, 1f);
                        var barBg = new Rectangle(rect.X + inPadX, rect.Y + inPadY + 52, rect.Width - inPadX * 2, 12);
                        b.Draw(Game1.staminaRect, barBg, Color.Black * 0.35f);
                        var barFill = new Rectangle(barBg.X + 1, barBg.Y + 1, (int)((barBg.Width - 2) * pct), barBg.Height - 2);
                        b.Draw(Game1.staminaRect, barFill, Color.Lime * 0.8f);
                    }
                }

                x += colW + colPad;
            }

            // Подсказка закрытия — ниже из‑за увеличенного паддинга
            var hint = _i18n.Get("overlay.hint.close"); // «F3/Esc — закрыть»
            var size = Game1.smallFont.MeasureString(hint);
            Utility.drawTextWithShadow(
                b, hint, Game1.smallFont,
                new Vector2(panel.Right - size.X - sidePad, panel.Bottom - size.Y - 16),
                Color.White * 0.9f
            );
        }
    }
}
