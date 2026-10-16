using System;
using System.Collections.Generic;

namespace EcoSimCitizens
{
    /// <summary>
    /// Skill growth for citizens. Each citizen accumulates experience in its
    /// profession; levels raise productivity up to a cap of 10. The math lives in
    /// SimulationLogic so the standalone tests can verify it without Eco.
    /// </summary>
    public static class CitizenSkills
    {
        public static int GetLevel(Citizen c)
        {
            if (c == null || c.SkillLevels == null) return 0;
            string key = SkillKey(c);
            return c.SkillLevels.TryGetValue(key, out int v) ? v : 0;
        }

        public static double GetExperience(Citizen c)
        {
            if (c == null || c.SkillExperience == null) return 0;
            string key = SkillKey(c);
            return c.SkillExperience.TryGetValue(key, out double v) ? v : 0.0;
        }

        public static void EnsureSkill(Citizen c)
        {
            if (c == null) return;
            string key = SkillKey(c);
            if (c.SkillExperience == null) c.SkillExperience = new Dictionary<string, double>();
            if (c.SkillLevels == null) c.SkillLevels = new Dictionary<string, int>();
            if (!c.SkillExperience.ContainsKey(key)) c.SkillExperience[key] = 0.0;
            if (!c.SkillLevels.ContainsKey(key)) c.SkillLevels[key] = 0;
        }

        /// <summary>
        /// Advances the citizen's profession skill with the tick's work output.
        /// Returns the number of levels gained (usually 0 or 1).
        /// </summary>
        public static int TickSkill(Citizen c, double output, double learningRate)
        {
            EnsureSkill(c);
            string key = SkillKey(c);
            double exp = c.SkillExperience[key];
            int level = c.SkillLevels[key];
            int gained = SimulationLogic.AdvanceSkill(ref exp, ref level, output, learningRate);
            c.SkillExperience[key] = exp;
            c.SkillLevels[key] = level;
            if (gained > 0)
            {
                CitizenSocial.AddTrail(c, $"skilled up to {c.Profession} level {level}");
                PluginLog.Info($"{c.Name} reached {c.Profession} level {level}.");
            }
            return gained;
        }

        /// <summary>Combined productivity multiplier: profession base x skill level bonus x trait.</summary>
        public static double EffectiveProductivity(Citizen c)
        {
            double prof = Professions.Get(c.Profession).Productivity;
            double skill = SimulationLogic.SkillProductivity(GetLevel(c));
            double trait = Traits.WorkMultiplier(c.Trait);
            return prof * skill * trait;
        }

        private static string SkillKey(Citizen c)
        {
            string p = c.Profession;
            return string.IsNullOrEmpty(p) ? "Laborer" : p;
        }
    }

    /// <summary>
    /// Social trail helper: keeps a citizen's recent social events, newest first,
    /// capped at a configurable maximum so the JSON state stays bounded.
    /// </summary>
    public static class CitizenSocial
    {
        public static int MaxTrail { get; set; } = 8;

        public static void AddTrail(Citizen c, string message)
        {
            if (c == null || string.IsNullOrEmpty(message)) return;
            if (c.SocialTrail == null) c.SocialTrail = new List<string>();
            c.SocialTrail.Insert(0, message);
            if (c.SocialTrail.Count > MaxTrail)
            {
                c.SocialTrail.RemoveRange(MaxTrail, c.SocialTrail.Count - MaxTrail);
            }
        }

        public static void Trim(Citizen c)
        {
            if (c == null || c.SocialTrail == null) return;
            if (c.SocialTrail.Count > MaxTrail)
            {
                c.SocialTrail.RemoveRange(MaxTrail, c.SocialTrail.Count - MaxTrail);
            }
        }
    }
}
