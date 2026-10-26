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
           Elo gap past the limit is moved right away (the game's own team menu does the same; a dead unit
           still counts as the player's ControlledUnit, so "wait for the respawn" never fired - 0.6.3), so no
           unit is destroyed under them. The list is cleared at round start and end.
   blocs   (0.7.0, for Si_Alliance) AllianceHook.GroupOf maps each team to a group id; teams with the same id
           are one bloc and every rule above works on blocs: bloc count = sum of the member teams' connected
           players, bloc Elo = player-weighted mean, a join to T is allowed when (bloc(T) + 1) - min(other
           blocs) <= maxDiff (2-/3-team formula by number of blocs), a count redirect goes to a team of the
           smallest bloc (ties: smallest team). Hook null or all ids distinct = exactly the per-team rules.
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
    /// <summary>
    /// Cross-mod hook for Si_Alliance (0.7.0). GroupOf returns a group id per team; teams with the same id form one
    /// bloc and the balance rules (join, redirect, !reserve, the volunteer ask, !elo balance) treat each bloc as one
    /// side. null (the default) or a hook that returns a distinct id per team = plain per-team balance. Si_Alliance
    /// sets it by reflection while an alliance stands and clears it to null on dissolve / round end. Exceptions from
    /// the hook are caught; that team then counts as its own bloc (team.Index).
    /// </summary>
    public static class AllianceHook
    {
        public static Func<Team, int>? GroupOf;
    }

    /// <summary>
    /// Pure placement rules over per-team counts and mean player Elo (the mover is never part of the states).
    /// Since 0.7.0 the balance unit is the bloc: teams with the same non-null TeamState.Group (AllianceHook.GroupOf)
    /// count as one side (bloc count = sum of member counts, bloc Elo = player-weighted mean). With every Group null
    /// or distinct each team is its own bloc and every result is bit for bit the 0.6.3 one.
    /// </summary>
    public static class BalanceRule
    {
        public class TeamState
        {
            public string Name = "";
            public int Count;
            public double MeanElo;
            /// <summary>Bloc id (AllianceHook.GroupOf). Teams with the same non-null Group form one bloc; null = a bloc of its own.</summary>
            public int? Group;
        }

        /// <summary>One bloc: member team indices (team order), summed count, player-weighted mean Elo (0 when empty).</summary>
        public class Bloc
        {
            public List<int> Members = new List<int>();
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

        // ---- blocs --------------------------------------------------------------
        public static bool SameBloc(List<TeamState> s, int a, int b) => a == b || (s[a].Group.HasValue && s[a].Group == s[b].Group);

        /// <summary>First index of each bloc, in team order.</summary>
        static IEnumerable<int> Leaders(List<TeamState> s)
        {
            for (int i = 0; i < s.Count; i++)
            {
                bool first = true;
                for (int j = 0; j < i && first; j++) if (SameBloc(s, j, i)) first = false;
                if (first) yield return i;
            }
        }

        /// <summary>Number of blocs (= number of teams without an alliance); feeds MaxDifference's 2-/3-team switch.</summary>
        public static int BlocTotal(List<TeamState> s) => Leaders(s).Count();

        /// <summary>Players in the bloc of team `i`.</summary>
        public static int BlocCount(List<TeamState> s, int i)
        {
            int n = 0;
            for (int j = 0; j < s.Count; j++) if (SameBloc(s, i, j)) n += s[j].Count;
            return n;
        }

        /// <summary>
        /// Mean Elo of the bloc of team `lead` with a player of rating rA added to team toA and rB to toB (-1 = none);
        /// false when the bloc is empty. `keepUntouched`: a single-team bloc nobody joins returns its MeanElo as is
        /// (Gap/GapAfter did so in 0.6.3, CanSwap recomputed sum / n), which keeps the old arithmetic bit for bit.
        /// </summary>
        static bool BlocMean(List<TeamState> s, int lead, int toA, double rA, int toB, double rB, bool keepUntouched, out double mean)
        {
            int n = 0, members = 0; double sum = 0; bool touched = false;
            for (int j = 0; j < s.Count; j++)
            {
                if (!SameBloc(s, lead, j)) continue;
                members++;
                n += s[j].Count; sum += s[j].MeanElo * s[j].Count;
                if (j == toA) { n++; sum += rA; touched = true; }
                if (j == toB) { n++; sum += rB; touched = true; }
            }
            mean = 0;
            if (n == 0) return false;
            mean = keepUntouched && members == 1 && !touched ? s[lead].MeanElo : sum / n;
            return true;
        }

        /// <summary>The blocs with summed counts and weighted mean Elo, in team order (display, volunteer ask).</summary>
        public static List<Bloc> Blocs(List<TeamState> s)
        {
            var list = new List<Bloc>();
            foreach (int l in Leaders(s))
            {
                var b = new Bloc();
                for (int j = 0; j < s.Count; j++) if (SameBloc(s, l, j)) b.Members.Add(j);
                b.Count = BlocCount(s, l);
                b.MeanElo = BlocMean(s, l, -1, 0, -1, 0, true, out double m) ? m : 0;
                list.Add(b);
            }
            return list;
        }

        // ---- counts -------------------------------------------------------------
        /// <summary>Counts after joining `target`: its bloc's count + 1 minus the smallest other bloc.</summary>
        static int DiffAfterJoin(List<TeamState> s, int target)
        {
            int min = int.MaxValue;
            foreach (int l in Leaders(s))
            {
                if (SameBloc(s, l, target)) continue;
                int c = BlocCount(s, l);
                if (c < min) min = c;
            }
            if (min == int.MaxValue) return 0;
            return BlocCount(s, target) + 1 - min;
        }

        public static bool CountOk(List<TeamState> s, int target, int maxDiff) => DiffAfterJoin(s, target) <= maxDiff;

        // ---- Elo ------------------------------------------------------------------
        /// <summary>Gap between the strongest and the weakest populated bloc (mean player Elo).</summary>
        public static double Gap(List<TeamState> s)
        {
            double max = double.NegativeInfinity, min = double.PositiveInfinity;
            foreach (int l in Leaders(s))
            {
                if (!BlocMean(s, l, -1, 0, -1, 0, true, out double m)) continue;
                if (m > max) max = m; if (m < min) min = m;
            }
            return max > min ? max - min : 0;
        }

        /// <summary>Gap after a player of `rating` joins `target`.</summary>
        public static double GapAfter(List<TeamState> s, int target, double rating)
        {
            double max = double.NegativeInfinity, min = double.PositiveInfinity;
            foreach (int l in Leaders(s))
            {
                if (!BlocMean(s, l, target, rating, -1, 0, true, out double m)) continue;
                if (m > max) max = m; if (m < min) min = m;
            }
            return max > min ? max - min : 0;
        }

        /// <summary>
        /// Placement for a join request. `states` exclude the requesting player. `isSwitch` = the player already has a
        /// team (then the only outcomes are Allow and Deny: nobody is redirected against a choice they already made).
        /// A count redirect goes to a team of the smallest eligible bloc (ties: the smallest team); an Elo redirect only
        /// to a team outside the target's bloc.
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
                if (eligible.Count == 0) eligible.Add(Enumerable.Range(0, states.Count).OrderBy(i => BlocCount(states, i)).ThenBy(i => states[i].Count).First());
                redirectTo = eloActive
                    ? eligible.OrderBy(i => GapAfter(states, i, rating)).ThenBy(i => BlocCount(states, i)).ThenBy(i => states[i].Count).First()
                    : eligible.OrderBy(i => BlocCount(states, i)).ThenBy(i => states[i].Count).First();
                return Verdict.Redirect;
            }

            if (eloActive && !isSwitch && Leaders(states).All(l => BlocCount(states, l) >= minPlayers))
            {
                double gap = GapAfter(states, target, rating);
                if (gap > eloGapMax)
                {
                    int alt = -1; double altGap = gap;
                    for (int i = 0; i < states.Count; i++)
                    {
                        if (SameBloc(states, i, target) || !CountOk(states, i, maxDiff)) continue;
                        double g = GapAfter(states, i, rating);
                        if (g < altGap - 1e-9) { alt = i; altGap = g; }
                    }
                    if (alt >= 0) { redirectTo = alt; reason = "Elo balance"; return Verdict.Redirect; }
                }
            }
            return Verdict.Allow;
        }

        /// <summary>Two mirrored reservations (A to `toA`, B to `toB`, both excluded from `states`): counts stay as they are, only the Elo gap is checked.</summary>
        public static bool CanSwap(List<TeamState> states, int toA, int toB, double ratingA, double ratingB, double eloGapMax, out string why)
        {
            why = "";
            if (toA < 0 || toB < 0 || toA >= states.Count || toB >= states.Count || toA == toB) { why = "teams not in this match"; return false; }
            double before = Gap(states);
            double max = double.NegativeInfinity, min = double.PositiveInfinity;
            foreach (int l in Leaders(states))
            {
                if (!BlocMean(states, l, toA, ratingA, toB, ratingB, false, out double m)) continue;
                if (m > max) max = m; if (m < min) min = m;
            }
            double after = max > min ? max - min : 0;
            if (after > eloGapMax && after > before + 1e-9) { why = $"the swap would widen the Elo gap to {after:0}"; return false; }
            return true;
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
        class Reservation
        {
            public string Steam = ""; public string Name = ""; public string Faction = ""; public float Since;
            /// <summary>Steam ID of a mirrored reservation this one was approved with as a swap (counts restored once both moved).</summary>
            public string? Partner;
        }
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
        static List<BalanceRule.TeamState> TeamStates(MP_Strategy strat, Player? exclude, out List<Team> teams, Player? exclude2 = null)
        {
            teams = new List<Team>();
            var states = new List<BalanceRule.TeamState>();
            try
            {
                var hook = AllianceHook.GroupOf;
                foreach (var setup in strat.BaseTeamSetups)
                {
                    if (setup == null || setup.Team == null || !strat.GetTeamSetupActive(setup)) continue;
                    bool lost = false; try { lost = setup.GetHasLost(); } catch { }
                    if (lost) continue;
                    teams.Add(setup.Team);
                    states.Add(new BalanceRule.TeamState { Name = GameApi.Faction(setup.Team), Group = hook != null ? GroupOf(hook, setup.Team) : (int?)null });
                }
                var sums = new double[states.Count];
                foreach (var p in GameApi.ConnectedPlayers())
                {
                    if (p == null || ReferenceEquals(p, exclude) || (exclude2 != null && ReferenceEquals(p, exclude2))) continue;
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

        static bool _groupOfFailed;

        /// <summary>AllianceHook.GroupOf for one team; a throwing hook counts the team as its own bloc (team.Index), warned once.</summary>
        static int? GroupOf(Func<Team, int> hook, Team team)
        {
            try { return hook(team); }
            catch (Exception ex)
            {
                if (!_groupOfFailed) { _groupOfFailed = true; MelonLogger.Warning("[Elo] Balance: AllianceHook.GroupOf threw, team counted on its own: " + ex.Message); }
                try { return team.Index; } catch { return null; }
            }
        }

        /// <summary>Allowed head-count difference; the 2-/3-team formula follows the number of blocs (an alliance of two of three teams = 2 sides).</summary>
        static int MaxDiffNow(List<BalanceRule.TeamState> states)
        {
            var (twoDiv, twoAdd, threeDiv, threeAdd, _) = CountPrefs();
            int total = 0; try { total = Player.Players.Count; } catch { }
            return BalanceRule.MaxDifference(total, BalanceRule.BlocTotal(states), twoDiv, twoAdd, threeDiv, threeAdd);
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

        /// <summary>"Sol 1520 (5) / Cent 1490 (4)"; allied teams appear as one bloc ("Sol+Cent 1505 (9) / Alien 1500 (8)").</summary>
        static string Means(List<BalanceRule.TeamState> s) => string.Join(" / ", BalanceRule.Blocs(s).Where(b => b.Count > 0).Select(b => $"{BlocName(s, b)} {b.MeanElo:0} ({b.Count})"));

        static string BlocName(List<BalanceRule.TeamState> s, BalanceRule.Bloc b) => string.Join("+", b.Members.Select(i => GameApi.Short(s[i].Name)));

        /// <summary>The member of a bloc a volunteer should pick: the smallest team (first on ties).</summary>
        static string BlocJoinTeam(List<BalanceRule.TeamState> s, BalanceRule.Bloc b) => GameApi.Short(s[b.Members.OrderBy(i => s[i].Count).First()].Name);

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
            int maxDiff = MaxDiffNow(states);
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
            PairReservations(strat, online);
            foreach (var r in _reserve.ToList())
            {
                if (!online.TryGetValue(r.Steam, out var player)) { Drop(r, moved: false); continue; }
                var states = TeamStates(strat, player, out var teams);
                int target = -1;
                for (int i = 0; i < teams.Count; i++) if (GameApi.Faction(teams[i]) == r.Faction) target = i;
                if (target < 0) { GameApi.Say(player, $"{r.Faction} is not in this match any more; reservation dropped."); Drop(r, moved: false); continue; }
                if (ReferenceEquals(player.Team, teams[target])) { Drop(r, moved: true); continue; }
                // an approved swap partner moves regardless of the counts: the other half restores them
                if (r.Partner == null && !BalanceRule.CanMove(states, target, Db.PlayerRating(r.Steam), MaxDiffNow(states), Cfg.BalanceEloGapMax, out _)) continue;
                if (MoveReserved(r, player, teams[target])) break;   // one move per tick, counts changed
            }
        }

        /// <summary>Move a queued player now: unit destroyed, team set, respawned on the new team (what the game's team menu does). Removes the reservation either way.</summary>
        static bool MoveReserved(Reservation r, Player player, Team team)
        {
            if (!GameApi.MovePlayer(player, team)) { Drop(r, moved: false); GameApi.Say(player, $"Could not move you to {GameApi.Short(r.Faction)}; reservation dropped."); return false; }
            GameApi.RespawnIfUnitless(player);
            string partner = r.Partner != null ? _reserve.FirstOrDefault(x => x.Steam == r.Partner)?.Name ?? "" : "";
            Drop(r, moved: true);
            MelonLogger.Msg($"[Elo] Reserve: {player.PlayerName} moved to {r.Faction} after {Time.time - r.Since:0}s" + (partner.Length > 0 ? $" (swap with {partner})" : "") + ".");
            GameApi.SayAll($"{GameApi.Colored(team, player.PlayerName)} moves to {GameApi.Short(r.Faction)} (" + (partner.Length > 0 ? $"swap with {partner}" : "reserved spot") + ").");
            return true;
        }

        /// <summary>Remove a reservation. A partner still waiting keeps its approval when this one moved (it owes the counts a move) and loses it otherwise.</summary>
        static void Drop(Reservation r, bool moved)
        {
            _reserve.Remove(r);
            if (r.Partner == null) return;
            var other = _reserve.FirstOrDefault(x => x.Steam == r.Partner);
            if (other != null && !moved) other.Partner = null;
        }

        /// <summary>Mirrored reservations (A on Sol wants Cent, B on Cent wants Sol) are approved as a swap when the Elo gap allows it.</summary>
        static void PairReservations(MP_Strategy strat, Dictionary<string, Player> online)
        {
            foreach (var a in _reserve)
            {
                if (a.Partner != null || !online.TryGetValue(a.Steam, out var pa) || pa.Team == null) continue;
                string aFrom = GameApi.Faction(pa.Team);
                foreach (var b in _reserve)
                {
                    if (ReferenceEquals(a, b) || b.Partner != null || !online.TryGetValue(b.Steam, out var pb) || pb.Team == null) continue;
                    if (GameApi.Faction(pb.Team) != a.Faction || b.Faction != aFrom) continue;
                    var states = TeamStates(strat, pa, out var teams, pb);
                    int toA = -1, toB = -1;
                    for (int i = 0; i < teams.Count; i++) { if (GameApi.Faction(teams[i]) == a.Faction) toA = i; if (GameApi.Faction(teams[i]) == b.Faction) toB = i; }
                    if (!BalanceRule.CanSwap(states, toA, toB, Db.PlayerRating(a.Steam), Db.PlayerRating(b.Steam), Cfg.BalanceEloGapMax, out string why))
                    {
                        if (Time.time - a.Since < 3f) GameApi.Say(pa, $"{b.Name} wants the opposite move, but {why}; both of you stay queued.");
                        continue;
                    }
                    a.Partner = b.Steam; b.Partner = a.Steam;
                    MelonLogger.Msg($"[Elo] Reserve: swap approved {a.Name} -> {a.Faction} / {b.Name} -> {b.Faction}.");
                    GameApi.Say(pa, $"Swap arranged with {b.Name}: you move to {GameApi.Short(a.Faction)}, they move to {GameApi.Short(b.Faction)}.");
                    GameApi.Say(pb, $"Swap arranged with {a.Name}: you move to {GameApi.Short(b.Faction)}, they move to {GameApi.Short(a.Faction)}.");
                    break;
                }
            }
        }

        static void AskVolunteers(MP_Strategy strat)
        {
            if (Cfg.BalanceAskIntervalSeconds <= 0) return;
            if (Time.time - _lastAsk < Cfg.BalanceAskIntervalSeconds) return;
            if (EloPlacementActive(strat)) return;
            _lastAsk = Time.time;
            var states = TeamStates(strat, null, out var teams);
            var blocs = BalanceRule.Blocs(states);   // allied teams count as one side (AllianceHook)
            if (blocs.Count < 2) return;
            int maxCount = blocs.Max(b => b.Count), minCount = blocs.Min(b => b.Count);
            if (maxCount == 0) return;   // nobody on a team (0.6.2 threw here on an empty server)
            var shortBloc = blocs.First(b => b.Count == minCount);
            double gap = BalanceRule.Gap(states);
            var weak = blocs.Where(b => b.Count > 0).OrderBy(b => b.MeanElo).First();
            if (maxCount - minCount >= 2)
                GameApi.SayAll($"{BlocName(states, shortBloc)} is short on players ({minCount} vs {maxCount}). Switch via the team menu or type !reserve {BlocJoinTeam(states, shortBloc).ToLowerInvariant()}.");
            else if (gap > Cfg.BalanceEloGapMax && blocs.All(b => b.Count >= Cfg.BalanceEloMinPlayersPerTeam))
                GameApi.SayAll($"{BlocName(states, weak)} is the weaker team by player Elo ({Means(states)}). Volunteers: !reserve {BlocJoinTeam(states, weak).ToLowerInvariant()}.");
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
                GameApi.Say(caller, (mine == null ? "No reservation. " : $"You wait for {GameApi.Short(mine.Faction)}. ") + $"Queue: {list}. Usage: !reserve sol|cent|alien, !reserve off. You are moved as soon as a spot is free and the Elo balance allows it.");
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
            if (mine != null)
            {
                // a changed wish drops a swap approval on both sides (Drop clears the partner's); the reservation itself stays
                if (mine.Faction != faction && mine.Partner != null) { Drop(mine, moved: false); mine.Partner = null; _reserve.Add(mine); }
                mine.Faction = faction; mine.Since = Time.time;
            }
            else { mine = new Reservation { Steam = steam, Name = caller.PlayerName ?? steam, Faction = faction, Since = Time.time }; _reserve.Add(mine); }
            var states = TeamStates(strat, caller, out var teams);
            int target = IndexOfTeam(teams, team);
            string why = "team not in this match";
            if (target >= 0 && BalanceRule.CanMove(states, target, Db.PlayerRating(steam), MaxDiffNow(states), Cfg.BalanceEloGapMax, out why))
            {
                // the spot is open: move right now, like the game's own team menu would
                MoveReserved(mine, caller, teams[target]);
                return;
            }
            int pos = _reserve.FindIndex(r => r.Steam == steam) + 1;
            MelonLogger.Msg($"[Elo] Reserve: {caller.PlayerName} queued for {faction} (position {pos}): {why}.");
            GameApi.Say(caller, $"Reserved a spot on {GameApi.Short(faction)} (position {pos}); waiting: {why}. You move the moment it is possible. !reserve off to cancel.");
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
                if (states.Count >= 2) GameApi.Say(caller, $"Now: {Means(states)}, gap {BalanceRule.Gap(states):0}, allowed head-count difference {MaxDiffNow(states)}, Elo placement {(EloPlacementActive(strat) ? "active" : "inactive")}.");
            }
        }
    }
}
