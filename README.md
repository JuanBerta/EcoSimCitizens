# EcoSim Citizens

An autonomous citizen simulation mod for **Eco Global Survival (Eco 0.14)**. It populates your server with **simulated citizens** that live in the world as real NPCs, work real jobs, gather real resources, run their own economy, and react to your player-driven world — a living backdrop that keeps the server feeling populated even between player sessions.

> Built against Eco.ReferenceAssemblies `0.14.2-beta-release-1099` (net10.0). Referenced API surface is verified against the official assemblies; see [docs.play.eco](https://docs.play.eco/).

## What it does today

EcoSim Citizens is a **server-side mod** (no client bundle is required for pure C# changes). Citizens are **real in-game WorldObjects**, not chat-bot placeholders:

- **Real NPCs in the world** — each citizen is a `CitizenNpc : WorldObject` with a physical position, a clickable profile, a headline (name / life stage / profession), and a **minimap marker** (MinimapComponent, category "Citizens") that tracks them on the in-game M-key map. NPCs **move smoothly** through the world (`MoveWorldObjectTo`) toward an independent per-NPC goal (home / work / shop / social / wander), commute to work during the day and sleep at home at night.
- **Automatic generation & top-up** — on server start and on every simulation tick the pool is auto-topped-up to the `MaxActiveNpcs` cap, so the configured number of physical citizens always exists. `/npccmd fill` batch-spawns random citizens (random names from culture pools + random real professions) up to the cap.
- **Real work & real gathering** — `CitizenProduction` puts produced goods into the NPC's **backpack / camp chest** (not a hidden ledger); during the day `CitizenRoutine.TryHarvest` walks the NPC up to a real world resource (trees, bushes, crops, stone…) and harvests the matching real item (log / plant fiber / stone / dirt…) straight into its inventory (`SetGatherGoal` keeps the NPC working a spot).
- **Starter kit & first settlement** — `SurvivalKit` grants each citizen the same starter kit a fresh player receives (Starter Camp, Claim Papers, Claim Tool, Stone Axe, Wooden Shovel, Stone Hammer, Stone Pickaxe), places a **Starter Camp + Workbench + Tool Bench** that never overlap the camp interior, and keeps the camp chest synced with the NPC backpack.
- **An autonomous shop economy** — `SimStore` / `SimStoreManager` give citizens a full closed loop: they **open their own stores, price adaptively, restock from their own production, buy food from other citizens' stores**, upgrade/close shops by profitability, all settled **NPC-account to NPC-account** (simulated settlement) so the real player stores stay player-only.
- **Native economy integration** — `NativeCurrencyBridge` registers settlement currencies as real Eco currencies (`CurrencyManager.AddCurrency`) and NPC bank accounts through `BankAccountManager.Registrar`, so NPC money is visible in the native banking system (degrades to ledger-only when no user/registrar is available).
- **Richer simulation layers** — professions & traits, needs (calories/comfort/social), skills with XP/levels, commuting, settlements & voting, life cycle (age/death), autonomous commerce agents, corporations & dividends, stock exchange & futures, settlement currencies & forex, work parties, construction plans, research contributions, environment tracker & meteor crisis, politics engine, contextual dialogue, JSON telemetry and a daily report broadcast.

## In-game commands

Two root keys: `/ecosimulator` (citizen simulation, user-facing) and `/npccmd` (NPC/pool administration). Root names are global and case-sensitive — chosen to avoid clashing with core Eco commands (`remove`, `give`, `spawn`).

### `/ecosimulator` — citizen simulation

| Command | Access | Description |
| --- | --- | --- |
| `/ecosimulator` | User | Status popup (population, happiness, market, civic score) |
| `/ecosimulator report` | User | Full daily report in chat |
| `/ecosimulator list` | User | List all citizens with profession, trait, happiness, ledger, home |
| `/ecosimulator professions` | User | List the real in-game professions available to citizens |
| `/ecosimulator citizens` | User | Detailed citizen overview (skills, production, life stage, civics) |
| `/ecosimulator citizeninfo <name>` | User | Full profile of one citizen |
| `/ecosimulator trail <name>` | User | Show a citizen's recent social trail |
| `/ecosimulator spawncitizen <name> <profession>` | Admin | Spawn a citizen as a real world NPC |
| `/ecosimulator removecitizen <name>` | Admin | Remove a citizen (despawns the NPC too) |
| `/ecosimulator givecash <name> <amount>` | Admin | Credit a citizen's ledger |
| `/ecosimulator citizenhome <name> <deed>` | Admin | Assign a deed as the citizen's home |
| `/ecosimulator citizensave` | Admin | Force-save citizen state & config |
| `/ecosimulator citizenconfig` | Admin | Show current config values |

### `/npccmd` — NPC & simulation admin

| Command | Access | Description |
| --- | --- | --- |
| `/npccmd` | Admin | Command overview |
| `/npccmd limit <int>` | Admin | Set the max number of physical NPC citizens (triggers top-up) |
| `/npccmd pool` | Admin | Agent pool + hybrid pathfinding stats |
| `/npccmd npcs` | User | List all world NPCs (name, profession, position, distance from you) |
| `/npccmd locate <name>` | User | Live position + distance of one NPC (re-run to refresh) |
| `/npccmd fill` | Admin | Batch-spawn random NPCs up to the pool cap |
| `/npccmd stats [settlement]` | Admin | JSON telemetry for the simulation |
| `/npccmd company create <name> <capital> \| list \| issue <corp> <name> <shares> \| vote <corp> <choice> \| resolve <corp> \| hire <corp> <citizen> <wage> \| dividends <corp> <amount>` | Admin | Corporation management |
| `/npccmd stock list \| addfuture <commodity> <base> \| buy <citizen> <commodity>` | Admin | Stock exchange & futures |
| `/npccmd currency list \| mint <settlement> <symbol> <reserve> \| rate <from> <to> \| convert <citizen> <from> <to> <amount>` | Admin | Settlement currencies & forex |
| `/npccmd party create <name> <task> <pool> <target> \| list \| join <citizen> <party> \| leave <citizen>` | Admin | Work parties |
| `/npccmd construct list \| status <citizen> \| plan <citizen>` | Admin | Construction plans |
| `/npccmd research` | Admin | Research hub status |
| `/npccmd env get \| set <pollution> <co2> <soil> \| cleanup <amount> \| meteor <power> <resources> \| impact <day>` | Admin | Environment & meteor crisis |
| `/npccmd dialogue <citizen>` | Admin | Show a citizen's recent dialogue |

## Installation & deployment

1. **Build** the mod DLL: `dotnet build -c Release` (produces `EcoSimCitizens.dll`, 0 errors expected; the mod targets net10.0 + Eco.ReferenceAssemblies `0.14.2-beta-release-1099`).
2. **Deploy** the built `EcoSimCitizens.dll` into your Eco server's mods folder, e.g. `Eco_Data\Server\Mods\EcoSimCitizens\` (next to it the mod will create `config.json` and `citizens.json` at runtime).
3. **Restart** the server. The plugin appears in the plugins list; on first start it writes a build marker (`build_marker.log`), a debug tick log (`debug_tick.log`) and tops up the NPC pool to the configured cap.
4. Verify: `Initializing Finished` in the server log, then `/npccmd npcs` to see world NPCs and `/ecosimulator` for the status popup.

**Client bundle** (only when the in-game NPC representation or map assets change): the client side is built with **Unity 6000.3.6f1** as the editor and the asset bundle is built with **Unity 6000.0.32f1**. Pure server-side C# changes (the normal case) do **not** require rebuilding the bundle — deploy the DLL only.

## Configuration

`EcoSimCitizens/config.json` is created next to the DLL on first run (or can be created manually). Key settings:

```json
{
  "TickIntervalMinutes": 30.0,
  "MaxCitizens": 50,
  "PoolLimit": 30,
  "ProductionEnabled": true,
  "SimShopsEnabled": true,
  "SimShopMaxPerOwner": 2,
  "SimShopBaseMargin": 0.25,
  "ShopOpenCost": 30.0,
  "SaveThrottleSeconds": 30.0,
  "BroadcastDailyReport": true
}
```

All plain numbers so server admins can tune the simulation without recompiling. The full set (needs, economy, commerce, parties, construction, research, corporations, exchange, currencies, environment, politics, dialogue…) is written by the mod on first run — see `ModConfig.cs` for every option.

## Known limitations & design decisions

- **NPCs are WorldObjects, not Users** — Eco has no native "NPC user". Citizens therefore cannot create real deeds, claim property, or open real stores (those need a `User` identity / `Deed` / `PropertyAuthComponent` / `ForSaleComponent`). `SurvivalKit` only hands out the claim papers/tool and places the physical workstations; deed and real-store creation stays out of scope.
- **NPC economy runs on simulated stores** — NPC↔NPC trades settle between NPC bank accounts (`BankAccount`-to-`BankAccount`) and never call `StoreComponent.PerformTrade`, keeping real player stores player-only. `MarketScanner.IsTradableStore` gates price indexing to genuinely tradable stores so the player market index is not polluted.
- **Graceful degradation** — if no user is available to register native currencies/accounts, NPC trades fall back to ledger-only simulation (no crash, no missing features beyond native visibility).
- **No item duplication** — starter kits, camps and workstations are idempotent: existing items are never duplicated and already-placed objects are never placed twice.
- **NPC harvesting is throttled** (~every 30s per NPC, day only; NPCs sleep at night) so the world does not get strip-mined by the simulation.

## Persistence

Citizen state, simulated stores and config survive restarts as JSON next to the mod DLL:

- `citizens.json` lives **directly next to the DLL** (no extra nested folder), written **atomically** (tmp file + replace) so a crash never leaves a half-written state.
- Rapid per-tick changes are batched behind a throttled save (`SaveThrottleSeconds`); shutdown and explicit `/ecosimulator citizensave` flush immediately.
- Loading runs a **three-way reconcile** between persisted citizens, world NPCs and `NpcId` anchors, so NPCs that vanished from the world are re-spawned and orphans are re-linked after a server restart.

## Version note

The assembly version (`EcoSimCitizens.csproj` `<Version>0.2.0`) has not been bumped through the feature stages; the in-code feature stages are v0.2 (real world NPCs) → v0.3 (production, skills, commuting, civics, lifecycle) → v0.4 (commerce, agent pool, construction, corporations, forex, ecology, dialogue, telemetry) → v0.5 (independent per-NPC goals + simulated shop economy), followed by the defect-fix batch (camp/workbench placement, camp chest sync, real gathering, smooth movement, native currency bridge, tradable-store filtering, persistence hardening). This README intentionally describes the actual shipped state rather than a single feature-stage label.

## Project structure

```
EcoSimCitizens/
├── EcoSimCitizens.csproj      net10.0 + Eco.ReferenceAssemblies 0.14.2
├── EcoSimCitizensPlugin.cs    SimplePlugin + IInitializablePlugin/IShutdownablePlugin, auto top-up, move loop
├── ModConfig.cs               runtime config (JSON, all plain numbers)
├── AssemblyInfo.cs            Windows platform declarations (CA1416)
├── PluginLog.cs               logging shim
├── Simulation
│   ├── CitizenModel.cs        Citizen + profession/trait tables
│   ├── CitizenNames.cs        culture-pool name generation
│   ├── SimulationCore.cs      per-tick simulation + report builder
│   ├── SimulationLogic.cs     pure decision logic (unit-tested)
│   ├── SimulationState.cs     simulation state containers
│   └── CitizensDatabase.cs    thread-safe store + atomic JSON persistence + 3-way reconcile
├── Citizens
│   ├── CitizenNpc.cs          real-world NPC behaviour (WorldObject, minimap, goals)
│   ├── CitizenRoutine.cs      real gathering, camp storage sync
│   ├── SurvivalKit.cs         starter kit, camp + workstations, camp chest
│   ├── NpcManager.cs          NPC spawn/despawn
│   ├── AgentPoolManager.cs    agent pool + hybrid pathfinding
│   ├── CitizenEconomy.cs      earn & spend
│   ├── CitizenProduction.cs   production & sales ledger (into backpack/camp chest)
│   ├── CitizenSkills.cs       skill xp & levels
│   ├── CitizenCivics.cs       voting & legal preferences
│   ├── CitizenCommute.cs      commuting to workplaces
│   └── CitizenLifecycle.cs    day/night, age, death
├── Systems
│   ├── SimStore.cs            simulated NPC store (mod-internal settlement)
│   ├── SimStoreManager.cs     open/price/restock/upgrade/close store loop
│   ├── NativeCurrencyBridge.cs native currency + NPC bank-account registration
│   ├── AgentCommerce.cs       autonomous commerce agents
│   ├── WorkPartySystem.cs     work parties & contracts
│   ├── ConstructionPlanner.cs construction plans
│   ├── CorporationSystem.cs   corporations, shares & dividends
│   ├── StockExchangeSystem.cs stock exchange & futures
│   ├── CurrencySystem.cs      settlement currencies & forex
│   ├── ResearchContribution.cs research hubs
│   ├── EnvironmentTracker.cs  pollution/CO2/soil & meteor crisis
│   ├── PoliticsEngine.cs      elections & politics
│   └── DialogueTree.cs        contextual dialogue
├── Observers
│   ├── MarketScanner.cs       reads real stores/TradeOffers (read-only; IsTradableStore gate)
│   ├── CivicObserver.cs       deeds/settlements/elections snapshot
│   ├── HousingEvaluator.cs    deed -> comfort mapping
│   └── TelemetryService.cs    JSON telemetry
└── Commands
    ├── EcoSimCommands.cs      /ecosimulator chat commands
    └── NpcCommands.cs         /npccmd NPC admin commands
```

## Roadmap

- [ ] Simulated contracts & work orders between citizens and player stores
- [ ] Population growth rules (immigration driven by civic score & housing)
- [ ] Citizen "tourism" events (visitors boost social, spend at stores)
- [ ] Law/Government integration (law activity affects civic score)
- [ ] Optional lightweight UI panel (ModKit) for admins

## License

MIT — see [LICENSE](LICENSE).

*Developed with AI assistance (Marvis).*
