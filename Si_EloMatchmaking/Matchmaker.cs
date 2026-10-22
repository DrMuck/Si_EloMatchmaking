/*
 Matchmaker.cs - choose one commander per active team from the applicant pool.

 Objective (higher is better):
   score = LevelWeight * mean(rating of seated commanders)
         + sum of wait bonuses of seated commanders (rounds passed over * WaitBonusPerRound, capped)
         - SpreadWeight * spread, spread = max over seated pairs of max(0, |Ri - Rj| - (sigma_i + sigma_j))
           (a gap is only penalised beyond what the two uncertainties can explain, so a
            provisional 1500? vs an established 1800 counts as a 60-point gap, not 300)
         - TierWeight * sum over seated of max(0, distance of the rating from the tier window - sigma)
           (the tier the round aims for: any / low / mid / midhigh / high; Tiers.cs)
         - SwapPenalty      per applicant seated on the other human faction
         - VolunteerPenalty per !flex volunteer seated on a faction they did not apply for
         - min(StreakPenaltyMax, RecentCommanderPenalty * streak) per commander who was seated in
           the previous `streak` consecutive rounds (rotation: a seat gets harder to keep)
         - NoCommanderPenalty per seat (team with players) left to the AI
 "mean" rewards high-Elo matchups (mildly at LevelWeight 0.3), "spread" rewards even ones and the
 tier term pulls the pick into the rating band the server size asks for. All assignments are
 enumerated (pools are tiny). !elo pass is applied before the search (ApplyPass).
*/
using System;
using System.Collections.Generic;
using System.Linq;

namespace Si_EloMatchmaking
{
    public class Candidate
    {
        public Player Player = null!;
        public string Steam = "";
        public string Name = "";
        public Team AppliedTeam = null!;
        public string AppliedFaction = "";
        public double Rating;
        public int Games;
        /// <summary>Rating uncertainty (Elo points); large for new commanders.</summary>
        public double Sigma;
        /// <summary>Seating bonus from rounds passed over (rotation guarantee).</summary>
        public double WaitBonus;
        /// <summary>!flex on: accepts any seat.</summary>
        public bool Flex;
        /// <summary>!flex alien / !flex sol cent: the factions accepted besides the one applied for (null = none). Also limits the Sol/Centauri swap for this player.</summary>
        public List<string>? FlexFactions;
        /// <summary>!elo pass: steps aside when a peer applies (ApplyPass).</summary>
        public bool Pass;
        /// <summary>Consecutive rounds this applicant was seated by the pick, ending with the previous round (0 = not last round).</summary>
        public int Streak;
        public bool Recent;
        public string Source = "";   // "button" | "chat" | "both"
    }

    public class Seat
    {
        public Team Team = null!;
        public string Faction = "";
        public int Players;
    }

    /// <summary>Rating band the pick aims for. Any = no bounds.</summary>
    public class TierWindow
    {
        public string Name = "any";
        public double Low = double.NegativeInfinity;
        public double High = double.PositiveInfinity;
        public bool IsAny => double.IsNegativeInfinity(Low) && double.IsPositiveInfinity(High);
        public double Distance(double rating) => rating < Low ? Low - rating : rating > High ? rating - High : 0;
    }

    public class Assignment
    {
        public List<Seat> Seats = new List<Seat>();
        public Candidate?[] Picks = Array.Empty<Candidate?>();
        public double Score = double.NegativeInfinity;
        public double Mean;
        public double Spread;
        public double TierCost;
        public string Tier = "any";
        public int Swaps;
        public int Volunteers;
        public int Unfilled;

        public IEnumerable<(Seat seat, Candidate cand)> Seated()
        {
            for (int i = 0; i < Seats.Count; i++)
                if (Picks[i] != null) yield return (Seats[i], Picks[i]!);
        }
    }

    public static class Matchmaker
    {
        enum Kind { Own, Swap, Volunteer }

        static bool Accepts(Candidate c, string faction) => c.Flex || (c.FlexFactions != null && c.FlexFactions.Contains(faction));

        static Kind? Eligibility(Seat seat, Candidate c, EloConfig cfg)
        {
            if (ReferenceEquals(c.AppliedTeam, seat.Team)) return Kind.Own;   // same Team instance (avoids Unity's fake-null ==)
            if (cfg.AllowHumanFactionSwap && GameApi.IsHumanFaction(seat.Faction) && GameApi.IsHumanFaction(c.AppliedFaction)
                && (c.FlexFactions == null || Accepts(c, seat.Faction))) return Kind.Swap;   // an explicit flex set also limits the swap
            if (cfg.AllowVolunteers && Accepts(c, seat.Faction)) return Kind.Volunteer;
            return null;
        }

        /// <summary>
        /// !elo pass: a passing applicant steps aside when the pick would seat them AND the applicant who takes
        /// that very seat without them is rated within PassGap below them (or higher) and is not passing himself.
        /// A passer is never replaced by the AI or by a clearly weaker commander. Removes them from `cands`
        /// and returns them, so the caller can respawn them.
        /// </summary>
        public static List<Candidate> ApplyPass(List<Seat> seats, List<Candidate> cands, EloConfig cfg, Random rng, TierWindow? tier = null)
        {
            var stepped = new List<Candidate>();
            foreach (var p in cands.Where(c => c.Pass).ToList())
            {
                var with = Solve(seats, cands, cfg, rng, null, tier);
                int seatIdx = Array.IndexOf(with.Picks, p);
                if (seatIdx < 0) continue;   // not seated anyway
                var without = Solve(seats, cands.Where(c => !ReferenceEquals(c, p)).ToList(), cfg, rng, null, tier);
                var repl = seatIdx < without.Picks.Length ? without.Picks[seatIdx] : null;
                if (repl == null || repl.Pass || repl.Rating < p.Rating - cfg.PassGap) continue;   // AI or clearly weaker: keep the seat
                stepped.Add(p);
                cands.Remove(p);
            }
            return stepped;
        }

        /// <param name="fixedPicks">Optional per-seat pre-assignment (admin pins): that seat always takes that candidate.</param>
        /// <param name="tier">Rating band the round aims for (null = any).</param>
        public static Assignment Solve(List<Seat> seats, List<Candidate> cands, EloConfig cfg, Random rng, Candidate?[]? fixedPicks = null, TierWindow? tier = null)
        {
            // Random order breaks ties between equal ratings fairly.
            var pool = cands.OrderBy(_ => rng.Next()).ToList();
            if (fixedPicks != null) pool = pool.Where(c => Array.IndexOf(fixedPicks, c) < 0).ToList();

            // Optional: when enough established applicants exist, ignore provisional ones.
            if (cfg.PreferEstablishedMinGames > 0)
            {
                var est = pool.Where(c => c.Games >= cfg.PreferEstablishedMinGames).ToList();
                if (est.Count >= seats.Count) pool = est;
            }

            var best = new Assignment { Seats = seats, Picks = new Candidate?[seats.Count], Tier = tier?.Name ?? "any" };
            var cur = new Candidate?[seats.Count];
            var used = new HashSet<Candidate>();
            Recurse(0, seats, pool, cfg, cur, used, best, fixedPicks, tier);
            return best;
        }

        static void Recurse(int i, List<Seat> seats, List<Candidate> pool, EloConfig cfg, Candidate?[] cur, HashSet<Candidate> used, Assignment best, Candidate?[]? fixedPicks, TierWindow? tier)
        {
            if (i == seats.Count)
            {
                Evaluate(seats, cur, cfg, best, tier);
                return;
            }
            if (fixedPicks != null && i < fixedPicks.Length && fixedPicks[i] != null)
            {
                cur[i] = fixedPicks[i];
                Recurse(i + 1, seats, pool, cfg, cur, used, best, fixedPicks, tier);
                cur[i] = null;
                return;
            }
            foreach (var c in pool)
            {
                if (used.Contains(c)) continue;
                if (Eligibility(seats[i], c, cfg) == null) continue;
                cur[i] = c; used.Add(c);
                Recurse(i + 1, seats, pool, cfg, cur, used, best, fixedPicks, tier);
                used.Remove(c); cur[i] = null;
            }
            cur[i] = null;
            Recurse(i + 1, seats, pool, cfg, cur, used, best, fixedPicks, tier);
        }

        public static double StreakPenalty(int streak, EloConfig cfg) => streak <= 0 ? 0 : Math.Min(cfg.StreakPenaltyMax, cfg.RecentCommanderPenalty * streak);

        static void Evaluate(List<Seat> seats, Candidate?[] cur, EloConfig cfg, Assignment best, TierWindow? tier)
        {
            double sum = 0, pen = 0, spread = 0, bonus = 0, tierCost = 0;
            int n = 0, swaps = 0, vols = 0, unfilled = 0;
            for (int i = 0; i < seats.Count; i++)
            {
                var c = cur[i];
                if (c == null)
                {
                    pen += cfg.NoCommanderPenalty;
                    unfilled++;
                    continue;
                }
                n++;
                sum += c.Rating;
                bonus += c.WaitBonus;
                for (int j = 0; j < i; j++)
                {
                    var o = cur[j];
                    if (o == null) continue;
                    double gap = Math.Abs(c.Rating - o.Rating) - (c.Sigma + o.Sigma);
                    if (gap > spread) spread = gap;
                }
                switch (Eligibility(seats[i], c, cfg))
                {
                    case Kind.Swap: pen += cfg.SwapPenalty; swaps++; break;
                    case Kind.Volunteer: pen += cfg.VolunteerPenalty; vols++; break;
                }
                pen += StreakPenalty(c.Streak > 0 ? c.Streak : (c.Recent ? 1 : 0), cfg);
                if (tier != null && !tier.IsAny)
                    tierCost += cfg.TierWeight * Math.Max(0, tier.Distance(c.Rating) - c.Sigma);
            }
            double mean = n > 0 ? sum / n : 0;
            double score = (n > 0 ? cfg.LevelWeight * mean - cfg.SpreadWeight * spread : 0) - pen - tierCost + bonus;
            if (score > best.Score)
            {
                best.Score = score;
                best.Mean = mean;
                best.Spread = spread;
                best.TierCost = tierCost;
                best.Swaps = swaps;
                best.Volunteers = vols;
                best.Unfilled = unfilled;
                Array.Copy(cur, best.Picks, cur.Length);
            }
        }
    }
}
