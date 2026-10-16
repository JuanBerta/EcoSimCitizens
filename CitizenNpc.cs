using System;
using System.Numerics;
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
        [Serialized] public string Headline;

        private readonly Random _rng = new Random();

        public CitizenNpc()
        {
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
            Headline = MakeHeadline(citizen);
            try { SetName(Headline); } catch { }
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
        /// Periodic per-tick behaviour driven by the simulated day/night phase:
        /// Day - at work, Dawn/Dusk - travelling, Night - asleep at home.
        /// Movement is guarded and only issued when the NPC is far from the target.
        /// </summary>
        public void BehaviorTick(Citizen c, SimulationLogic.DayPhase phase)
        {
            var target = TargetFor(c, phase);
            var last = new Vector3(LastX, LastY, LastZ);
            if (Vector3.Distance(last, target) < 0.6f) return;

            try
            {
                MoveWorldObjectTo(target, Quaternion.Identity);
                LastX = target.X;
                LastY = target.Y;
                LastZ = target.Z;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenNpc move failed: " + ex.Message);
            }
        }

        private Vector3 TargetFor(Citizen c, SimulationLogic.DayPhase phase)
        {
            switch (phase)
            {
                case SimulationLogic.DayPhase.Day:
                    return HasWorkplace ? Jitter(WorkX, WorkY, WorkZ, 3.0f) : Jitter(HomeX, HomeY, HomeZ, 4.0f);
                case SimulationLogic.DayPhase.Dawn:
                case SimulationLogic.DayPhase.Dusk:
                    // commuting: wander around home while transitioning
                    return Jitter(HomeX, HomeY, HomeZ, 6.0f);
                default:
                    // night: sleeping at home
                    return new Vector3(HomeX, HomeY, HomeZ);
            }
        }

        private Vector3 Jitter(float x, float y, float z, float radius)
        {
            float jx = (float)((_rng.NextDouble() - 0.5) * 2.0 * radius);
            float jz = (float)((_rng.NextDouble() - 0.5) * 2.0 * radius);
            return new Vector3(x + jx, y, z + jz);
        }
    }
}
