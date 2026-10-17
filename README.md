# EcoSim Citizens

An autonomous citizen simulation mod for **Eco Global Survival**. It adds a layer of simulated residents to your world who work, earn, shop, live in deeds, and react to your player-driven economy and civics — a living backdrop that makes server economies feel populated even between player sessions.

> Built against Eco.ReferenceAssemblies `0.14.2-beta-release-1099` (net10.0). Referenced API surface is verified against the official assemblies; see [docs.play.eco](https://docs.play.eco/).

## Why

Eco's beauty is that everything is player-made. But when nobody is online, the world goes quiet. EcoSim Citizens introduces lightweight **simulated citizens** that:

- **Work** — productivity = profession × trait × health (calories).
- **Earn & spend** — each citizen has a private ledger and buys food from *real* player stores at *real* market prices.
- **Live somewhere** — citizens can be assigned a real in-game **deed**; comfort scales with plot count and settlement support.
- **Feel the civic pulse** — deeds, settlements and active elections feed a civic health score.
- **Persist** — state is saved as JSON next to the mod DLL and survives restarts.

The mod is **observational only**: it never claims land, spawns items, moves players, or mutates stores/contracts. Everything it reads is public read-only Eco state. The simulation is a closed loop *driven by* the live economy, so a server with a booming market and settled towns produces happier citizens than an empty frontier.

## Features (v0.4.0)

- 7 professions: Laborer, Farmer, Miner, Crafter, Merchant, Builder, Scholar.
- 6 traits: Diligent, Lazy, Curious, Moody, Gregarious, Balanced (auto-assigned, affects output & happiness).
- Three tracked needs: **Calories**, **Comfort**, **Social** (0–100, decay each tick).
- Live **market index**: store count, offer count, priced offers, food availability, avg/min price, price trend %.
- Live **civic snapshot**: settlement count, active elections, deed count, real online population, civic score.
- Daily report broadcast to online players.
- JSON persistence for citizens and config.
- In-game chat commands (see below).

### v0.2 – real world NPCs

- Citizens become **real in-game NPCs** that occupy the world (spawn/despawn managed, hybrid physical/logical pathfinding with an agent pool).
- Spawning is driven by real Eco professions from `Skill.AllSkills`; productivity = profession × trait × health.

### v0.3 – production, skills & civics

- Real production/sales ledger, per-citizen skill progression (xp → level), commuting to workplaces, settlement membership and day/night lifecycle.
- Citizen voting, legal preferences and politics engine.

### v0.4 – commerce, ecology & systems

- Autonomous commerce (private agents, store interactions), construction planning, corporations with shares & dividends, stock exchange with futures, settlement currencies & forex.
- Work parties, research contributions, environment tracker with meteor crisis, contextual dialogue and JSON telemetry.

## Installation

1. Download/build the mod: `dotnet build -c Release`.
2. Copy `EcoSimCitizens.dll` into your Eco server's `Mods` folder.
3. Restart the server. The plugin appears in the plugins list and creates `EcoSimCitizens/config.json` + `EcoSimCitizens/citizens.json` next to the DLL.
4. Spawn your first citizens (admin):

```
/ecosimulator spawncitizen Ada Farmer
/ecosimulator spawncitizen Bo Builder
```

5. Watch the daily report roll in, or check status any time:

```
/ecosimulator
```

## Commands

All commands live under two root keys: `/ecosimulator` (citizen simulation) and `/npccmd` (PRD system admin). Command keys are global and case-sensitive — the root names were chosen to avoid clashing with core Eco commands (`remove`, `give`, `spawn`).

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

### `/npccmd` — PRD system admin

| Command | Access | Description |
| --- | --- | --- |
| `/npccmd` | Admin | Command overview |
| `/npccmd limit <int>` | Admin | Set the max number of physical NPC citizens |
| `/npccmd pool` | Admin | Agent pool + hybrid pathfinding stats |
| `/npccmd stats [settlement]` | Admin | JSON telemetry for the simulation |
| `/npccmd company create <name> <capital> \| list \| issue <corp> <name> <shares> \| vote <corp> <choice> \| resolve <corp> \| hire <corp> <citizen> <wage> \| dividends <corp> <amount>` | Admin | Corporation management |
| `/npccmd stock list \| addfuture <commodity> <base> \| buy <citizen> <commodity>` | Admin | Stock exchange & futures |
| `/npccmd currency list \| mint <settlement> <symbol> <reserve> \| rate <from> <to> \| convert <citizen> <from> <to> <amount>` | Admin | Settlement currencies & forex |
| `/npccmd party create <name> <task> <pool> <target> \| list \| join <citizen> <party> \| leave <citizen>` | Admin | Work parties |
| `/npccmd construct list \| status <citizen> \| plan <citizen>` | Admin | Construction plans |
| `/npccmd research` | Admin | Research hub status |
| `/npccmd env get \| set <pollution> <co2> <soil> \| cleanup <amount> \| meteor <power> <resources> \| impact <day>` | Admin | Environment & meteor crisis |
| `/npccmd dialogue <citizen>` | Admin | Show a citizen's recent dialogue |

## Configuration

Edit `EcoSimCitizens/config.json` (created on first run, or manually):

```json
{
  "TickIntervalMinutes": 30.0,
  "CalorieDecayPerTick": 4.0,
  "ComfortDecayPerTick": 2.5,
  "SocialDecayPerTick": 1.5,
  "CalorieGainFromFood": 12.0,
  "FoodSpendFactor": 0.6,
  "WagePerTick": 20.0,
  "StartingBalance": 100.0,
  "PriceTrendSensitivity": 1.0,
  "MaxCitizens": 50,
  "BroadcastDailyReport": true
}
```

## Project structure

```
EcoSimCitizens/
├── Core
│   ├── EcoSimCitizens.csproj      net10.0 + Eco.ReferenceAssemblies 0.14.2
│   ├── EcoSimCitizensPlugin.cs    SimplePlugin<EcoSimCitizens> + IThreadedPlugin
│   ├── ModConfig.cs               runtime config (JSON)
│   ├── AssemblyInfo.cs            Windows platform declarations (CA1416)
│   └── PluginLog.cs               logging shim
├── Simulation
│   ├── CitizenModel.cs            Citizen + profession/trait tables
│   ├── SimulationCore.cs          per-tick simulation + report builder
│   ├── SimulationLogic.cs         pure decision logic (unit-tested)
│   ├── SimulationState.cs         simulation state containers
│   └── CitizensDatabase.cs        thread-safe store + JSON persistence
├── Citizens
│   ├── CitizenNpc.cs              real-world NPC behaviour
│   ├── NpcManager.cs              NPC spawn/despawn
│   ├── AgentPoolManager.cs        agent pool + hybrid pathfinding
│   ├── CitizenEconomy.cs          commerce, earn & spend
│   ├── CitizenProduction.cs       production & sales ledger
│   ├── CitizenSkills.cs           skill xp & levels
│   ├── CitizenCivics.cs           voting & legal preferences
│   ├── CitizenCommute.cs          commuting to workplaces
│   └── CitizenLifecycle.cs        day/night, age, death
├── Systems
│   ├── AgentCommerce.cs           autonomous commerce agents
│   ├── WorkPartySystem.cs         work parties & contracts
│   ├── ConstructionPlanner.cs     construction plans
│   ├── CorporationSystem.cs       corporations, shares & dividends
│   ├── StockExchangeSystem.cs     stock exchange & futures
│   ├── CurrencySystem.cs          settlement currencies & forex
│   ├── ResearchContribution.cs    research hubs
│   ├── EnvironmentTracker.cs      pollution/CO2/soil & meteor crisis
│   ├── PoliticsEngine.cs          elections & politics
│   └── DialogueTree.cs            contextual dialogue
├── Observers
│   ├── MarketScanner.cs           reads real stores/TradeOffers (read-only)
│   ├── CivicObserver.cs           deeds/settlements/elections snapshot
│   ├── HousingEvaluator.cs        deed -> comfort mapping
│   └── TelemetryService.cs        JSON telemetry
└── Commands
    ├── EcoSimCommands.cs          /ecosimulator chat commands
    └── NpcCommands.cs             /npccmd PRD admin commands
```

## Roadmap

- [ ] Citizen names generated from culture pools
- [ ] Simulated contracts & work orders between citizens and player stores
- [ ] Population growth rules (immigration driven by civic score & housing)
- [ ] Citizen "tourism" events (visitors boost social, spend at stores)
- [ ] Law/Government integration (law activity affects civic score)
- [ ] Per-citizen skill progression and specialization over time
- [ ] Optional lightweight UI panel (ModKit) for admins

## License

MIT — see [LICENSE](LICENSE).

*Developed with AI assistance (Marvis).*
