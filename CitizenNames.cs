using System;

namespace EcoSimCitizens
{
    /// <summary>
    /// Random citizen name generator. Picks a first name and a surname from the
    /// built-in pools and produces a unique, human-readable name for a given
    /// database. Used by /npccmd fill so every batch-spawned NPC gets a distinct
    /// name instead of a placeholder.
    /// </summary>
    public static class CitizenNames
    {
        private static readonly string[] FirstNames =
        {
            "Aldous", "Briar", "Callum", "Dahlia", "Emeric", "Fenna", "Garrick", "Hazel", "Ivor", "Juniper",
            "Kellan", "Liora", "Maren", "Nico", "Odessa", "Piers", "Quill", "Rosalind", "Soren", "Tessa",
            "Ulric", "Vesper", "Wren", "Xander", "Yara", "Zephyr", "Abner", "Beatrix", "Casper", "Delia",
            "Elias", "Faye", "Gideon", "Hattie", "Isaac", "Jorah", "Kira", "Leander", "Mira", "Nell",
            "Oswin", "Petra", "Quentin", "Rowan", "Sylvie", "Thane", "Una", "Vaughn", "Willow", "Ezra",
        };

        private static readonly string[] Surnames =
        {
            "Ashford", "Bracken", "Caldwell", "Dunmore", "Ellis", "Farrow", "Greaves", "Holloway", "Ingram", "Joplin",
            "Kessler", "Larkin", "Mercer", "Newcomb", "Osborne", "Pemberton", "Quincy", "Ravenscroft", "Sterling", "Thornwood",
            "Underhill", "Vance", "Whitlock", "Yarrow", "Zimmer", "Aldridge", "Barnes", "Crane", "Dawson", "Everly",
            "Fletcher", "Graham", "Hale", "Irving", "Joyce", "Kane", "Lang", "Marsh", "Nolan", "Owens",
            "Pierce", "Quinn", "Reed", "Sawyer", "Tate", "Vaughan", "Wade", "Young", "Brooks", "Cole",
        };

        public static string RandomName(Random rng)
        {
            string first = FirstNames[rng.Next(FirstNames.Length)];
            string last = Surnames[rng.Next(Surnames.Length)];
            return $"{first} {last}";
        }

        /// <summary>Returns a random name that is not already used in the database.</summary>
        public static string UniqueRandomName(CitizensDatabase db, Random rng)
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                string name = RandomName(rng);
                if (db.Find(name) == null) return name;
            }
            // Practically unreachable with the pool above; guarded fallback so fill
            // can never stall on name collisions.
            for (int i = 1; i < 1000; i++)
            {
                string name = $"Citizen {i}";
                if (db.Find(name) == null) return name;
            }
            throw new InvalidOperationException("No unique citizen name available.");
        }
    }
}
