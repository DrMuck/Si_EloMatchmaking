# Si_EloMatchmaking

Elo-based commander matchmaking for Silica dedicated servers (MelonLoader, Mono server).

Supported game versions: the Steam **default** branch (0.9.47) and the **test** branch (1.0.2).
The source is the same for both; only the reference DLLs differ. Git branch `master` is built
against the default branch, git branch `test` against the test branch (see Build and deploy).

## What it does

- **Commander Elo** per Steam ID, seeded from the server's Half-Life style logs (Si_Logging) and
  updated live after every round. A soft **player Elo** is kept alongside for team-balance reporting.
- **Pick at round start.** The applicant pool is the union of the in-game Commander button (the
  vanilla lottery) and Si_CommManagement's `!commander` list. The mod seats the assignment with the
  best score and both random lotteries find empty lists.
- **Faction shifting.** A Sol applicant may be seated as Centauri commander (and vice versa) when
  that gives a better matchup. For an empty Alien seat, applicants who typed `!flex` may be used.
  When a seat has no applicant but a leftover applicant exists, the countdown is extended once
  (`AskExtendSeconds`) and chat asks for `!flex` volunteers.
- **Leaver rule.** A commander who disconnects, resigns or changes team within `LateLeaveSeconds`
  (10 min) of the end of a round their team lost is charged as if they had stayed to the end.
- **Chat display.** Applicants with rating during the countdown, the matchup with win chances at
  round start, team mean player Elo at round start, rating changes at round end.
- **Admin pins.** `!elo seat <faction> <player>` fixes a commander seat for the next round ahead of
  the pick, for organised matches (see Commands).

## Matchmaking objective

```
score = mean(rating of seated commanders)
      + wait bonus of each seated commander (rounds passed over * 60, capped at 420)
      - SpreadWeight * spread
      - SwapPenalty per Sol<->Centauri move (30)
      - VolunteerPenalty per !flex volunteer on a foreign faction (60)
      - RecentCommanderPenalty per commander of the previous round (30)
      - NoCommanderPenalty per team with players left to the AI (400)

spread = max over seated pairs of max(0, |Ri - Rj| - (sigma_i + sigma_j))
sigma  = 200 at 0 games, 100 at 10 games, 50 at 30 games, floor 40
```

`mean` prefers high-Elo matchups, `spread` prefers even ones; `SpreadWeight` (default 1.0) is the
exchange rate. All assignments are enumerated. Ties between equal ratings are broken randomly.

**Newcomers and rotation.** A rating is uncertain until it has games behind it, so a gap is only
penalised beyond what the two uncertainties can explain: a 1500? newcomer against an established
1800 counts as a 50-point gap, not 300, and gets that match when nothing better is available. On top
of that, every applicant who is passed over collects a wait bonus; a newcomer behind a 1900/1850
pair displaces one of them after about 4-5 rounds (sooner, because both incumbents also carry the
recent-commander penalty). Once seated, K = 60 for the first 10 games moves a good newcomer up fast.
Rounds passed over are persisted in `ratings.json` (`waiting`) and shown by `!elo`.

Examples (from the scenario harness): Sol applicants 1900 and 1500, Centauri 1500 -> 1500 vs 1500.
Sol applicants 1900 and 1850, no Centauri applicant -> one is moved to Centauri, 1900 vs 1850.
Sol newcomer 1500? and Centauri 1800 + 1500 -> newcomer vs 1800. Newcomer behind a 1900/1850 pair
-> the pair first, the newcomer vs 1850 once the wait bonus reaches 300.

## Rating model (mirrored in `tools/compute_elo.py` and `RatingDb.cs`)

- Start 1500, K = 60 for the first 10 games, 24 afterwards (60 validated slightly better than 40
  on the logs and lets newcomers settle faster).
- Team rating = tenure-share-weighted mean of its human commanders. Teams without a human commander
  (AI) are ignored. Pairwise update winner vs each loser; loser-vs-loser pairs in 3-way rounds carry
  no information and are skipped. Each commander gets `K * share * (S - E) / (teams - 1)`.
- Small faction/mode advantage term estimated from the logs (shrunk to 0, capped at +-100).
- Rounds shorter than 4 min are not rated.
- Seed validation on 3647 logged rounds: the pre-round favourite won 73 % of rounds (71 % when both
  commanders had >= 10 games), log-loss 0.55 vs 0.69 for a coin flip. The Elo carries real signal.

## Files

```
Si_EloMatchmaking/        C# mod (netstandard2.1, references in include/netstandard2.1)
  EloMod.cs               entry point, Harmony patches, round tracking, the pick
  Matchmaker.cs           assignment search
  RatingDb.cs             ratings.json + Elo update
  GameApi.cs              reflection over MP_Strategy + Si_CommManagement, chat helpers
  Commands.cs             !elo / !flex
  Config.cs               JSON config
tools/parse_logs.py       server logs -> data/rounds.jsonl (one record per finished round)
tools/compute_elo.py      rounds.jsonl -> data/ratings.json + data/report.md
data/                     seed built from Si_RTS_AI/Serverdata logs (2025-09-21 .. 2026-08-08)
```

Server files:

```
Mods\Si_EloMatchmaking.dll
UserData\cfg\EloMatchmaking\ratings.json                 (seed: copy data/ratings.json, mod extends it)
UserData\cfg\EloMatchmaking\Si_EloMatchmaking_Config.json (created on first run)
```

## Build and deploy

```
cd Si_EloMatchmaking
dotnet build -c Release                          (Steam default branch -> bin\Release\default\)
dotnet build -c Release -p:SilicaBranch=test     (Steam test branch    -> bin\Release\test\)
copy bin\Release\default\Si_EloMatchmaking.dll "<server>\Mods\"
```

`include/default` and `include/test` hold the reference DLLs of the respective Silica branch:
SilicaCore.dll and the Unity modules (from that server's Silica_Data\Managed), MelonLoader net6
`MelonLoader.dll` + `0Harmony.dll`, `Newtonsoft.Json.dll`, `Si_AdminMod.dll`. Refresh after a game
update. `SilicaBranch` defaults to `default` on git `master` and to `test` on git `test`.

Both builds were checked with a Mono.Cecil reference check against both game versions on
2026-10-20: every SilicaCore member the mod uses (public API plus the reflected
`CommanderLottery`, `SetCommander`, `RPC_SynchCommander`, `RPC_SynchRole`) exists in 0.9.47 and
1.0.2; the only difference is that the seating methods are `private` in 0.9.47 and `protected` in
1.0.2, which reflection does not care about.

## Rebuilding the seed from logs

```
python -I tools\parse_logs.py "<dir with L*.log files>" -o data\rounds.jsonl
python -I tools\compute_elo.py data\rounds.jsonl -o data\ratings.json --report data\report.md
```

Then copy `data\ratings.json` to `UserData\cfg\EloMatchmaking\` and `!elo reload` (or restart). The
`flex` set in the server copy is lost when you overwrite it; merge by hand if needed.

## Commands

| command | who | effect |
|---|---|---|
| `!elo` | all | own commander and player rating |
| `!elo top` | all | top 5 established commanders |
| `!elo <name>` | all | look up a commander |
| `!flex` / `!elo flex` | all | toggle "I take any commander seat, Alien included" (persisted) |
| `!elo status` | all | applicant pool and projected matchup during the countdown |
| `!elo on` / `off` (also `!elo matchmaking on\|off`) | admin (Commander power) | master switch; off = vanilla lottery, ratings still tracked |
| `!elo reload` | admin | reload ratings.json |
| `!elo reset confirm` | admin | new season: back up and wipe all ratings (for the 1.0 launch) |
| `!elo set <name> <rating>` | admin | override a commander rating |
| `!elo swap on/off`, `!elo volunteers on/off`, `!elo spread <w>` | admin | tune the objective |
| `!elo seat <sol\|cent\|alien> <player>` | admin | pin this player as commander of that faction for the next round, ahead of the Elo pick (organised matches); `!elo seat` lists the pins |
| `!elo unseat <faction>` / `!elo unseat all` | admin | drop a pin |

Pins are kept in memory and consumed at the next round start (one round only). They also work while
matchmaking is OFF: the pick then runs for that one round with the pinned seats fixed and the other
seats filled by rating. A pinned player who is not on the server at round start is skipped with a chat
notice. The pinned player is moved to the faction if needed and counts as that faction's own applicant
(no swap or volunteer penalty), so the remaining seats are still chosen for the best matchup around them.

### Si_ModUI

`Pins.cs` exposes `ModUIData()` for the Si_ModUI server mod: the `!elo` sub-commands become
buttons in ModUI (player ones for everyone, admin ones only for admins), `/elo seat` opens a team
picker followed by a player picker, `/elo unseat` lists the current pins.

## How the pick is wired into the game (0.9.47 and 1.0.2, same flow)

- Before the round, a Commander-button press sends `REQUEST_COMMANDER_LOTTERY_ENTRY` into the private
  `MP_Strategy.CommanderLottery` dictionary. `!commander` (Si_CommManagement) fills
  `CommanderApplications.commanderApplicants`. With Admin Mod 2.14.18 + Si_CommManagement 1.9.15
  the Commander button itself is intercepted ("Denied early game commander join"): the player lands
  in `commanderApplicants`, the vanilla dictionary stays empty, and one second before the start
  CommManagement moves all applicants to role NONE. So that list is the real pool on current
  servers. Note the namespace: the DLL is `Si_CommManagement.dll` but the types live in
  `Si_CommanderManagement` (0.3.0 looked up the wrong name and saw no applicants; fixed in 0.3.1).
- `OnMissionStateChanged(STARTED)` fires the "AI controlled" chat and `GameEvents.OnGameStarted`
  (Si_Logging's `Round_Start`), then Si_CommManagement's postfix runs its lottery, then in the same
  frame `UpdateGameLoop` calls the vanilla `ResolveCommanderLottery()`.
- This mod's postfix on `OnMissionStateChanged` has `Priority.High`, so it runs first: it reads both
  lists, clears them, moves players if needed (`player.Team = t; NetworkLayer.SendPlayerSelectTeam`),
  then `SetCommander` + `RPC_SynchCommander(team, false)` + `RPC_SynchRole(player, COMMANDER)` via
  reflection, and fires AdminMod's `OnRoleChanged` so Si_Logging writes `changed role to "Commander"`.
- Tenure tracking polls `GetCommanderForTeam` every 2 s; the rating update runs in a postfix on
  `MusicJukeboxHandler.OnGameEnded(GameMode, Team)`.

## Cost on the server

Measured with the 1318-commander / 3672-player seed (.NET 8 dev box; Mono will be somewhat slower):
round-end rating update 0.07 ms; the pick at round start enumerates at most a few thousand
assignments (microseconds); the 2-second in-round poll touches each connected player once; the
per-frame OnUpdate is a handful of property reads. Saving is diff-based and off the main thread:
a save appends one line with the entries touched since the last save to
`ratings.json.journal.jsonl` (main-thread cost 0.03 ms, ~12 KB per round on a pool thread); every
tenth save, on reset and on shutdown the full file is rewritten (~40 ms on the pool thread) and the
journal truncated. Load replays the journal (verified identical in a harness).

## Known limits / not done

- Player Elo is **report only**; nobody is moved for player balance (Si_BasicTeamBalance handles
  player counts; mixing two movers is risky).
- Mid-round commander takeovers are left to the game (whoever presses Commander gets the empty seat).
- Not yet through a full live round. First live start 2026-10-20 on 1.0.2: reflection targets
  resolved and a commander was seated. First start on the public 0.9.47 server the same evening
  (Admin Mod 2.14.18, CommManagement 1.9.15): mod loaded, but 0.3.0 found no applicants because of
  the namespace mix-up above; 0.3.1 fixes the lookup and logs the found type and version.
