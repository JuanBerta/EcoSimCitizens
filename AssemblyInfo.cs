using System.Runtime.Versioning;

// Eco's server assemblies (Eco.Gameplay.dll) are annotated as Windows-only.
// Declaring this mod as Windows-supported silences platform-compatibility
// analyzer warnings (CA1416) for ChatCommand / ChatSubCommand usage.
[assembly: SupportedOSPlatform("windows")]
