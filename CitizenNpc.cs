using System;
using System.Collections.Generic;
using System.Numerics;
using Eco.Gameplay.Components;
using Eco.Gameplay.Components.Storage;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Players;
using Eco.Shared.Localization;
using Eco.Shared.Math;
using Eco.Shared.Serialization;
using Quaternion = Eco.Shared.Math.Quaternion;

namespace EcoSimCitizens
{
    /// <summary>
    /// A real NPC in the Eco world. Backed by a WorldObject so the citizen has a
    /// physical presence, a clickable profile and a position that can be moved.
    /// Since v0.3 the NPC follows a real day/night routine: it commutes to its
    /// workplace during the day and sleeps at home at night. The visual headline
    /// shows the citizen's name, life stage and profession.
    /// </summary>
    [Serialized]
    [RequireComponent(typeof(MinimapComponent))]
    [RequireComponent(typeof(PublicStorageComponent))]
    public class CitizenNpc : WorldObject
    {
        [Serialized] public string CitizenName;
        [Serialized] public string ProfessionSkill;
        [Serialized] public float HomeX;
        [Serialized] public float HomeY;
        [Serialized] public float HomeZ;
        [Serialized] public float WorkX;
        [Serialized] public float WorkY;
        [Serialized] public float WorkZ;
        [Serialized] public float LastX;
        [Serialized] public float LastY;
        [Serialized] public float LastZ;
        [Serialized] public float NextWanderUtc;
        [Serialized] public float NextActionUtc;
        [Serialized] public string Headline;

        // --- v0.5 independent goal system: each NPC has its own target ---
        [Serialized] public string GoalType;   // Home / Work / Shop / Social / Wander
        [Serialized] public float GoalX;
        [Serialized] public float GoalY;
        [Serialized] public float GoalZ;
        [Serialized] public bool LastMoveFailed;

        // --- v0.7: appearance mirrors the invoker player (data-layer copy, the
        // vanilla mannequin prefab stays visible - no Avatar swap on the WorldObject) ---
        [Serialized] public string AppearanceUmaDefinition;
        [Serialized] public string AppearanceGender;
        [Serialized] public string AppearanceSourcePlayer;

        private readonly Random _rng = new Random();

        public CitizenNpc()
        {
        }

        /// <summary>
        /// Adds the minimap component (NPC marker on the in-game M-key map) once
        /// the world object has finished initializing. The component auto-registers
        /// with MinimapManager and keeps the marker position in sync with every
        /// MoveWorldObjectTo call. Category "Citizens" groups the NPCs on the map
        /// legend; if the component was already created by RequireComponent this
        /// just sets its category.
        /// </summary>
        protected override void PostInitialize()
        {
            base.PostInitialize();
            try
            {
                GetOrCreateComponent<MinimapComponent>()?.SetCategory(Localizer.DoStr("Citizens"));
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenNpc minimap setup failed: " + ex.Message);
            }
        }

        /// <summary>Attaches citizen state to this world object after spawn.</summary>
        public void Bind(Citizen citizen, Vector3 home)
        {
            CitizenName = citizen.Name;
            ProfessionSkill = citizen.Profession;
            HomeX = home.X;
            HomeY = home.Y;
            HomeZ = home.Z;
            WorkX = home.X;
            WorkY = home.Y;
            WorkZ = home.Z;
            LastX = home.X;
            LastY = home.Y;
            LastZ = home.Z;
            NextWanderUtc = 0;
            GoalType = "Wander";
            GoalX = home.X;
            GoalY = home.Y;
            GoalZ = home.Z;
            Headline = MakeHeadline(citizen);
            try { SetName(Headline); } catch { }
            // v0.5 persistence: anchor the citizen to this world object's ID so a
            // server restart can three-way reconcile citizens.json vs world NPCs.
            try { citizen.NpcId = ID.ToString(); } catch { }
            // v0.7: copy the citizen's persisted appearance data onto the NPC.
            ApplyAppearance(citizen);
        }

        /// <summary>
        /// Copies appearance data from the citizen record onto this NPC. The NPC
        /// keeps its vanilla mannequin prefab (visibility chain untouched); the
        /// appearance is stored as serialized data on the WorldObject so it can be
        /// read/displayed by commands and reports.
        /// </summary>
        public void ApplyAppearance(Citizen c)
        {
            if (c == null) return;
            AppearanceUmaDefinition = c.AppearanceUmaDefinition;
            AppearanceGender = c.AppearanceGender;
            AppearanceSourcePlayer = c.AppearanceSourcePlayer;
        }

        /// <summary>
        /// Copies the invoker player's avatar (UmaDefinition + Gender) onto the
        /// NPC and back onto the citizen record. Uses only server-readable
        /// User.Avatar data; the NPC stays a WorldObject and never gains a User.
        /// </summary>
        public void ApplyAppearanceFrom(User invoker, Citizen c = null)
        {
            if (invoker == null || invoker.Avatar == null) return;
            try
            {
                string def = invoker.Avatar.UmaDefinition;
                string gender = invoker.Avatar.Gender.ToString();
                AppearanceUmaDefinition = def;
                AppearanceGender = gender;
                AppearanceSourcePlayer = invoker.Name;
                if (c != null)
                {
                    c.AppearanceUmaDefinition = def;
                    c.AppearanceGender = gender;
                    c.AppearanceSourcePlayer = invoker.Name;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenNpc.ApplyAppearanceFrom failed: " + ex.Message);
            }
        }

        /// <summary>Readable appearance summary for commands/reports.</summary>
        public string AppearanceSummary
        {
            get
            {
                if (string.IsNullOrEmpty(AppearanceSourcePlayer)) return "default mannequin";
                return $"mirrors '{AppearanceSourcePlayer}' (gender {AppearanceGender}, uma {(!string.IsNullOrEmpty(AppearanceUmaDefinition) ? "set" : "none")})";
            }
        }

        /// <summary>Visual name: name + life-stage suffix + profession tag.</summary>
        public static string MakeHeadline(Citizen c)
        {
            string stage = c.LifeStage switch
            {
                "Child" => " Jr.",
                "Elder" => " Sr.",
                _ => ""
            };
            string prof = string.IsNullOrEmpty(c.Profession) ? "" : " - " + c.Profession;
            return c.Name + stage + prof;
        }

        public bool HasWorkplace => WorkX != 0 || WorkY != 0 || WorkZ != 0;

        public void SetWorkplace(Vector3 pos)
        {
            WorkX = pos.X;
            WorkY = pos.Y;
            WorkZ = pos.Z;
        }

        /// <summary>
        /// Decides a fresh independent goal for this NPC based on its needs and the
        /// day/night phase. Called from the slow simulation tick; the high-frequency
        /// movement loop only executes BehaviorTick so every NPC walks towards its
        /// own target instead of teleporting to a shared spot.
        /// </summary>
        public void ComputeGoal(Citizen c, SimulationLogic.DayPhase phase, IList<Vector3> storePositions)
        {
            double now = NowUtc();

            // night: always head home to sleep
            if (phase == SimulationLogic.DayPhase.Night)
            {
                SetGoal("Home", HomeX, HomeY, HomeZ);
                return;
            }

            // cooldown: keep the current goal until NextWanderUtc passes
            if (now < NextWanderUtc) return;

            // hungry: walk to the nearest store that sells food
            if (c.Calories < 65 && storePositions != null && storePositions.Count > 0)
            {
                var target = NearestStore(storePositions);
                SetGoal("Shop", target.X, target.Y, target.Z);
                NextWanderUtc = (float)(now + 180.0 + _rng.NextDouble() * 120.0);
                return;
            }

            // working day: commute to the workplace
            if (phase == SimulationLogic.DayPhase.Day && HasWorkplace)
            {
                SetGoal("Work", JitterPoint(WorkX, WorkY, WorkZ, 3.0f));
                NextWanderUtc = (float)(now + 240.0 + _rng.NextDouble() * 120.0);
                return;
            }

            // lonely: socialise on the square near home
            if (c.Social < 40)
            {
                SetGoal("Social", JitterPoint(HomeX, HomeY, HomeZ, 15.0f));
                NextWanderUtc = (float)(now + 120.0 + _rng.NextDouble() * 60.0);
                return;
            }

            // default: wander around home
            SetGoal("Wander", JitterPoint(HomeX, HomeY, HomeZ, 25.0f));
            NextWanderUtc = (float)(now + 60.0 + _rng.NextDouble() * 90.0);
        }

        /// <summary>
        /// Step-wise movement towards GoalPos: moves a small fixed step (0.3~0.6 m)
        /// each call so the client sees smooth walking instead of teleport jumps.
        /// The high-frequency MoveLoop calls this roughly twice a second. Arrives
        /// (distance &lt; step) it settles, tries to harvest if the goal was a gather
        /// target, and records LastX/Y/Z. Move failures are logged once to avoid spam.
        /// </summary>
        public void BehaviorTick()
        {
            var goal = GoalPos;
            var current = Position;
            float dist = Vector3.Distance(current, goal);
            float step = 0.3f + (float)(_rng.NextDouble() * 0.3f);

            if (dist < step)
            {
                LastX = goal.X;
                LastY = goal.Y;
                LastZ = goal.Z;
                LastMoveFailed = false;
                // Arrived at a gather target: harvest the nearby resource in-place
                // (real items into the backpack). If harvesting is still possible the
                // routine re-targets the next resource; otherwise idle-wander resumes.
                if (GoalType == "Gather")
                {
                    if (CitizenRoutine.TryHarvest(this, null, null))
                        return; // still gathering - keep the goal until resources run out
                }
                // Arrived: immediately pick a fresh nearby idle goal so the NPC never
                // stands still waiting for the slow 30-min simulation tick. The slow
                // tick (ComputeGoal) can still override this with business goals
                // (night home / shop / work / social) once its cooldown passes.
                PickIdleGoal();
                return;
            }

            var dir = Vector3.Normalize(goal - current);
            var next = current + dir * step;
            try
            {
                MoveWorldObjectTo(next, Quaternion.Identity);
                LastX = next.X;
                LastY = next.Y;
                LastZ = next.Z;
                LastMoveFailed = false;
            }
            catch (Exception ex)
            {
                if (!LastMoveFailed)
                {
                    PluginLog.Warn("CitizenNpc move failed: " + ex.Message);
                    LastMoveFailed = true;
                }
            }
        }

        /// <summary>
        /// Picks a fresh idle-wander goal 15~30 m from the current position at a
        /// random angle (Y stays at the current height). Called immediately on
        /// arrival so NPCs keep moving between slow business-goal ticks. The short
        /// NextWanderUtc cooldown prevents the slow tick from instantly overriding
        /// the idle walk while still allowing night/shop/work goals to take over.
        /// </summary>
        private void PickIdleGoal()
        {
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            double radius = 15.0 + _rng.NextDouble() * 15.0; // 15..30 m
            var cur = Position;
            SetGoal("Wander",
                cur.X + (float)(Math.Cos(angle) * radius),
                cur.Y,
                cur.Z + (float)(Math.Sin(angle) * radius));
            NextWanderUtc = (float)(NowUtc() + 30.0 + _rng.NextDouble() * 30.0);
        }

        public Vector3 GoalPos => new Vector3(GoalX, GoalY, GoalZ);

        /// <summary>
        /// Routine actions called from the high-frequency movement loop, throttled
        /// internally: real-resource gathering and camp-storage usage. c may be null
        /// (the loop only has the world object at hand; ledger stats are then skipped
        /// but real inventory actions still happen).
        /// </summary>
        public void RoutineTick(Citizen c, User creator)
        {
            double now = NowUtc();
            if (now < NextActionUtc) return;
            NextActionUtc = (float)(now + 30.0 + _rng.NextDouble() * 15.0);
            try
            {
                CitizenRoutine.Perform(this, c, creator);
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"{CitizenName}: RoutineTick failed: {ex.Message}");
            }
        }

        /// <summary>Points the NPC at a real resource to gather (day-time work).</summary>
        public void SetGatherGoal(Vector3 pos)
        {
            SetGoal("Gather", pos.X, pos.Y, pos.Z);
        }

        private void SetGoal(string type, float x, float y, float z)
        {
            GoalType = type;
            GoalX = x;
            GoalY = y;
            GoalZ = z;
        }

        private void SetGoal(string type, Vector3 pos)
        {
            SetGoal(type, pos.X, pos.Y, pos.Z);
        }

        private Vector3 JitterPoint(float x, float y, float z, float radius)
        {
            float jx = (float)((_rng.NextDouble() - 0.5) * 2.0 * radius);
            float jz = (float)((_rng.NextDouble() - 0.5) * 2.0 * radius);
            return new Vector3(x + jx, y, z + jz);
        }

        private Vector3 NearestStore(IList<Vector3> storePositions)
        {
            Vector3 current = Position;
            Vector3 best = storePositions[0];
            float bestDist = float.MaxValue;
            foreach (var s in storePositions)
            {
                float d = Vector3.Distance(current, s);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = s;
                }
            }
            return best;
        }

        private static double NowUtc()
            => DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }
}
