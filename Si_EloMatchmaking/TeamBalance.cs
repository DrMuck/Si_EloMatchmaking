/*
 TeamBalance.cs - Elo-aware team placement, the mid-round volunteer ask and !reserve.

 Takes over the REQUEST_JOIN_TEAM handling that Si_BasicTeamBalance (databomb) does in a prefix on
 MP_Strategy.ProcessNetRPC. Two mods cannot share that packet (the first reader consumes it), so at
 start-up this mod removes Si_BasicTeamBalance's prefix on MP_Strategy.ProcessNetRPC (only that one:
 its MP_TowerDefense prefix and its !team admin command stay) and handles the join itself with the
 same head-count rule, reading Si_BasicTeamBalance's own MelonPreferences values so there is one
 config for the counts. Set BalanceTakeOverJoinHandling=false to leave Si_BasicTeamBalance alone
 (then only !reserve and the ask remain).

 Rules (BalanceRule.Decide, pure, covered by the scenario harness):
   counts  a join may not leave the target team more than maxDiff players ahead of the smallest
           team (maxDiff = ceil(players / divisor + addend), the Si_BasicTeamBalance formula; this
           checks the counts AFTER the join, one player stricter than Si_BasicTeamBalance).
           A fresh joiner who asks for a too-full team is placed on the team that needs them most;
           a player who already has a team is denied the switch (the game's team menu clears).
   elo     only for FRESH joiners (no team yet), only during the countdown and the first
           BalanceEloWindowSeconds of a round, and only when every team already has
           BalanceEloMinPlayersPerTeam players: when their pick would push the gap in mean player Elo
           above BalanceEloGapMax and another count-eligible team closes it, they go there instead.
           Players who have a team are never redirected for Elo, nobody is moved mid-round.
   ask     every BalanceAskIntervalSeconds during a round: when a team is short by two or more or
           weaker by more than the Elo gap, chat asks for volunteers (team menu or !reserve).
   reserve !reserve <team> queues for a spot on that team; !reserve off leaves, !reserve shows the
           queue. Every 2 s the first in line whose move keeps the counts legal and does not widen the
           Elo gap past the limit is moved, at their next respawn (BalanceReserveAtRespawnOnly) so no
           unit is destroyed under them. The list is cleared at round start and end.
*/
using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Si_EloMatchmaking
{
    /// <summary>Pure placement rules over per-team counts and mean player Elo (the mover is never part of the states).</summary>
    public static class BalanceRule
    {
        public class TeamState
        {
            public string Name = "";
            public int Count;
            public double MeanElo;
        }

        public enum Verdict { Allow, Deny, Redirect }

        /// <summary>Si_BasicTeamBalance's allowed head-count difference.</summary>
        public static int MaxDifference(int totalPlayers, int activeTeams, double twoDiv, double twoAdd, double threeDiv, double threeAdd)
        {
            double div = activeTeams >= 3 ? threeDiv : twoDiv, add = activeTeams >= 3 ? threeAdd : twoAdd;
            if (div <= 0) div = 1;
            return Math.Max(0, (int)Math.Ceiling(totalPlayers / div + add));
        }

        /// <summary>Counts after joining `target`: difference to the smallest other team.</summary>
        static int DiffAfterJoin(List<TeamState> s, int target)
        {
            int min = int.MaxValue;
            for (int i = 0; i < s.Count; i++) if (i != target && s[i].Count < min) min = s[i].Count;
            if (min == int.MaxValue) return 0;
            return s[target].Count + 1 - min;
        }

        public static bool CountOk(List<TeamState> s, int target, int maxDiff) => DiffAfterJoin(s, target) <= maxDiff;

        /// <summary>Gap between the strongest and the weakest populated team (mean player Elo).</summary>
        public static double Gap(List<TeamState> s)
        {
            double max = double.NegativeInfinity, min = double.PositiveInfinity;
            foreach (var t in s) { if (t.Count == 0) continue; if (t.MeanElo > max) max = t.MeanElo; if (t.MeanElo < min) min = t.MeanElo; }
            return max > min ? max - min : 0;
        }

        /// <summary>Gap after a player of `rating` joins `target`.</summary>
        public static double GapAfter(List<TeamState> s, int target, double rating)
        {
            double max = double.NegativeInfinity, min = double.PositiveInfinity;
            for (int i = 0; i < s.Count; i++)
            {
                int n = s[i].Count + (i == target ? 1 : 0);
                if (n == 0) continue;
                double m = i == target ? (s[i].MeanElo * s[i].Count + rating) / n : s[i].MeanElo;
                if (m > max) max = m; if (m < min) min = m;
            }
            return max > min ? max - min : 0;
        }

        /// <summary>
        /// Placement for a join request. `states` exclude the requesting player. `isSwitch` = the player already has a
        /// team (then the only outcomes are Allow and Deny: nobody is redirected against a choice they already made).
        /// </summary>
        public static Verdict Decide(List<TeamState> states, int target, bool isSwitch, double rating, int maxDiff,
                                     bool eloActive, double eloGapMax, int minPlayers, out int redirectTo, out string reason)
        {
            redirectTo = -1; reason = "";
            if (target < 0 || target >= states.Count) return Verdict.Allow;

            if (!CountOk(states, target, maxDiff))
            {
                reason = "team imbalance";
                if (isSwitch) return Verdict.Deny;
                var eligible = Enumerable.Range(0, states.Count).Where(i => CountOk(states, i, maxDiff)).ToList();
                if (eligible.Count == 0) eligible.Add(Enumerable.Range(0, states.Count).OrderBy(i => states[i].Count).First());
                redirectTo = eloActive
                    ? eligible.OrderBy(i => GapAfter(states, i, rating)).ThenBy(i => states[i].Count).First()
                    : eligible.OrderBy(i => states[i].Count).First();
                return Verdict.Redirect;
            }

            if (eloActive && !isSwitch && states.All(t => t.Count >= minPlayers))
            {
                double gap = GapAfter(states, target, rating);
                if (gap > eloGapMax)
                {
                    int alt = -1; double altGap = gap;
                    for (int i = 0; i < states.Count; i++)
                    {
                        if (i == target || !CountOk(states, i, maxDiff)) continue;
                        double g = GapAfter(states, i, rating);
                        if (g < altGap - 1e-9) { alt = i; altGap = g; }
                    }
                    if (alt >= 0) { redirectTo = alt; reason = "Elo balance"; return Verdict.Redirect; }
                }
            }
            return Verdict.Allow;
        }

        /// <summary>!reserve: may the queued player (excluded from `states`) move to `target` now?</summary>
        public static bool CanMove(List<TeamState> states, int target, double rating, int maxDiff, double eloGapMax, out string why)
        {
            why = "";
            if (target < 0 || target >= states.Count) { why = "team not in this match"; return false; }
            if (!CountOk(states, target, maxDiff)) { why = "no free spot yet (team counts)"; return false; }
            double before = Gap(states), after = GapAfter(states, target, rating);
            if (after > eloGapMax && after > before + 1e-9) { why = $"it would widen the Elo gap to {after:0}"; return false; }
            return true;
        }
    }

    public partial class EloMod
    {
        static byte _rpcJoin, _rpcClear;
        static bool _rpcResolved;
        static bool _btbChecked;
        static float _balancePoll, _lastAsk;
        static Player? _lastBalanceChat;
        class Reservation { public string Steam = ""; public string Name = ""; public string Faction = ""; public float Since; }
        static readonly List<Reservation> _reserve = new List<Reservation>();

        // ---- set-up ---------------------------------------------------------
        static void InitBalance()
        {
            try
            {
                _rpcJoin = HelperMethods.FindByteValueInEnum(typeof(MP_Strategy), "ERPCs", "REQUEST_JOIN_TEAM");
                _rpcClear = HelperMethods.FindByteValueInEnum(typeof(MP_Strategy), "ERPCs", "CLEAR_REQUEST");
                _rpcResolved = true;
            }
            catch (Exception ex) { MelonLogger.Error("[Elo] Balance: RPC ids not found, join handling stays vanilla: " + ex.Message); }
            try { HelperMethods.RegisterPlayerCommand("reserve", OnReserveCommand, true); }
            catch (Exception ex) { MelonLogger.Error("[Elo] !reserve registration failed: " + ex.Message); }
            UnpatchBasicTeamBalance();
            var (twoDiv, twoAdd, threeDiv, threeAdd, src) = CountPrefs();
            MelonLogger.Msg($"[Elo] Balance: enabled={Cfg.BalanceEnabled} takeover={Cfg.BalanceTakeOverJoinHandling} rpc={_rpcResolved} counts from {src} (2-team /{twoDiv}+{twoAdd}, 3-team /{threeDiv}+{threeAdd}) eloGap={Cfg.BalanceEloGapMax} window={Cfg.BalanceEloWindowSeconds}s");
        }

        /// <summary>Remove Si_BasicTeamBalance's prefix on MP_Strategy.ProcessNetRPC (it would read the join packet after we consumed it).</summary>
        static void UnpatchBasicTeamBalance()
        {
            if (_btbChecked || !Cfg.BalanceEnabled || !Cfg.BalanceTakeOverJoinHandling) return;
            _btbChecked = true;
            try
            {
                var original = AccessTools.Method(typeof(MP_Strategy), "ProcessNetRPC");
                if (original == null) return;
                var info = HarmonyLib.Harmony.GetPatchInfo(original);
                if (info == null) return;
                int n = 0;
                var h = new HarmonyLib.Harmony("Si_EloMatchmaking.balance");
                foreach (var p in info.Prefixes.ToList())
                {
                    string asm = p.PatchMethod?.DeclaringType?.Assembly.GetName().Name ?? "";
                    if (!asm.Equals("Si_BasicTeamBalance", StringComparison.OrdinalIgnoreCase)) continue;
                    h.Unpatch(original, p.PatchMethod);
                    n++;
                }
                MelonLogger.Msg(n > 0 ? $"[Elo] Balance: took over the join-team handling from Si_BasicTeamBalance ({n} prefix removed; its !team command and tower-defense handling stay)."
                                      : "[Elo] Balance: Si_BasicTeamBalance join prefix not present; handling joins directly.");
            }
            catch (Exception ex) { MelonLogger.Warning("[Elo] Balance: could not unpatch Si_BasicTeamBalance: " + ex.Message); }
        }

        /// <summary>Head-count formula values: Si_BasicTeamBalance's MelonPreferences when present, else this mod's config.</summary>
        static (double twoDiv, double twoAdd, double threeDiv, double threeAdd, string source) CountPrefs()
        {
            try
            {
                var cat = MelonPreferences.GetCategory("Silica");
                if (cat != null && cat.HasEntry("TeamBalance_TwoTeam_Divisor"))
                {
                    double G(string name, double d) { try { var e = cat.GetEntry<float>(name); return e != null ? e.Value : d; } catch { return d; } }
                    return (G("TeamBalance_TwoTeam_Divisor", Cfg.BalanceTwoTeamDivisor), G("TeamBalance_TwoTeam_Addend", Cfg.BalanceTwoTeamAddend),
                            G("TeamBalance_ThreeTeam_Divisor", Cfg.BalanceThreeTeamDivisor), G("TeamBalance_ThreeTeam_Addend", Cfg.BalanceThreeTeamAddend), "Si_BasicTeamBalance prefs");
                }
            }
            catch { }
            return (Cfg.BalanceTwoTeamDivisor, Cfg.BalanceTwoTeamAddend, Cfg.BalanceThreeTeamDivisor, Cfg.BalanceThreeTeamAddend, "Si_EloMatchmaking config");
        }

        // ---- state snapshot ---------------------------------------------------
        /// <summary>Active, not-yet-lost teams with their connected players' counts and mean player Elo, `exclude` left out.</summary>
        static List<BalanceRule.TeamState> TeamStates(MP_Strategy strat, Player? exclude, out List<Team> teams)
        {
            teams = new List<Team>();
            var states = new List<BalanceRule.TeamState>();
            try
            {
                foreach (var setup in strat.BaseTeamSetups)
                {
                    if (setup == null || setup.Team == null || !strat.GetTeamSetupActive(setup)) continue;
                    bool lost = false; try { lost = setup.GetHasLost(); } catch { }
                    if (lost) continue;
                    teams.Add(setup.Team);
                    states.Add(new BalanceRule.TeamState { Name = GameApi.Faction(setup.Team) });
                }
                var sums = new double[states.Count];
                foreach (var p in GameApi.ConnectedPlayers())
                {
                    if (p == null || ReferenceEquals(p, exclude)) continue;
                    var t = p.Team;
                    if (t == null) continue;
                    int i = IndexOfTeam(teams, t);
                    if (i < 0) continue;
                    states[i].Count++;
                    sums[i] += Db.PlayerRating(GameApi.SteamId(p));
                }
                for (int i = 0; i < states.Count; i++) states[i].MeanElo = states[i].Count > 0 ? sums[i] / states[i].Count : 0;
            }
            catch (Exception ex) { MelonLogger.Warning("[Elo] Balance TeamStates: " + ex.Message); }
            return states;
        }

        static int IndexOfTeam(List<Team> teams, Team t)
        {
            for (int i = 0; i < teams.Count; i++) if (ReferenceEquals(teams[i], t)) return i;
            return -1;
        }

        static int MaxDiffNow(int activeTeams)
        {
            var (twoDiv, twoAdd, threeDiv, threeAdd, _) = CountPrefs();
            int total = 0; try { total = Player.Players.Count; } catch { }
            return BalanceRule.MaxDifference(total, activeTeams, twoDiv, twoAdd, threeDiv, threeAdd);
        }

        /// <summary>Elo placement is active during the countdown and the first BalanceEloWindowSeconds of a round.</summary>
        static bool EloPlacementActive(MP_Strategy strat)
        {
            try
            {
                if (strat.MissionState == GameModeExt.EMissionState.INIT && !strat.GameBegun) return true;
            }
            catch { }
            return _round != null && Time.time - _round.startTime <= Cfg.BalanceEloWindowSeconds;
        }

        static string Means(List<BalanceRule.TeamState> s) => string.Join(" / ", s.Where(t => t.Count > 0).Select(t => $"{GameApi.Short(t.Name)} {t.MeanElo:0} ({t.Count})"));

        // ---- the join request ---------------------------------------------------
        [HarmonyPatch(typeof(MP_Strategy), "ProcessNetRPC")]
        static class Patch_JoinTeam
        {
            [HarmonyPriority(HarmonyLib.Priority.High)]
            static bool Prefix(MP_Strategy __instance, GameByteStreamReader __0, byte __1)
            {
                bool consumed = false;
                try { return !HandleJoinRpc(__instance, __0, __1, ref consumed); }
                catch (Exception ex)
                {
                    MelonLogger.Error("[Elo] Balance join handler: " + ex);
                    return !consumed;   // once the packet is read the original must not run on it
                }
            }
        }

        /// <summary>Returns true when the request was handled here (the original and Si_BasicTeamBalance must not run).</summary>
        static bool HandleJoinRpc(MP_Strategy strat, GameByteStreamReader reader, byte rpc, ref bool consumed)
        {
            if (!Cfg.BalanceEnabled || !Cfg.BalanceTakeOverJoinHandling || !_rpcResolved || rpc != _rpcJoin) return false;
            if (reader == null) return false;
            try { if (GameMode.CurrentGameMode.GameOver) return false; } catch { }
            if (!_btbChecked) UnpatchBasicTeamBalance();

            consumed = true;
            ulong steam64 = reader.ReadUInt64();
            int channel = reader.ReadByte();
            var player = Player.FindPlayer(new NetworkID(steam64), channel);
            Team? target = reader.ReadTeam();
            if (player == null)
            {
                MelonLogger.Warning($"[Elo] Balance: join request from unknown player {steam64}/{channel}.");
                return true;
            }
            if (ReferenceEquals(player.Team, target) || target == null)
            {
                SendClearRequest(steam64, channel);
                return true;
            }
            // a player on a team that is not part of the match (game bug): let the change through
            bool currentInvalid = false;
            try { currentInvalid = player.Team != null && Event_Roles.GetTeamSetup(strat, player.Team) == null; } catch { }

            var states = TeamStates(strat, player, out var teams);
            int tIdx = IndexOfTeam(teams, target);
            if (currentInvalid || tIdx < 0 || states.Count < 2)
            {
                Place(player, target);
                return true;
            }

            bool isSwitch = player.Team != null;
            double rating = Db.PlayerRating(GameApi.SteamId(player));
            int maxDiff = MaxDiffNow(states.Count);
            bool eloActive = EloPlacementActive(strat);
            var verdict = BalanceRule.Decide(states, tIdx, isSwitch, rating, maxDiff, eloActive, Cfg.BalanceEloGapMax, Cfg.BalanceEloMinPlayersPerTeam, out int redirect, out string reason);

            switch (verdict)
            {
                case BalanceRule.Verdict.Allow:
                    Place(player, target);
                    return true;
                case BalanceRule.Verdict.Deny:
                    if (!ReferenceEquals(_lastBalanceChat, player))
                    {
                        HelperMethods.ReplyToCommand_Player(player, "'s switch was denied due to team imbalance");
                        _lastBalanceChat = player;
                    }
                    MelonLogger.Msg($"[Elo] Balance: {player.PlayerName} -> {GameApi.Faction(target)} denied ({reason}).");
                    SendClearRequest(steam64, channel);
                    return true;
                default:
                    {
                        var to = teams[redirect];
                        string why = reason == "Elo balance" ? $"Elo balance: {Means(states)}" : "team imbalance";
                        if (!ReferenceEquals(_lastBalanceChat, player))
                        {
                            HelperMethods.ReplyToCommand_Player(player, $" was placed on {GameApi.TeamColor(to)}{to.TeamShortName}</color> ({why})");
                            _lastBalanceChat = player;
                        }
                        MelonLogger.Msg($"[Elo] Balance: {player.PlayerName} asked {GameApi.Faction(target)}, placed on {GameApi.Faction(to)} ({reason}; rating {rating:0}; {Means(states)}).");
                        Place(player, to);
                        return true;
                    }
            }
        }

        static void Place(Player player, Team team)
        {
            player.Team = team;
            NetworkLayer.SendPlayerSelectTeam(player, team);
        }

        static void SendClearRequest(ulong steam64, int channel)
        {
            try
            {
                var w = GameMode.CurrentGameMode.CreateRPCPacket(_rpcClear);
                if (w == null) return;
                w.WriteUInt64(steam64);
                w.WriteByte((byte)channel);
                GameMode.CurrentGameMode.SendRPCPacket(w);
            }
            catch (Exception ex) { MelonLogger.Warning("[Elo] Balance ClearRequest: " + ex.Message); }
        }

        // ---- periodic: reserve list + volunteer ask -------------------------------
        static void TickBalance()
        {
            if (!Cfg.BalanceEnabled) return;
            _balancePoll -= Time.deltaTime;
            if (_balancePoll > 0f) return;
            _balancePoll = 2f;
            var strat = GameApi.Strat;
            if (strat == null || _round == null) return;
            ProcessReserve(strat);
            AskVolunteers(strat);
        }

        static void ClearReserve()
        {
            _reserve.Clear();
            _lastAsk = Time.time;
        }

        static void ProcessReserve(MP_Strategy strat)
        {
            if (_reserve.Count == 0) return;
            var online = GameApi.ConnectedPlayers().ToDictionary(GameApi.SteamId, p => p);
            foreach (var r in _reserve.ToList())
            {
                if (!online.TryGetValue(r.Steam, out var player)) { _reserve.Remove(r); continue; }
                var states = TeamStates(strat, player, out var teams);
                int target = -1;
                for (int i = 0; i < teams.Count; i++) if (GameApi.Faction(teams[i]) == r.Faction) target = i;
                if (target < 0) { GameApi.Say(player, $"{r.Faction} is not in this match any more; reservation dropped."); _reserve.Remove(r); continue; }
                if (ReferenceEquals(player.Team, teams[target])) { _reserve.Remove(r); continue; }
                if (Cfg.BalanceReserveAtRespawnOnly) { try { if (player.ControlledUnit != null) continue; } catch { } }
                int maxDiff = MaxDiffNow(states.Count);
                if (!BalanceRule.CanMove(states, target, Db.PlayerRating(r.Steam), maxDiff, Cfg.BalanceEloGapMax, out string why)) continue;
                if (!GameApi.MovePlayer(player, teams[target])) { _reserve.Remove(r); continue; }
                GameApi.RespawnIfUnitless(player);
                _reserve.Remove(r);
                MelonLogger.Msg($"[Elo] Reserve: {player.PlayerName} moved to {r.Faction} after {Time.time - r.Since:0}s.");
                GameApi.SayAll($"{GameApi.Colored(teams[target], player.PlayerName)} moves to {GameApi.Short(r.Faction)} (reserved spot).");
                break;   // one move per tick, counts changed
            }
        }

        static void AskVolunteers(MP_Strategy strat)
        {
            if (Cfg.BalanceAskIntervalSeconds <= 0) return;
            if (Time.time - _lastAsk < Cfg.BalanceAskIntervalSeconds) return;
            if (EloPlacementActive(strat)) return;
            _lastAsk = Time.time;
            var states = TeamStates(strat, null, out var teams);
            if (states.Count < 2) return;
            int maxCount = states.Max(t => t.Count), minCount = states.Min(t => t.Count);
            var shortTeam = states.First(t => t.Count == minCount);
            double gap = BalanceRule.Gap(states);
            var weak = states.Where(t => t.Count > 0).OrderBy(t => t.MeanElo).First();
            if (maxCount - minCount >= 2)
                GameApi.SayAll($"{GameApi.Short(shortTeam.Name)} is short on players ({minCount} vs {maxCount}). Switch via the team menu or type !reserve {GameApi.Short(shortTeam.Name).ToLowerInvariant()} to move at your next respawn.");
            else if (gap > Cfg.BalanceEloGapMax && states.All(t => t.Count >= Cfg.BalanceEloMinPlayersPerTeam))
                GameApi.SayAll($"{GameApi.Short(weak.Name)} is the weaker team by player Elo ({Means(states)}). Volunteers: !reserve {GameApi.Short(weak.Name).ToLowerInvariant()}.");
        }

        // ---- commands -----------------------------------------------------------
        static void OnReserveCommand(Player? caller, string args)
        {
            if (caller == null) { GameApi.Say(null, "!reserve is a player command."); return; }
            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            string steam = GameApi.SteamId(caller);
            var mine = _reserve.FirstOrDefault(r => r.Steam == steam);
            if (arg == "off" || arg == "cancel" || arg == "none")
            {
                if (mine == null) { GameApi.Say(caller, "You have no reservation."); return; }
                _reserve.Remove(mine);
                GameApi.Say(caller, "Reservation dropped.");
                return;
            }
            if (arg.Length == 0)
            {
                string list = _reserve.Count == 0 ? "nobody is waiting" : string.Join(", ", _reserve.Select((r, i) => $"{i + 1}. {r.Name} -> {GameApi.Short(r.Faction)}"));
                GameApi.Say(caller, (mine == null ? "No reservation. " : $"You wait for {GameApi.Short(mine.Faction)}. ") + $"Queue: {list}. Usage: !reserve sol|cent|alien, !reserve off. You are moved at your next respawn once a spot is free and the Elo balance allows it.");
                return;
            }
            if (!Cfg.BalanceEnabled) { GameApi.Say(caller, "Team balance is off on this server."); return; }
            string? faction = ParseFaction(arg);
            var strat = GameApi.Strat;
            if (faction == null || strat == null) { GameApi.Say(caller, "Usage: !reserve sol|cent|alien, !reserve off"); return; }
            var team = GameApi.ActiveTeams(strat).FirstOrDefault(t => GameApi.Faction(t) == faction);
            if (team == null) { GameApi.Say(caller, $"{faction} is not in this match."); return; }
            if (ReferenceEquals(caller.Team, team)) { GameApi.Say(caller, $"You are already on {GameApi.Short(faction)}."); return; }
            if (_round == null) { GameApi.Say(caller, "No round running; pick the team in the team menu."); return; }
            if (mine != null) { mine.Faction = faction; mine.Since = Time.time; }
            else _reserve.Add(new Reservation { Steam = steam, Name = caller.PlayerName ?? steam, Faction = faction, Since = Time.time });
            int pos = _reserve.FindIndex(r => r.Steam == steam) + 1;
            var states = TeamStates(strat, caller, out var teams);
            int target = IndexOfTeam(teams, team);
            string why = "team not in this match";
            string now = target >= 0 && BalanceRule.CanMove(states, target, Db.PlayerRating(steam), MaxDiffNow(states.Count), Cfg.BalanceEloGapMax, out why) ? "a spot is free, you move at your next respawn" : $"waiting: {why}";
            GameApi.Say(caller, $"Reserved a spot on {GameApi.Short(faction)} (position {pos}); {now}. !reserve off to cancel.");
        }

        static void HandleBalance(Player? caller, string rest)
        {
            string arg = (rest ?? "").Trim().ToLowerInvariant();
            var strat = GameApi.Strat;
            if (arg == "on" || arg == "off")
            {
                if (!IsAdmin(caller)) { GameApi.Say(caller, "Admin only."); return; }
                Cfg.BalanceEnabled = arg == "on"; ConfigStore.Save(Cfg);
                if (Cfg.BalanceEnabled) UnpatchBasicTeamBalance();
                GameApi.SayAll(Cfg.BalanceEnabled ? "Team balance ON: Elo-aware placement at round start, !reserve for team changes." : "Team balance OFF: joins are handled by the game / Si_BasicTeamBalance (if its prefix is still in place); !reserve is off.");
                return;
            }
            var (twoDiv, twoAdd, threeDiv, threeAdd, src) = CountPrefs();
            GameApi.Say(caller, $"Team balance {(Cfg.BalanceEnabled ? "ON" : "OFF")}: counts {src} (2-team /{twoDiv}+{twoAdd}, 3-team /{threeDiv}+{threeAdd}), Elo gap limit {Cfg.BalanceEloGapMax:0} with >= {Cfg.BalanceEloMinPlayersPerTeam} per team during the countdown + first {Cfg.BalanceEloWindowSeconds}s; reserve list: {_reserve.Count}.");
            if (strat != null)
            {
                var states = TeamStates(strat, null, out _);
                if (states.Count >= 2) GameApi.Say(caller, $"Now: {Means(states)}, gap {BalanceRule.Gap(states):0}, allowed head-count difference {MaxDiffNow(states.Count)}, Elo placement {(EloPlacementActive(strat) ? "active" : "inactive")}.");
            }
        }
    }
}
