using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;

namespace DroneWarehouseMod.Core
{
    // Аудио: ванильные и опционально кастомные SFX
    internal static class Audio
    {
        private static IModHelper _helper = null!;
        private static IMonitor   _mon    = null!;
        private static bool  _customEnabled;
        private static float _customVolume = 1f;

        private static SoundEffect? _lidOpen, _lidClose, _harvest;

        // Инициализация и загрузка кастомных эффектов (если включены)
        public static void Init(IModHelper helper, IMonitor monitor, DroneWarehouseMod.ModConfig cfg)
        {
            _helper = helper;
            _mon    = monitor;

            _customEnabled = cfg.EnableCustomSfx;
            _customVolume  = MathHelper.Clamp(cfg.CustomSfxVolume, 0f, 1f);

            if (!_customEnabled) return;

            TryLoad(ref _lidOpen,  cfg.LidOpenSfx);
            TryLoad(ref _lidClose, cfg.LidCloseSfx);
            // _harvest можно подключить через конфиг при необходимости
        }

        // Ванильный cue — только если игрок в той же локации
        public static void PlayFarmOnly(GameLocation loc, string cue)
        {
            if (loc != null && Game1.currentLocation == loc)
                loc.localSound(cue);
        }

        private static void TryLoad(ref SoundEffect? slot, string relPath)
        {
            try
            {
                string full = Path.Combine(_helper.DirectoryPath, relPath.Replace('/', Path.DirectorySeparatorChar));
                using var s = File.OpenRead(full);
                slot = SoundEffect.FromStream(s);
            }
            catch (Exception ex)
            {
                slot = null;
                _mon.Log($"[Audio] FAILED to load '{relPath}': {ex.Message}", LogLevel.Warn);
            }
        }

        private static void Play(SoundEffect? sfx, float volume = 1f, float pitch = 0f, float pan = 0f)
        {
            if (!_customEnabled || sfx == null) return;
            float vol = MathHelper.Clamp(Game1.options.soundVolumeLevel * _customVolume * volume, 0f, 1f);
            if (vol <= 0f) return;
            sfx.Play(vol, pitch, pan);
        }

        // Кастомный SFX — только если игрок в заданной локации
        private static void PlayFarmOnly(SoundEffect? sfx, GameLocation? where, float volume = 1f)
        {
            if (where != null && Game1.currentLocation != where) return;
            Play(sfx, volume);
        }

        public static void LidOpen (GameLocation? where = null) => PlayFarmOnly(_lidOpen,  where, 1f);
        public static void LidClose(GameLocation? where = null) => PlayFarmOnly(_lidClose, where, 1f);
        public static void Harvest (GameLocation? where = null) => PlayFarmOnly(_harvest,  where, 0.9f);

        // «Урожай» с простым затуханием по расстоянию от тайла
        public static void HarvestAt(GameLocation loc, Vector2 tile)
        {
            if (!_customEnabled || Game1.currentLocation != loc) return;
            float dist  = Vector2.Distance(Game1.player.Tile, tile);
            float atten = MathHelper.Clamp(1f - (dist / 20f), 0f, 1f);
            float vol   = 0.25f + 0.75f * atten;
            Play(_harvest, vol);
        }
    }
}
