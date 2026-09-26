"""File output shared by cli.py and api.py: raw JSON per call (playbook
"SaveRawJsonPage" - debugging and re-parsing without re-crawling) and the
normalized result per event. Output dirs are gitignored (python/output/)."""

import dataclasses
import json
import os


def to_jsonable(res: dict) -> dict:
    ev = res["event"]
    return {
        "event": dataclasses.asdict(ev) | {"event_url": ev.event_url},
        "coverage": res.get("coverage", ""),
        "notes": res.get("notes", []),
        "price_levels": [dataclasses.asdict(p) for p in res.get("price_levels", [])],
        "listings": [dataclasses.asdict(l) | {"quantity": l.quantity, "seat_range": l.seat_range_label}
                     for l in res.get("listings", [])],
    }


def raw_writer(out_dir: str):
    """on_raw hook for AxsClient: {out_dir}/{eventId}/raw_{name}.json.
    "event" is always the first raw of a fetch, so it clears that event's
    previous raw_*.json - otherwise a file from an older run (e.g. a
    mapinfo that this run didn't capture) sits next to fresh ones."""
    def write(event_id, name, obj):
        d = os.path.join(out_dir, str(event_id))
        os.makedirs(d, exist_ok=True)
        if name == "event":
            for f in os.listdir(d):
                if f.startswith("raw_") and f.endswith(".json"):
                    os.remove(os.path.join(d, f))
        with open(os.path.join(d, f"raw_{name}.json"), "w") as f:
            json.dump(obj, f)
    return write


def write_result(out_dir: str, res: dict) -> str:
    d = os.path.join(out_dir, str(res["event"].event_id))
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, "result.json")
    with open(path, "w") as f:
        json.dump(to_jsonable(res), f, indent=1)
    return path
