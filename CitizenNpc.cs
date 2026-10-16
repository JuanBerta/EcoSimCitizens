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
    /// The Eco engine instantiates this type through the serializer; behaviour is
    /// driven by SimulationCore so it stays on the plugin thread.
    /// </summary>
    [Serialized]
    public class CitizenNpc : WorldObject
    {
        [Serialized] public string CitizenName;
        [Serialized] public string ProfessionSkill;
        [Serialized] public float HomeX;
        [Serialized] public float HomeY;
        [Serialized] public float HomeZ;
        [Serialized] public float NextWanderUtc;

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
            NextWanderUtc = 0;
            try { SetName(CitizenName); } catch { }
        }

        /// <summary>Periodic per-tick behaviour: citizens wander near their home.</summary>
        public void BehaviorTick(Citizen c)
        {
            double now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            if (now < NextWanderUtc) return;

            NextWanderUtc = (float)(now + 15 + _rng.NextDouble() * 30);
            float x = HomeX + (float)((_rng.NextDouble() - 0.5) * 8.0);
            float y = HomeY;
            float z = HomeZ + (float)((_rng.NextDouble() - 0.5) * 8.0);
            try
            {
                MoveWorldObjectTo(new Vector3(x, y, z), Quaternion.Identity);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenNpc wander failed: " + ex.Message);
            }
        }
    }
}
