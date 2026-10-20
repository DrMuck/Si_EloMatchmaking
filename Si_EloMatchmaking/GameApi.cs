/*
 GameApi.cs - thin reflection layer over the game (SilicaCore, Mono dedicated
 server) and over Si_CommManagement, so the mod has no compile-time dependency
 on private members or on CommManagement being installed.

 Verified against the 0.9.47 decompile (default branch; SetCommander/RPC_* are private there,
 lines 1511/963/935) and the 1.0.2 decompile (C:\tmp\silica_decompile_102\MP_Strategy.cs):
   private Dictionary<Team, List<Player>> CommanderLottery          (line 155)
   protected void SetCommander(Team team, Player player)              (line ~1553)
   protected void RPC_SynchCommander(Team team, bool kicked = false)  (line 949)
   protected void RPC_SynchRole(Player player, ETeamRole role)        (line 921)
   public List<StrategyTeamSetup> TeamSetups                          (line 82)
*/
using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Si_EloMatchmaking
{
    public static class GameApi
    {
        static readonly FieldInfo? _lotteryField = AccessTools.Field(typeof(MP_Strategy), "CommanderLottery");
        static readonly MethodInfo? _setCommander = AccessTools.Method(typeof(MP_Strategy), "SetCommander", new[] { typeof(Team), typeof(Player) });
        static readonly MethodInfo? _synchCommander = AccessTools.Method(typeof(MP_Strategy), "RPC_SynchCommander");
        static readonly MethodInfo? _synchRole = AccessTools.Method(typeof(MP_Strategy), "RPC_SynchRole", new[] { typeof(Player), typeof(GameModeExt.ETeamRole) });

        // Si_CommManagement.CommanderApplications.commanderApplicants (public static List<Player>[])
        static Type? _commApplicationsType;
        static FieldInfo? _commApplicantsField;
        static bool _commLookedUp;

        public static MP_Strategy? Strat => GameMode.CurrentGameMode as MP_Strategy;

        public static void LogReflectionState()
        {
            MelonLogger.Msg($"[Elo] Reflection: CommanderLottery={(_lotteryField != null)} SetCommander={(_setCommander != null)} " +
                            $"RPC_SynchCommander={(_synchCommander != null)}({_synchCommander?.GetParameters().Length}) RPC_SynchRole={(_synchRole != null)}");
        }

        // ---- identity -----------------------------------------------------
        public static string SteamId(Player p)
        {
            try { return p.PlayerID.m_ID.ToString(); }
            catch { return "0"; }
        }

        public static string Faction(Team? t)
        {
            if (t == null) return "";
            string n = t.TeamShortName ?? "";
            if (n.StartsWith("Sol", StringComparison.OrdinalIgnoreCase)) return "Sol";
            if (n.StartsWith("Cent", StringComparison.OrdinalIgnoreCase)) return "Centauri";
            if (n.StartsWith("Alien", StringComparison.OrdinalIgnoreCase)) return "Alien";
            return n;
        }

        public static bool IsHumanFaction(string f) => f == "Sol" || f == "Centauri";

        public static string Short(string faction) => faction == "Centauri" ? "Cent" : faction;

        /// <summary>Teams with an active strategy setup in the current versus mode.</summary>
        public static List<Team> ActiveTeams(MP_Strategy strat)
        {
            var res = new List<Team>();
            try
            {
                foreach (var setup in strat.TeamSetups)
                {
                    if (setup == null || setup.Team == null) continue;
                    if (!strat.GetTeamSetupActive(setup)) continue;
                    res.Add(setup.Team);
                }
            }
            catch (Exception ex) { MelonLogger.Error("[Elo] ActiveTeams: " + ex.Message); }
            return res;
        }

        public static Player? CommanderOf(MP_Strategy strat, Team team)
        {
            try { return strat.GetCommanderForTeam(team); } catch { return null; }
        }

        public static bool IsServerPlayer(Player p)
        {
            try { return p == NetworkGameServer.GetServerPlayer(); } catch { return false; }
        }

        public static IEnumerable<Player> ConnectedPlayers()
        {
            foreach (var p in Player.Players)
            {
                if (p == null) continue;
                if (IsServerPlayer(p)) continue;
                yield return p;
            }
        }

        // ---- applicant pools ------------------------------------------------
        /// <summary>Snapshot of the vanilla commander lottery (Commander button before round start).</summary>
        public static Dictionary<Team, List<Player>> VanillaLottery(MP_Strategy strat)
        {
            var res = new Dictionary<Team, List<Player>>();
            if (_lotteryField == null) return res;
            try
            {
                if (_lotteryField.GetValue(strat) is Dictionary<Team, List<Player>> dict)
                    foreach (var kv in dict)
                        if (kv.Key != null && kv.Value != null && kv.Value.Count > 0)
                            res[kv.Key] = kv.Value.Where(p => p != null).ToList();
            }
            catch (Exception ex) { MelonLogger.Error("[Elo] VanillaLottery read: " + ex.Message); }
            return res;
        }

        /// <summary>Empty the vanilla lottery lists so ResolveCommanderLottery() seats nobody.</summary>
        public static void ClearVanillaLottery(MP_Strategy strat)
        {
            if (_lotteryField == null) return;
            try
            {
                if (_lotteryField.GetValue(strat) is Dictionary<Team, List<Player>> dict)
                    foreach (var kv in dict) kv.Value?.Clear();
            }
            catch (Exception ex) { MelonLogger.Error("[Elo] VanillaLottery clear: " + ex.Message); }
        }

        static void LookupCommMgmt()
        {
            if (_commLookedUp) return;
            _commLookedUp = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType("Si_CommManagement.CommanderApplications");
                    if (t == null) continue;
                    _commApplicationsType = t;
                    _commApplicantsField = t.GetField("commanderApplicants", BindingFlags.Public | BindingFlags.Static);
                    MelonLogger.Msg($"[Elo] Si_CommManagement found; commanderApplicants={(_commApplicantsField != null)}");
                    return;
                }
                catch { }
            }
            MelonLogger.Msg("[Elo] Si_CommManagement not loaded; only the in-game Commander button feeds the pool.");
        }

        public static bool HasCommMgmt { get { LookupCommMgmt(); return _commApplicantsField != null; } }

        /// <summary>Snapshot of Si_CommManagement's !commander applicants, keyed by Team.</summary>
        public static Dictionary<Team, List<Player>> CommMgmtApplicants()
        {
            var res = new Dictionary<Team, List<Player>>();
            LookupCommMgmt();
            if (_commApplicantsField == null) return res;
            try
            {
                if (_commApplicantsField.GetValue(null) is List<Player>[] arr)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var list = arr[i];
                        if (list == null || list.Count == 0) continue;
                        if (i >= Team.Teams.Count || Team.Teams[i] == null) continue;
                        res[Team.Teams[i]] = list.Where(p => p != null).ToList();
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Error("[Elo] CommMgmt applicants read: " + ex.Message); }
            return res;
        }

        public static void ClearCommMgmtApplicants()
        {
            if (_commApplicantsField == null) return;
            try
            {
                if (_commApplicantsField.GetValue(null) is List<Player>[] arr)
                    foreach (var list in arr) list?.Clear();
            }
            catch (Exception ex) { MelonLogger.Error("[Elo] CommMgmt applicants clear: " + ex.Message); }
        }

        // ---- actions ----------------------------------------------------------
        /// <summary>Server-side team move (same call pair Si_BasicTeamBalance uses). No respawn.</summary>
        public static bool MovePlayer(Player player, Team team)
        {
            try
            {
                GameMode.CurrentGameMode.DestroyAllUnitsForPlayer(player);
                player.Team = team;
                NetworkLayer.SendPlayerSelectTeam(player, team);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Elo] MovePlayer {player?.PlayerName} -> {team?.TeamShortName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>SetCommander + RPC_SynchCommander + RPC_SynchRole + AdminMod role event (for Si_Logging).</summary>
        public static bool SeatCommander(MP_Strategy strat, Team team, Player player)
        {
            if (_setCommander == null || _synchCommander == null)
            {
                MelonLogger.Error("[Elo] SeatCommander: reflection targets missing.");
                return false;
            }
            try
            {
                if (player.Team != team)
                {
                    MelonLogger.Warning($"[Elo] SeatCommander: {player.PlayerName} is not on {team.TeamShortName}; moving first.");
                    if (!MovePlayer(player, team)) return false;
                }
                _setCommander.Invoke(strat, new object[] { team, player });

                var ps = _synchCommander.GetParameters();
                var args = new object[ps.Length];
                args[0] = team;
                for (int i = 1; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue! : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType)! : null!);
                _synchCommander.Invoke(strat, args);

                if (_synchRole != null && player != Player.CurrentPlayer)
                    _synchRole.Invoke(strat, new object[] { player, GameModeExt.ETeamRole.COMMANDER });

                try { Event_Roles.FireOnRoleChangedEvent(player, GameModeExt.ETeamRole.COMMANDER); }
                catch (Exception ex) { MelonLogger.Warning("[Elo] FireOnRoleChangedEvent: " + ex.Message); }
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Elo] SeatCommander {player?.PlayerName} on {team?.TeamShortName}: {ex}");
                return false;
            }
        }

        public static void RespawnIfUnitless(Player p)
        {
            try
            {
                if (p == null || p.Team == null) return;
                if (p.ControlledUnit != null) return;
                GameMode.CurrentGameMode.TrySpawnPlayerAtSpawnPoint(p, p.Team);
            }
            catch (Exception ex) { MelonLogger.Warning($"[Elo] Respawn {p?.PlayerName}: {ex.Message}"); }
        }

        // ---- chat -----------------------------------------------------------
        public static string TeamColor(Team? t)
        {
            try { return t == null ? "<color=#FFFFFF>" : HelperMethods.GetTeamColor(t); } catch { return "<color=#FFFFFF>"; }
        }

        public static string Colored(Team? t, string text) => TeamColor(t) + text + "</color>";

        public static void SayAll(string msg)
        {
            try { HelperMethods.SendChatMessageToAll("<color=#D6B85A>[Elo]</color> " + msg); }
            catch (Exception ex) { MelonLogger.Warning("[Elo] SayAll: " + ex.Message); }
        }

        public static void Say(Player? p, string msg)
        {
            try
            {
                if (p == null) MelonLogger.Msg("[Elo] " + StripTags(msg));
                else HelperMethods.SendChatMessageToPlayer(p, "<color=#D6B85A>[Elo]</color> " + msg);
            }
            catch (Exception ex) { MelonLogger.Warning("[Elo] Say: " + ex.Message); }
        }

        public static string StripTags(string s)
        {
            var sb = new System.Text.StringBuilder();
            bool inTag = false;
            foreach (char c in s)
            {
                if (c == '<') inTag = true;
                else if (c == '>') inTag = false;
                else if (!inTag) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
