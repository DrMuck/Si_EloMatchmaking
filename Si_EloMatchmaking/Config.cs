/*
 Config.cs - JSON config for Si_EloMatchmaking.
 File: UserData\cfg\EloMatchmaking\Si_EloMatchmaking_Config.json (created with defaults on first run).
*/
using MelonLoader;
using Newtonsoft.Json;
using System;
using System.IO;

namespace Si_EloMatchmaking
{
    public class EloConfig
    {
        // ---- master switches -------------------------------------------------
        /// <summary>Mod does the commander pick at round start. Off = vanilla / CommManagement lottery.</summary>
        public bool Enabled = true;
        /// <summary>Update ratings.json from finished rounds.</summary>
        public bool UpdateRatingsLive = true;

        // ---- matchmaking objective ------------------------------------------
        /// <summary>Score = mean(rating) - SpreadWeight * (max - min) - penalties. 1.0 = one Elo point of spread costs one point of level.</summary>
        public double SpreadWeight = 1.0;
        /// <summary>Applicant for Sol may be seated as Centauri commander and vice versa.</summary>
        public bool AllowHumanFactionSwap = true;
        /// <summary>Cost of moving an applicant to the other human faction.</summary>
        public double SwapPenalty = 30;
        /// <summary>Applicants who typed !flex may be seated on any faction (incl. Alien).</summary>
        public bool AllowVolunteers = true;
        /// <summary>Cost of using a !flex volunteer for a seat they did not apply for.</summary>
        public double VolunteerPenalty = 60;
        /// <summary>Cost for seating someone who commanded in the previous round (0 = off). Helps rotation.</summary>
        public double RecentCommanderPenalty = 30;
        /// <summary>Rating uncertainty of a commander with 0 games (Elo points). A mismatch against an uncertain rating is discounted by the sum of both sigmas.</summary>
        public double SigmaNew = 200;
        /// <summary>Uncertainty floor for well-established commanders.</summary>
        public double SigmaMin = 40;
        /// <summary>Each round an applicant is passed over adds this to their effective rating for the pick (rotation guarantee). Reset when seated.</summary>
        public double WaitBonusPerRound = 60;
        /// <summary>Cap for the accumulated wait bonus.</summary>
        public double WaitBonusMax = 420;
        /// <summary>Cost of leaving a seat with players to the AI. Keep large.</summary>
        public double NoCommanderPenalty = 400;
        /// <summary>Only pick among applicants with at least this many rated games when enough are available (0 = off).</summary>
        public int PreferEstablishedMinGames = 0;

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
