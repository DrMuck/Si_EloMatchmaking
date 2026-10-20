/*
 Commands.cs - chat commands (registered through Si_AdminMod).

   !elo                      your commander / player rating
   !elo top                  top 5 commanders (>= ProvisionalGames)
   !elo <name>               look up a commander
   !elo flex   or  !flex     toggle: "I take any commander seat, Alien included" (persisted)
   !elo status               current applicant pool and the matchup it would produce
   !elo help

   admin (Power.Commander):
   !elo on | off             matchmaking master switch
   !elo reload               reload ratings.json (after replacing it with a new seed)
   !elo reset confirm        new season: back up ratings.json and start everyone at the start rating (flex flags kept)
   !elo set <name|steamid> <rating>
   !elo swap on|off          Sol<->Centauri applicant moves
   !elo volunteers on|off    !flex volunteers for seats without applicants
   !elo spread <weight>      SpreadWeight (1.0 = balanced-first, 0.3 = level-first)
*/
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Globalization;
using System.Linq;

namespace Si_EloMatchmaking
{
    public partial class EloMod
    {
        static void RegisterCommands()
        {
            try
            {
                HelperMethods.RegisterPlayerCommand("elo", OnEloCommand, true);
                HelperMethods.RegisterPlayerCommand("flex", OnFlexCommand, true);
                MelonLogger.Msg("[Elo] Registered !elo and !flex.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[Elo] Command registration failed (Admin Mod missing?): " + ex.Message);
            }
        }

        static bool IsAdmin(Player? caller)
        {
            if (caller == null) return true; // console
            try { return caller.CanAdminExecute(Power.Commander); } catch { return false; }
        }

        static void OnFlexCommand(Player? caller, string args)
        {
            if (caller == null) { GameApi.Say(null, "!flex is a player command."); return; }
            ToggleFlex(caller);
        }

        static void ToggleFlex(Player caller)
        {
            bool now = Db.ToggleFlex(GameApi.SteamId(caller));
            GameApi.Say(caller, now
                ? "Flex ON: you may be seated as commander of any faction (Alien included) when that gives the best matchup. !flex again to turn off."
                : "Flex OFF: you are only seated on the faction you applied for (Sol/Centauri may still be swapped).");
            if (now && _preRoundActive) GameApi.SayAll($"{GameApi.Colored(caller.Team, caller.PlayerName)} volunteers for any commander seat.");
        }

        static void OnEloCommand(Player? caller, string args)
        {
            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            string rest = parts.Length > 2 ? string.Join(" ", parts.Skip(2)) : "";

            switch (sub)
            {
                case "":
                    ShowOwn(caller);
                    return;
                case "help":
                    GameApi.Say(caller, "!elo | !elo top | !elo <name> | !elo flex | !elo status" + (IsAdmin(caller) ? " | admin: on/off, reload, set <name> <rating>, swap on/off, volunteers on/off, spread <w>" : ""));
                    return;
                case "top":
                    ShowTop(caller);
                    return;
                case "flex":
                    if (caller == null) GameApi.Say(null, "flex is a player command.");
                    else ToggleFlex(caller);
                    return;
                case "status":
                    ShowStatus(caller);
                    return;
            }

            if (sub == "on" || sub == "off" || sub == "reload" || sub == "set" || sub == "swap" || sub == "volunteers" || sub == "spread" || sub == "reset")
            {
                if (!IsAdmin(caller)) { GameApi.Say(caller, "Admin only."); return; }
                switch (sub)
                {
                    case "on":
                        Cfg.Enabled = true; ConfigStore.Save(Cfg);
                        GameApi.SayAll("Elo matchmaking ON: commanders are picked by rating at round start.");
                        return;
                    case "off":
                        Cfg.Enabled = false; ConfigStore.Save(Cfg);
                        GameApi.SayAll("Elo matchmaking OFF: the normal commander lottery is back (ratings are still tracked).");
                        return;
                    case "reset":
                        if (!rest.StartsWith("confirm", StringComparison.OrdinalIgnoreCase))
                        {
                            GameApi.Say(caller, $"This wipes all ratings ({Db.Data.commanders.Count} commanders, {Db.Data.players.Count} players) after backing up the file. Type: !elo reset confirm");
                            return;
                        }
                        {
                            string backup = Db.ResetSeason();
                            GameApi.SayAll($"Season reset: all commander and player ratings start again at {Cfg.StartRating:0}. Backup: {System.IO.Path.GetFileName(backup)}");
                        }
                        return;
                    case "reload":
                        Db.Load();
                        GameApi.Say(caller, $"Ratings reloaded: {Db.Data.commanders.Count} commanders, {Db.Data.players.Count} players.");
                        return;
                    case "swap":
                        Cfg.AllowHumanFactionSwap = rest.StartsWith("on", StringComparison.OrdinalIgnoreCase); ConfigStore.Save(Cfg);
                        GameApi.Say(caller, $"Sol<->Centauri applicant swap = {(Cfg.AllowHumanFactionSwap ? "on" : "off")}.");
                        return;
                    case "volunteers":
                        Cfg.AllowVolunteers = rest.StartsWith("on", StringComparison.OrdinalIgnoreCase); ConfigStore.Save(Cfg);
                        GameApi.Say(caller, $"!flex volunteers = {(Cfg.AllowVolunteers ? "on" : "off")}.");
                        return;
                    case "spread":
                        if (double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w >= 0)
                        {
                            Cfg.SpreadWeight = w; ConfigStore.Save(Cfg);
                            GameApi.Say(caller, "SpreadWeight = " + w.ToString("0.##", CultureInfo.InvariantCulture) + ".");
                        }
                        else GameApi.Say(caller, "SpreadWeight = " + Cfg.SpreadWeight.ToString("0.##", CultureInfo.InvariantCulture) + ". Usage: !elo spread <weight>");
                        return;
                    case "set":
                    {
                        var sp = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (sp.Length < 2 || !double.TryParse(sp[sp.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double rating))
                        {
                            GameApi.Say(caller, "Usage: !elo set <name|steamid> <rating>");
                            return;
                        }
                        string target = string.Join(" ", sp.Take(sp.Length - 1));
                        string? steam = ResolveSteam(target, out string shown);
                        if (steam == null) { GameApi.Say(caller, $"No unique match for '{target}'."); return; }
                        var e = Db.GetOrCreateCommander(steam, shown);
                        double before = e.rating;
                        e.rating = rating;
                        e.peak = Math.Max(e.peak, rating);
                        Db.Save();
                        GameApi.Say(caller, $"{shown}: commander rating {before:0} -> {rating:0}.");
                        return;
                    }
                }
            }

            // anything else: name lookup
            string needle = string.Join(" ", parts.Skip(1));
            var hits = Db.FindByName(needle);
            if (hits.Count == 0) { GameApi.Say(caller, $"No commander named like '{needle}'."); return; }
            foreach (var kv in hits.Take(3))
                GameApi.Say(caller, Describe(kv.Key, kv.Value));
        }

        static string? ResolveSteam(string target, out string shown)
        {
            shown = target;
            if (target.All(char.IsDigit) && target.Length >= 15)
            {
                var e = Db.Commander(target);
                if (e != null) shown = e.name;
                return target;
            }
            // connected player first
            Player? online = null;
            try { online = HelperMethods.FindTargetPlayer(target); } catch { }
            if (online != null) { shown = online.PlayerName; return GameApi.SteamId(online); }
            var hits = Db.FindByName(target);
            if (hits.Count == 1) { shown = hits[0].Value.name; return hits[0].Key; }
            var exact = hits.Where(h => string.Equals(h.Value.name, target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) { shown = exact[0].Value.name; return exact[0].Key; }
            return null;
        }

        static string Describe(string steam, RatingEntry e)
        {
            string prov = e.games < Cfg.ProvisionalGames ? " (provisional)" : "";
            string fac = string.Join(" ", e.factions.Where(f => f.Value.games > 0).Select(f => $"{GameApi.Short(f.Key)} {f.Value.wins}/{f.Value.games}"));
            return $"{e.name}: {e.rating:0}{prov}, {e.wins}-{e.losses} in {e.games} games, peak {e.peak:0}" + (fac.Length > 0 ? $" [{fac}]" : "");
        }

        static void ShowOwn(Player? caller)
        {
            if (caller == null) { GameApi.Say(null, "Console has no rating."); return; }
            string steam = GameApi.SteamId(caller);
            var c = Db.Commander(steam);
            var p = Db.PlayerEntry(steam);
            string cm = c == null ? $"no commander games yet (start {Cfg.StartRating:0})" : Describe(steam, c);
            string pl = p == null ? "no rated rounds" : $"{p.rating:0} in {p.games} rounds";
            GameApi.Say(caller, $"Commander: {cm}");
            GameApi.Say(caller, $"Player: {pl}" + (Db.IsFlex(steam) ? " | flex ON" : " | flex off (!flex to accept any seat)") + (Db.WaitingRounds(steam) > 0 ? $" | passed over {Db.WaitingRounds(steam)}x, seating bonus +{Db.WaitBonus(steam):0}" : ""));
        }

        static void ShowTop(Player? caller)
        {
            var top = Db.TopCommanders(5, establishedOnly: true);
            if (top.Count == 0) { GameApi.Say(caller, "No established commanders yet."); return; }
            int i = 1;
            foreach (var kv in top)
                GameApi.Say(caller, $"{i++}. {kv.Value.name} {kv.Value.rating:0} ({kv.Value.wins}-{kv.Value.losses})");
        }

        static void ShowStatus(Player? caller)
        {
            var strat = GameApi.Strat;
            if (strat == null) { GameApi.Say(caller, "No strategy game mode active."); return; }
            GameApi.Say(caller, $"Matchmaking {(Cfg.Enabled ? "ON" : "OFF")}, swap {(Cfg.AllowHumanFactionSwap ? "on" : "off")}, volunteers {(Cfg.AllowVolunteers ? "on" : "off")}, spread weight {Cfg.SpreadWeight.ToString("0.##", CultureInfo.InvariantCulture)}, CommMgmt {(GameApi.HasCommMgmt ? "linked" : "absent")}.");
            bool pre;
            try { pre = strat.MissionState == GameModeExt.EMissionState.INIT && strat.TeamsVersus != GameModeExt.ETeamsVersus.NONE; } catch { pre = false; }
            if (pre)
            {
                var seats = BuildSeats(strat);
                var cands = CollectPool(strat, seats, consume: false);
                if (cands.Count == 0) { GameApi.Say(caller, "No applicants yet."); return; }
                GameApi.Say(caller, "Applicants: " + string.Join(", ", cands.Select(c => $"{c.Name}@{GameApi.Short(c.AppliedFaction)} {Db.FormatShort(c.Steam)}{(c.Flex ? " flex" : "")}{(c.WaitBonus > 0 ? $"+{c.WaitBonus:0}w" : "")}")));
                var best = Matchmaker.Solve(seats, cands, Cfg, Rng);
                var parts = seats.Select((s, i) => $"{GameApi.Short(s.Faction)}: {(best.Picks[i] == null ? "AI" : best.Picks[i]!.Name)}");
                GameApi.Say(caller, "Projected: " + string.Join(" vs ", parts) + $" (mean {best.Mean:0}, spread {best.Spread:0})");
            }
            else if (LastAssignment != null)
            {
                var a = LastAssignment;
                var parts = a.Seats.Select((s, i) => $"{GameApi.Short(s.Faction)}: {(a.Picks[i] == null ? "AI" : a.Picks[i]!.Name + " " + a.Picks[i]!.Rating.ToString("0"))}");
                GameApi.Say(caller, "Current round: " + string.Join(" vs ", parts));
            }
            else GameApi.Say(caller, "No assignment this round.");
        }
    }
}
