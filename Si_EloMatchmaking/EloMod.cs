/*
 Si_EloMatchmaking - Elo-based commander matchmaking for Silica dedicated servers.

 What it does
 - Keeps a commander Elo (and a soft player Elo) in UserData\cfg\EloMatchmaking\ratings.json,
   seeded from server logs by tools/compute_elo.py and updated live after every round.
 - At round start it takes over the commander pick: the applicant pool is the union of the
   in-game Commander button (vanilla lottery) and Si_CommManagement's !commander list. It
   seats the assignment that maximises rating level minus rating spread, may move a Sol
   applicant to Centauri (or vice versa) and may use !flex volunteers for an empty Alien seat.
 - Shows the matchup with ratings and win chances in chat, and the rating changes at the end.
 - Admin pins (!elo seat <faction> <player>) fix a seat for the next round ahead of the pick
   (organised matches); Pins.cs. Pins.cs also exports the sub-commands to Si_ModUI.
 - Rotation and tiers (Tiers.cs): the pick aims at a rating tier chosen by player count (small
   server: anyone; fuller: mid or high; full: high), overridable for one round by a player vote
   (!elo vote) or an admin (!elo tier). Consecutive rounds in the seat cost a growing streak
   penalty, applicants passed over collect a wait bonus, and !elo pass lets a commander step
   aside when a peer applies. Seats are always filled when anyone applied.
 - Leaver rule: a commander who drops out in the last 10 minutes of a lost round still takes
   the loss.

 How it hooks the game (0.9.47 and 1.0.2, Mono server)
 - Postfix on MP_Strategy.OnMissionStateChanged(STARTED) with Priority.High: runs before
   Si_CommManagement's own lottery postfix and before the vanilla ResolveCommanderLottery()
   (same frame, later). It consumes both applicant lists, so neither random pick seats anyone.
 - Postfix on MusicJukeboxHandler.OnGameEnded(GameMode, Team): rating update.
 - OnUpdate polling: applicant announcements during the countdown, commander tenure and
   player presence tracking during the round (2 s granularity).

 Server-only. Deploy: <server>\Mods\Si_EloMatchmaking.dll
*/
using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_EloMatchmaking.EloMod), "EloMatchmaking", Si_EloMatchmaking.EloMod.Version, "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("Admin Mod", "Commander Management")]

namespace Si_EloMatchmaking
{
    public partial class EloMod : MelonMod
    {
        public const string Version = "0.5.0";
        public static EloConfig Cfg = new EloConfig();
        public static RatingDb Db = null!;
        static readonly System.Random Rng = new System.Random();

        // ---- pre-round state ----
        static bool _preRoundActive;
        static float _preRoundPoll;
        static bool _askedThisRound;
        static readonly HashSet<string> _announcedApplicants = new HashSet<string>();

        // ---- last pick ----
        static HashSet<string> _lastRoundCommanders = new HashSet<string>();
        public static Assignment? LastAssignment;

        // ---- in-round tracking ----
        class Tenure
        {
            public string steam = "";
            public string name = "";
            public Team team = null!;
            public float start;
            public float end = -1;
            public string reason = "round_end";
        }

        class RoundTracker
        {
            public float startTime;
            public string gametype = "";
            public List<Team> teams = new List<Team>();
            public List<Tenure> tenures = new List<Tenure>();
            public Dictionary<Team, Tenure?> open = new Dictionary<Team, Tenure?>();
            public Dictionary<Team, Dictionary<string, (string name, double seconds)>> presence = new Dictionary<Team, Dictionary<string, (string, double)>>();
            public float lastPoll;
        }

        static RoundTracker? _round;
        const float ROUND_POLL_S = 2f;

        // =====================================================================
        public override void OnInitializeMelon()
        {
            Cfg = ConfigStore.Load();
            Db = new RatingDb(ConfigStore.DbPath, Cfg);
            Db.Load();
            GameApi.LogReflectionState();
            MelonLogger.Msg($"[Elo] Si_EloMatchmaking v{Version} loaded. Enabled={Cfg.Enabled} swap={Cfg.AllowHumanFactionSwap} " +
                            $"volunteers={Cfg.AllowVolunteers} spreadWeight={Cfg.SpreadWeight} live={Cfg.UpdateRatingsLive}");
        }

        public override void OnLateInitializeMelon()
        {
            RegisterCommands();
        }

        public override void OnApplicationQuit()
        {
            try { Db.SaveNow(); } catch { }
        }

        public override void OnUpdate()
        {
            try
            {
                TickPreRound();
                TickRound();
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[Elo] OnUpdate: " + ex);
            }
        }

        // =====================================================================
        // Harmony patches
        // =====================================================================
        [HarmonyPatch(typeof(MP_Strategy), "OnMissionStateChanged")]
        static class Patch_MissionState
        {
            [HarmonyPriority(HarmonyLib.Priority.High)]
            static void Postfix(MP_Strategy __instance, GameModeExt.EMissionState __0)
            {
                try
                {
                    if (__0 == GameModeExt.EMissionState.STARTED) OnRoundStarted(__instance);
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("[Elo] OnMissionStateChanged postfix: " + ex);
                }
            }
        }

        [HarmonyPatch(typeof(MusicJukeboxHandler), "OnGameEnded")]
        static class Patch_GameEnded
        {
            static void Postfix(GameMode __0, Team __1)
            {
                try { OnRoundEnded(__1); }
                catch (Exception ex) { MelonLogger.Error("[Elo] OnGameEnded postfix: " + ex); }
            }
        }

        // =====================================================================
        // Round start: the pick
        // =====================================================================
        static void OnRoundStarted(MP_Strategy strat)
        {
            _preRoundActive = false;
            if (Cfg.Enabled || HasPins) RunMatchmaking(strat);   // admin pins run the pick for this one round even while OFF
            StartTracker(strat);
            if (Cfg.PlayerBalanceReport) ReportPlayerBalance(strat);
        }

        static List<Seat> BuildSeats(MP_Strategy strat)
        {
            return GameApi.ActiveTeams(strat)
                .Select(t => new Seat { Team = t, Faction = GameApi.Faction(t), Players = SafeNumPlayers(t) })
                .Where(s => s.Players > 0)
                .ToList();
        }

        static int SafeNumPlayers(Team t)
        {
            try { return t.GetNumPlayers(); } catch { return 0; }
        }

        /// <summary>Union of the vanilla lottery and CommManagement's list, keyed by the player's CURRENT team.</summary>
        static List<Candidate> CollectPool(MP_Strategy strat, List<Seat> seats, bool consume)
        {
            var bySteam = new Dictionary<string, Candidate>();
            var seatTeams = new HashSet<Team>(seats.Select(s => s.Team));

            void Add(Player p, string source)
            {
                if (p == null) return;
                if (GameApi.IsServerPlayer(p)) return;
                var team = p.Team;
                if (team == null || !seatTeams.Contains(team)) return;
                string steam = GameApi.SteamId(p);
                if (bySteam.TryGetValue(steam, out var existing))
                {
                    if (existing.Source != source) existing.Source = "both";
                    return;
                }
                bySteam[steam] = new Candidate
                {
                    Player = p,
                    Steam = steam,
                    Name = p.PlayerName ?? steam,
                    AppliedTeam = team,
                    AppliedFaction = GameApi.Faction(team),
                    Rating = Db.CommanderRating(steam),
                    Games = Db.CommanderGames(steam),
                    Sigma = Db.Sigma(Db.CommanderGames(steam)),
                    WaitBonus = Db.WaitBonus(steam),
                    Flex = Db.IsFlex(steam),
                    Pass = Db.IsPass(steam),
                    Streak = Db.Streak(steam),
                    Recent = Db.Streak(steam) > 0,
                    Source = source,
                };
            }

            foreach (var kv in GameApi.VanillaLottery(strat))
                foreach (var p in kv.Value) Add(p, "button");
            foreach (var kv in GameApi.CommMgmtApplicants())
                foreach (var p in kv.Value) Add(p, "chat");

            if (consume)
            {
                GameApi.ClearVanillaLottery(strat);
                GameApi.ClearCommMgmtApplicants();
            }
            return bySteam.Values.ToList();
        }

        static void RunMatchmaking(MP_Strategy strat)
        {
            var seats = BuildSeats(strat);
            var cands = CollectPool(strat, seats, consume: true);
            int players = ConnectedCount();
            string tierName = ResolveTier(players, out string tierReason);
            var tier = Window(tierName);
            ClearTierState();   // votes and the admin override are for this one pick
            var stepped = Matchmaker.ApplyPass(seats, cands, Cfg, Rng, tier);
            foreach (var s in stepped)
                GameApi.SayAll($"{GameApi.Colored(s.AppliedTeam, s.Name)} passes the commander seat this round (!elo pass): another applicant of similar rating is in.");
            var fixedPicks = FixedPicks(strat, seats, cands, announceMissing: true);
            _pins.Clear();   // one round only
            LastAssignment = null;
            if (seats.Count == 0)
            {
                MelonLogger.Msg("[Elo] No active teams with players; nothing to assign.");
                return;
            }
            if (cands.Count == 0)
            {
                MelonLogger.Msg("[Elo] No commander applicants; all seats stay AI.");
                return;
            }

            var best = Matchmaker.Solve(seats, cands, Cfg, Rng, fixedPicks, tier);
            LastAssignment = best;
            if (fixedPicks != null)
                for (int i = 0; i < seats.Count; i++)
                    if (fixedPicks[i] != null) GameApi.SayAll($"Admin pick: {GameApi.Colored(seats[i].Team, fixedPicks[i]!.Name)} commands {GameApi.Short(seats[i].Faction)}.");
            if (Cfg.Debug)
            {
                MelonLogger.Msg($"[Elo] Pool: " + string.Join(", ", cands.Select(c => $"{c.Name}@{GameApi.Short(c.AppliedFaction)}={c.Rating:0}~{c.Sigma:0}+{c.WaitBonus:0}/{c.Games}g{(c.Flex ? " flex" : "")}{(c.Streak > 0 ? $" streak{c.Streak}" : "")}[{c.Source}]"))
                                + $"; tier {tierName} ({tierReason}) window {tier.Low:0}..{tier.High:0}");
                MelonLogger.Msg($"[Elo] Best: score={best.Score:0.0} mean={best.Mean:0} spread={best.Spread:0} tierCost={best.TierCost:0} swaps={best.Swaps} vols={best.Volunteers} unfilled={best.Unfilled}");
            }

            var seated = new List<(Seat seat, Candidate cand, bool moved)>();
            foreach (var (seat, c) in best.Seated())
            {
                if (c.Player == null) continue;
                bool moved = false;
                if (c.Player.Team != seat.Team)
                {
                    if (!GameApi.MovePlayer(c.Player, seat.Team))
                    {
                        MelonLogger.Warning($"[Elo] Could not move {c.Name} to {seat.Team.TeamShortName}; seat stays empty.");
                        continue;
                    }
                    moved = true;
                }
                if (GameApi.SeatCommander(strat, seat.Team, c.Player))
                    seated.Add((seat, c, moved));
                else
                    MelonLogger.Warning($"[Elo] Seating {c.Name} on {seat.Team.TeamShortName} failed.");
            }

            // Applicants that were sent to "no role" by CommManagement at the end of the countdown
            // need a unit again; button applicants already have one.
            foreach (var c in cands.Concat(stepped))
                if (!seated.Any(s => s.cand == c)) GameApi.RespawnIfUnitless(c.Player);

            _lastRoundCommanders = new HashSet<string>(seated.Select(s => s.cand.Steam));
            Db.UpdateStreaks(_lastRoundCommanders);
            Db.UpdateWaiting(_lastRoundCommanders, cands.Where(c => !_lastRoundCommanders.Contains(c.Steam)).Select(c => c.Steam));
            Db.Save();

            MelonLogger.Msg("[Elo] Seated: " + string.Join(", ", seated.Select(s => $"{s.cand.Name}({s.cand.Rating:0}) -> {s.seat.Team.TeamShortName}{(s.moved ? " [moved]" : "")}")) +
                            (best.Unfilled > 0 ? $"; {best.Unfilled} seat(s) AI" : "") + $"; tier {tierName} ({tierReason})" +
                            (stepped.Count > 0 ? "; passed: " + string.Join(", ", stepped.Select(s => s.Name)) : ""));

            if (Cfg.AnnounceMatchup) AnnounceMatchup(seats, seated, tierName, tierReason);
        }

        static void AnnounceMatchup(List<Seat> seats, List<(Seat seat, Candidate cand, bool moved)> seated, string tierName, string tierReason)
        {
            var parts = new List<string>();
            var ratings = new List<double>();
            var ratedSeats = new List<Seat>();
            foreach (var seat in seats)
            {
                var s = seated.FirstOrDefault(x => x.seat == seat);
                if (s.cand == null)
                {
                    parts.Add(GameApi.Colored(seat.Team, GameApi.Short(seat.Faction) + ": AI"));
                    continue;
                }
                string tag = s.moved ? $" (moved from {GameApi.Short(s.cand.AppliedFaction)})" : "";
                parts.Add(GameApi.Colored(seat.Team, $"{GameApi.Short(seat.Faction)}: {s.cand.Name} {Db.FormatShort(s.cand.Steam)}") + tag);
                ratings.Add(s.cand.Rating);
                ratedSeats.Add(seat);
            }
            GameApi.SayAll("Commander matchup" + (tierName != "any" ? $" [{TierLabel(tierName)} tier, {tierReason}]" : "") + ": " + string.Join(" vs ", parts));
            if (ratings.Count >= 2)
            {
                var p = RatingDb.WinProbabilities(ratings);
                var odds = new List<string>();
                for (int i = 0; i < ratedSeats.Count; i++)
                    odds.Add(GameApi.Colored(ratedSeats[i].Team, $"{GameApi.Short(ratedSeats[i].Faction)} {p[i] * 100:0}%"));
                GameApi.SayAll("Win chance by commander Elo: " + string.Join(" / ", odds) + " | ? = provisional (<" + Cfg.ProvisionalGames + " games)");
            }
        }

        static void ReportPlayerBalance(MP_Strategy strat)
        {
            try
            {
                var teams = GameApi.ActiveTeams(strat);
                var parts = new List<string>();
                foreach (var t in teams)
                {
                    var members = GameApi.ConnectedPlayers().Where(p => p.Team == t).ToList();
                    if (members.Count == 0) continue;
                    double mean = members.Average(p => Db.PlayerRating(GameApi.SteamId(p)));
                    parts.Add(GameApi.Colored(t, $"{GameApi.Short(GameApi.Faction(t))} {mean:0} ({members.Count})"));
                }
                if (parts.Count >= 2) GameApi.SayAll("Team player Elo (mean, players): " + string.Join(" | ", parts));
            }
            catch (Exception ex) { MelonLogger.Warning("[Elo] Player balance report: " + ex.Message); }
        }

        // =====================================================================
        // Pre-round countdown: announcements + volunteer ask
        // =====================================================================
        static void TickPreRound()
        {
            var strat = GameApi.Strat;
            if (strat == null) { _preRoundActive = false; return; }

            bool pre;
            try { pre = strat.MissionState == GameModeExt.EMissionState.INIT && strat.TeamsVersus != GameModeExt.ETeamsVersus.NONE && !strat.GameBegun; }
            catch { pre = false; }

            if (!pre)
            {
                _preRoundActive = false;
                return;
            }
            if (!_preRoundActive)
            {
                _preRoundActive = true;
                _announcedApplicants.Clear();
                _askedThisRound = false;
                _preRoundPoll = 0f;
                _announcedTier = "";
            }
            _preRoundPoll -= Time.deltaTime;
            if (_preRoundPoll > 0f) return;
            _preRoundPoll = 1f;
            if (!Cfg.Enabled) return;

            var seats = BuildSeats(strat);
            var cands = CollectPool(strat, seats, consume: false);
            AnnounceTier(ConnectedCount());

            if (Cfg.AnnounceApplicants)
            {
                foreach (var c in cands)
                {
                    string key = c.Steam + "|" + c.AppliedFaction;
                    if (!_announcedApplicants.Add(key)) continue;
                    GameApi.SayAll($"{GameApi.Colored(c.AppliedTeam, c.Name)} applied for {GameApi.Short(c.AppliedFaction)} commander, Elo {Db.Format(c.Steam)}" + (c.Flex ? " [flex]" : "") + (c.WaitBonus > 0 ? $" [waited {Db.WaitingRounds(c.Steam)}, +{c.WaitBonus:0}]" : ""));
                }
            }

            if (Cfg.AskExtendSeconds > 0 && !_askedThisRound && strat.Timer <= 6f && strat.Timer > 1.5f && seats.Count >= 2 && cands.Count > 0)
            {
                var best = Matchmaker.Solve(seats, cands, Cfg, Rng, null, Window(ResolveTier(ConnectedCount(), out _)));
                int filled = seats.Count - best.Unfilled;
                if (best.Unfilled > 0 && cands.Count > filled)
                {
                    var empty = new List<string>();
                    for (int i = 0; i < seats.Count; i++)
                        if (best.Picks[i] == null) empty.Add(GameApi.Colored(seats[i].Team, GameApi.Short(seats[i].Faction)));
                    GameApi.SayAll($"No commander applicant for {string.Join(", ", empty)}. Applicants willing to take any seat: type !flex now (countdown +{Cfg.AskExtendSeconds}s).");
                    try { strat.SetTimer(strat.Timer + Cfg.AskExtendSeconds); }
                    catch (Exception ex) { MelonLogger.Warning("[Elo] SetTimer: " + ex.Message); }
                    _askedThisRound = true;
                }
            }
        }

        // =====================================================================
        // In-round tracking (commander tenures, player presence)
        // =====================================================================
        static void StartTracker(MP_Strategy strat)
        {
            _round = new RoundTracker
            {
                startTime = Time.time,
                gametype = strat.TeamsVersus.ToString(),
                teams = GameApi.ActiveTeams(strat),
                lastPoll = Time.time,
            };
            foreach (var t in _round.teams)
            {
                _round.open[t] = null;
                _round.presence[t] = new Dictionary<string, (string, double)>();
            }
            PollRound(strat, 0f);
        }

        static void TickRound()
        {
            if (_round == null) return;
            var strat = GameApi.Strat;
            if (strat == null) return;
            float now = Time.time;
            if (now - _round.lastPoll < ROUND_POLL_S) return;
            float dt = now - _round.lastPoll;
            _round.lastPoll = now;
            PollRound(strat, dt);
        }

        static void PollRound(MP_Strategy strat, float dt)
        {
            if (_round == null) return;
            float now = Time.time;
            foreach (var team in _round.teams)
            {
                var cur = GameApi.CommanderOf(strat, team);
                string? curSteam = cur != null ? GameApi.SteamId(cur) : null;
                var open = _round.open[team];
                if (open != null && (curSteam == null || open.steam != curSteam))
                {
                    open.end = now;
                    open.reason = LeaveReason(open);
                    _round.open[team] = null;
                    open = null;
                }
                if (cur != null && open == null)
                {
                    var t = new Tenure { steam = curSteam!, name = cur.PlayerName ?? curSteam!, team = team, start = now };
                    _round.tenures.Add(t);
                    _round.open[team] = t;
                }
            }
            if (dt > 0f)
            {
                foreach (var p in GameApi.ConnectedPlayers())
                {
                    var team = p.Team;
                    if (team == null || !_round.presence.TryGetValue(team, out var dict)) continue;
                    string steam = GameApi.SteamId(p);
                    dict.TryGetValue(steam, out var cur);
                    dict[steam] = (p.PlayerName ?? steam, cur.seconds + dt);
                }
            }
        }

        static string LeaveReason(Tenure t)
        {
            foreach (var p in GameApi.ConnectedPlayers())
            {
                if (GameApi.SteamId(p) != t.steam) continue;
                return p.Team == t.team ? "resign" : "team_change";
            }
            return "disconnect";
        }

        // =====================================================================
        // Round end: rating update
        // =====================================================================
        static void OnRoundEnded(Team? winner)
        {
            var r = _round;
            _round = null;
            if (r == null) return;
            float now = Time.time;
            foreach (var t in r.tenures)
                if (t.end < 0) { t.end = now; t.reason = "round_end"; }

            double dur = now - r.startTime;
            string winFaction = GameApi.Faction(winner);
            if (winner == null || string.IsNullOrEmpty(winFaction))
            {
                MelonLogger.Msg($"[Elo] Round ended without a winner after {dur:0}s; not rated.");
                return;
            }
            if (!Cfg.UpdateRatingsLive)
            {
                MelonLogger.Msg("[Elo] Live rating updates disabled; round not rated.");
                return;
            }

            var rr = new RoundResult { gametype = r.gametype, winner = winFaction, durationS = dur };
            var factionTeam = new Dictionary<string, Team>();
            foreach (var team in r.teams)
            {
                var tr = new TeamResult { faction = GameApi.Faction(team) };
                factionTeam[tr.faction] = team;
                var agg = new Dictionary<string, TenureResult>();
                foreach (var t in r.tenures.Where(x => x.team == team).OrderBy(x => x.start))
                {
                    if (!agg.TryGetValue(t.steam, out var a))
                    {
                        a = new TenureResult { steam = t.steam, name = t.name };
                        agg[t.steam] = a;
                    }
                    a.seconds += Math.Max(0, t.end - t.start);
                    a.endS = t.end - r.startTime;
                    a.endReason = t.reason;
                    a.name = t.name;
                }
                tr.commanders = agg.Values.OrderByDescending(a => a.seconds).ToList();
                if (r.presence.TryGetValue(team, out var pres))
                    foreach (var kv in pres) tr.players[kv.Key] = kv.Value;
                rr.teams.Add(tr);
            }

            var changes = Db.ApplyRound(rr, out string summary);
            Db.Save();
            MelonLogger.Msg($"[Elo] Round rated: {summary}; {dur:0}s, winner {winFaction}, {changes.Count} commander change(s).");

            if (Cfg.AnnounceRatingChanges && changes.Count > 0)
            {
                foreach (var c in changes.OrderBy(c => c.faction))
                {
                    factionTeam.TryGetValue(c.faction, out var team);
                    double d = c.after - c.before;
                    string line = $"{GameApi.Colored(team, c.name)} ({GameApi.Short(c.faction)} cmdr, {c.share * 100:0}% of round): {c.before:0} -> {c.after:0} ({(d >= 0 ? "+" : "")}{d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)})";
                    if (c.lateLeave) line += " [left before the end, loss still counted]";
                    GameApi.SayAll(line);
                }
            }
        }
    }
}
