"""
parse_logs.py - turn Silica Half-Life style server logs (Si_Logging) into one
JSON record per finished round, with commander tenures and player presence.

Usage:
    python -I parse_logs.py <log_dir> [<log_dir> ...] -o rounds.jsonl

Each output line:
{
  "id": "20260601_001427", "start": "2026-06-01T00:14:27", "end": ..., "duration_s": 2955,
  "map": "NarakaCity", "layout": "...", "gametype": "HUMANS_VS_HUMANS_VS_ALIENS",
  "version": "Beta:0.9.27", "winner": "Centauri",
  "teams": {
     "Sol": {"score": 128180, "players_end": 0,
             "commanders": [{"steam": "7656...", "name": "Ko557", "seconds": 210, "share": 0.07,
                             "start_s": 7, "end_s": 217, "end_reason": "disconnect"}],
             "players": {"7656...": {"name": "Ko557", "seconds": 1500}}},
     ...
  }
}

Rules
- A round opens at World "Round_Start" and closes at the first Team "Victory"
  (Round_Win follows in the same second). A new Round_Start, "Loading map" or
  "Log file started" before Victory aborts the open round (written with
  "aborted": true so later stages can skip it).
- Commander tenure for a team starts at 'changed role to "Commander"' and ends
  at the player's next role change to None/Infantry, team change, disconnect,
  another player taking the Commander role on that team, or round end.
- Player presence on a team = seconds between 'joined team' and leaving
  (disconnect / other team), clipped to the round.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from datetime import datetime

LINE_RE = re.compile(r'^L (\d\d)/(\d\d)/(\d{4}) - (\d\d):(\d\d):(\d\d): (.*)$')
# name is non-greedy: kill lines carry a second <slot><steam><team> group for the victim
PLAYER_RE = re.compile(r'^"(?P<name>(?:(?!" ).)*?)<(?P<slot>-?\d+)><(?P<steam>[^<>]*)><(?P<team>[^<>]*)>" (?P<rest>.*)$')
WORLD_RE = re.compile(r'^World triggered "(?P<ev>[^"]+)"(?P<args>.*)$')
TEAM_TRIG_RE = re.compile(r'^Team "(?P<team>[^"]+)" triggered "(?P<ev>[^"]+)"(?P<args>.*)$')
TEAM_SCORE_RE = re.compile(r'^Team "(?P<team>[^"]+)" scored "(?P<score>-?\d+)" with "(?P<n>\d+)" players')
AI_ATTACKER_RE = re.compile(r'^"(?P<aname>(?:(?!" ).)*?)<><><(?P<ateam>[^<>]*)>" (?P<rest>.*)$')
KILL_RE = re.compile(r'^killed "(?P<vname>.*)<(?P<vslot>-?\d*)><(?P<vsteam>[^<>]*)><(?P<vteam>[^<>]*)>" with "(?P<weapon>[^"]*)"(?P<args>.*)$')
PLAYER_SCORE_RE = re.compile(r'^Player "(?P<name>.*)<(?P<slot>-?\d+)><(?P<steam>[^<>]*)><(?P<team>[^<>]*)>" scored "(?P<score>-?\d+)" \(kills "(?P<kills>\d+)"\) \(deaths "(?P<deaths>\d+)"\)')
ARG_RE = re.compile(r'\((\w+) "([^"]*)"\)')
LOADMAP_RE = re.compile(r'^Loading map "(?P<map>[^"]+)"')
LOGSTART_RE = re.compile(r'^Log file started .*\(version "(?P<ver>[^"]+)"\)')

PLAYABLE_TEAMS = ("Sol", "Centauri", "Alien")
# victim names that are buildings (HL "killed" lines use the same format for structures)
STRUCTURE_NAMES = {
    "Headquarters", "Barracks", "Refinery", "LightFactory", "HeavyFactory", "AirFactory", "Outpost",
    "FusionReactor", "FusionReactor_01", "ResearchFacility", "RepairFacility", "Silo", "RadarStation",
    "CommandPost", "DefenseTurret", "AntiAirTurret", "AntiAirRocketTurret", "MachineGunTurret",
    "RocketTurret", "ArtilleryTurret", "ShieldGenerator", "Wall", "Gate", "Bunker",
    "Nest", "Node", "BioCache", "QuantumCortex", "LesserSpawningCyst", "GreaterSpawningCyst",
    "GrandSpawningCyst", "ColossalSpawningCyst", "HiveSpire", "ThornSpire", "Spire", "ShockSpire",
}


def parse_ts(m):
    mo, d, y, hh, mm, ss = (int(m.group(i)) for i in range(1, 7))
    return datetime(y, mo, d, hh, mm, ss)


class Presence:
    """Open interval of a player on a team."""
    __slots__ = ("steam", "name", "team", "since")

    def __init__(self, steam, name, team, since):
        self.steam, self.name, self.team, self.since = steam, name, team, since


class Round:
    def __init__(self, rid, start, map_name, gametype, version):
        self.id = rid
        self.start = start
        self.end = None
        self.map = map_name
        self.layout = None
        self.gametype = gametype
        self.version = version
        self.winner = None
        self.aborted = False
        self.abort_reason = None
        self.scores = {}          # team -> (score, players_end)
        self.player_scores = {}   # steam -> dict
        self.combat = {}          # steam -> {kills_h, kills_ai, deaths_h, deaths_ai, structure_kills}
        self.duels = []           # [attacker_steam, attacker_team, victim_steam, victim_team] in log order
        self.tenures = []         # dict(team, steam, name, start, end, end_reason)
        self.presence = []        # dict(team, steam, name, start, end)
        self.open_cmd = {}        # team -> open tenure dict

    # --- commander tenure -------------------------------------------------
    def start_cmd(self, team, steam, name, ts):
        cur = self.open_cmd.get(team)
        if cur is not None:
            if cur["steam"] == steam:
                return  # re-assertion of the same commander
            self.end_cmd(team, ts, "replaced")
        t = {"team": team, "steam": steam, "name": name, "start": ts, "end": None, "end_reason": None}
        self.tenures.append(t)
        self.open_cmd[team] = t

    def end_cmd(self, team, ts, reason):
        cur = self.open_cmd.pop(team, None)
        if cur is not None:
            cur["end"] = ts
            cur["end_reason"] = reason

    def end_cmd_of_player(self, steam, ts, reason):
        for team, cur in list(self.open_cmd.items()):
            if cur["steam"] == steam:
                self.end_cmd(team, ts, reason)

    def close(self, ts):
        self.end = ts
        for team in list(self.open_cmd):
            self.end_cmd(team, ts, "round_end")

    def combat_of(self, steam):
        c = self.combat.get(steam)
        if c is None:
            c = {"kills_h": 0, "kills_ai": 0, "deaths_h": 0, "deaths_ai": 0, "structure_kills": 0}
            self.combat[steam] = c
        return c

    # --- serialisation ----------------------------------------------------
    def to_record(self):
        dur = max(1, int((self.end - self.start).total_seconds())) if self.end else 0
        teams = {}
        for team in PLAYABLE_TEAMS:
            score, n_end = self.scores.get(team, (0, 0))
            cmds = []
            agg = {}
            for t in self.tenures:
                if t["team"] != team:
                    continue
                s = max(0, int((t["start"] - self.start).total_seconds()))
                e = int(((t["end"] or self.end) - self.start).total_seconds())
                e = min(e, dur)
                if e <= s:
                    continue
                a = agg.setdefault(t["steam"], {"steam": t["steam"], "name": t["name"], "seconds": 0,
                                                "start_s": s, "end_s": e, "end_reason": t["end_reason"]})
                a["seconds"] += e - s
                a["end_s"] = e
                a["end_reason"] = t["end_reason"]
                a["name"] = t["name"]
            for a in agg.values():
                a["share"] = round(a["seconds"] / dur, 4)
                cmds.append(a)
            cmds.sort(key=lambda a: -a["seconds"])
            players = {}
            for p in self.presence:
                if p["team"] != team:
                    continue
                s = max(0, int((p["start"] - self.start).total_seconds()))
                e = int(((p["end"] or self.end) - self.start).total_seconds())
                e = min(e, dur)
                if e <= s:
                    continue
                q = players.setdefault(p["steam"], {"name": p["name"], "seconds": 0})
                q["seconds"] += e - s
                q["name"] = p["name"]
            for steam, q in players.items():
                q["share"] = round(q["seconds"] / dur, 4)
                ps = self.player_scores.get(steam)
                if ps and ps["team"] == team:
                    q["score"] = ps["score"]
                    q["kills"] = ps["kills"]
                    q["deaths"] = ps["deaths"]
                cb = self.combat.get(steam)
                if cb:
                    q.update(cb)
            teams[team] = {"score": score, "players_end": n_end, "commanders": cmds, "players": players}
        return {
            "id": self.id,
            "start": self.start.isoformat(),
            "end": self.end.isoformat() if self.end else None,
            "duration_s": dur,
            "map": self.map,
            "layout": self.layout,
            "gametype": self.gametype,
            "version": self.version,
            "winner": self.winner,
            "aborted": self.aborted,
            "abort_reason": self.abort_reason,
            "teams": teams,
            "duels": self.duels,
        }


class LogParser:
    def __init__(self):
        self.rounds = []
        self.cur: Round | None = None
        self.map = None
        self.version = "UNKNOWN"
        self.online = {}   # steam -> Presence (team may be "")
        self.names = {}    # steam -> last name
        self.last_ts = None

    # --- round lifecycle --------------------------------------------------
    def abort(self, ts, reason):
        if self.cur is not None:
            self.cur.aborted = True
            self.cur.abort_reason = reason
            self.cur.close(ts)
            self._close_presence_all(ts)
            self.rounds.append(self.cur)
            self.cur = None

    def start_round(self, ts, gametype):
        if self.cur is not None:
            self.abort(ts, "new_round_start")
        rid = ts.strftime("%Y%m%d_%H%M%S")
        self.cur = Round(rid, ts, self.map, gametype, self.version)
        # everyone already on a team is present from round start
        for pr in self.online.values():
            if pr.team in PLAYABLE_TEAMS:
                self.cur.presence.append({"team": pr.team, "steam": pr.steam, "name": pr.name, "start": ts, "end": None})

    def _close_presence_all(self, ts):
        if self.cur is None:
            return
        for p in self.cur.presence:
            if p["end"] is None:
                p["end"] = ts

    def finish_round(self, ts):
        if self.cur is None:
            return
        self.cur.close(ts)
        self._close_presence_all(ts)
        self.rounds.append(self.cur)
        self.cur = None

    # --- player presence --------------------------------------------------
    def _end_presence(self, steam, ts):
        if self.cur is None:
            return
        for p in self.cur.presence:
            if p["steam"] == steam and p["end"] is None:
                p["end"] = ts

    def set_team(self, steam, name, team, ts):
        pr = self.online.get(steam)
        if pr is None:
            pr = Presence(steam, name, "", ts)
            self.online[steam] = pr
        if pr.team != team:
            self._end_presence(steam, ts)
            if self.cur is not None:
                self.cur.end_cmd_of_player(steam, ts, "team_change")
            pr.team = team
            pr.since = ts
            if self.cur is not None and team in PLAYABLE_TEAMS:
                self.cur.presence.append({"team": team, "steam": steam, "name": name, "start": ts, "end": None})
        pr.name = name

    def disconnect(self, steam, ts):
        self._end_presence(steam, ts)
        if self.cur is not None:
            self.cur.end_cmd_of_player(steam, ts, "disconnect")
        self.online.pop(steam, None)

    # --- main dispatch ----------------------------------------------------
    def feed_line(self, line):
        m = LINE_RE.match(line)
        if not m:
            return
        ts = parse_ts(m)
        self.last_ts = ts
        rest = m.group(7)

        if rest.startswith('"'):
            pm = PLAYER_RE.match(rest)
            if not pm:
                # AI attacker ("Crab<><><Alien>" killed "...") -> count the human victim's death
                am = AI_ATTACKER_RE.match(rest)
                if am and self.cur is not None:
                    km = KILL_RE.match(am.group("rest"))
                    if km and km.group("vsteam").isdigit():
                        args = dict(ARG_RE.findall(km.group("args")))
                        if args.get("victim", km.group("vname")) not in STRUCTURE_NAMES:
                            self.cur.combat_of(km.group("vsteam"))["deaths_ai"] += 1
                return
            steam = pm.group("steam")
            if not steam or not steam.isdigit():
                # AI attacker: only the human victim's death counts
                km = KILL_RE.match(pm.group("rest"))
                if km and self.cur is not None and km.group("vsteam").isdigit():
                    args = dict(ARG_RE.findall(km.group("args")))
                    if args.get("victim", km.group("vname")) not in STRUCTURE_NAMES:
                        self.cur.combat_of(km.group("vsteam"))["deaths_ai"] += 1
                return
            name = pm.group("name")
            team = pm.group("team")
            action = pm.group("rest")
            self.names[steam] = name
            if action.startswith("joined team "):
                new_team = action[len("joined team "):].strip('"')
                self.set_team(steam, name, new_team, ts)
            elif action.startswith("changed role to "):
                role = action[len("changed role to "):].strip('"')
                if team in PLAYABLE_TEAMS:
                    # make sure presence agrees with the team in the tag
                    self.set_team(steam, name, team, ts)
                if self.cur is not None:
                    if role == "Commander" and team in PLAYABLE_TEAMS:
                        self.cur.start_cmd(team, steam, name, ts)
                    else:
                        cur = self.cur.open_cmd.get(team)
                        if cur is not None and cur["steam"] == steam:
                            self.cur.end_cmd(team, ts, "role_" + role.lower())
            elif action.startswith("killed "):
                km = KILL_RE.match(action)
                if km and self.cur is not None:
                    vsteam = km.group("vsteam")
                    victim_human = vsteam.isdigit()
                    args = dict(ARG_RE.findall(km.group("args")))
                    vunit = args.get("victim", km.group("vname"))
                    is_structure = vunit in STRUCTURE_NAMES
                    if team in PLAYABLE_TEAMS and km.group("vteam") != team:
                        c = self.cur.combat_of(steam)
                        if is_structure:
                            c["structure_kills"] += 1
                        elif victim_human:
                            c["kills_h"] += 1
                            if vsteam != steam:
                                self.cur.duels.append([steam, team, vsteam, km.group("vteam")])
                        else:
                            c["kills_ai"] += 1
                    if victim_human and not is_structure:
                        self.cur.combat_of(vsteam)["deaths_h"] += 1
            elif action == "disconnected":
                self.disconnect(steam, ts)
            elif action == "entered the game":
                if steam not in self.online:
                    self.online[steam] = Presence(steam, name, "", ts)
            # kills, say, suicide, etc. are ignored here
            return

        if rest.startswith("World triggered"):
            wm = WORLD_RE.match(rest)
            if not wm:
                return
            ev = wm.group("ev")
            args = dict(ARG_RE.findall(wm.group("args")))
            if ev == "Round_Start":
                self.start_round(ts, args.get("gametype", "?"))
            elif ev == "map_layout":
                if self.cur is not None:
                    self.cur.layout = args.get("layout")
            elif ev == "Round_Win":
                if self.cur is not None and self.cur.winner is not None:
                    self.finish_round(ts)
            return

        if rest.startswith("Team "):
            tm = TEAM_TRIG_RE.match(rest)
            if tm:
                if tm.group("ev") == "Victory" and self.cur is not None:
                    if self.cur.winner is None:
                        self.cur.winner = tm.group("team")
                        # Round_Win follows in the same second; close now in case it does not
                        self._pending_close = ts
                return
            sm = TEAM_SCORE_RE.match(rest)
            if sm and self.cur is not None:
                self.cur.scores[sm.group("team")] = (int(sm.group("score")), int(sm.group("n")))
            return

        if rest.startswith("Player "):
            pm = PLAYER_SCORE_RE.match(rest)
            if pm and self.cur is not None and pm.group("steam").isdigit():
                self.cur.player_scores[pm.group("steam")] = {
                    "team": pm.group("team"), "score": int(pm.group("score")),
                    "kills": int(pm.group("kills")), "deaths": int(pm.group("deaths"))}
            return

        if rest.startswith("Loading map"):
            lm = LOADMAP_RE.match(rest)
            if self.cur is not None:
                if self.cur.winner is not None:
                    self.finish_round(ts)   # Victory seen but Round_Win missing
                else:
                    self.abort(ts, "map_load")
            if lm:
                self.map = lm.group("map")
            # a map load drops everyone back to "no team" (not logged per player)
            for pr in self.online.values():
                pr.team = ""
                pr.since = ts
            return

        if rest.startswith("Log file started"):
            lm = LOGSTART_RE.match(rest)
            if lm and lm.group("ver") != "UNKNOWN":
                self.version = lm.group("ver")
            # server (re)start: everyone is gone
            if self.cur is not None:
                if self.cur.winner is not None:
                    self.finish_round(ts)
                else:
                    self.abort(ts, "server_restart")
            self.online.clear()
            return

        if rest.startswith("Log file closed"):
            return

    def feed_file(self, path):
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                self.feed_line(line.rstrip("\r\n"))

    def finish(self):
        if self.cur is not None:
            if self.cur.winner is not None:
                self.finish_round(self.last_ts)
            else:
                self.abort(self.last_ts, "eof")


def iter_log_files(dirs):
    files = []
    for d in dirs:
        for fn in os.listdir(d):
            if fn.lower().endswith(".log") and fn.startswith("L"):
                files.append(os.path.join(d, fn))
    files.sort(key=lambda p: os.path.basename(p))
    return files


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("log_dirs", nargs="+")
    ap.add_argument("-o", "--out", required=True)
    a = ap.parse_args(argv)

    p = LogParser()
    files = iter_log_files(a.log_dirs)
    for i, path in enumerate(files):
        p.feed_file(path)
    p.finish()

    n_ok = n_ab = 0
    with open(a.out, "w", encoding="utf-8") as f:
        for r in p.rounds:
            rec = r.to_record()
            n_ab += rec["aborted"]
            n_ok += not rec["aborted"]
            f.write(json.dumps(rec) + "\n")
    print(f"files={len(files)} rounds_ok={n_ok} rounds_aborted={n_ab} players_seen={len(p.names)} -> {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
