/*
 Config.cs - JSON config for Si_EloMatchmaking.
 File: UserData\cfg\EloMatchmaking\Si_EloMatchmaking_Config.json (created with defaults on first run).
*/
using MelonLoader;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace Si_EloMatchmaking
{
    /// <summary>One row of the tier-by-player-count table: from this many connected players on, aim for this tier.</summary>
    public class TierStep
    {
        public int Players;
        public string Tier = "any";
        public TierStep() { }
        public TierStep(int players, string tier) { Players = players; Tier = tier; }
    }

    public class EloConfig
    {
        // ---- master switches -------------------------------------------------
        /// <summary>Mod does the commander pick at round start. Off = vanilla / CommManagement lottery.</summary>
        public bool Enabled = true;
        /// <summary>Update ratings.json from finished rounds.</summary>
        public bool UpdateRatingsLive = true;

        // ---- matchmaking objective ------------------------------------------
        /// <summary>Score = LevelWeight * mean(rating) - SpreadWeight * spread - tier cost - penalties + wait bonus. SpreadWeight 1.0 = one Elo point of spread costs one point.</summary>
        public double SpreadWeight = 1.0;
        /// <summary>Weight of the mean rating of the seated commanders. 1.0 = level first (strongest applicants nearly always seated); 0.5 = preference for strong commanders that streak penalty, wait bonus and tier can overturn.</summary>
        public double LevelWeight = 0.5;
        /// <summary>Applicant for Sol may be seated as Centauri commander and vice versa.</summary>
        public bool AllowHumanFactionSwap = true;
        /// <summary>Cost of moving an applicant to the other human faction.</summary>
        public double SwapPenalty = 30;
        /// <summary>Applicants who typed !flex may be seated on any faction (incl. Alien).</summary>
        public bool AllowVolunteers = true;
        /// <summary>Cost of using a !flex volunteer for a seat they did not apply for.</summary>
        public double VolunteerPenalty = 60;
        /// <summary>Streak penalty per consecutive round already commanded (30 after one round, 60 after two, ...), capped by StreakPenaltyMax. 0 = off. Rotation: a seat gets harder to keep, never impossible.</summary>
        public double RecentCommanderPenalty = 30;
        /// <summary>Cap for the streak penalty.</summary>
        public double StreakPenaltyMax = 150;
        /// <summary>!elo pass: a passing applicant steps aside when another applicant for the same seat is rated within this many points below them (or higher).</summary>
        public double PassGap = 150;
        /// <summary>Rating uncertainty of a commander with 0 games (Elo points). A mismatch against an uncertain rating is discounted by the sum of both sigmas.</summary>
        public double SigmaNew = 200;
        /// <summary>Uncertainty floor for well-established commanders.</summary>
        public double SigmaMin = 40;
        /// <summary>Each round an applicant is passed over adds this to their score for the pick (rotation guarantee). Reset when seated.</summary>
        public double WaitBonusPerRound = 60;
        /// <summary>Cap for the accumulated wait bonus.</summary>
        public double WaitBonusMax = 420;
        /// <summary>Cost of leaving a seat with players to the AI. Must exceed any spread + tier cost a lopsided pair can collect, so an applicant is always seated rather than the AI (values below 1000 are raised to 1500 on load).</summary>
        public double NoCommanderPenalty = 1500;
        /// <summary>Only pick among applicants with at least this many rated games when enough are available (0 = off).</summary>
        public int PreferEstablishedMinGames = 0;

        // ---- Elo tier by player count (Tiers.cs) -----------------------------
        /// <summary>Which rating tier the pick aims for, by connected players: the row with the largest Players &lt;= count applies.
        /// Tiers: any, low, mid, midhigh, high. The tier is a cost, not a filter: a seat is never left to the AI because of it, and
        /// when too few commanders of the tier apply the best available fill in (a mid vs high pair is fine).</summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]   // else Newtonsoft APPENDS the file's rows to these defaults on every load
        public List<TierStep> TierByPlayers = new List<TierStep> { new TierStep(0, "any"), new TierStep(10, "midhigh"), new TierStep(20, "high") };
        /// <summary>Use the larger of the players connected now and at the end of the previous round for the table (the countdown runs right after the map change, when players are still loading).</summary>
        public bool TierPlayersUsePreviousRound = true;
        /// <summary>Cost per Elo point a seated commander's rating sits outside the tier window (after the sigma discount).</summary>
        public double TierWeight = 1.0;
        /// <summary>Tier bounds as quantiles of the established commander population (games &gt;= TierMinGames): "mid" starts at TierMidQuantile, "high" at TierHighQuantile.</summary>
        public double TierMidQuantile = 0.34;
        public double TierHighQuantile = 0.67;
        public int TierMinGames = 5;
        /// <summary>Absolute bounds instead of quantiles (0 = use the quantile).</summary>
        public double TierMidRating = 0;
        public double TierHighRating = 0;
        /// <summary>Players may vote the tier of the next round with !elo vote &lt;tier&gt; (one round, admin override wins).</summary>
        public bool TierVote = true;
        /// <summary>A vote needs at least this many ballots AND this share of the connected players to override the player-count tier.</summary>
        public int TierVoteMinVotes = 3;
        public double TierVoteMinShare = 0.34;

        // ---- team balance (TeamBalance.cs) -------------------------------------
        /// <summary>Elo-aware team placement, the volunteer ask and !reserve.</summary>
        public bool BalanceEnabled = true;
        /// <summary>Handle REQUEST_JOIN_TEAM here and remove Si_BasicTeamBalance's prefix on MP_Strategy.ProcessNetRPC at start-up (the two cannot share the packet). false = Si_BasicTeamBalance keeps the joins; only !reserve and the ask remain.</summary>
        public bool BalanceTakeOverJoinHandling = true;
        /// <summary>Head-count rule, Si_BasicTeamBalance's formula: allowed difference = ceil(players / divisor + addend). Its MelonPreferences values are used when present; these are the fallback.</summary>
        public double BalanceTwoTeamDivisor = 8;
        public double BalanceTwoTeamAddend = 1;
        public double BalanceThreeTeamDivisor = 10;
        public double BalanceThreeTeamAddend = 0;
        /// <summary>A fresh joiner whose pick would push the gap in mean player Elo above this is placed on the team that closes it (counts permitting).</summary>
        public double BalanceEloGapMax = 60;
        /// <summary>Elo placement only when every team already has this many players (small samples are noise).</summary>
        public int BalanceEloMinPlayersPerTeam = 4;
        /// <summary>Elo placement is active during the countdown and this many seconds after round start; afterwards only the head-count rule.</summary>
        public int BalanceEloWindowSeconds = 60;
        /// <summary>During a round, ask for volunteers this often when a team is short by two or weaker than the Elo gap (0 = never).</summary>
        public int BalanceAskIntervalSeconds = 180;
        /// <summary>!reserve moves happen only while the player has no unit (dead / spawn screen).</summary>
        public bool BalanceReserveAtRespawnOnly = true;

        // ---- countdown behaviour --------------------------------------------
        /// <summary>When a seat has no applicant but a !flex volunteer could fill it, extend the countdown once by this many seconds and ask. 0 = never.</summary>
        public int AskExtendSeconds = 15;
        public bool AnnounceApplicants = true;
        public bool AnnounceMatchup = true;
        public bool AnnounceRatingChanges = true;
        /// <summary>Report mean player Elo per team at round start (no players are moved).</summary>
        public bool PlayerBalanceReport = true;

        // ---- rating model (keep in sync with tools/compute_elo.py) ----------
        public double StartRating = 1500;
        public double KNew = 60;
        public double KBase = 24;
        public int ProvisionalGames = 10;
        public double MinShare = 0.10;
        public int LateLeaveSeconds = 600;
        public int MinRoundSeconds = 240;
        public double KPlayer = 16;
        public double PlayerMinShare = 0.25;

        public bool Debug = false;
    }

    public static class ConfigStore
    {
        /// <summary>UserData/cfg/EloMatchmaking — the server's cfg/ folder. A pre-0.2
        /// UserData/EloMatchmaking/ folder (config + ratings.json) is moved there once.</summary>
        public static string Dir => _dir ??= ResolveDir();
        static string? _dir;

        static string ResolveDir()
        {
            string userData = MelonLoader.Utils.MelonEnvironment.UserDataDirectory;
            string dir = Path.Combine(userData, "cfg", "EloMatchmaking");
            string legacy = Path.Combine(userData, "EloMatchmaking");
            try
            {
                if (!Directory.Exists(dir) && Directory.Exists(legacy))
                {
                    Directory.CreateDirectory(Path.Combine(userData, "cfg"));
                    Directory.Move(legacy, dir);
                    MelonLogger.Msg($"[Elo] Moved data folder {legacy} -> {dir}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Elo] Could not move {legacy} -> {dir}: {ex.Message}");
            }
            return dir;
        }
        public static string ConfigPath => Path.Combine(Dir, "Si_EloMatchmaking_Config.json");
        public static string DbPath => Path.Combine(Dir, "ratings.json");

        public static EloConfig Load()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonConvert.DeserializeObject<EloConfig>(File.ReadAllText(ConfigPath)) ?? new EloConfig();
                    if (cfg.TierByPlayers == null || cfg.TierByPlayers.Count == 0) cfg.TierByPlayers = new EloConfig().TierByPlayers;
                    // one row per player count (0.6.0 configs grew duplicate rows on every load), last one wins, sorted
                    var byCount = new Dictionary<int, TierStep>();
                    foreach (var s in cfg.TierByPlayers) if (s != null) byCount[s.Players] = s;
                    cfg.TierByPlayers = new List<TierStep>(byCount.Values);
                    cfg.TierByPlayers.Sort((a, b) => a.Players.CompareTo(b.Players));
                    if (cfg.NoCommanderPenalty < 1000)   // pre-0.5 default 400 could leave a seat to the AI next to a lopsided pair
                    {
                        MelonLogger.Msg($"[Elo] NoCommanderPenalty {cfg.NoCommanderPenalty} raised to 1500 (0.5.0: seats are always filled when anyone applied).");
                        cfg.NoCommanderPenalty = 1500;
                    }
                    Save(cfg); // write back so new keys appear
                    return cfg;
                }
                var fresh = new EloConfig();
                Save(fresh);
                return fresh;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[Elo] Config load failed, using defaults: " + ex.Message);
                return new EloConfig();
            }
        }

        public static void Save(EloConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(cfg, Formatting.Indented));
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[Elo] Config save failed: " + ex.Message);
            }
        }
    }
}
