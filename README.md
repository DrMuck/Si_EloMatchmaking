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
- **Tier by player count (0.5.0).** On a small server anyone commands; from 10 players the pick
  aims at mid-or-high rated commanders, from 20 at high rated ones (`TierByPlayers`, admin
  `!elo tiers`). The tier is a cost, never a filter: a seat is always filled when anyone applied,
  and when too few commanders of the tier apply the best available fill in (high vs mid is fine).
  Players can vote the tier of the next round (`!elo vote`), an admin can force it (`!elo tier`).
- **Rotation.** Each consecutive round in the seat adds a streak penalty, each round passed over
  adds a wait bonus, so a strong pair keeps playing while nobody comparable applies and rotates
  out within a few rounds once someone does. `!elo pass` lets a commander step aside whenever a
  peer applies.
- **Admin pins.** `!elo seat <faction> <player>` fixes a commander seat for the next round ahead of
  the pick, for organised matches (see Commands).

## Matchmaking objective

```
score = LevelWeight * mean(rating of seated commanders)              (0.5)
      + wait bonus of each seated commander (rounds passed over * 60, capped at 420)
      - SpreadWeight * spread                                          (1.0)
      - TierWeight * sum over seated of max(0, distance from the tier window - sigma)   (1.0)
      - SwapPenalty per Sol<->Centauri move (30)
      - VolunteerPenalty per !flex volunteer on a foreign faction (60)
      - min(150, 30 * streak) per commander seated in the previous `streak` consecutive rounds
      - NoCommanderPenalty per team with players left to the AI (1500, dominates everything)

spread = max over seated pairs of max(0, |Ri - Rj| - (sigma_i + sigma_j))
sigma  = 200 at 0 games, 100 at 10 games, 50 at 30 games, floor 40
```

`mean` prefers strong commanders, `spread` prefers even matchups, the tier term pulls the pick into
the band the server size asks for, streak and wait bonus rotate. All assignments are enumerated.
Ties between equal ratings are broken randomly.

**Tiers.** `any` has no window; `low` ends at the mid bound, `mid` lies between the two bounds,
`midhigh` starts at the mid bound, `high` at the high bound. The bounds are the 34 % and 67 %
quantiles of the established commander population (>= 5 games), or `TierMidRating` /
`TierHighRating` when set. After a season reset everyone sits at 1500, so the tiers are inert until
the ratings spread. The tier for a pick comes from, in this order: an admin `!elo tier` override
(one round), a `!elo vote` with quorum (at least 3 ballots and a third of the players, plurality,
one round), the `TierByPlayers` table by player count (default `0=any,10=midhigh,20=high`). The
count is the larger of the players connected now and at the end of the previous round
(`TierPlayersUsePreviousRound`), because the countdown runs right after the map change while
players are still loading; when the server empties the tier follows one round later. The countdown
announces the tier when it is not `any`.

**Newcomers and rotation.** A rating is uncertain until it has games behind it, so a gap is only
penalised beyond what the two uncertainties can explain: a 1500? newcomer against an established
1800 counts as a 50-point gap, not 300, and gets that match when nothing better is available. Every
applicant who is passed over collects a wait bonus (persisted in `ratings.json` as `waiting`), every
commander who keeps the seat collects a streak penalty (`streak`); both are shown by `!elo`. The
streak penalty is a deduction in the pick score only, ratings are never touched. Once seated,
K = 60 for the first 10 games moves a good newcomer up fast.

Measured in the scenario harness with the defaults, a 1900/1850 pair that keeps applying:

| who else applies | tier any (small server) | tier high (full server) |
|---|---|---|
| nobody | the pair, every round | the pair, every round |
| a 1700 | 1700 takes a seat after 2 rounds | a 1800 takes a seat after 1 round, a 1600 after 4 |
| a 1550 | after 4 rounds | the pair stays (1600 after 4 rounds) |
| a 1600 and a 1620 | both after 1 round | the pair stays |
| a 1500? newcomer | after 4 rounds | with no second high applicant: newcomer vs 1900 |

Other harness cases: high tier with one high and two mid applicants -> high vs mid; high tier with
only mids -> the two best mids; mid-or-high tier with a 1900, a 1600 and a 1450 -> 1900 vs 1600; the
same with only the 1900 and the 1450 -> 1900 vs 1450, never an AI seat; `!elo pass` on a 1900 steps
aside for a 1800 peer, not for a 1600, and never when the AI would take the seat.

## Team balance (0.6.0, `TeamBalance.cs`)

Si_BasicTeamBalance decides team joins in a Harmony prefix on `MP_Strategy.ProcessNetRPC` and
consumes the packet, so a second mod cannot look at the same request. This mod therefore takes the
join handling over: at start-up it removes Si_BasicTeamBalance's prefix on that one method (its
tower-defense handling and its `!team` admin command stay), applies the same head-count rule with
Si_BasicTeamBalance's own MelonPreferences values (one config for the counts), and adds:

- **Elo-aware placement, fresh joiners only, early only.** During the countdown and the first
  `BalanceEloWindowSeconds` (60) of a round, when every team has at least
  `BalanceEloMinPlayersPerTeam` (4) players, a joiner without a team whose pick would push the gap
  in mean player Elo above `BalanceEloGapMax` (60) is placed on the count-eligible team that closes
  the gap most, with a one-line reason. Players who already have a team are never redirected for
  Elo, and nobody is moved during a round. After the window only the head-count rule applies.
- **Head counts after the join.** A join may not leave the target team more than the allowed
  difference ahead of the smallest team, checked after the join (one player stricter than
  Si_BasicTeamBalance, which checks before). Allowed difference = `ceil(players / divisor + addend)`.
- **Volunteer ask.** Every `BalanceAskIntervalSeconds` (180) during a round, when a team is short by
  two or more or weaker by more than the Elo gap, chat asks for volunteers.
- **`!reserve <team>`.** Queue for a spot on another team; `!reserve off` leaves, `!reserve` shows
  the queue. Every 2 s the first in line whose move keeps the counts legal and does not widen the
  Elo gap past the limit is moved, at their next respawn (`BalanceReserveAtRespawnOnly`), so no unit
  is destroyed under them. The list is cleared at round start and end.

`!elo balance` shows the state (mean player Elo per team, allowed difference, whether Elo placement
is active); `!elo balance on|off` (admin) is the master switch. `BalanceTakeOverJoinHandling=false`
leaves Si_BasicTeamBalance's join handling in place and keeps only the ask and `!reserve`.

Harness cases (`BalanceRule`, defaults, allowed difference 1): a fresh joiner asking the fuller team
(5 vs 4) goes to the smaller one; equal counts keep the pick; a switcher into the fuller team is
denied, into the smaller one allowed; a 1800 joiner asking the already stronger side (1560 vs 1480,
5 each) is placed on the weaker side, asking the weaker side is fine, and a 1300 joiner asking the
strong side is fine (closes the gap); outside the window or below 4 per team the pick stands; a
switcher is never redirected for Elo; a reserve move waits while the target is not smaller and while
a 1900 would widen the gap, and goes through when it closes it.

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
| `!flex` / `!elo flex` | all | toggle "I take any commander seat, Alien included" (persisted per Steam ID in ratings.json) |
| `!flex alien` (toggle one), `!flex alien on|off`, `!flex sol cent` (set), `!flex humans`, `!flex on|off` | all | the factions you accept a seat on besides your own; an explicit set also limits the Sol/Centauri swap for you. In Si_ModUI: one button per faction |
| `!elo pass` | all | toggle "seat someone else when a peer (within `PassGap` 150) would take my seat" (persisted) |
| `!elo vote <any|low|mid|midhigh|high>` | all | vote the tier of the next commander matchup (needs a quorum, one round) |
| `!elo status` | all | switches, next tier, applicant pool and projected matchup during the countdown |
| `!elo balance` | all | team balance state: mean player Elo per team, allowed head-count difference, Elo placement active? |
| `!reserve <sol|cent|alien>` / `!reserve off` / `!reserve` | all | queue for a spot on another team, moved at the next respawn once counts and Elo allow it |
| `!elo on` / `off` (also `!elo matchmaking on\|off`) | admin (Commander power) | master switch; off = vanilla lottery, ratings still tracked |
| `!elo reload` | admin | reload ratings.json |
| `!elo reset confirm` | admin | new season: back up and wipe all ratings (for the 1.0 launch) |
| `!elo set <name> <rating>` | admin | override a commander rating |
| `!elo swap on/off`, `!elo volunteers on/off`, `!elo spread <w>`, `!elo level <w>` | admin | tune the objective |
| `!elo tier <tier>` / `!elo tier auto` | admin | force the tier of the next pick / back to vote + player count |
| `!elo balance on|off` | admin | team balance master switch |
| `!elo tiers 0=any,10=midhigh,20=high` | admin | the tier-by-player-count table (persisted in the config) |
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

- Player Elo balance only acts on fresh joiners at round start and on `!reserve` volunteers; nobody
  is moved against their choice. Si_BasicTeamBalance's join prefix is removed at start-up when
  `BalanceTakeOverJoinHandling` is on (see Team balance).
- The join takeover is harness-tested for the rule and reference-checked for the API, but the live
  packet path (read, clear request, placement) has not been exercised in game yet.
- Mid-round commander takeovers are left to the game (whoever presses Commander gets the empty seat).
- 0.5.0 (2026-10-22, tiers / rotation / pass / vote) is verified in the scenario harness only; the
  pick itself (seating, moves, CommManagement handover) is unchanged from 0.3.1.
- Not yet through a full live round. First live start 2026-10-20 on 1.0.2: reflection targets
  resolved and a commander was seated. First start on the public 0.9.47 server the same evening
  (Admin Mod 2.14.18, CommManagement 1.9.15): mod loaded, but 0.3.0 found no applicants because of
  the namespace mix-up above; 0.3.1 fixes the lookup and logs the found type and version.
