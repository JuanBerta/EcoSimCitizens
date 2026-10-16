using System;
using System.Collections.Generic;

namespace EcoSimCitizens
{
    /// <summary>
    /// PRD Module 1 (autonomous agent core) + Module 7 (admin telemetry).
    ///
    /// Governs how many citizens are allowed to exist as physical NPCs in the
    /// world at once. Citizens above the cap keep simulating logically (work,
    /// economy, needs) but are not spawned as WorldObjects, which keeps server
    /// load bounded on big populations.
    ///
    /// Hybrid pathfinding (PRD Req.1): when a citizen has a physical NPC and the
    /// player is in render range, the NPC moves in the world (v0.3 BehaviorTick);
    /// otherwise the agent travels by math. This class tracks how often each
    /// branch is used so admins can see the performance trade-off.
    /// </summary>
    public class AgentPoolManager
    {
        public int MaxActiveNpcs { get; set; } = 30;

        // hybrid pathfinding counters
        public long PhysicalMoveTicks { get; private set; }
        public long LogicalMoveTicks { get; private set; }
        public long TotalSpawnAttempts { get; private set; }
        public long SpawnBlockedByCap { get; private set; }

        private readonly HashSet<string> _active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int ActiveNpcCount => _active.Count;

        /// <summary>True when this citizen may occupy a physical NPC slot.</summary>
        public bool CanSpawn(string citizenName)
        {
            TotalSpawnAttempts++;
            if (_active.Contains(citizenName)) return true;
            if (_active.Count >= MaxActiveNpcs)
            {
                SpawnBlockedByCap++;
                return false;
            }
            _active.Add(citizenName);
            return true;
        }

        public void Release(string citizenName) => _active.Remove(citizenName);

        /// <summary>Records which pathfinding branch was used this tick.</summary>
        public void NoteMove(bool physical)
        {
            if (physical) PhysicalMoveTicks++;
            else LogicalMoveTicks++;
        }

        /// <summary>Fraction of movement ticks that used real world pathfinding (0..1).</summary>
        public double PhysicalPathfindingRatio()
        {
            long total = PhysicalMoveTicks + LogicalMoveTicks;
            return total <= 0 ? 0 : (double)PhysicalMoveTicks / total;
        }

        public void ResetStats()
        {
            PhysicalMoveTicks = 0;
            LogicalMoveTicks = 0;
            TotalSpawnAttempts = 0;
            SpawnBlockedByCap = 0;
        }
    }
}
