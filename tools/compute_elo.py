"""
compute_elo.py - commander (and optional player) Elo from rounds.jsonl.

Usage:
    python -I compute_elo.py data/rounds.jsonl -o data/ratings.json [--report data/report.md]

Model
- Every finished round with >= MIN_DURATION_S is scored. A team takes part in
  the commander rating when at least one human commanded it for >= MIN_SHARE
  of the round. Teams without a human commander (AI or nobody) are ignored.
- Team rating = share-weighted mean of its commanders' ratings.
- Pairwise Elo between the winner and every other commanded team. Loser-vs-
  loser pairs in 3-way rounds carry no information and are skipped.
  Expected score E = 1 / (1 + 10^((Rb - Ra - adv) / 400)), where adv is a
  small faction/mode advantage estimated from the data (shrunk to 0).
- Each commander on a team receives K * share * (S - E) / (n_teams - 1), so a
  commander who ran 85 % of a lost round takes 85 % of the loss.
- Leaver rule: a commander whose tenure ended by disconnect / resign / team
  change within LATE_LEAVE_S of the end of a round their team LOST is treated
  as if they had stayed to the end (share extended). Leaving late does not
  dodge the loss.
- K = K_NEW for the first PROVISIONAL_GAMES games, K_BASE afterwards.
- Players (optional balance): same scheme over everybody present >= PLAYER_MIN_SHARE
  of the round, team rating = mean of player ratings, K_PLAYER * share.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from collections import defaultdict
from datetime import datetime, timezone

START = 1500.0
K_NEW = 60.0
K_BASE = 24.0
PROVISIONAL_GAMES = 10
MIN_DURATION_S = 240
MIN_SHARE = 0.10
LATE_LEAVE_S = 600
LEAVE_REASONS = ("disconnect", "role_none", "role_infantry", "team_change")
ADV_PRIOR_N = 60          # pseudo-rounds pulling the faction advantage to 0
ADV_CAP = 100.0
K_PLAYER = 16.0
PLAYER_MIN_SHARE = 0.25
PLAYABLE = ("Sol", "Centauri", "Alien")


def expected(ra, rb, adv=0.0):
    return 1.0 / (1.0 + 10 ** ((rb - ra - adv) / 400.0))


def load_rounds(path):
    out = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            r = json.loads(line)
            if r.get("aborted") or r.get("winner") not in PLAYABLE:
                continue
            if r["duration_s"] < MIN_DURATION_S:
                continue
            out.append(r)
    out.sort(key=lambda r: r["start"])
    return out


def commanded_teams(r):
    """team -> list of (steam, name, share) after the leaver rule and MIN_SHARE."""
    dur = r["duration_s"]
    res = {}
    for team in PLAYABLE:
        tv = r["teams"].get(team)
        if not tv or not tv["commanders"]:
            continue
        lost = team != r["winner"]
        entries = []
        for c in tv["commanders"]:
            secs = c["seconds"]
            late_leave = False
            if lost and c["end_reason"] in LEAVE_REASONS and (dur - c["end_s"]) <= LATE_LEAVE_S:
                secs += dur - c["end_s"]
                late_leave = True
            share = secs / dur
            if share >= MIN_SHARE:
                entries.append({"steam": c["steam"], "name": c["name"], "share": share, "late_leave": late_leave})
        if not entries:
            continue
        tot = sum(e["share"] for e in entries)
        if tot > 1.0:
            for e in entries:
                e["share"] /= tot
        res[team] = entries
    return res


def estimate_faction_adv(rounds):
    """adv[(gametype, A, B)] = shrunk 400*log10(p/(1-p)) of A beating B (A<B order)."""
    wins = defaultdict(float)
    n = defaultdict(float)
    for r in rounds:
        ct = commanded_teams(r)
        teams = sorted(ct)
        if r["winner"] not in ct:
            continue
        for i, a in enumerate(teams):
            for b in teams[i + 1:]:
                if r["winner"] not in (a, b):
                    continue
                key = (r["gametype"], a, b)
                n[key] += 1
                wins[key] += 1.0 if r["winner"] == a else 0.0
    adv = {}
    for key, cnt in n.items():
        p = (wins[key] + ADV_PRIOR_N * 0.5) / (cnt + ADV_PRIOR_N)
        v = 400.0 * math.log10(p / (1 - p))
        adv[key] = max(-ADV_CAP, min(ADV_CAP, v))
    return adv, n, wins


def adv_for(adv, gametype, a, b):
    if a < b:
        return adv.get((gametype, a, b), 0.0)
    return -adv.get((gametype, b, a), 0.0)


def new_entry(name):
    return {"name": name, "rating": START, "games": 0, "wins": 0, "losses": 0,
            "peak": START, "last_seen": None, "late_leaves": 0,
            "factions": {t: {"games": 0, "wins": 0, "rating": START} for t in PLAYABLE}}


def k_for(games):
    return K_NEW if games < PROVISIONAL_GAMES else K_BASE


def run(rounds, with_players=True):
    adv, adv_n, adv_w = estimate_faction_adv(rounds)
    cmd = {}
    ply = {}
    preds = []   # (expected_for_winner_side, both_established) for validation

    for r in rounds:
        ct = commanded_teams(r)
        winner = r["winner"]
        nteams = len(ct)
        # ---- commanders ------------------------------------------------
        if nteams >= 2 and winner in ct:
            for team, entries in ct.items():
                for e in entries:
                    cmd.setdefault(e["steam"], new_entry(e["name"]))
            team_rating = {}
            for team, entries in ct.items():
                w = sum(e["share"] for e in entries)
                team_rating[team] = sum(cmd[e["steam"]]["rating"] * e["share"] for e in entries) / w
            deltas = defaultdict(float)
            # per-faction ratings: same scheme, expectations from the faction ratings
            team_rating_f = {}
            for team, entries in ct.items():
                w = sum(e["share"] for e in entries)
                team_rating_f[team] = sum(cmd[e["steam"]]["factions"][team]["rating"] * e["share"] for e in entries) / w
            deltas_f = defaultdict(float)
            for team, entries in ct.items():
                if team == winner:
                    continue
                ra, rb = team_rating[winner], team_rating[team]
                ea = expected(ra, rb, adv_for(adv, r["gametype"], winner, team))
                ea_f = expected(team_rating_f[winner], team_rating_f[team], adv_for(adv, r["gametype"], winner, team))
                established = all(cmd[e["steam"]]["games"] >= PROVISIONAL_GAMES
                                  for e in ct[winner] + entries)
                preds.append((ea, established, r["gametype"]))
                for e in ct[winner]:
                    g = cmd[e["steam"]]["games"]
                    deltas[e["steam"]] += k_for(g) * e["share"] * (1.0 - ea) / (nteams - 1)
                    deltas_f[e["steam"]] += k_for(g) * e["share"] * (1.0 - ea_f) / (nteams - 1)
                for e in entries:
                    g = cmd[e["steam"]]["games"]
                    deltas[e["steam"]] += k_for(g) * e["share"] * (0.0 - (1.0 - ea)) / (nteams - 1)
                    deltas_f[e["steam"]] += k_for(g) * e["share"] * (0.0 - (1.0 - ea_f)) / (nteams - 1)
            for team, entries in ct.items():
                for e in entries:
                    c = cmd[e["steam"]]
                    c["rating"] += deltas[e["steam"]]
                    c["factions"][team]["rating"] += deltas_f[e["steam"]]
                    c["games"] += 1
                    c["name"] = e["name"]
                    c["last_seen"] = r["end"]
                    c["peak"] = max(c["peak"], c["rating"])
                    c["factions"][team]["games"] += 1
                    if team == winner:
                        c["wins"] += 1
                        c["factions"][team]["wins"] += 1
                    else:
                        c["losses"] += 1
                    if e["late_leave"]:
                        c["late_leaves"] += 1
        # ---- players ---------------------------------------------------
        if with_players:
            pt = {}
            for team in PLAYABLE:
                tv = r["teams"].get(team)
                if not tv:
                    continue
                members = [(s, q) for s, q in tv["players"].items() if q["share"] >= PLAYER_MIN_SHARE]
                if members:
                    pt[team] = members
            if len(pt) >= 2 and winner in pt:
                for team, members in pt.items():
                    for s, q in members:
                        ply.setdefault(s, new_entry(q["name"]))
                tr = {t: sum(ply[s]["rating"] for s, _ in m) / len(m) for t, m in pt.items()}
                deltas = defaultdict(float)
                for team, members in pt.items():
                    if team == winner:
                        continue
                    ea = expected(tr[winner], tr[team], adv_for(adv, r["gametype"], winner, team))
                    for s, q in pt[winner]:
                        deltas[s] += K_PLAYER * q["share"] * (1.0 - ea) / (len(pt) - 1)
                    for s, q in members:
                        deltas[s] += K_PLAYER * q["share"] * (0.0 - (1.0 - ea)) / (len(pt) - 1)
                for team, members in pt.items():
                    for s, q in members:
                        p = ply[s]
                        p["rating"] += deltas[s]
                        p["games"] += 1
                        p["name"] = q["name"]
                        p["last_seen"] = r["end"]
                        p["peak"] = max(p["peak"], p["rating"])
                        p["factions"][team]["games"] += 1
                        if team == winner:
                            p["wins"] += 1
                            p["factions"][team]["wins"] += 1
                        else:
                            p["losses"] += 1
    return cmd, ply, adv, adv_n, adv_w, preds


def validation(preds):
    """Log-loss / accuracy of the pre-round expectation for the eventual winner."""
    out = {}
    for label, sel in (("all", preds), ("established", [p for p in preds if p[1]])):
        if not sel:
            continue
        ll = -sum(math.log(max(1e-9, e)) for e, _, _ in sel) / len(sel)
        acc = sum(1 for e, _, _ in sel if e > 0.5) / len(sel)
        out[label] = {"n": len(sel), "logloss": round(ll, 4), "baseline_logloss": round(math.log(2), 4),
                      "winner_was_favourite": round(acc, 3)}
    return out


def write_report(path, cmd, ply, adv, adv_n, adv_w, val, n_rounds):
    def esc(s):
        return s.replace("|", "\\|")
    lines = [f"# Silica commander Elo - log-derived seed\n",
             f"Rounds scored: {n_rounds}. Commanders rated: {len(cmd)} "
             f"({sum(1 for c in cmd.values() if c['games'] >= PROVISIONAL_GAMES)} with >= {PROVISIONAL_GAMES} games). "
             f"Players rated: {len(ply)}.\n",
             "## Validation (pre-round expectation of the eventual winner)\n",
             "| set | n | log-loss | coin-flip log-loss | winner was favourite |", "|---|---|---|---|---|"]
    for k, v in val.items():
        lines.append(f"| {k} | {v['n']} | {v['logloss']} | {v['baseline_logloss']} | {v['winner_was_favourite']} |")
    lines += ["", "## Faction advantage used (Elo points, A over B)\n",
              "| mode | A | B | rounds | A win rate | adv |", "|---|---|---|---|---|---|"]
    for key in sorted(adv):
        gt, a, b = key
        lines.append(f"| {gt} | {a} | {b} | {int(adv_n[key])} | {adv_w[key] / adv_n[key]:.3f} | {adv[key]:+.1f} |")
    lines += ["", f"## Top commanders (>= {PROVISIONAL_GAMES} games)\n",
              "| # | commander | rating | games | W-L | Sol | Cent | Alien | late leaves | last seen |",
              "|---|---|---|---|---|---|---|---|---|---|"]
    ranked = sorted((c for c in cmd.values() if c["games"] >= PROVISIONAL_GAMES), key=lambda c: -c["rating"])
    for i, c in enumerate(ranked[:60], 1):
        f = c["factions"]
        lines.append(f"| {i} | {esc(c['name'])} | {c['rating']:.0f} | {c['games']} | {c['wins']}-{c['losses']} | "
                     f"{f['Sol']['wins']}/{f['Sol']['games']} | {f['Centauri']['wins']}/{f['Centauri']['games']} | "
                     f"{f['Alien']['wins']}/{f['Alien']['games']} | {c['late_leaves']} | {(c['last_seen'] or '')[:10]} |")
    lines += ["", "## Rating distribution (commanders with >= 10 games)\n"]
    rs = sorted(c["rating"] for c in ranked)
    if rs:
        q = lambda p: rs[min(len(rs) - 1, int(p * len(rs)))]
        lines.append(f"min {rs[0]:.0f} | p10 {q(0.1):.0f} | p25 {q(0.25):.0f} | median {q(0.5):.0f} | "
                     f"p75 {q(0.75):.0f} | p90 {q(0.9):.0f} | max {rs[-1]:.0f}")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("rounds")
    ap.add_argument("-o", "--out", required=True)
    ap.add_argument("--report")
    ap.add_argument("--no-players", action="store_true")
    a = ap.parse_args(argv)

    rounds = load_rounds(a.rounds)
    cmd, ply, adv, adv_n, adv_w, preds = run(rounds, with_players=not a.no_players)
    val = validation(preds)

    def rnd(d):
        return {k: {**v, "rating": round(v["rating"], 1), "peak": round(v["peak"], 1)} for k, v in d.items()}

    db = {
        "schema": 1,
        "generated": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "source": a.rounds,
        "rounds_scored": len(rounds),
        "params": {"start": START, "k_new": K_NEW, "k_base": K_BASE, "provisional_games": PROVISIONAL_GAMES,
                   "min_share": MIN_SHARE, "late_leave_s": LATE_LEAVE_S, "k_player": K_PLAYER,
                   "player_min_share": PLAYER_MIN_SHARE},
        "faction_adv": {f"{gt}|{x}|{y}": round(v, 1) for (gt, x, y), v in adv.items()},
        "validation": val,
        "commanders": rnd(cmd),
        "players": rnd(ply),
    }
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(db, f, indent=1, ensure_ascii=False)
    if a.report:
        write_report(a.report, cmd, ply, adv, adv_n, adv_w, val, len(rounds))
    print(f"rounds={len(rounds)} commanders={len(cmd)} players={len(ply)} validation={val}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
