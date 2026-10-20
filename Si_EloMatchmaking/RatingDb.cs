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
using System.Threading;

namespace Si_EloMatchmaking
{
    public class FactionStat
    {
        public int games;
        public int wins;
        /// <summary>Per-faction Elo (same scheme as the overall rating, expectations from faction ratings). 0 in old files = start rating.</summary>
        public double rating;
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
            if (!factions.TryGetValue(f, out var s)) { s = new FactionStat { rating = StartRatingForNew }; factions[f] = s; }
            if (s.rating <= 0) s.rating = StartRatingForNew;
            return s;
        }

        /// <summary>Create the three faction buckets so the dictionary never changes shape during a background save.</summary>
        public void EnsureBuckets()
        {
            Faction("Sol"); Faction("Centauri"); Faction("Alien");
        }
        /// <summary>Set by RatingDb before use so FactionStat defaults match the config.</summary>
        public static double StartRatingForNew = 1500;
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
            RatingEntry.StartRatingForNew = cfg.StartRating;
        }

        /// <summary>Per-faction commander rating (start rating when the commander never played that faction).</summary>
        public double CommanderFactionRating(string steam, string faction)
        {
            var e = Commander(steam);
            if (e == null) return _cfg.StartRating;
            return e.Faction(faction).rating;
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
                int replayed = ReplayJournal();
                if (replayed > 0) _journalSaves = FullSaveEvery;   // compact on the next save
                foreach (var e in Data.commanders.Values) e.EnsureBuckets();
                foreach (var e in Data.players.Values) e.EnsureBuckets();
                MelonLogger.Msg($"[Elo] Loaded ratings: {Data.commanders.Count} commanders, {Data.players.Count} players, " +
                                $"{Data.faction_adv.Count} faction terms, {Data.flex.Count} flex volunteers" + (replayed > 0 ? $", {replayed} journal entries replayed" : "") + ".");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[Elo] Failed to load ratings: " + ex);
                Data = new RatingDbData();
                return false;
            }
        }

        // ---- persistence: journal + periodic full rewrite -----------------------
        // Save() appends one line with only the entries touched since the last save to
        // ratings.journal.jsonl (plus the small flex/waiting sets); every FullSaveEvery-th save,
        // on reset and on shutdown the whole file is rewritten and the journal truncated.
        // Load() reads the full file and replays the journal. File work runs on a pool thread.
        int _saveRunning;
        bool _savePending;
        int _journalSaves;
        public int FullSaveEvery = 10;
        readonly HashSet<string> _dirtyCommanders = new HashSet<string>();
        readonly HashSet<string> _dirtyPlayers = new HashSet<string>();
        readonly object _fileLock = new object();
        string JournalPath => _path + ".journal.jsonl";

        class Delta
        {
            public string? t;
            public int rounds_live;
            public Dictionary<string, RatingEntry> commanders = new Dictionary<string, RatingEntry>();
            public Dictionary<string, RatingEntry> players = new Dictionary<string, RatingEntry>();
            public HashSet<string> flex = new HashSet<string>();
            public Dictionary<string, int> waiting = new Dictionary<string, int>();
        }

        public void Save()
        {
            if (Interlocked.CompareExchange(ref _saveRunning, 1, 0) != 0) { _savePending = true; return; }
            _savePending = false;
            bool full = _journalSaves >= FullSaveEvery;
            string path = _path, journal = JournalPath;
            if (full)
            {
                _journalSaves = 0;
                _dirtyCommanders.Clear(); _dirtyPlayers.Clear();
                var snapshot = new RatingDbData
                {
                    schema = Data.schema,
                    generated = Data.generated,
                    source = Data.source,
                    rounds_scored = Data.rounds_scored,
                    parameters = Data.parameters,
                    faction_adv = new Dictionary<string, double>(Data.faction_adv),
                    validation = Data.validation,
                    commanders = new Dictionary<string, RatingEntry>(Data.commanders),
                    players = new Dictionary<string, RatingEntry>(Data.players),
                    flex = new HashSet<string>(Data.flex),
                    rounds_live = Data.rounds_live,
                    waiting = new Dictionary<string, int>(Data.waiting),
                    extra = Data.extra,
                };
                ThreadPool.QueueUserWorkItem(_ => RunSave(() => { lock (_fileLock) { WriteFull(path, snapshot); TruncateJournal(journal); } }));
            }
            else
            {
                _journalSaves++;
                var delta = new Delta
                {
                    t = DateTime.UtcNow.ToString("o"),
                    rounds_live = Data.rounds_live,
                    flex = new HashSet<string>(Data.flex),
                    waiting = new Dictionary<string, int>(Data.waiting),
                };
                foreach (var k in _dirtyCommanders) if (Data.commanders.TryGetValue(k, out var c)) delta.commanders[k] = c;
                foreach (var k in _dirtyPlayers) if (Data.players.TryGetValue(k, out var p)) delta.players[k] = p;
                _dirtyCommanders.Clear(); _dirtyPlayers.Clear();
                ThreadPool.QueueUserWorkItem(_ => RunSave(() =>
                {
                    string line = JsonConvert.SerializeObject(delta, Formatting.None);
                    lock (_fileLock) File.AppendAllText(journal, line + "\n");
                }));
            }
        }

        void RunSave(Action work)
        {
            try { work(); }
            catch (Exception ex) { MelonLogger.Error("[Elo] Background save failed: " + ex.Message); }
            finally
            {
                Interlocked.Exchange(ref _saveRunning, 0);
                if (_savePending) { _savePending = false; Save(); }
            }
        }

        /// <summary>Synchronous full save (reset, shutdown).</summary>
        public void SaveNow()
        {
            try
            {
                int spins = 0;
                while (Volatile.Read(ref _saveRunning) != 0 && spins++ < 300) Thread.Sleep(10);
                lock (_fileLock) { WriteFull(_path, Data); TruncateJournal(JournalPath); }
                _dirtyCommanders.Clear(); _dirtyPlayers.Clear();
                _journalSaves = 0;
            }
            catch (Exception ex) { MelonLogger.Error("[Elo] Failed to save ratings: " + ex.Message); }
        }

        static void WriteFull(string path, RatingDbData data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.None));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
        }

        static void TruncateJournal(string journal)
        {
            try { if (File.Exists(journal)) File.Delete(journal); } catch { }
        }

        int ReplayJournal()
        {
            string journal = JournalPath;
            if (!File.Exists(journal)) return 0;
            int n = 0;
            try
            {
                foreach (var line in File.ReadLines(journal))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    Delta? d = null;
                    try { d = JsonConvert.DeserializeObject<Delta>(line); } catch { continue; }
                    if (d == null) continue;
                    foreach (var kv in d.commanders) Data.commanders[kv.Key] = kv.Value;
                    foreach (var kv in d.players) Data.players[kv.Key] = kv.Value;
                    if (d.flex != null) Data.flex = d.flex;
                    if (d.waiting != null) Data.waiting = d.waiting;
                    if (d.rounds_live > Data.rounds_live) Data.rounds_live = d.rounds_live;
                    n++;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[Elo] Journal replay: " + ex.Message); }
            return n;
        }

        /// <summary>New season: back up the current file, wipe ratings and counters, keep flex preferences. Returns the backup path.</summary>
        public string ResetSeason()
        {
            string backup = Path.Combine(Path.GetDirectoryName(_path)!, $"ratings_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            try { if (File.Exists(_path)) File.Copy(_path, backup, true); }
            catch (Exception ex) { MelonLogger.Error("[Elo] Backup before reset failed: " + ex.Message); }
            var keepFlex = Data.flex;
            var keepAdv = Data.faction_adv;
            Data = new RatingDbData { flex = keepFlex, faction_adv = keepAdv, generated = DateTime.UtcNow.ToString("o"), source = "season reset" };
            SaveNow();
            MelonLogger.Msg("[Elo] Season reset; backup at " + backup);
            return backup;
        }

        // ---- lookups --------------------------------------------------------
        public RatingEntry? Commander(string steam) => Data.commanders.TryGetValue(steam, out var e) ? e : null;
        public RatingEntry? PlayerEntry(string steam) => Data.players.TryGetValue(steam, out var e) ? e : null;

        public RatingEntry GetOrCreateCommander(string steam, string name)
        {
            if (!Data.commanders.TryGetValue(steam, out var e))
            {
                e = new RatingEntry { name = name, rating = _cfg.StartRating, peak = _cfg.StartRating };
                e.EnsureBuckets();
                Data.commanders[steam] = e;
            }
            _dirtyCommanders.Add(steam);
            return e;
        }

        public RatingEntry GetOrCreatePlayer(string steam, string name)
        {
            if (!Data.players.TryGetValue(steam, out var e))
            {
                e = new RatingEntry { name = name, rating = _cfg.StartRating, peak = _cfg.StartRating };
                e.EnsureBuckets();
                Data.players[steam] = e;
            }
            _dirtyPlayers.Add(steam);
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
                var teamRatingF = new Dictionary<string, double>();
                foreach (var kv in commanded)
                {
                    double w = kv.Value.Sum(e => e.share);
                    teamRating[kv.Key] = kv.Value.Sum(e => Data.commanders[e.steam].rating * e.share) / w;
                    teamRatingF[kv.Key] = kv.Value.Sum(e => Data.commanders[e.steam].Faction(kv.Key).rating * e.share) / w;
                }
                var delta = new Dictionary<string, double>();
                var deltaF = new Dictionary<string, double>();
                void Add(string s, double d) { delta[s] = (delta.TryGetValue(s, out var x) ? x : 0) + d; }
                void AddF(string s, double d) { deltaF[s] = (deltaF.TryGetValue(s, out var x) ? x : 0) + d; }

                foreach (var kv in commanded)
                {
                    if (kv.Key == r.winner) continue;
                    double adv = AdvFor(r.gametype, r.winner, kv.Key);
                    double ea = Expected(teamRating[r.winner], teamRating[kv.Key], adv);
                    double eaF = Expected(teamRatingF[r.winner], teamRatingF[kv.Key], adv);
                    foreach (var e in commanded[r.winner])
                    {
                        double k = KFor(Data.commanders[e.steam].games);
                        Add(e.steam, k * e.share * (1.0 - ea) / (nTeams - 1));
                        AddF(e.steam, k * e.share * (1.0 - eaF) / (nTeams - 1));
                    }
                    foreach (var e in kv.Value)
                    {
                        double k = KFor(Data.commanders[e.steam].games);
                        Add(e.steam, k * e.share * (0.0 - (1.0 - ea)) / (nTeams - 1));
                        AddF(e.steam, k * e.share * (0.0 - (1.0 - eaF)) / (nTeams - 1));
                    }
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
                        var fs = c.Faction(kv.Key);
                        fs.rating += deltaF.TryGetValue(e.steam, out var df) ? df : 0;
                        fs.games++;
                        if (kv.Key == r.winner) { c.wins++; fs.wins++; } else c.losses++;
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
