using System;
using System.Numerics;
using Eco.Shared.Math;
using Eco.World;

namespace EcoSimCitizens
{
    /// <summary>
    /// Snaps an XZ position to the terrain surface using the live world's top
    /// solid block, fixing the flying-spawn bug where NPCs and mod-placed objects
    /// inherited an in-air Y (world-centre height, tree-top height, etc.).
    ///
    /// Placement semantics match the game's own pattern:
    /// topBlockPos = World.GetTopBlockPos(xz); object sits at topBlockPos + Up
    /// (block coordinate), which maps to a world Y of top.Y + 1.5 (block face plus
    /// half the object's voxel height).
    ///
    /// The original position is returned unchanged when sampling is unavailable or
    /// fails, so spawns never silently break - the caller's existing fallback
    /// logic still applies.
    /// </summary>
    public static class GroundSampler
    {
        /// <summary>
        /// Returns a copy of <paramref name="pos"/> with Y snapped to the terrain
        /// surface under its XZ, preserving X/Z continuity.
        /// </summary>
        public static Vector3 SnapToGround(Vector3 pos)
        {
            try
            {
                var xz = new Vector2i((int)Math.Floor(pos.X), (int)Math.Floor(pos.Z));
                Vector3i top = World.GetTopBlockPos(xz);
                if (top.Y < 0) return pos; // no terrain sampled yet - keep original
                float groundY = top.Y + 1.5f;
                return new Vector3(pos.X, groundY, pos.Z);
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"GroundSampler.SnapToGround failed at {pos}: {ex.Message}");
                return pos;
            }
        }
    }
}
