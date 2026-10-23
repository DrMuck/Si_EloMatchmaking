/*
 Tiers.cs - which rating tier the pick aims for, the player vote / admin override for it, and !elo pass.

   Tier by player count (config TierByPlayers, default 0=any 10=midhigh 20=high): on a small server
   anyone may command, on a fuller server the pick aims at mid-or-high rated commanders, on a full
   server at high rated ones. The tier is a cost in the objective, not a filter: a seat is never left
   to the AI because of it, and when too few commanders of the tier apply the best available fill in
   (a mid vs high pair is fine when only one high applicant is there).

   Bounds: "mid" starts at the TierMidQuantile (0.34) and "high" at the TierHighQuantile (0.67) of
   the established commander population (games >= TierMinGames), or at TierMidRating / TierHighRating
   when set. Right after a season reset everyone sits at the start rating, so the tiers are inert
   until the ratings spread.

   !elo vote <any|low|mid|midhigh|high>   vote the tier of the NEXT round (one ballot per player; needs
                                          TierVoteMinVotes ballots and TierVoteMinShare of the players)
   !elo tier <tier>|auto                  admin: force the tier for the next round / back to the table
   !elo tiers 0=any,10=midhigh,20=high    admin: set the player-count table (persisted in the config)
   !elo pass [on|off]                     "seat someone else if another applicant within PassGap applies"
*/
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Si_EloMatchmaking
{
    public partial class EloMod
    {
        public static readonly string[] TierNames = { "any", "low", "mid", "midhigh", "high" };
        static string? _tierOverride;                                                         // admin, next round only
        static string _tierOverrideBy = "";
        static readonly Dictionary<string, string> _tierVotes = new Dictionary<string, string>();   // steam -> tier, next round only
        static string _announcedTier = "";
        static (int roundsLive, int commanders, double mid, double high) _boundsCache = (-1, -1, 0, 0);

        public static string? ParseTier(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace("/", "").Replace("+", "").Replace(" ", "");
            switch (s)
            {
                case "any": case "all": return "any";
                case "low": return "low";
                case "mid": case "middle": case "medium": return "mid";
                case "midhigh": case "midorhigh": case "highmid": case "midandhigh": return "midhigh";
                case "high": case "top": return "high";
            }
            return null;
        }

        public static string TierLabel(string tier)
        {
            switch (tier)
            {
                case "low": return "Low";
                case "mid": return "Mid";
                case "midhigh": return "Mid or High";
                case "high": return "High";
                default: return "Any";
            }
        }

        /// <summary>Ratings at which "mid" and "high" start (cached per rated round).</summary>
        public static (double mid, double high) TierBounds()
        {
            if (Cfg.TierMidRating > 0 && Cfg.TierHighRating > 0) return (Cfg.TierMidRating, Math.Max(Cfg.TierMidRating, Cfg.TierHighRating));
            int rl = Db.Data.rounds_live, n = Db.Data.commanders.Count;
            if (_boundsCache.roundsLive == rl && _boundsCache.commanders == n) return (_boundsCache.mid, _boundsCache.high);
            var pop = Db.Data.commanders.Values.Where(e => e.games >= Cfg.TierMinGames).Select(e => e.rating).OrderBy(r => r).ToList();
            double mid = Cfg.StartRating, high = Cfg.StartRating;
            if (pop.Count >= 3)
            {
                double Q(double q) => pop[Math.Min(pop.Count - 1, Math.Max(0, (int)Math.Round(q * (pop.Count - 1))))];
                mid = Cfg.TierMidRating > 0 ? Cfg.TierMidRating : Q(Cfg.TierMidQuantile);
                high = Cfg.TierHighRating > 0 ? Cfg.TierHighRating : Q(Cfg.TierHighQuantile);
                if (high < mid) high = mid;
            }
            _boundsCache = (rl, n, mid, high);
            return (mid, high);
        }

        public static TierWindow Window(string tier)
        {
            var (mid, high) = TierBounds();
            var w = new TierWindow { Name = tier };
            switch (tier)
            {
                case "low": w.High = mid; break;
                case "mid": w.Low = mid; w.High = high; break;
                case "midhigh": w.Low = mid; break;
                case "high": w.Low = high; break;
            }
            return w;
        }

        public static string PlayerCountTier(int players)
        {
            string t = "any";
            foreach (var step in (Cfg.TierByPlayers ?? new List<TierStep>()).OrderBy(s => s.Players))
                if (players >= step.Players) t = ParseTier(step.Tier) ?? t;
            return t;
        }

        static int ConnectedCount()
        {
            try { return GameApi.ConnectedPlayers().Count(); } catch { return 0; }
        }

        /// <summary>Players connected at the end of the previous round (set in OnRoundEnded; 0 after a server start).</summary>
        static int _prevRoundPlayers;

        /// <summary>Player count the tier table is read with: the larger of "now" and "end of the previous round"
        /// (TierPlayersUsePreviousRound), because the countdown runs right after the map change while players still load.</summary>
        public static int TierPlayerCount()
        {
            int now = ConnectedCount();
            return Cfg.TierPlayersUsePreviousRound ? Math.Max(now, _prevRoundPlayers) : now;
        }

        /// <summary>Tier for the next pick: admin override, else a vote with quorum (quorum on the players connected now), else the player-count table.</summary>
        public static string ResolveTier(int players, out string reason)
        {
            if (_tierOverride != null) { reason = "set by " + _tierOverrideBy; return _tierOverride; }
            var v = VoteResult(ConnectedCount());
            if (v.winner != null && v.votes >= v.needed) { reason = $"voted {v.top} of {v.votes}"; return v.winner; }
            reason = $"{players} players" + (players > ConnectedCount() ? " at the end of last round" : "");
            return PlayerCountTier(players);
        }

        public static (string? winner, int top, int votes, int needed) VoteResult(int players)
        {
            int needed = Math.Max(Cfg.TierVoteMinVotes, (int)Math.Ceiling(Cfg.TierVoteMinShare * players));
            if (_tierVotes.Count == 0) return (null, 0, 0, needed);
            var tally = _tierVotes.Values.GroupBy(t => t).Select(g => (tier: g.Key, n: g.Count())).OrderByDescending(x => x.n).ToList();
            string? winner = tally.Count == 1 || tally[0].n > tally[1].n ? tally[0].tier : null;   // a tie decides nothing
            return (winner, tally[0].n, _tierVotes.Count, needed);
        }

        static string VoteTally()
        {
            if (_tierVotes.Count == 0) return "no votes";
            return string.Join(", ", _tierVotes.Values.GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => $"{TierLabel(g.Key)} {g.Count()}"));
        }

        public static string TierTable() => string.Join(",", (Cfg.TierByPlayers ?? new List<TierStep>()).OrderBy(s => s.Players).Select(s => $"{s.Players}={s.Tier}"));

        public static string TierSummary(int players)
        {
            string t = ResolveTier(players, out string reason);
            var (mid, high) = TierBounds();
            return $"{TierLabel(t)} tier ({reason}; mid from {mid:0}, high from {high:0}; by players {TierTable()})";
        }

        /// <summary>Countdown: tell everyone which tier the next pick aims for (once, and again when it changes).</summary>
        static void AnnounceTier(int players)
        {
            string t = ResolveTier(players, out string reason);
            string key = t + "|" + reason;
            if (key == _announcedTier) return;
            _announcedTier = key;
            if (t == "any" && _tierOverride == null && _tierVotes.Count == 0) return;   // nothing worth saying on a small server
            GameApi.SayAll($"Next commander matchup aims for the {TierLabel(t)} tier ({reason}).");
        }

        static void ClearTierState()
        {
            _tierVotes.Clear();
            _tierOverride = null;
            _tierOverrideBy = "";
            _announcedTier = "";
        }

        // ---- commands -------------------------------------------------------
        static void HandleVote(Player? caller, string rest)
        {
            if (caller == null) { GameApi.Say(null, "!elo vote is a player command."); return; }
            if (!Cfg.TierVote) { GameApi.Say(caller, "Tier votes are disabled on this server."); return; }
            int players = ConnectedCount();
            string arg = (rest ?? "").Trim();
            if (arg.Length == 0)
            {
                var v0 = VoteResult(players);
                GameApi.Say(caller, $"Vote the Elo tier of the next commander matchup: !elo vote any|low|mid|midhigh|high. Votes: {VoteTally()} (needs {v0.needed} of {players}). Without a winning vote: {TierLabel(PlayerCountTier(TierPlayerCount()))} by player count.");
                return;
            }
            string? tier = ParseTier(arg);
            if (tier == null) { GameApi.Say(caller, "Unknown tier. Use: any, low, mid, midhigh, high."); return; }
            _tierVotes[GameApi.SteamId(caller)] = tier;
            var v = VoteResult(players);
            string state = v.winner != null && v.votes >= v.needed ? $"{TierLabel(v.winner)} wins so far" : $"needs {v.needed} ballots";
            GameApi.SayAll($"{GameApi.Colored(caller.Team, caller.PlayerName)} votes {TierLabel(tier)} for the next commander matchup. Votes: {VoteTally()} ({state}).");
            if (_tierOverride != null) GameApi.Say(caller, $"Note: an admin fixed the next tier to {TierLabel(_tierOverride)}; the vote only counts once that is lifted (!elo tier auto).");
            _announcedTier = "";
        }

        static void HandleTier(Player? caller, string rest)
        {
            string arg = (rest ?? "").Trim();
            int players = TierPlayerCount();
            if (arg.Length == 0) { GameApi.Say(caller, "Next pick: " + TierSummary(players) + ". Usage: !elo tier any|low|mid|midhigh|high|auto"); return; }
            if (arg.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                _tierOverride = null; _tierOverrideBy = ""; _announcedTier = "";
                GameApi.SayAll($"Commander tier back to automatic: {TierLabel(ResolveTier(players, out string r))} ({r}).");
                return;
            }
            string? tier = ParseTier(arg);
            if (tier == null) { GameApi.Say(caller, "Unknown tier. Use: any, low, mid, midhigh, high, auto."); return; }
            _tierOverride = tier; _tierOverrideBy = caller?.PlayerName ?? "console"; _announcedTier = "";
            GameApi.SayAll($"Admin set the next commander matchup to the {TierLabel(tier)} tier.");
        }

        /// <summary>The tier table as exactly three sorted rows for the UI (small / medium / full server); pads with the defaults.</summary>
        public static List<TierStep> TierRows()
        {
            var rows = (Cfg.TierByPlayers ?? new List<TierStep>()).Where(s => s != null).OrderBy(s => s.Players).ToList();
            var def = new EloConfig().TierByPlayers;
            while (rows.Count < 3) rows.Add(new TierStep(rows.Count == 0 ? 0 : rows[rows.Count - 1].Players + 10, def[Math.Min(rows.Count, def.Count - 1)].Tier));
            if (rows[0].Players != 0) rows[0].Players = 0;
            return rows;
        }

        // !elo tierat <row> <tier>      tier of table row 1..3 (the ModUI switch rows)
        // !elo tierfrom <row> <players> player threshold of row 2..3 (row 1 always starts at 0)
        static void HandleTierRow(Player? caller, string rest, bool threshold)
        {
            var sp = (rest ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var rows = TierRows();
            if (sp.Length < 2 || !int.TryParse(sp[0], out int row) || row < 1 || row > rows.Count)
            {
                GameApi.Say(caller, threshold ? "Usage: !elo tierfrom <2|3> <players>" : "Usage: !elo tierat <1|2|3> <any|low|mid|midhigh|high>");
                return;
            }
            var r = rows[row - 1];
            if (threshold)
            {
                if (row == 1) { GameApi.Say(caller, "Row 1 always starts at 0 players."); return; }
                if (!int.TryParse(sp[1], out int n) || n < 1) { GameApi.Say(caller, "Usage: !elo tierfrom <2|3> <players>"); return; }
                int lo = rows[row - 2].Players + 1, hi = row < rows.Count ? rows[row].Players - 1 : int.MaxValue;
                if (n < lo || n > hi) { GameApi.Say(caller, $"Row {row} must start between {lo} and {(hi == int.MaxValue ? "any" : hi.ToString())} players (rows stay in order)."); return; }
                r.Players = n;
            }
            else
            {
                string? t = ParseTier(sp[1]);
                if (t == null) { GameApi.Say(caller, "Usage: !elo tierat <1|2|3> <any|low|mid|midhigh|high>"); return; }
                r.Tier = t;
            }
            Cfg.TierByPlayers = rows;
            ConfigStore.Save(Cfg);
            _announcedTier = "";
            GameApi.SayAll($"Commander tier by player count is now {TierTable()}.");
        }

        static void HandleTiers(Player? caller, string rest)
        {
            string arg = (rest ?? "").Trim();
            if (arg.Length == 0) { GameApi.Say(caller, $"Tier by player count: {TierTable()}. Usage: !elo tiers 0=any,10=midhigh,20=high"); return; }
            var steps = new List<TierStep>();
            foreach (var part in arg.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                string? t = kv.Length == 2 ? ParseTier(kv[1]) : null;
                if (t == null || !int.TryParse(kv[0], out int n) || n < 0) { GameApi.Say(caller, $"Bad entry '{part}'. Usage: !elo tiers 0=any,10=midhigh,20=high"); return; }
                steps.Add(new TierStep(n, t));
            }
            if (!steps.Any(s => s.Players == 0)) steps.Add(new TierStep(0, "any"));
            Cfg.TierByPlayers = steps.OrderBy(s => s.Players).ToList();
            ConfigStore.Save(Cfg);
            _announcedTier = "";
            GameApi.SayAll($"Commander tier by player count is now {TierTable()}.");
        }

        static void HandlePass(Player? caller, string rest)
        {
            if (caller == null) { GameApi.Say(null, "!elo pass is a player command."); return; }
            string steam = GameApi.SteamId(caller);
            string want = (rest ?? "").Trim().ToLowerInvariant();
            if ((want == "on" || want == "off") && Db.IsPass(steam) == (want == "on")) { GameApi.Say(caller, "Pass is already " + want.ToUpperInvariant() + "."); return; }
            bool now = Db.TogglePass(steam);
            GameApi.Say(caller, now
                ? $"Pass ON: you still apply, but when another applicant rated within {Cfg.PassGap:0} points of you (or higher) applies for your seat, they get it and you play infantry. !elo pass again to turn off."
                : "Pass OFF: you compete for the seat like everyone else.");
        }
    }
}
