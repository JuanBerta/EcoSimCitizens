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

        /// <summary>
        /// v1.2: direction-aware landing for /fixfloating. Unlike SnapToGround
        /// (which follows the column's highest block and can therefore drag an
        /// object UP onto a roof or a player-built floor), this method only looks
        /// BELOW the object:
        ///   - if the object's own voxel is embedded in a solid block (buried), it
        ///     walks upward to the top of that solid column and surfaces there;
        ///   - otherwise it scans straight down from the object's feet and lands on
        ///     the first solid support it finds (terrain, a roof, any floor), i.e.
        ///     floating objects move DOWN to contact ground;
        ///   - when no support exists below (over water / void) the position is
        ///     returned unchanged so the object is never dragged sideways or up.
        /// This converges: a correctly placed object's Y equals support+1.5, so
        /// repeated runs never lift it higher (the old fixfloating bug).
        /// </summary>
        public static Vector3 SnapToGroundDown(Vector3 pos)
        {
            try
            {
                int x = (int)Math.Floor(pos.X);
                int cur = (int)Math.Floor(pos.Y);
                int z = (int)Math.Floor(pos.Z);

                // buried / embedded: the voxel at the object's centre is solid.
                // Surface at the highest solid voxel of this column (>= centre).
                if (World.IsSolidOrOccupied(new Vector3i(x, cur, z)))
                {
                    int top = cur;
                    while (top < 255 && World.IsSolidOrOccupied(new Vector3i(x, top + 1, z))) top++;
                    float surfY = top + 1.5f;
                    return new Vector3(pos.X, surfY, pos.Z);
                }

                // floating / on ground: first solid support strictly below the feet.
                for (int scanY = cur - 1; scanY >= 0; scanY--)
                {
                    if (!World.IsSolidOrOccupied(new Vector3i(x, scanY, z))) continue;
                    float groundY = scanY + 1.5f;
                    return new Vector3(pos.X, groundY, pos.Z);
                }

                // no support found below - keep the object where it is
                return pos;
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"GroundSampler.SnapToGroundDown failed at {pos}: {ex.Message}");
                return pos;
            }
        }
    }
}
