/*
 Pins.cs - admin pre-assignment of commander seats, and the Si_ModUI command export.

   !elo seat <faction> <player>   seat this player as commander of that faction at the next round
                                  start, ahead of the Elo pick (organised matches, show matches).
   !elo seat                      list the pins
   !elo unseat <faction> | all    drop a pin

 Pins live in memory until the next round start consumes them (one round only). They apply even
 while matchmaking is OFF: the pick then runs for that one round with the pinned seats fixed and
 the remaining seats filled by rating as usual. A pinned player who is not connected at round
 start is skipped with a chat notice.
*/
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Si_EloMatchmaking
{
    public partial class EloMod
    {
        class Pin { public string Steam = ""; public string Name = ""; public string By = ""; }
        static readonly Dictionary<string, Pin> _pins = new Dictionary<string, Pin>(StringComparer.OrdinalIgnoreCase);   // faction -> pin
        public static bool HasPins => _pins.Count > 0;

        static string? ParseFaction(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("sol", StringComparison.OrdinalIgnoreCase)) return "Sol";
            if (s.StartsWith("cent", StringComparison.OrdinalIgnoreCase)) return "Centauri";
            if (s.StartsWith("alien", StringComparison.OrdinalIgnoreCase)) return "Alien";
            return null;
        }

        static void HandleSeat(Player? caller, string rest)
        {
            var sp = (rest ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length == 0)
            {
                if (_pins.Count == 0) { GameApi.Say(caller, "No pinned seats. Usage: !elo seat <sol|cent|alien> <player>"); return; }
                GameApi.Say(caller, "Pinned for the next round: " + string.Join(", ", _pins.Select(p => $"{GameApi.Short(p.Key)}: {p.Value.Name}")) + ". !elo unseat <faction|all> to drop.");
                return;
            }
            string? faction = ParseFaction(sp[0]);
            if (faction == null || sp.Length < 2) { GameApi.Say(caller, "Usage: !elo seat <sol|cent|alien> <player>"); return; }
            string target = string.Join(" ", sp.Skip(1));
            string? steam = ResolveSteam(target, out string shown);
            if (steam == null) { GameApi.Say(caller, $"No unique match for '{target}'."); return; }
            foreach (var other in _pins.Where(p => p.Value.Steam == steam).Select(p => p.Key).ToList()) _pins.Remove(other);
            _pins[faction] = new Pin { Steam = steam, Name = shown, By = caller?.PlayerName ?? "console" };
            MelonLogger.Msg($"[Elo] Pin: {shown} ({steam}) -> {faction} by {caller?.PlayerName ?? "console"}");
            GameApi.SayAll($"Admin pick: {shown} commands {GameApi.Short(faction)} next round.");
        }

        static void HandleUnseat(Player? caller, string rest)
        {
            rest = (rest ?? "").Trim();
            if (rest.Length == 0 || rest.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (_pins.Count == 0) { GameApi.Say(caller, "No pinned seats."); return; }
                _pins.Clear();
                GameApi.SayAll("Admin picks cleared; the next commanders are chosen by rating again.");
                return;
            }
            string? faction = ParseFaction(rest);
            if (faction == null || !_pins.TryGetValue(faction, out var pin)) { GameApi.Say(caller, "No pin for that faction. " + (_pins.Count > 0 ? "Pinned: " + string.Join(", ", _pins.Keys.Select(GameApi.Short)) : "")); return; }
            _pins.Remove(faction);
            GameApi.SayAll($"Admin pick for {GameApi.Short(faction)} ({pin.Name}) dropped.");
        }

        /// <summary>
        /// Fixed picks for the matchmaker: one per pinned faction whose player is connected. The pinned player joins
        /// the candidate list (as applicant for that very team, so no swap or volunteer penalty applies) and a seat is
        /// added for a pinned faction whose team is active but still empty. Returns null when nothing is pinned.
        /// </summary>
        // !elo force: seat the pinned commanders in the running round right now, then drop the pins so they do not
        // apply again at the next round start. Uses the same move + seat path as the round-start assignment.
        static void HandleForce(Player? caller)
        {
            if (_pins.Count == 0) { GameApi.Say(caller, "No pinned seats. Pin first: !elo seat <faction> <player>."); return; }
            var strat = GameApi.Strat;
            if (strat == null) { GameApi.Say(caller, "No strategy match is running."); return; }
            var online = GameApi.ConnectedPlayers().ToDictionary(GameApi.SteamId, p => p);
            var teams = GameApi.ActiveTeams(strat);
            int done = 0;
            foreach (var kv in _pins.ToList())
            {
                if (!online.TryGetValue(kv.Value.Steam, out var player)) { GameApi.Say(caller, $"{kv.Value.Name} is not on the server; {GameApi.Short(kv.Key)} unchanged."); continue; }
                var team = teams.FirstOrDefault(t => GameApi.Faction(t) == kv.Key);
                if (team == null) { GameApi.Say(caller, $"{GameApi.Short(kv.Key)} is not in this match."); continue; }
                if (player.Team != team && !GameApi.MovePlayer(player, team)) { GameApi.Say(caller, $"Could not move {kv.Value.Name} to {GameApi.Short(kv.Key)}."); continue; }
                if (GameApi.SeatCommander(strat, team, player))
                {
                    done++;
                    MelonLogger.Msg($"[Elo] Forced seat: {player.PlayerName} -> {kv.Key} by {caller?.PlayerName ?? "console"}");
                    GameApi.SayAll($"Admin pick: {GameApi.Colored(team, player.PlayerName)} now commands {GameApi.Short(kv.Key)}.");
                }
                else GameApi.Say(caller, $"Seating {kv.Value.Name} on {GameApi.Short(kv.Key)} failed (see the server log).");
            }
            _pins.Clear();
            GameApi.Say(caller, $"{done} commander seat(s) forced; pins cleared, the next round is chosen by rating again.");
        }

        static Candidate?[]? FixedPicks(MP_Strategy strat, List<Seat> seats, List<Candidate> cands, bool announceMissing)
        {
            if (_pins.Count == 0) return null;
            var online = GameApi.ConnectedPlayers().ToDictionary(GameApi.SteamId, p => p);
            foreach (var kv in _pins.ToList())
            {
                if (!online.TryGetValue(kv.Value.Steam, out var player))
                {
                    if (announceMissing) GameApi.SayAll($"Admin pick {kv.Value.Name} for {GameApi.Short(kv.Key)} is not on the server; the seat is chosen by rating.");
                    continue;
                }
                var seat = seats.FirstOrDefault(s => s.Faction == kv.Key);
                if (seat == null)
                {
                    var team = GameApi.ActiveTeams(strat).FirstOrDefault(t => GameApi.Faction(t) == kv.Key);
                    if (team == null) { if (announceMissing) GameApi.SayAll($"Admin pick for {GameApi.Short(kv.Key)}: that faction is not in this match."); continue; }
                    seat = new Seat { Team = team, Faction = kv.Key, Players = SafeNumPlayers(team) };
                    seats.Add(seat);
                }
                var cand = cands.FirstOrDefault(c => c.Steam == kv.Value.Steam);
                if (cand == null)
                {
                    cand = new Candidate
                    {
                        Player = player, Steam = kv.Value.Steam, Name = player.PlayerName ?? kv.Value.Name,
                        Rating = Db.CommanderRating(kv.Value.Steam), Games = Db.CommanderGames(kv.Value.Steam), Sigma = Db.Sigma(Db.CommanderGames(kv.Value.Steam)),
                        WaitBonus = Db.WaitBonus(kv.Value.Steam), Flex = Db.IsFlex(kv.Value.Steam), FlexFactions = Db.FlexFactions(kv.Value.Steam), Streak = Db.Streak(kv.Value.Steam), Recent = Db.Streak(kv.Value.Steam) > 0, Source = "pinned",
                    };
                    cands.Add(cand);
                }
                cand.AppliedTeam = seat.Team; cand.AppliedFaction = seat.Faction;
            }
            var fixedPicks = new Candidate?[seats.Count];
            bool any = false;
            for (int i = 0; i < seats.Count; i++)
            {
                if (!_pins.TryGetValue(seats[i].Faction, out var pin)) continue;
                var cand = cands.FirstOrDefault(c => c.Steam == pin.Steam && ReferenceEquals(c.AppliedTeam, seats[i].Team));
                if (cand != null) { fixedPicks[i] = cand; any = true; }
            }
            return any ? fixedPicks : null;
        }

        // ---- Si_ModUI: the sub-commands behind !elo as buttons (ModUI_Export.cs builder, contract in ModUI README).
        // Admin entries never reach players without the power; switches carry their current state.
        // Si_ModUI per-player hook: this player's own flex state for the /flex switch row.
        public static string? ModUIPlayerData(ulong steamId)
        {
            try
            {
                string steam = steamId.ToString();
                return ModUI.ModUIExport.PlayerValues(
                    ("/flex", Db == null ? "" : FlexSwitchValue(steam)),
                    ("/elo pass", Db != null && Db.IsPass(steam) ? "on" : "off"),
                    ("/elo vote", _tierVotes.TryGetValue(steam, out var v) ? v : ""));
            }
            catch (Exception ex) { MelonLogger.Warning("[Elo] ModUIPlayerData: " + ex.Message); return null; }
        }

        /// <summary>The /flex multi-select row value: the accepted factions as "sol,cent,alien" tokens ("" = off).</summary>
        static string FlexSwitchValue(string steam)
        {
            var set = Db.IsFlex(steam) ? RatingDb.AllFactions.ToList() : (Db.FlexFactions(steam) ?? new List<string>());
            return string.Join(",", set.Select(f => f == "Sol" ? "sol" : f == "Centauri" ? "cent" : "alien"));
        }

        public static string? ModUIData()
        {
            try
            {
                var x = ModUI.ModUIExport.New()
                    .Enabled(Cfg.Enabled)
                    .Info($"Matchmaking {(Cfg.Enabled ? "ON" : "OFF")} | Sol-Centauri swap {(Cfg.AllowHumanFactionSwap ? "on" : "off")} | flex volunteers {(Cfg.AllowVolunteers ? "on" : "off")} | spread weight {Cfg.SpreadWeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}")
                    .Info((_pins.Count == 0 ? "no pinned commanders" : "pinned: " + string.Join(", ", _pins.Select(p => $"{GameApi.Short(p.Key)} {p.Value.Name}"))) + " (pins apply to the next round start, even while matchmaking is OFF)")
                    .Info("Next pick: " + TierSummary(TierPlayerCount()))
                    .Action("/elo", "My rating", "Your commander and player Elo, flex/pass state, streak and seating bonus.")
                    .Action("/elo top", "Top commanders", "The five best established commanders.")
                    .Action("/elo status", "Status", "Switches and the next tier; during the countdown the applicant pool and the projected matchup.")
                    // Per-player state: the shared export has no current value; ModUIPlayerData fills it per player.
                    .Multi("/flex", "Flex", "The factions whose commander seat you accept besides your own when that gives the best matchup; click a faction to add or remove it. Your own setting, remembered across sessions.", admin: false, current: "", ("sol", "Sol"), ("cent", "Centauri"), ("alien", "Alien"))
                    .Switch("/elo pass", "Pass", "Still apply, but step aside when another applicant of similar rating applies for your seat. Your own setting, remembered across sessions.", admin: false, current: "", ("on", "ON"), ("off", "OFF"))
                    .Switch("/elo vote", "Vote next tier", "Vote which Elo tier the next commander matchup aims for (needs a quorum; an admin setting wins).", admin: false, current: "", ("any", "Any"), ("low", "Low"), ("mid", "Mid"), ("midhigh", "Mid/High"), ("high", "High"))
                    .Switch("/elo tier", "Next tier", "Force the tier of the next commander pick; AUTO = by vote, else by player count.", admin: true, current: _tierOverride ?? "auto", ("auto", "AUTO"), ("any", "Any"), ("low", "Low"), ("mid", "Mid"), ("midhigh", "Mid/High"), ("high", "High"))
                    .Value("/elo tiers", "Tier by players", "players=tier list, e.g. 0=any,10=midhigh,20=high (any, low, mid, midhigh, high).", admin: true, current: TierTable())
                    .Picker("/elo seat", "Pin commander", "Pick the faction next to the player: that player commands it at the next round start, ahead of the Elo pick (one round).", admin: true, arg: "team,player");
                if (_pins.Count > 0)
                    x.Picker("/elo unseat", "Drop one pin", "Drop a single pinned seat.", admin: true, arg: "choice",
                        _pins.Select(p => (p.Key, $"{GameApi.Short(p.Key)}: {p.Value.Name}")).ToArray());
                x.Action("/elo unseat all", "Clear all pins", _pins.Count == 0 ? "Nothing is pinned right now." : "Undo every pin; the next commanders are chosen by rating again.", admin: true)
                 .Action("/elo force", "Force pins now", "Seats the pinned commanders in the running round immediately and clears the pins, so they do not apply again next round.", admin: true);
                x.OnOff("/elo matchmaking", "Matchmaking", "ON: commanders are picked by rating at round start. OFF: the normal commander lottery; ratings are still tracked.", admin: true, on: Cfg.Enabled)
                 .OnOff("/elo swap", "Sol-Centauri swap", "May a Sol applicant be seated as Centauri commander and vice versa?", admin: true, on: Cfg.AllowHumanFactionSwap)
                 .OnOff("/elo volunteers", "Flex volunteers", "May !flex volunteers fill a seat nobody applied for?", admin: true, on: Cfg.AllowVolunteers)
                 .Value("/elo spread", "Spread weight", "Cost per Elo point of rating gap between the seated commanders (1.0 = even matchups first).", admin: true, current: Cfg.SpreadWeight)
                 .Value("/elo level", "Level weight", "Weight of the mean rating: 0.3 = mild preference for strong commanders, 1.0 = level first.", admin: true, current: Cfg.LevelWeight)
                 .Picker("/elo set", "Set rating", "Pick the player, then type the new rating.", admin: true, arg: "player,text")
                 .Action("/elo reload", "Reload ratings", "Re-read ratings.json after replacing it.", admin: true)
                 .Picker("/elo reset", "Season reset", "Backs up and wipes all ratings; asks for confirmation.", admin: true, arg: "choice", ("confirm", "Confirm wipe"));
                return x.Build();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[Elo] ModUIData failed: " + ex.Message);
                return null;
            }
        }
    }
}
