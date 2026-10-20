/*
 RatingDb.cs - the Elo database (UserData\cfg\EloMatchmaking\ratings.json) and the
 rating update applied after each finished round.

 The JSON schema is the one tools/compute_elo.py writes, so the log-derived
 seed can be dropped in as-is and the mod keeps extending it. Model summary:
 - Team rating = share-weighted mean of its human commanders' ratings.
 - Pairwise Elo winner vs. every other commanded team (loser-loser pairs skipped).
 - Each commander receives K * share * (S - E) / (teams - 1).
 - Leaver rule: a commander who left (disconnect / resign / team change) within
   LateLeaveSeconds of the end of a LOST round is charged as if they had stayed.
*/
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Si_EloMatchmaking
{
    public class FactionStat
    {
        public int games;
        public int wins;
    }

    public class RatingEntry
    {
        public string name = "";
        public double rating;
        public int games;
        public int wins;
        public int losses;
        public double peak;
        public string? last_seen;
        public int late_leaves;
        public Dictionary<string, FactionStat> factions = new Dictionary<string, FactionStat>();

        public FactionStat Faction(string f)
        {
            if (!factions.TryGetValue(f, out var s)) { s = new FactionStat(); factions[f] = s; }
            return s;
        }
    }

    public class RatingDbData
    {
        public int schema = 1;
        public string? generated;
        public string? source;
        public int rounds_scored;
        [JsonProperty("params")] public JToken? parameters;
        public Dictionary<string, double> faction_adv = new Dictionary<string, double>();
        public JToken? validation;
        public Dictionary<string, RatingEntry> commanders = new Dictionary<string, RatingEntry>();
        public Dictionary<string, RatingEntry> players = new Dictionary<string, RatingEntry>();
        /// <summary>Steam IDs of players who accept any commander seat (!flex). Mod-only field.</summary>
        public HashSet<string> flex = new HashSet<string>();
        /// <summary>Rounds applied live by the mod (appended after the seed's rounds_scored).</summary>
        public int rounds_live;
        /// <summary>Steam ID -> rounds this applicant was passed over since last seated (wait bonus). Mod-only field.</summary>
        public Dictionary<string, int> waiting = new Dictionary<string, int>();
        [JsonExtensionData] public IDictionary<string, JToken>? extra;
    }

    // ---- input of one finished round --------------------------------------
    public class TenureResult
    {
        public string steam = "";
        public string name = "";
        public double seconds;
        public double endS;          // seconds after round start when the tenure ended
        public string endReason = "round_end";   // round_end | disconnect | resign | team_change
    }

    public class TeamResult
    {
        public string faction = "";
        public List<TenureResult> commanders = new List<TenureResult>();
        public Dictionary<string, (string name, double seconds)> players = new Dictionary<string, (string, double)>();
    }

    public class RoundResult
    {
        public string gametype = "";
        public string winner = "";
        public double durationS;
        public List<TeamResult> teams = new List<TeamResult>();
    }

    public class RatingChange
    {
        public string steam = "";
        public string name = "";
        public string faction = "";
        public double before;
        public double after;
        public double share;
        public bool lateLeave;
    }

    public class RatingDb
    {
        public RatingDbData Data = new RatingDbData();
        readonly string _path;
        readonly EloConfig _cfg;
        static readonly string[] Factions = { "Sol", "Centauri", "Alien" };

        public RatingDb(string path, EloConfig cfg)
        {
            _path = path;
            _cfg = cfg;
        }

        // ---- persistence --------------------------------------------------
        public bool Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    MelonLogger.Warning($"[Elo] No ratings file at {_path}; starting empty. Seed it with tools/compute_elo.py output.");
                    Data = new RatingDbData();
                    return false;
                }
                var d = JsonConvert.DeserializeObject<RatingDbData>(File.ReadAllText(_path));
                Data = d ?? new RatingDbData();
                Data.flex ??= new HashSet<string>();
                Data.commanders ??= new Dictionary<string, RatingEntry>();
                Data.players ??= new Dictionary<string, RatingEntry>();
                Data.faction_adv ??= new Dictionary<string, double>();
                Data.waiting ??= new Dictionary<string, int>();
                MelonLogger.Msg($"[Elo] Loaded ratings: {Data.commanders.Count} commanders, {Data.players.Count} players, " +
                                $"{Data.faction_adv.Count} faction terms, {Data.flex.Count} flex volunteers.");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[Elo] Failed to load ratings: " + ex);
                Data = new RatingDbData();
                return false;
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(Data, Formatting.Indented));
                if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak");
                else File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[Elo] Failed to save ratings: " + ex.Message);
            }
        }

        // ---- lookups --------------------------------------------------------
        public RatingEntry? Commander(string steam) => Data.commanders.TryGetValue(steam, out var e) ? e : null;
        public RatingEntry? PlayerEntry(string steam) => Data.players.TryGetValue(steam, out var e) ? e : null;

        public RatingEntry GetOrCreateCommander(string steam, string name)
        {
            if (!Data.commanders.TryGetValue(steam, out var e))
            {
                e = new RatingEntry { name = name, rating = _cfg.StartRating, peak = _cfg.StartRating };
                Data.commanders[steam] = e;
            }
            return e;
        }

        public RatingEntry GetOrCreatePlayer(string steam, string name)
        {
            if (!Data.players.TryGetValue(steam, out var e))
            {
                e = new RatingEntry { name = name, rating = _cfg.StartRating, peak = _cfg.StartRating };
                Data.players[steam] = e;
            }
            return e;
        }

        public double CommanderRating(string steam) => Commander(steam)?.rating ?? _cfg.StartRating;
        public int CommanderGames(string steam) => Commander(steam)?.games ?? 0;
        public double PlayerRating(string steam) => PlayerEntry(steam)?.rating ?? _cfg.StartRating;

        public bool IsFlex(string steam) => Data.flex.Contains(steam);

        /// <summary>Rating uncertainty: SigmaNew at 0 games, halved at ProvisionalGames, floored at SigmaMin.</summary>
        public double Sigma(int games) => Math.Max(_cfg.SigmaMin, _cfg.SigmaNew * _cfg.ProvisionalGames / (double)(_cfg.ProvisionalGames + games));

        public int WaitingRounds(string steam) => Data.waiting.TryGetValue(steam, out var n) ? n : 0;
        public double WaitBonus(string steam) => Math.Min(_cfg.WaitBonusMax, WaitingRounds(steam) * _cfg.WaitBonusPerRound);

        /// <summary>Seated applicants reset their wait counter, passed-over applicants gain one.</summary>
        public void UpdateWaiting(IEnumerable<string> seated, IEnumerable<string> passedOver)
        {
            foreach (var s in seated) Data.waiting.Remove(s);
            foreach (var s in passedOver) Data.waiting[s] = WaitingRounds(s) + 1;
        }
        public bool ToggleFlex(string steam)
        {
            bool now;
            if (Data.flex.Contains(steam)) { Data.flex.Remove(steam); now = false; }
            else { Data.flex.Add(steam); now = true; }
            Save();
            return now;
        }

        /// <summary>Entries sorted by rating, optionally only established ones.</summary>
        public List<KeyValuePair<string, RatingEntry>> TopCommanders(int n, bool establishedOnly)
        {
            IEnumerable<KeyValuePair<string, RatingEntry>> q = Data.commanders;
            if (establishedOnly) q = q.Where(kv => kv.Value.games >= _cfg.ProvisionalGames);
            return q.OrderByDescending(kv => kv.Value.rating).Take(n).ToList();
        }

        public List<KeyValuePair<string, RatingEntry>> FindByName(string needle)
        {
            needle = needle.ToLowerInvariant();
            return Data.commanders.Where(kv => kv.Value.name.ToLowerInvariant().Contains(needle))
                                  .OrderByDescending(kv => kv.Value.games).Take(5).ToList();
        }

        /// <summary>"1650" or "1500?" for provisional entries, plus game count.</summary>
        public string Format(string steam)
        {
            var e = Commander(steam);
            if (e == null) return $"{_cfg.StartRating:0}? (new)";
            string q = e.games < _cfg.ProvisionalGames ? "?" : "";
            return $"{e.rating:0}{q} ({e.games}g)";
        }

        public string FormatShort(string steam)
        {
            var e = Commander(steam);
            if (e == null) return $"{_cfg.StartRating:0}?";
            return e.games < _cfg.ProvisionalGames ? $"{e.rating:0}?" : $"{e.rating:0}";
        }

        // ---- Elo maths ----------------------------------------------------
        public static double Expected(double ra, double rb, double adv) => 1.0 / (1.0 + Math.Pow(10.0, (rb - ra - adv) / 400.0));

        /// <summary>Faction advantage of a over b in this game type (Elo points), from the seed estimate.</summary>
        public double AdvFor(string gametype, string a, string b)
        {
            if (string.CompareOrdinal(a, b) < 0)
                return Data.faction_adv.TryGetValue($"{gametype}|{a}|{b}", out var v) ? v : 0.0;
            return Data.faction_adv.TryGetValue($"{gametype}|{b}|{a}", out var w) ? -w : 0.0;
        }

        double KFor(int games) => games < _cfg.ProvisionalGames ? _cfg.KNew : _cfg.KBase;

        /// <summary>Win probabilities for display, Bradley-Terry over the seated ratings (3-way aware).</summary>
        public static double[] WinProbabilities(IList<double> ratings)
        {
            var w = ratings.Select(r => Math.Pow(10.0, r / 400.0)).ToArray();
            double s = w.Sum();
            return w.Select(x => s > 0 ? x / s : 0).ToArray();
        }

        /// <summary>
        /// Apply one finished round. Returns the commander rating changes (empty when the
        /// round carried no commander information).
        /// </summary>
        public List<RatingChange> ApplyRound(RoundResult r, out string summary)
        {
            var changes = new List<RatingChange>();
            summary = "";
            if (r.durationS < _cfg.MinRoundSeconds)
            {
                summary = $"round too short ({r.durationS:0}s), not rated";
                return changes;
            }
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

            // ---- commander tenures after leaver rule / MinShare ----------
            var commanded = new Dictionary<string, List<(string steam, string name, double share, bool late)>>();
            foreach (var t in r.teams)
            {
                bool lost = t.faction != r.winner;
                var entries = new List<(string, string, double, bool)>();
                foreach (var c in t.commanders)
                {
                    double secs = c.seconds;
                    bool late = false;
                    if (lost && c.endReason != "round_end" && (r.durationS - c.endS) <= _cfg.LateLeaveSeconds)
                    {
                        secs += r.durationS - c.endS;
                        late = true;
                    }
                    double share = secs / r.durationS;
                    if (share >= _cfg.MinShare) entries.Add((c.steam, c.name, share, late));
                }
                if (entries.Count == 0) continue;
                double tot = entries.Sum(e => e.Item3);
                if (tot > 1.0) entries = entries.Select(e => (e.Item1, e.Item2, e.Item3 / tot, e.Item4)).ToList();
                commanded[t.faction] = entries;
            }

            int nTeams = commanded.Count;
            if (nTeams >= 2 && commanded.ContainsKey(r.winner))
            {
                foreach (var kv in commanded)
                    foreach (var e in kv.Value) GetOrCreateCommander(e.steam, e.name);

                var teamRating = new Dictionary<string, double>();
                foreach (var kv in commanded)
                {
                    double w = kv.Value.Sum(e => e.share);
                    teamRating[kv.Key] = kv.Value.Sum(e => Data.commanders[e.steam].rating * e.share) / w;
                }
                var delta = new Dictionary<string, double>();
                void Add(string s, double d) { delta[s] = (delta.TryGetValue(s, out var x) ? x : 0) + d; }

                foreach (var kv in commanded)
                {
                    if (kv.Key == r.winner) continue;
                    double ea = Expected(teamRating[r.winner], teamRating[kv.Key], AdvFor(r.gametype, r.winner, kv.Key));
                    foreach (var e in commanded[r.winner])
                        Add(e.steam, KFor(Data.commanders[e.steam].games) * e.share * (1.0 - ea) / (nTeams - 1));
                    foreach (var e in kv.Value)
                        Add(e.steam, KFor(Data.commanders[e.steam].games) * e.share * (0.0 - (1.0 - ea)) / (nTeams - 1));
                }
                foreach (var kv in commanded)
                {
                    foreach (var e in kv.Value)
                    {
                        var c = Data.commanders[e.steam];
                        double before = c.rating;
                        c.rating += delta.TryGetValue(e.steam, out var d) ? d : 0;
                        c.games++;
                        c.name = e.name;
                        c.last_seen = stamp;
                        c.peak = Math.Max(c.peak, c.rating);
                        c.Faction(kv.Key).games++;
                        if (kv.Key == r.winner) { c.wins++; c.Faction(kv.Key).wins++; } else c.losses++;
                        if (e.late) c.late_leaves++;
                        changes.Add(new RatingChange { steam = e.steam, name = e.name, faction = kv.Key, before = before, after = c.rating, share = e.share, lateLeave = e.late });
                    }
                }
                summary = $"{nTeams} commanded teams, winner {r.winner}";
            }
            else summary = $"only {nTeams} commanded team(s) (winner commanded: {commanded.ContainsKey(r.winner)}), commanders not rated";

            // ---- players --------------------------------------------------
            var pt = new Dictionary<string, List<(string steam, string name, double share)>>();
            foreach (var t in r.teams)
            {
                var members = t.players.Where(p => p.Value.seconds / r.durationS >= _cfg.PlayerMinShare)
                                       .Select(p => (p.Key, p.Value.name, p.Value.seconds / r.durationS)).ToList();
                if (members.Count > 0) pt[t.faction] = members;
            }
            if (pt.Count >= 2 && pt.ContainsKey(r.winner))
            {
                foreach (var kv in pt) foreach (var m in kv.Value) GetOrCreatePlayer(m.steam, m.name);
                var tr = pt.ToDictionary(kv => kv.Key, kv => kv.Value.Average(m => Data.players[m.steam].rating));
                var delta = new Dictionary<string, double>();
                void Add(string s, double d) { delta[s] = (delta.TryGetValue(s, out var x) ? x : 0) + d; }
                foreach (var kv in pt)
                {
                    if (kv.Key == r.winner) continue;
                    double ea = Expected(tr[r.winner], tr[kv.Key], AdvFor(r.gametype, r.winner, kv.Key));
                    foreach (var m in pt[r.winner]) Add(m.steam, _cfg.KPlayer * m.share * (1.0 - ea) / (pt.Count - 1));
                    foreach (var m in kv.Value) Add(m.steam, _cfg.KPlayer * m.share * (0.0 - (1.0 - ea)) / (pt.Count - 1));
                }
                foreach (var kv in pt)
                {
                    foreach (var m in kv.Value)
                    {
                        var p = Data.players[m.steam];
                        p.rating += delta.TryGetValue(m.steam, out var d) ? d : 0;
                        p.games++;
                        p.name = m.name;
                        p.last_seen = stamp;
                        p.peak = Math.Max(p.peak, p.rating);
                        p.Faction(kv.Key).games++;
                        if (kv.Key == r.winner) { p.wins++; p.Faction(kv.Key).wins++; } else p.losses++;
                    }
                }
            }

            Data.rounds_live++;
            return changes;
        }
    }
}
