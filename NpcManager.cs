using System;
using System.Collections.Generic;
using System.Numerics;
using Eco.Core;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Players;
using Eco.Shared.Math;
using Quaternion = Eco.Shared.Math.Quaternion;

namespace EcoSimCitizens
{
    /// <summary>
    /// Bridges citizens and their live WorldObject NPCs: spawn, lookup and despawn.
    /// Spawning requires a real in-game User as creator (the admin issuing the
    /// command), so no fake player accounts are created.
    /// </summary>
    public static class NpcManager
    {
        public static CitizenNpc Spawn(Citizen citizen, User creator, Vector3 pos)
        {
            try
            {
                CitizenNpc created = null;
                WorldObjectManager.ForceAdd(
                    typeof(CitizenNpc),
                    creator,
                    pos,
                    Quaternion.Identity,
                    false,
                    null,
                    wo =>
                    {
                        created = wo as CitizenNpc;
                        created?.Bind(citizen, pos);
                    },
                    null);
                return created;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("NpcManager.Spawn failed: " + ex.Message);
                return null;
            }
        }

        public static CitizenNpc Find(string citizenName)
        {
            foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
            {
                if (wo is CitizenNpc npc && string.Equals(npc.CitizenName, citizenName, StringComparison.OrdinalIgnoreCase))
                    return npc;
            }
            return null;
        }

        public static IEnumerable<CitizenNpc> GetAll()
        {
            foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
            {
                if (wo is CitizenNpc npc && !npc.IsDestroyed) yield return npc;
            }
        }

        public static void DestroyFor(Citizen citizen)
        {
            if (citizen == null) return;
            var npc = Find(citizen.Name);
            if (npc == null) return;
            try
            {
                npc.Destroy();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("NpcManager.DestroyFor failed: " + ex.Message);
            }
        }
    }
}
