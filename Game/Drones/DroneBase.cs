using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Buildings;

namespace DroneWarehouseMod.Game.Drones
{
    internal abstract class DroneBase
    {
        // Визуал/анимации
        protected const int DRAW_WIDTH = 72;
        public virtual int DrawPixelSize => 72;
        public const int ANIM_FLY_TPF = 8;
        public const int ANIM_LAUNCH_TPF = 12;
        public const int ANIM_LAND_TPF = 12;
        public const int ANIM_WORK_TPF = 12;

        public virtual float SpeedPxPerTick => 2.6f;

        public abstract DroneKind Kind { get; }
        public Building Home { get; }
        public Vector2 Position;
        public DroneState State { get; internal set; } = DroneState.Docked;
        public bool IsDocked => State == DroneState.Docked;

        protected int _waitTicks = 0;
        protected int _animTick = 0;
        public int AnimTick => _animTick;
        protected int _workTotalTicks = 0;

        protected virtual void OnWorkProgress(Farm farm, DroneManager mgr) { }
        protected DroneAnimMode _anim = DroneAnimMode.Fly;
        public DroneAnimMode AnimMode => _anim;
        protected virtual void OnDocked(Farm farm, DroneManager mgr) { }

        protected Point _targetTile;
        protected WorkKind _workKind = WorkKind.None;

        protected readonly DroneAnimSet Anim;
        protected virtual Vector2? GetDynamicMoveTarget(GameLocation loc) => null;

        // Path/A*
        private List<Vector2> _path = new();
        private int _pathIdx = 0;
        private Point _lastDestTile;
        private int _repathCooldown = 0;
        private Vector2 _lastPos;
        private int _stallTicks = 0;

        protected DroneBase(Building home, DroneAnimSet anim)
        {
            Home = home;
            Anim = anim;
            Position = DroneManager.HatchCenter(home);
        }

        public virtual void BeginLaunching(Vector2 hatch, int ticks)
        {
            Position = hatch; _waitTicks = ticks;
            State = DroneState.Launching; _anim = DroneAnimMode.Launch; _animTick = 0;
        }

        public virtual void BeginLanding(Vector2 hatch, int ticks)
        {
            Position = hatch; _waitTicks = ticks;
            State = DroneState.Landing; _anim = DroneAnimMode.Land; _animTick = 0;
        }

        private static Point ToTile(Vector2 px) =>
            new((int)(px.X / Game1.tileSize), (int)(px.Y / Game1.tileSize));

        private bool NeedRepath(Farm farm, DroneManager mgr, Vector2 dest, Building home)
        {
            if (_pathIdx >= _path.Count) return true;
            if (_repathCooldown > 0) { _repathCooldown--; return false; }

            Point curDest = ToTile(dest);
            if (!_lastDestTile.Equals(curDest)) return true;

            // застряли
            if (Vector2.Distance(_lastPos, Position) < 0.05f)
            {
                if (++_stallTicks > 30) return true;
            }
            else
            {
                _stallTicks = 0;
            }
            return false;
        }

        public void Update(Farm farm, DroneManager mgr)
        {
            if (farm is null || mgr is null) return;
            if (State != DroneState.Docked) _animTick++;

            switch (State)
            {
                case DroneState.Docked: return;
                case DroneState.Launching:
                    if (_waitTicks-- > 0) return;
                    State = DroneState.Idle; _anim = DroneAnimMode.Fly; return;
                case DroneState.Landing:
                    if (_waitTicks-- > 0) return;
                    State = DroneState.Docked; OnDocked(farm, mgr); return;
            }

            if (State == DroneState.WaitingAtTarget)
            {
                OnWorkProgress(farm, mgr);
                if (_waitTicks-- > 0) return;

                DoWorkAt(farm, mgr, _targetTile, _workKind);
                mgr.ReleaseTarget(_targetTile);

                _anim = DroneAnimMode.Fly;
                OnAfterWork(farm, mgr);
                return;
            }

            if (State == DroneState.Idle)
            {
                if (NeedsRefill() && TryGetRefillPoint(farm, mgr, out var refill))
                {
                    _workKind = WorkKind.Refill;
                    _targetTile = new Point((int)(refill.X / Game1.tileSize), (int)(refill.Y / Game1.tileSize));
                    State = DroneState.MovingToTarget;
                    return;
                }

                if (TryAcquireWork(farm, mgr, out _targetTile, out _workKind))
                {
                    State = DroneState.MovingToTarget;
                }
                else
                {
                    State = DroneState.ReturningDock;
                    if (!mgr.IsInLandingQueue(this))
                        mgr.EnqueueLanding(this);
                }
                return;
            }

            Vector2 dest =
                State == DroneState.MovingToTarget
                    ? (GetDynamicMoveTarget(farm) ?? DroneManager.TileCenter(_targetTile))
                    :
                State == DroneState.ReturningUnload ? DroneManager.HatchCenter(Home) :
                State == DroneState.ReturningDock
                    ? (mgr.IsFirstInLandingQueue(this) ? DroneManager.HatchCenter(Home) : mgr.GetHoldPoint(Home, this))
                    : Position;

            // Навигация: A* + сглаживание в менеджере
            if (_path.Count == 0 || NeedRepath(farm, mgr, dest, Home))
            {
                if (mgr.FindPath(farm, Position, dest, Home, out var newPath) && newPath.Count > 0)
                {
                    _path = newPath;
                    _pathIdx = 0;
                    _lastDestTile = ToTile(dest);
                    _repathCooldown = 10;
                }
                else
                {
                    _path.Clear();
                    _pathIdx = 0;
                }
            }

            Vector2 navTarget = (_path.Count > 0 && _pathIdx < _path.Count) ? _path[_pathIdx] : dest;
            Vector2 dirTarget = navTarget - Position;
            float dist = dirTarget.Length();

            float arrivalEps = SpeedPxPerTick + 0.1f;
            if (_workKind == WorkKind.PetSmall || _workKind == WorkKind.PetBig)
                arrivalEps = 6f;

            if (dist <= arrivalEps)
            {
                Position = navTarget;

                if (_pathIdx < _path.Count) _pathIdx++;
                if (_pathIdx >= _path.Count) _repathCooldown = 0;

                if (State == DroneState.ReturningDock)
                {
                    if (mgr.TryStartLanding(farm, this)) return;
                }
                else if (State == DroneState.MovingToTarget)
                {
                    State = DroneState.WaitingAtTarget;
                    _animTick = 0;
                    _workTotalTicks = System.Math.Max(1, WorkDurationTicks());
                    _waitTicks = _workTotalTicks;
                    _anim = WorkAnimMode();
                }
                else if (State == DroneState.ReturningUnload)
                {
                    OnReachedUnloadPoint(farm, mgr);
                    _anim = DroneAnimMode.Fly;
                    State = DroneState.Idle;
                }
            }
            else if (dist > 0.0001f)
            {
                dirTarget.Normalize();
                Position += dirTarget * SpeedPxPerTick;
                _lastPos = Position;
            }
        }

        public Texture2D GetCurrentFrameTexture()
        {
            Texture2D[] frames;
            int baseTpf;
            switch (_anim)
            {
                case DroneAnimMode.Launch:       frames = Anim.Launch;      baseTpf = ANIM_LAUNCH_TPF; break;
                case DroneAnimMode.Land:         frames = Anim.Land;        baseTpf = ANIM_LAND_TPF;   break;
                case DroneAnimMode.WorkLoaded:   frames = Anim.WorkLoaded;  baseTpf = ANIM_WORK_TPF;   break;
                case DroneAnimMode.WorkEmpty:    frames = Anim.WorkEmpty;   baseTpf = ANIM_WORK_TPF;   break;
                case DroneAnimMode.WorkRefill:   frames = Anim.Refill;      baseTpf = ANIM_WORK_TPF;   break;
                case DroneAnimMode.WorkPetSmall: frames = Anim.WorkPetSmall;baseTpf = ANIM_WORK_TPF;   break;
                case DroneAnimMode.WorkPetBig:   frames = Anim.WorkPetBig;  baseTpf = ANIM_WORK_TPF;   break;
                case DroneAnimMode.WorkFarmerPlant:
                case DroneAnimMode.WorkFarmerFail:
                case DroneAnimMode.WorkFarmerClear:
                    frames = (_anim == DroneAnimMode.WorkFarmerFail && (Anim.FarmerFail?.Length > 0))
                        ? Anim.FarmerFail
                        : (_anim == DroneAnimMode.WorkFarmerClear && (Anim.FarmerClear?.Length > 0))
                            ? Anim.FarmerClear
                            : (Anim.FarmerWork?.Length > 0 ? Anim.FarmerWork : Anim.WorkEmpty);
                    baseTpf = ANIM_WORK_TPF;
                    break;
                case DroneAnimMode.Fly:
                default:
                    frames = IsLoadedVisual ? Anim.FlyLoaded : Anim.FlyEmpty;
                    baseTpf = ANIM_FLY_TPF;
                    break;
            }

            if (frames == null || frames.Length == 0)
                return Game1.mouseCursors;

            bool isFarmerWork = _anim == DroneAnimMode.WorkFarmerPlant
                             || _anim == DroneAnimMode.WorkFarmerFail
                             || _anim == DroneAnimMode.WorkFarmerClear;

            int tpf = baseTpf;
            if (isFarmerWork && _workTotalTicks > 0)
                tpf = System.Math.Max(1, _workTotalTicks / frames.Length);

            int idx = isFarmerWork
                ? System.Math.Min(frames.Length - 1, AnimTick / System.Math.Max(1, tpf)) // один проход
                : (AnimTick / System.Math.Max(1, tpf)) % frames.Length;                  // цикл

            return frames[idx];
        }

        public void DrawManual(SpriteBatch b, Texture2D sprite)
        {
            if (State == DroneState.Docked) return;

            Vector2 screen = Game1.GlobalToLocal(Game1.viewport, Position);
            int drawW = DrawPixelSize;
            int drawH = (int)System.Math.Round(drawW * (sprite.Height / (float)sprite.Width));

            var dst = new Rectangle((int)screen.X - drawW / 2, (int)screen.Y - drawH, drawW, drawH);
            float layer = System.Math.Clamp((Position.Y + 8) / 10000f, 0.0001f, 0.9999f);
            b.Draw(sprite, dst, null, Color.White, 0f, Vector2.Zero, SpriteEffects.None, layer);
        }

        // Абстракции поведения
        protected abstract bool TryAcquireWork(Farm farm, DroneManager mgr, out Point tile, out WorkKind kind);
        protected abstract void DoWorkAt(Farm farm, DroneManager mgr, Point tile, WorkKind kind);
        protected abstract DroneAnimMode WorkAnimMode();
        protected abstract int WorkDurationTicks();

        protected virtual bool NeedsRefill() => false;
        protected virtual bool TryGetRefillPoint(Farm farm, DroneManager mgr, out Vector2 dest) { dest = default; return false; }
        protected virtual void OnAfterWork(Farm farm, DroneManager mgr) { }
        protected virtual void OnReachedUnloadPoint(Farm farm, DroneManager mgr) { }

        protected virtual bool IsLoadedVisual => false;
    }
}
