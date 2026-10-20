namespace EcoSimCitizens
{
    /// <summary>
    /// v0.8: registry entry for a WorldObject placed by the mod on behalf of an
    /// NPC (starter camp, workbench, tool bench, campfire, stockpile and any
    /// future mod-built structure). Persisted in citizens.json so:
    ///   - /npccmd despawnall can clear ALL NPC-built facilities/structures at
    ///     any distance (not just the old 18 m neighbourhood) while player-built
    ///     objects are never touched;
    ///   - /npccmd fixfloating can locate and correct old floating objects.
    /// </summary>
    public class PlacedObject
    {
        /// <summary>WorldObject.ID string of the placed object.</summary>
        public string WoId { get; set; }

        /// <summary>CLR type name, e.g. StarterCampObject / WorkbenchObject.</summary>
        public string TypeName { get; set; }

        /// <summary>Citizen the object belongs to (OwnerNpcName).</summary>
        public string OwnerNpcName { get; set; }

        /// <summary>True for buildings/structures (e.g. adobe house), false for facilities.</summary>
        public bool IsStructure { get; set; }

        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        /// <summary>Unix seconds when the object was placed (UTC).</summary>
        public long PlacedAtUnix { get; set; }
    }
}
