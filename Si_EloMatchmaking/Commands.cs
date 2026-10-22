/*
 Commands.cs - chat commands (registered through Si_AdminMod).

   !elo                      your commander / player rating
   !elo top                  top 5 commanders (>= ProvisionalGames)
   !elo <name>               look up a commander
   !elo flex   or  !flex     toggle: "I take any commander seat, Alien included" (persisted per Steam ID)
   !flex alien | sol cent | humans | on | off   the factions you accept a seat on besides your own
   !elo pass [on|off]        toggle: "seat someone else if another applicant of similar rating applies" (persisted; Tiers.cs)
   !elo vote <tier>          vote the Elo tier of the next commander matchup (any|low|mid|midhigh|high; Tiers.cs)
   !elo status               current applicant pool, next tier and the matchup it would produce
   !elo help

   admin (Power.Commander):
   !elo on | off             matchmaking master switch (also !elo matchmaking on|off)
   !elo reload               reload ratings.json (after replacing it with a new seed)
   !elo reset confirm        new season: back up ratings.json and start everyone at the start rating (flex flags kept)
   !elo set <name|steamid> <rating>
   !elo swap on|off          Sol<->Centauri applicant moves
   !elo volunteers on|off    !flex volunteers for seats without applicants
   !elo spread <weight>      SpreadWeight (1.0 = balanced-first, 0.3 = level-first)
   !elo level <weight>       LevelWeight (0.3 = mild preference for strong commanders, 1.0 = level-first)
   !elo tier <tier>|auto     force the tier of the next pick / back to vote + player count (Tiers.cs)
   !elo tiers 0=any,10=midhigh,20=high   tier by player count (persisted)
   !elo seat <faction> <player>   pin a commander for the next round (Pins.cs); !elo seat lists, !elo unseat drops
*/
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
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

        // !flex                  toggle any <-> off
        // !flex on|off           any seat / none
        // !flex alien            toggle one faction in your set (the Si_ModUI faction buttons send this form)
        // !flex alien on|off     add / remove one faction explicitly
        // !flex sol cent         set exactly these factions;  "humans" = sol cent
        // The set lists the factions you accept a seat on besides the one you applied for. An explicit set also
        // limits the Sol/Centauri swap for you ("Sol or Alien only" is expressible). Stored per Steam ID on the server.
        static void OnFlexCommand(Player? caller, string args)
        {
            if (caller == null) { GameApi.Say(null, "!flex is a player command."); return; }
            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(p => p.ToLowerInvariant()).ToList();
            if (parts.Count == 0) { ToggleFlex(caller); return; }
            string steam = GameApi.SteamId(caller);
            const string usage = "Usage: !flex (toggle) | !flex on | !flex off | !flex alien (toggle Alien) | !flex alien on|off | !flex sol cent | !flex humans. The factions you accept a commander seat on besides your own.";
            List<string>? set;
            if (parts[0] == "on" || parts[0] == "any" || parts[0] == "all") set = RatingDb.AllFactions.ToList();
            else if (parts[0] == "off" || parts[0] == "none") set = null;
            else
            {
                bool? explicitOn = null;
                if (parts[parts.Count - 1] == "on" || parts[parts.Count - 1] == "off") { explicitOn = parts[parts.Count - 1] == "on"; parts.RemoveAt(parts.Count - 1); }
                var listed = new List<string>();
                foreach (var p in parts)
                {
                    if (p == "humans" || p == "human") { listed.Add("Sol"); listed.Add("Centauri"); continue; }
                    string? f = ParseFaction(p);
                    if (f == null) { GameApi.Say(caller, usage); return; }
                    listed.Add(f);
                }
                if (listed.Count == 0) { GameApi.Say(caller, usage); return; }
                var current = Db.IsFlex(steam) ? RatingDb.AllFactions.ToList() : (Db.FlexFactions(steam) ?? new List<string>());
                if (explicitOn == null && listed.Count > 1) set = listed;                                     // "!flex sol cent" sets
                else
                {
                    bool add = explicitOn ?? !current.Contains(listed[0]);                                      // single faction: toggle
                    set = add ? current.Concat(listed).ToList() : current.Where(f => !listed.Contains(f)).ToList();
                }
            }
            string before = Db.FlexLabel(steam);
            string label = Db.SetFlex(steam, set);
            if (label == before) { GameApi.Say(caller, "Flex is already " + label.ToUpperInvariant() + "."); return; }
            SayFlex(caller, label);
        }

        static void ToggleFlex(Player caller)
        {
            string steam = GameApi.SteamId(caller);
            SayFlex(caller, Db.SetFlex(steam, Db.IsFlex(steam) ? null : RatingDb.AllFactions));
        }

        static void SayFlex(Player caller, string label)
        {
            switch (label)
            {
                case "any": GameApi.Say(caller, "Flex ANY: you may be seated as commander of any faction (Alien included) when that gives the best matchup. !flex again to turn off."); break;
                case "off": GameApi.Say(caller, "Flex OFF: you are only seated on the faction you applied for (the Sol/Centauri swap still applies when the server allows it)."); break;
                default: GameApi.Say(caller, $"Flex {label}: besides your own faction you may be seated on {label.Replace("+", " or ")} only; you are not moved anywhere else."); break;
            }
            if (label != "off" && _preRoundActive)
                GameApi.SayAll($"{GameApi.Colored(caller.Team, caller.PlayerName)} volunteers for " + (label == "any" ? "any commander seat." : $"a {label.Replace("+", "/")} commander seat."));
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
                    GameApi.Say(caller, "!elo | !elo top | !elo <name> | !elo flex | !elo pass | !elo vote <any|low|mid|midhigh|high> | !elo status" + (IsAdmin(caller) ? " | admin: on/off, reload, set <name> <rating>, swap on/off, volunteers on/off, spread <w>, level <w>, tier <tier|auto>, tiers <n=tier,...>, seat <faction> <player>, unseat <faction|all>, force" : ""));
                    return;
                case "top":
                    ShowTop(caller);
                    return;
                case "pass":
                    HandlePass(caller, rest);
                    return;
                case "vote":
                    HandleVote(caller, rest);
                    return;
                case "flex":
                    if (caller == null) GameApi.Say(null, "flex is a player command.");
                    else ToggleFlex(caller);
                    return;
                case "status":
                    ShowStatus(caller);
                    return;
            }

            // "!elo matchmaking on|off" is the switch form of on/off (used by the Si_ModUI switch row)
            if (sub == "matchmaking")
            {
                string want = rest.Trim().ToLowerInvariant();
                if (want != "on" && want != "off") { GameApi.Say(caller, $"Matchmaking is {(Cfg.Enabled ? "ON" : "OFF")}. Usage: !elo matchmaking on|off"); return; }
                sub = want;
            }
            if (sub == "on" || sub == "off" || sub == "reload" || sub == "set" || sub == "swap" || sub == "volunteers" || sub == "spread" || sub == "level" || sub == "tier" || sub == "tiers" || sub == "reset" || sub == "seat" || sub == "unseat" || sub == "force")
            {
                if (!IsAdmin(caller)) { GameApi.Say(caller, "Admin only."); return; }
                switch (sub)
                {
                    case "tier":
                        HandleTier(caller, rest);
                        return;
                    case "tiers":
                        HandleTiers(caller, rest);
                        return;
                    case "level":
                        if (double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out double lw) && lw >= 0)
                        {
                            Cfg.LevelWeight = lw; ConfigStore.Save(Cfg);
                            GameApi.Say(caller, "LevelWeight = " + lw.ToString("0.##", CultureInfo.InvariantCulture) + ".");
                        }
                        else GameApi.Say(caller, "LevelWeight = " + Cfg.LevelWeight.ToString("0.##", CultureInfo.InvariantCulture) + ". Usage: !elo level <weight>");
                        return;
                    case "seat":
                        HandleSeat(caller, rest);
                        return;
                    case "force":
                        HandleForce(caller);
                        return;
                    case "unseat":
                        HandleUnseat(caller, rest);
                        return;
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
            GameApi.Say(caller, $"Player: {pl} | flex {Db.FlexLabel(steam)}" + (Db.FlexLabel(steam) == "off" ? " (!flex = any seat, !flex alien = Alien only)" : "") + (Db.IsPass(steam) ? " | pass ON" : "")
                + (Db.Streak(steam) > 0 ? $" | commanded the last {Db.Streak(steam)} round(s), streak penalty -{Matchmaker.StreakPenalty(Db.Streak(steam), Cfg):0}" : "")
                + (Db.WaitingRounds(steam) > 0 ? $" | passed over {Db.WaitingRounds(steam)}x, seating bonus +{Db.WaitBonus(steam):0}" : ""));
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
            GameApi.Say(caller, $"Matchmaking {(Cfg.Enabled ? "ON" : "OFF")}, swap {(Cfg.AllowHumanFactionSwap ? "on" : "off")}, volunteers {(Cfg.AllowVolunteers ? "on" : "off")}, spread weight {Cfg.SpreadWeight.ToString("0.##", CultureInfo.InvariantCulture)}, level weight {Cfg.LevelWeight.ToString("0.##", CultureInfo.InvariantCulture)}, CommMgmt {(GameApi.HasCommMgmt ? "linked" : "absent")}."
                + (_pins.Count > 0 ? " Pinned: " + string.Join(", ", _pins.Select(p => $"{GameApi.Short(p.Key)}: {p.Value.Name}")) + "." : ""));
            GameApi.Say(caller, "Next pick: " + TierSummary(TierPlayerCount()) + ".");
            bool pre;
            try { pre = strat.MissionState == GameModeExt.EMissionState.INIT && strat.TeamsVersus != GameModeExt.ETeamsVersus.NONE; } catch { pre = false; }
            if (pre)
            {
                var seats = BuildSeats(strat);
                var cands = CollectPool(strat, seats, consume: false);
                if (cands.Count == 0) { GameApi.Say(caller, "No applicants yet."); return; }
                GameApi.Say(caller, "Applicants: " + string.Join(", ", cands.Select(c => $"{c.Name}@{GameApi.Short(c.AppliedFaction)} {Db.FormatShort(c.Steam)}{(c.Flex ? " flex" : "")}{(c.Pass ? " pass" : "")}{(c.Streak > 0 ? $" streak{c.Streak}" : "")}{(c.WaitBonus > 0 ? $"+{c.WaitBonus:0}w" : "")}")));
                cands = cands.ToList();
                var tier = Window(ResolveTier(TierPlayerCount(), out _));
                Matchmaker.ApplyPass(seats, cands, Cfg, Rng, tier);
                var fixedPicks = FixedPicks(strat, seats, cands, announceMissing: false);
                var best = Matchmaker.Solve(seats, cands, Cfg, Rng, fixedPicks, tier);
                var parts = seats.Select((s, i) => $"{GameApi.Short(s.Faction)}: {(best.Picks[i] == null ? "AI" : best.Picks[i]!.Name)}{(fixedPicks != null && fixedPicks[i] != null ? " (pinned)" : "")}");
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
