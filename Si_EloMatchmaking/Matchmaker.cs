/*
 Matchmaker.cs - choose one commander per active team from the applicant pool.

 Objective (higher is better):
   score = mean(rating of seated commanders)
         + sum of wait bonuses of seated commanders (rounds passed over * WaitBonusPerRound, capped)
         - SpreadWeight * spread, spread = max over seated pairs of max(0, |Ri - Rj| - (sigma_i + sigma_j))
           (a gap is only penalised beyond what the two uncertainties can explain, so a
            provisional 1500? vs an established 1800 counts as a 60-point gap, not 300)
         - SwapPenalty      per applicant seated on the other human faction
         - VolunteerPenalty per !flex volunteer seated on a faction they did not apply for
         - RecentCommanderPenalty per commander of the previous round
         - NoCommanderPenalty per seat (team with players) left to the AI
 "mean" rewards high-Elo matchups, "spread" rewards even ones; SpreadWeight
 sets the exchange rate. All assignments are enumerated (pools are tiny).
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
        public bool Flex;
        public bool Recent;
        public string Source = "";   // "button" | "chat" | "both"
    }

    public class Seat
    {
        public Team Team = null!;
        public string Faction = "";
        public int Players;
    }

    public class Assignment
    {
        public List<Seat> Seats = new List<Seat>();
        public Candidate?[] Picks = Array.Empty<Candidate?>();
        public double Score = double.NegativeInfinity;
        public double Mean;
        public double Spread;
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

        static Kind? Eligibility(Seat seat, Candidate c, EloConfig cfg)
        {
            if (ReferenceEquals(c.AppliedTeam, seat.Team)) return Kind.Own;   // same Team instance (avoids Unity's fake-null ==)
            if (cfg.AllowHumanFactionSwap && GameApi.IsHumanFaction(seat.Faction) && GameApi.IsHumanFaction(c.AppliedFaction)) return Kind.Swap;
            if (cfg.AllowVolunteers && c.Flex) return Kind.Volunteer;
            return null;
        }

        public static Assignment Solve(List<Seat> seats, List<Candidate> cands, EloConfig cfg, Random rng)
        {
            // Random order breaks ties between equal ratings fairly.
            var pool = cands.OrderBy(_ => rng.Next()).ToList();

            // Optional: when enough established applicants exist, ignore provisional ones.
            if (cfg.PreferEstablishedMinGames > 0)
            {
                var est = pool.Where(c => c.Games >= cfg.PreferEstablishedMinGames).ToList();
                if (est.Count >= seats.Count) pool = est;
            }

            var best = new Assignment { Seats = seats, Picks = new Candidate?[seats.Count] };
            var cur = new Candidate?[seats.Count];
            var used = new HashSet<Candidate>();
            Recurse(0, seats, pool, cfg, cur, used, best);
            return best;
        }

        static void Recurse(int i, List<Seat> seats, List<Candidate> pool, EloConfig cfg, Candidate?[] cur, HashSet<Candidate> used, Assignment best)
        {
            if (i == seats.Count)
            {
                Evaluate(seats, cur, cfg, best);
                return;
            }
            foreach (var c in pool)
            {
                if (used.Contains(c)) continue;
                if (Eligibility(seats[i], c, cfg) == null) continue;
                cur[i] = c; used.Add(c);
                Recurse(i + 1, seats, pool, cfg, cur, used, best);
                used.Remove(c); cur[i] = null;
            }
            cur[i] = null;
            Recurse(i + 1, seats, pool, cfg, cur, used, best);
        }

        static void Evaluate(List<Seat> seats, Candidate?[] cur, EloConfig cfg, Assignment best)
        {
            double sum = 0, pen = 0, spread = 0, bonus = 0;
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
                if (c.Recent) pen += cfg.RecentCommanderPenalty;
            }
            double mean = n > 0 ? sum / n : 0;
            double score = (n > 0 ? mean - cfg.SpreadWeight * spread : 0) - pen + bonus;
            if (score > best.Score)
            {
                best.Score = score;
                best.Mean = mean;
                best.Spread = spread;
                best.Swaps = swaps;
                best.Volunteers = vols;
                best.Unfilled = unfilled;
                Array.Copy(cur, best.Picks, cur.Length);
            }
        }
    }
}
