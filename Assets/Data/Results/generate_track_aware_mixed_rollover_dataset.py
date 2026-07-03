import csv
import math
import os
import uuid
from collections import defaultdict


BASE_DIR = "/Users/kimgyuri/Desktop/coding/unity/roadTest/road_iso/Assets/Data/Results"
SOURCE_CSV = os.path.join(BASE_DIR, "combined_timeseries_20260703_223624.csv")
OUTPUT_CSV = os.path.join(BASE_DIR, "combined_timeseries_track_aware_mixed_rollover_20260704.csv")
SUMMARY_CSV = os.path.join(BASE_DIR, "track_aware_mixed_rollover_summary_20260704.csv")
EVENTS_CSV = os.path.join(BASE_DIR, "track_event_analysis_20260704.csv")


def num(row, key, default=0.0):
    try:
        return float(row[key])
    except Exception:
        return default


def clamp(value, low, high):
    return max(low, min(high, value))


def fmt(value):
    return f"{float(value):.6f}"


def percentile(values, pct):
    if not values:
        return 0.0
    ordered = sorted(values)
    index = int(round((len(ordered) - 1) * pct))
    return ordered[index]


def robust_scale(value, denominator):
    return abs(value) / max(abs(denominator), 1e-6)


def write_meta(path):
    meta_path = path + ".meta"
    if os.path.exists(meta_path):
        return
    with open(meta_path, "w", newline="") as meta:
        meta.write("fileFormatVersion: 2\n")
        meta.write(f"guid: {uuid.uuid4().hex}\n")
        meta.write("TextScriptImporter:\n")
        meta.write("  externalObjects: {}\n")
        meta.write("  userData: \n")
        meta.write("  assetBundleName: \n")
        meta.write("  assetBundleVariant: \n")


def sigmoid(value):
    if value >= 40.0:
        return 1.0
    if value <= -40.0:
        return 0.0
    return 1.0 / (1.0 + math.exp(-value))


def select_separated(candidates, min_gap, count):
    selected = []
    for score, index in sorted(candidates, reverse=True):
        if all(abs(index - other_index) >= min_gap for _, other_index in selected):
            selected.append((score, index))
            if len(selected) >= count:
                break
    return selected


def analyze_layout(rows):
    total_forces = [
        num(row, "FL_NormalForce")
        + num(row, "FR_NormalForce")
        + num(row, "RL_NormalForce")
        + num(row, "RR_NormalForce")
        for row in rows
    ]
    force_delta = [0.0]
    for index in range(1, len(total_forces)):
        force_delta.append(total_forces[index] - total_forces[index - 1])

    abs_steer = [abs(num(row, "SteerAngle")) for row in rows]
    abs_lat = [abs(num(row, "LatAcc")) for row in rows]
    abs_vert = [abs(num(row, "VertAcc")) for row in rows]
    abs_pitch_rate = [abs(num(row, "PitchRate")) for row in rows]
    abs_ltr = [abs(num(row, "LTR_Total")) for row in rows]

    steer_p90 = percentile(abs_steer, 0.90)
    lat_p90 = percentile(abs_lat, 0.90)
    vert_p90 = percentile(abs_vert, 0.90)
    pitch_rate_p90 = percentile(abs_pitch_rate, 0.90)
    force_delta_p90 = percentile([abs(value) for value in force_delta], 0.90)
    ltr_p90 = percentile(abs_ltr, 0.90)

    curve_candidates = []
    bump_candidates = []
    transition_candidates = []
    for index, row in enumerate(rows):
        steer = num(row, "SteerAngle")
        lat = num(row, "LatAcc")
        ltr = num(row, "LTR_Total")
        curve_score = (
            0.45 * robust_scale(steer, steer_p90)
            + 0.40 * robust_scale(lat, lat_p90)
            + 0.15 * robust_scale(ltr, ltr_p90)
        )
        bump_score = (
            0.45 * robust_scale(num(row, "VertAcc"), vert_p90)
            + 0.35 * robust_scale(num(row, "PitchRate"), pitch_rate_p90)
            + 0.20 * robust_scale(force_delta[index], force_delta_p90)
        )
        transition_score = 0.0
        if 4 <= index < len(rows) - 4:
            previous_steer = num(rows[index - 4], "SteerAngle")
            next_steer = num(rows[index + 4], "SteerAngle")
            if previous_steer * next_steer < 0:
                transition_score = (
                    robust_scale(previous_steer - next_steer, steer_p90)
                    + 0.5 * robust_scale(lat, lat_p90)
                )
        curve_candidates.append((curve_score, index))
        bump_candidates.append((bump_score, index))
        transition_candidates.append((transition_score, index))

    curve_events = select_separated(curve_candidates, min_gap=45, count=3)
    bump_events = select_separated(bump_candidates, min_gap=45, count=2)
    transition_events = select_separated(transition_candidates, min_gap=45, count=2)

    def event_dict(event_type, score, index):
        row = rows[index]
        return {
            "event_type": event_type,
            "score": score,
            "index": index,
            "run_time": num(row, "RunTime"),
            "pos_x": num(row, "PosX"),
            "pos_z": num(row, "PosZ"),
            "steer": num(row, "SteerAngle"),
            "lat_acc": num(row, "LatAcc"),
            "vert_acc": num(row, "VertAcc"),
            "ltr": num(row, "LTR_Total"),
        }

    events = []
    events.extend(event_dict("curve", score, index) for score, index in curve_events)
    events.extend(event_dict("bump_or_load_unsettle", score, index) for score, index in bump_events)
    events.extend(event_dict("steer_transition", score, index) for score, index in transition_events if score > 0.0)
    events.sort(key=lambda event: (event["run_time"], event["event_type"]))
    return events


def gaussian_by_distance(row, event, width_m, width_s):
    dx = num(row, "PosX") - event["pos_x"]
    dz = num(row, "PosZ") - event["pos_z"]
    distance = math.sqrt(dx * dx + dz * dz)
    time_delta = num(row, "RunTime") - event["run_time"]
    distance_term = math.exp(-((distance / max(width_m, 1e-6)) ** 2))
    time_term = math.exp(-((time_delta / max(width_s, 1e-6)) ** 2))
    return max(distance_term, time_term * 0.75)


with open(SOURCE_CSV, newline="") as source_file:
    reader = csv.DictReader(source_file)
    fieldnames = reader.fieldnames
    all_rows = list(reader)

rows_by_layout = defaultdict(list)
for source_row in all_rows:
    rows_by_layout[source_row["LayoutID"]].append(source_row)

layout_events = {}
event_rows = []
for layout_id, rows in sorted(rows_by_layout.items()):
    events = analyze_layout(rows)
    layout_events[layout_id] = events
    for rank, event in enumerate(events, start=1):
        event_rows.append(
            {
                "layout_id": layout_id,
                "rank": rank,
                "event_type": event["event_type"],
                "score": f"{event['score']:.4f}",
                "sample_index": event["index"],
                "run_time": f"{event['run_time']:.3f}",
                "pos_x": f"{event['pos_x']:.3f}",
                "pos_z": f"{event['pos_z']:.3f}",
                "steer_angle": f"{event['steer']:.3f}",
                "lat_acc": f"{event['lat_acc']:.3f}",
                "vert_acc": f"{event['vert_acc']:.3f}",
                "ltr_total": f"{event['ltr']:.3f}",
            }
        )

families = {
    "HAZMAT_UNSECURED": {
        "cargo_count": 8,
        "mass": 420.0,
        "secured": 0.20,
        "friction": "WET",
        "road": "TRACK_EVENTS",
        "bank": 0.0,
        "slope": 0.0,
        "cog_x": 1.20,
        "cog_z": -0.65,
        "cog_h": 1.55,
        "description": "위험물/액체성 적재물 비고정",
        "preferred_event": "steer_transition",
        "secondary_event": "curve",
        "sign_policy": "event",
        "mass_scale": 1.00,
        "slip_bias": 0.35,
        "roll_factor": 0.85,
        "lat_factor": 0.95,
    },
    "OVERWEIGHT_LOAD": {
        "cargo_count": 12,
        "mass": 1450.0,
        "secured": 0.85,
        "friction": "DRY",
        "road": "TRACK_EVENTS",
        "bank": 0.0,
        "slope": 1.5,
        "cog_x": 0.25,
        "cog_z": -0.20,
        "cog_h": 1.20,
        "description": "과적/고중량 적재",
        "preferred_event": "bump_or_load_unsettle",
        "secondary_event": "curve",
        "sign_policy": "event",
        "mass_scale": 1.18,
        "slip_bias": -0.10,
        "roll_factor": 0.70,
        "lat_factor": 0.86,
    },
    "WRONG_COG_LEFT_HIGH": {
        "cargo_count": 7,
        "mass": 720.0,
        "secured": 0.55,
        "friction": "DRY",
        "road": "TRACK_EVENTS",
        "bank": 3.5,
        "slope": 0.0,
        "cog_x": -1.85,
        "cog_z": 0.45,
        "cog_h": 2.35,
        "description": "좌측 편중/높은 무게중심",
        "preferred_event": "curve",
        "secondary_event": "steer_transition",
        "sign_policy": "left",
        "mass_scale": 1.00,
        "slip_bias": 0.05,
        "roll_factor": 1.15,
        "lat_factor": 1.00,
    },
}

levels = [
    {
        "name": "mild",
        "ltr_amp": 0.24,
        "lat_amp": 1.15,
        "roll_amp": 5.0,
        "slip_mult": 1.15,
        "risk_gain": 0.50,
        "rollover": False,
        "partial": False,
        "distance_width_m": 18.0,
        "time_width_s": 3.5,
    },
    {
        "name": "near_miss",
        "ltr_amp": 0.58,
        "lat_amp": 1.65,
        "roll_amp": 20.0,
        "slip_mult": 1.95,
        "risk_gain": 0.90,
        "rollover": False,
        "partial": False,
        "distance_width_m": 20.0,
        "time_width_s": 2.7,
    },
    {
        "name": "partial_rollover",
        "ltr_amp": 0.78,
        "lat_amp": 1.95,
        "roll_amp": 50.0,
        "slip_mult": 2.35,
        "risk_gain": 1.00,
        "rollover": True,
        "partial": True,
        "distance_width_m": 16.0,
        "time_width_s": 2.2,
    },
    {
        "name": "rollover",
        "ltr_amp": 0.92,
        "lat_amp": 2.25,
        "roll_amp": 96.0,
        "slip_mult": 2.85,
        "risk_gain": 1.00,
        "rollover": True,
        "partial": False,
        "distance_width_m": 15.0,
        "time_width_s": 2.0,
    },
]


def choose_event(events, family, level_index):
    preferred = [event for event in events if event["event_type"] == family["preferred_event"]]
    secondary = [event for event in events if event["event_type"] == family["secondary_event"]]
    candidates = preferred or secondary or events
    if not candidates:
        raise RuntimeError("No events available")
    return candidates[level_index % len(candidates)]


def event_sign(event, family):
    if family["sign_policy"] == "left":
        return -1.0
    if abs(event["lat_acc"]) > 1e-6:
        return 1.0 if event["lat_acc"] > 0.0 else -1.0
    if abs(event["steer"]) > 1e-6:
        return 1.0 if event["steer"] > 0.0 else -1.0
    return 1.0


output_rows = []
summary_rows = []
BASE_MASS_KG = 3500.0

for layout_index, (source_layout_id, source_rows) in enumerate(sorted(rows_by_layout.items()), start=1):
    events = layout_events[source_layout_id]
    if not events:
        continue
    for scenario_id, family in families.items():
        for level_index, level in enumerate(levels):
            event = choose_event(events, family, level_index)
            sign = event_sign(event, family)
            run_id = f"20260704_ta_{layout_index:02d}_{list(families).index(scenario_id) + 1:02d}_{level_index + 1:02d}"
            metrics = defaultdict(list)
            rollover_active = False
            rollover_start = event["run_time"] + (0.25 if level["partial"] else -0.10)
            rollover_end = event["run_time"] + (1.80 if level["partial"] else 999.0)

            for sample_index, source_row in enumerate(source_rows):
                row = dict(source_row)
                event_peak = gaussian_by_distance(
                    source_row,
                    event,
                    level["distance_width_m"],
                    level["time_width_s"],
                )
                # Sloshing and suspension settling lag the detected track event.
                lagged_time = num(source_row, "RunTime") - event["run_time"] - 0.9
                late_peak = math.exp(-((lagged_time / max(level["time_width_s"] * 1.15, 1e-6)) ** 2))
                local_phase = math.sin((num(source_row, "RunTime") - event["run_time"]) * 2.0)
                peak = clamp(event_peak + 0.30 * late_peak, 0.0, 1.0)

                base_ltr = num(source_row, "LTR_Total") * 0.35
                if scenario_id == "WRONG_COG_LEFT_HIGH":
                    base_ltr = num(source_row, "LTR_Total") * 0.15
                ltr_target = clamp(
                    base_ltr
                    + sign * level["ltr_amp"] * peak
                    + sign * 0.08 * local_phase * late_peak,
                    -1.0,
                    1.0,
                )
                ltr_front = clamp(ltr_target * 1.10 + sign * 0.035 * local_phase, -1.0, 1.0)
                ltr_rear = clamp(ltr_target * 0.92 + sign * 0.025 * math.cos(num(source_row, "RunTime")), -1.0, 1.0)

                original_cargo_mass = num(source_row, "TotalMassKg", 213.5)
                mass_scale = (BASE_MASS_KG + family["mass"] * family["mass_scale"]) / (BASE_MASS_KG + original_cargo_mass)
                total_normal_force = (
                    num(source_row, "FL_NormalForce")
                    + num(source_row, "FR_NormalForce")
                    + num(source_row, "RL_NormalForce")
                    + num(source_row, "RR_NormalForce")
                ) * mass_scale * (1.0 + 0.06 * peak)
                source_front = num(source_row, "FrontNormalForce")
                source_rear = num(source_row, "RearNormalForce")
                front_ratio = clamp(source_front / (source_front + source_rear + 1e-9), 0.30, 0.70)
                front_force = total_normal_force * front_ratio
                rear_force = total_normal_force - front_force
                fl = max(0.0, front_force * (1.0 - ltr_front) / 2.0)
                fr = max(0.0, front_force * (1.0 + ltr_front) / 2.0)
                rl = max(0.0, rear_force * (1.0 - ltr_rear) / 2.0)
                rr = max(0.0, rear_force * (1.0 + ltr_rear) / 2.0)

                if abs(ltr_target) > (0.88 if level["rollover"] else 0.94):
                    if ltr_target > 0.0:
                        fl *= 0.035
                        rl *= 0.045
                    else:
                        fr *= 0.035
                        rr *= 0.045

                left_force = fl + rl
                right_force = fr + rr
                front_force = fl + fr
                rear_force = rl + rr
                ltr_total = clamp((right_force - left_force) / (left_force + right_force + 1e-9), -1.0, 1.0)
                ltr_front_final = clamp((fr - fl) / (front_force + 1e-9), -1.0, 1.0)
                ltr_rear_final = clamp((rr - rl) / (rear_force + 1e-9), -1.0, 1.0)

                lat_acc = (
                    num(source_row, "LatAcc") * level["lat_amp"] * family["lat_factor"]
                    + sign * (1.20 + 1.70 * level["ltr_amp"]) * peak
                )
                vert_event_gain = 0.20 if event["event_type"] == "curve" else 0.65
                vert_acc = num(source_row, "VertAcc") + vert_event_gain * peak
                roll = (
                    num(source_row, "Roll")
                    + sign * level["roll_amp"] * family["roll_factor"] * peak
                    + sign * 0.55 * local_phase * late_peak
                )
                if level["rollover"]:
                    transition = 86.0 * sigmoid((num(source_row, "RunTime") - event["run_time"]) * 1.45)
                    if level["partial"]:
                        recovery = sigmoid((num(source_row, "RunTime") - rollover_end) * 1.9)
                        roll += sign * transition * (1.0 - 0.70 * recovery)
                    else:
                        roll += sign * transition
                pitch = num(source_row, "Pitch") + (0.9 if event["event_type"] == "curve" else 2.0) * peak
                roll_rate = num(source_row, "RollRate") + sign * level["roll_amp"] * (peak - late_peak) / max(level["time_width_s"], 1.0)
                pitch_rate = num(source_row, "PitchRate") + (0.60 if event["event_type"] == "curve" else 1.20) * (peak - late_peak)
                yaw_rate = num(source_row, "YawRate") * (1.0 + 0.18 * peak)
                max_side_slip = (
                    abs(num(source_row, "MaxSideSlip")) * (level["slip_mult"] + family["slip_bias"])
                    + 0.030 * peak
                    + (0.050 if family["friction"] == "WET" else 0.0) * late_peak
                )
                max_forward_slip = abs(num(source_row, "MaxForwardSlip")) * (1.0 + 0.32 * level["slip_mult"] * peak) + 0.055 * peak

                in_rollover_window = level["rollover"] and rollover_start <= num(source_row, "RunTime") <= rollover_end
                if in_rollover_window and (abs(roll) > 48.0 or abs(ltr_total) > 0.94):
                    rollover_active = True
                if level["partial"] and num(source_row, "RunTime") > rollover_end:
                    rollover_active = False
                is_rollover = 1 if rollover_active else 0

                left_lift = 1 if fl < 250.0 or rl < 250.0 else 0
                right_lift = 1 if fr < 250.0 or rr < 250.0 else 0
                any_lift = 1 if left_lift or right_lift else 0
                risk = clamp((abs(ltr_total) - 0.55) / 0.45 * level["risk_gain"] + max_side_slip * 0.12, 0.0, 1.0)

                row.update(
                    {
                        "DatasetVersion": "V1.3_SYNTHETIC_TRACK_AWARE",
                        "LayoutID": f"{source_layout_id}_{scenario_id.lower()}",
                        "RunID": run_id,
                        "ScenarioID": scenario_id,
                        "SampleIndex": str(sample_index),
                        "FrictionCondition": family["friction"],
                        "RoadCondition": family["road"],
                        "RoadBankAngleDeg": fmt(family["bank"]),
                        "RoadSlopeDeg": fmt(family["slope"]),
                        "VehicleBaseMassKg": fmt(BASE_MASS_KG),
                        "CargoCount": str(family["cargo_count"]),
                        "TotalMassKg": fmt(family["mass"]),
                        "SecuredFrac": fmt(family["secured"]),
                        "SpeedKmh": fmt(max(0.0, num(source_row, "SpeedKmh") * (0.97 if family["mass"] > 1000 else 1.0) - (1.6 * peak if level["rollover"] else 0.0))),
                        "AccX": fmt(num(source_row, "AccX") * (1.0 + 0.08 * peak)),
                        "AccY": fmt(lat_acc),
                        "AccZ": fmt(vert_acc),
                        "LongAcc": fmt(num(source_row, "LongAcc") * (1.0 + 0.06 * peak)),
                        "LatAcc": fmt(lat_acc),
                        "VertAcc": fmt(vert_acc),
                        "Roll": fmt(roll),
                        "Pitch": fmt(pitch),
                        "RollRate": fmt(roll_rate),
                        "PitchRate": fmt(pitch_rate),
                        "YawRate": fmt(yaw_rate),
                        "AngVelX": fmt(num(source_row, "AngVelX") + roll_rate * 0.018),
                        "AngVelY": fmt(num(source_row, "AngVelY") + pitch_rate * 0.018),
                        "AngVelZ": fmt(num(source_row, "AngVelZ") + yaw_rate * 0.018),
                        "AngAccX": fmt(num(source_row, "AngAccX") * (1.0 + 0.22 * peak)),
                        "AngAccY": fmt(num(source_row, "AngAccY") * (1.0 + 0.18 * peak)),
                        "AngAccZ": fmt(num(source_row, "AngAccZ") * (1.0 + 0.18 * peak)),
                        "Throttle": fmt(clamp(num(source_row, "Throttle") - (0.10 * peak if level["rollover"] else 0.01 * peak), 0.0, 1.0)),
                        "Brake": fmt(clamp(num(source_row, "Brake") + (0.24 * peak if level["rollover"] else 0.035 * peak), 0.0, 1.0)),
                        "FL_Grounded": "0" if fl < 250.0 else "1",
                        "FR_Grounded": "0" if fr < 250.0 else "1",
                        "RL_Grounded": "0" if rl < 250.0 else "1",
                        "RR_Grounded": "0" if rr < 250.0 else "1",
                        "FL_NormalForce": fmt(fl),
                        "FR_NormalForce": fmt(fr),
                        "RL_NormalForce": fmt(rl),
                        "RR_NormalForce": fmt(rr),
                        "LeftNormalForce": fmt(left_force),
                        "RightNormalForce": fmt(right_force),
                        "FrontNormalForce": fmt(front_force),
                        "RearNormalForce": fmt(rear_force),
                        "LTR_Total": fmt(ltr_total),
                        "LTR_Front": fmt(ltr_front_final),
                        "LTR_Rear": fmt(ltr_rear_final),
                        "LeftWheelLift": str(left_lift),
                        "RightWheelLift": str(right_lift),
                        "AnyWheelLift": str(any_lift),
                        "FL_ForwardSlip": fmt(num(source_row, "FL_ForwardSlip") * (1.0 + 0.26 * level["slip_mult"] * peak)),
                        "FR_ForwardSlip": fmt(num(source_row, "FR_ForwardSlip") * (1.0 + 0.26 * level["slip_mult"] * peak)),
                        "RL_ForwardSlip": fmt(num(source_row, "RL_ForwardSlip") * (1.0 + 0.34 * level["slip_mult"] * peak)),
                        "RR_ForwardSlip": fmt(num(source_row, "RR_ForwardSlip") * (1.0 + 0.34 * level["slip_mult"] * peak)),
                        "FL_SideSlip": fmt(num(source_row, "FL_SideSlip") * (1.0 + level["slip_mult"] * peak)),
                        "FR_SideSlip": fmt(num(source_row, "FR_SideSlip") * (1.0 + level["slip_mult"] * peak)),
                        "RL_SideSlip": fmt(num(source_row, "RL_SideSlip") * (1.0 + level["slip_mult"] * peak)),
                        "RR_SideSlip": fmt(num(source_row, "RR_SideSlip") * (1.0 + level["slip_mult"] * peak)),
                        "MaxForwardSlip": fmt(max_forward_slip),
                        "MaxSideSlip": fmt(max_side_slip),
                        "LegacyRolloverRisk": fmt(risk),
                        "IsRollover": str(is_rollover),
                    }
                )
                output_rows.append(row)

                metrics["roll"].append(abs(roll))
                metrics["pitch"].append(abs(pitch))
                metrics["lat_g"].append(abs(lat_acc) / 9.80665)
                metrics["ltr"].append(abs(ltr_total))
                metrics["shift"].append((1.0 - family["secured"]) * max_side_slip * 18.0 + (2.5 * peak if level["rollover"] else 1.0 * peak))
                metrics["wheel_lift_rows"].append(any_lift)
                metrics["rollover_rows"].append(is_rollover)

            max_ltr = max(metrics["ltr"])
            rollover_rows = sum(metrics["rollover_rows"])
            risk_grade = 3 if rollover_rows > 0 or max_ltr >= 0.92 else 2 if max_ltr >= 0.72 else 1
            summary_rows.append(
                {
                    "run_id": run_id,
                    "source_layout_id": source_layout_id,
                    "layout_id": f"{source_layout_id}_{scenario_id.lower()}",
                    "scenario_id": scenario_id,
                    "severity": level["name"],
                    "track_event_type": event["event_type"],
                    "event_time_s": f"{event['run_time']:.3f}",
                    "event_pos_x": f"{event['pos_x']:.3f}",
                    "event_pos_z": f"{event['pos_z']:.3f}",
                    "event_steer_angle": f"{event['steer']:.3f}",
                    "event_lat_acc": f"{event['lat_acc']:.3f}",
                    "event_vert_acc": f"{event['vert_acc']:.3f}",
                    "cargo_count": family["cargo_count"],
                    "total_mass_kg": f"{family['mass']:.1f}",
                    "secured_frac": f"{family['secured']:.2f}",
                    "init_cog_x": f"{family['cog_x']:.3f}",
                    "init_cog_z": f"{family['cog_z']:.3f}",
                    "init_cog_height": f"{family['cog_h']:.3f}",
                    "duration_s": f"{num(source_rows[-1], 'RunTime'):.1f}",
                    "max_roll_deg": f"{max(metrics['roll']):.2f}",
                    "max_pitch_deg": f"{max(metrics['pitch']):.2f}",
                    "max_lat_accel_g": f"{max(metrics['lat_g']):.3f}",
                    "max_abs_ltr": f"{max_ltr:.3f}",
                    "max_cargo_shift_m": f"{max(metrics['shift']):.3f}",
                    "wheel_lift_rows": str(sum(metrics["wheel_lift_rows"])),
                    "rollover_rows": str(rollover_rows),
                    "rollover": str(1 if rollover_rows > 0 else 0),
                    "risk_grade": str(risk_grade),
                    "risk_cause": f"{family['description']} - {event['event_type']} 기반 {level['name']} 강도",
                }
            )

with open(OUTPUT_CSV, "w", newline="") as output_file:
    writer = csv.DictWriter(output_file, fieldnames=fieldnames)
    writer.writeheader()
    writer.writerows(output_rows)

summary_fields = [
    "run_id",
    "source_layout_id",
    "layout_id",
    "scenario_id",
    "severity",
    "track_event_type",
    "event_time_s",
    "event_pos_x",
    "event_pos_z",
    "event_steer_angle",
    "event_lat_acc",
    "event_vert_acc",
    "cargo_count",
    "total_mass_kg",
    "secured_frac",
    "init_cog_x",
    "init_cog_z",
    "init_cog_height",
    "duration_s",
    "max_roll_deg",
    "max_pitch_deg",
    "max_lat_accel_g",
    "max_abs_ltr",
    "max_cargo_shift_m",
    "wheel_lift_rows",
    "rollover_rows",
    "rollover",
    "risk_grade",
    "risk_cause",
]

with open(SUMMARY_CSV, "w", newline="") as summary_file:
    writer = csv.DictWriter(summary_file, fieldnames=summary_fields)
    writer.writeheader()
    writer.writerows(summary_rows)

event_fields = [
    "layout_id",
    "rank",
    "event_type",
    "score",
    "sample_index",
    "run_time",
    "pos_x",
    "pos_z",
    "steer_angle",
    "lat_acc",
    "vert_acc",
    "ltr_total",
]
with open(EVENTS_CSV, "w", newline="") as events_file:
    writer = csv.DictWriter(events_file, fieldnames=event_fields)
    writer.writeheader()
    writer.writerows(event_rows)

for path in (OUTPUT_CSV, SUMMARY_CSV, EVENTS_CSV):
    write_meta(path)

print(OUTPUT_CSV)
print(SUMMARY_CSV)
print(EVENTS_CSV)
print(f"rows={len(output_rows)} cols={len(fieldnames)} runs={len(summary_rows)} events={len(event_rows)}")
