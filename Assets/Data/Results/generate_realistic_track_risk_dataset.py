import csv
import math
import os
import uuid
from collections import defaultdict


BASE_DIR = "/Users/kimgyuri/Desktop/coding/unity/roadTest/road_iso/Assets/Data/Results"
SOURCE_CSV = os.path.join(BASE_DIR, "combined_timeseries_20260703_223624.csv")
OUTPUT_CSV = os.path.join(BASE_DIR, "combined_timeseries_realistic_track_risk_20260704.csv")
SUMMARY_CSV = os.path.join(BASE_DIR, "realistic_track_risk_summary_20260704.csv")
EVENTS_CSV = os.path.join(BASE_DIR, "realistic_track_event_analysis_20260704.csv")


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
    values = sorted(values)
    return values[int((len(values) - 1) * pct)]


def write_meta(path):
    meta_path = path + ".meta"
    if os.path.exists(meta_path):
        return
    with open(meta_path, "w") as meta:
        meta.write("fileFormatVersion: 2\n")
        meta.write(f"guid: {uuid.uuid4().hex}\n")
        meta.write("TextScriptImporter:\n")
        meta.write("  externalObjects: {}\n")
        meta.write("  userData: \n")
        meta.write("  assetBundleName: \n")
        meta.write("  assetBundleVariant: \n")


def separated_peaks(candidates, min_gap, count):
    selected = []
    for score, index in sorted(candidates, reverse=True):
        if all(abs(index - selected_index) >= min_gap for _, selected_index in selected):
            selected.append((score, index))
            if len(selected) >= count:
                break
    return selected


def analyze_events(layout_id, rows):
    abs_steer = [abs(num(row, "SteerAngle")) for row in rows]
    abs_lat = [abs(num(row, "LatAcc")) for row in rows]
    abs_ltr = [abs(num(row, "LTR_Total")) for row in rows]
    abs_vert = [abs(num(row, "VertAcc")) for row in rows]
    abs_pitch_rate = [abs(num(row, "PitchRate")) for row in rows]

    steer_p90 = max(percentile(abs_steer, 0.90), 1e-6)
    lat_p90 = max(percentile(abs_lat, 0.90), 1e-6)
    ltr_p90 = max(percentile(abs_ltr, 0.90), 1e-6)
    vert_p95 = max(percentile(abs_vert, 0.95), 1e-6)
    pitch_rate_p95 = max(percentile(abs_pitch_rate, 0.95), 1e-6)

    curve_candidates = []
    bump_candidates = []
    for index, row in enumerate(rows):
        if num(row, "RunTime") < 10.0 or num(row, "SpeedKmh") < 10.0:
            continue
        curve_score = (
            0.45 * abs(num(row, "SteerAngle")) / steer_p90
            + 0.35 * abs(num(row, "LatAcc")) / lat_p90
            + 0.20 * abs(num(row, "LTR_Total")) / ltr_p90
        )
        bump_score = (
            0.60 * abs(num(row, "VertAcc")) / vert_p95
            + 0.40 * abs(num(row, "PitchRate")) / pitch_rate_p95
        )
        curve_candidates.append((curve_score, index))
        bump_candidates.append((bump_score, index))

    selected = []
    for event_type, candidates, limit in (
        ("curve", curve_candidates, 4),
        ("bump_or_vertical_load", bump_candidates, 2),
    ):
        for score, index in separated_peaks(candidates, min_gap=50, count=limit):
            row = rows[index]
            selected.append(
                {
                    "layout_id": layout_id,
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
            )
    selected.sort(key=lambda event: event["run_time"])
    return selected


def event_peak(row, event, width_s):
    dt = num(row, "RunTime") - event["run_time"]
    return math.exp(-((dt / max(width_s, 1e-6)) ** 2))


def signed_direction(row, event, scenario_id):
    if scenario_id == "WRONG_COG_LEFT_HIGH":
        return -1.0
    lat = event["lat_acc"] if abs(event["lat_acc"]) > 0.05 else num(row, "LatAcc")
    if abs(lat) > 0.05:
        return 1.0 if lat > 0.0 else -1.0
    steer = event["steer"] if abs(event["steer"]) > 0.05 else num(row, "SteerAngle")
    return 1.0 if steer >= 0.0 else -1.0


def risk_grade_from_ltr(abs_ltr):
    if abs_ltr >= 0.95:
        return 4
    if abs_ltr >= 0.90:
        return 3
    if abs_ltr >= 0.75:
        return 2
    if abs_ltr >= 0.60:
        return 1
    return 0


with open(SOURCE_CSV, newline="") as source_file:
    reader = csv.DictReader(source_file)
    fieldnames = reader.fieldnames
    all_rows = list(reader)

rows_by_layout = defaultdict(list)
for row in all_rows:
    rows_by_layout[row["LayoutID"]].append(row)

events_by_layout = {}
event_output_rows = []
for layout_id, rows in sorted(rows_by_layout.items()):
    events = analyze_events(layout_id, rows)
    events_by_layout[layout_id] = events
    for rank, event in enumerate(events, start=1):
        event_output_rows.append(
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

scenarios = {
    "HAZMAT_UNSECURED": {
        "cargo_count": 8,
        "mass": 420.0,
        "secured": 0.20,
        "friction": "WET",
        "road": "TRACK_REALISTIC",
        "preferred_event": "curve",
        "mass_gain": 1.03,
        "lat_gain": 1.05,
        "roll_add_max": 1.0,
        "ltr_add": 0.08,
        "slip_gain": 1.25,
        "description": "위험물/비고정 적재",
    },
    "OVERWEIGHT_LOAD": {
        "cargo_count": 12,
        "mass": 1450.0,
        "secured": 0.85,
        "friction": "DRY",
        "road": "TRACK_REALISTIC",
        "preferred_event": "bump_or_vertical_load",
        "mass_gain": 1.12,
        "lat_gain": 1.02,
        "roll_add_max": 0.6,
        "ltr_add": 0.06,
        "slip_gain": 1.08,
        "description": "과적/고중량 적재",
    },
    "WRONG_COG_LEFT_HIGH": {
        "cargo_count": 7,
        "mass": 720.0,
        "secured": 0.55,
        "friction": "DRY",
        "road": "TRACK_REALISTIC",
        "preferred_event": "curve",
        "mass_gain": 1.05,
        "lat_gain": 1.07,
        "roll_add_max": 1.3,
        "ltr_add": 0.12,
        "slip_gain": 1.15,
        "description": "좌측 편중/높은 무게중심",
    },
}

levels = [
    {
        "name": "baseline_like",
        "target_ltr_floor": 0.62,
        "ltr_scale": 0.45,
        "lat_extra": 0.04,
        "roll_scale": 0.45,
        "width_s": 2.8,
    },
    {
        "name": "caution",
        "target_ltr_floor": 0.76,
        "ltr_scale": 0.70,
        "lat_extra": 0.08,
        "roll_scale": 0.70,
        "width_s": 2.5,
    },
    {
        "name": "high_risk",
        "target_ltr_floor": 0.90,
        "ltr_scale": 1.00,
        "lat_extra": 0.13,
        "roll_scale": 1.00,
        "width_s": 2.2,
    },
]

output_rows = []
summary_rows = []
BASE_MASS_KG = 3500.0

for layout_index, (layout_id, source_rows) in enumerate(sorted(rows_by_layout.items()), start=1):
    events = events_by_layout[layout_id]
    if not events:
        continue
    for scenario_index, (scenario_id, scenario) in enumerate(scenarios.items(), start=1):
        preferred_events = [event for event in events if event["event_type"] == scenario["preferred_event"]]
        fallback_events = [event for event in events if event["event_type"] != scenario["preferred_event"]]
        event_pool = preferred_events or fallback_events or events

        for level_index, level in enumerate(levels, start=1):
            event = event_pool[(level_index - 1) % len(event_pool)]
            run_id = f"20260704_rt_{layout_index:02d}_{scenario_index:02d}_{level_index:02d}"
            metrics = defaultdict(list)
            original_cargo_mass = num(source_rows[0], "TotalMassKg", 213.5)
            mass_scale = (BASE_MASS_KG + scenario["mass"] * scenario["mass_gain"]) / (BASE_MASS_KG + original_cargo_mass)

            for sample_index, source_row in enumerate(source_rows):
                row = dict(source_row)
                peak = event_peak(source_row, event, level["width_s"])
                sign = signed_direction(source_row, event, scenario_id)
                base_ltr = num(source_row, "LTR_Total")
                abs_base_ltr = abs(base_ltr)
                target_abs_ltr = max(
                    abs_base_ltr,
                    min(0.96, level["target_ltr_floor"] + scenario["ltr_add"] * level["ltr_scale"]),
                )
                desired_ltr = sign * target_abs_ltr
                ltr_total_target = base_ltr + (desired_ltr - base_ltr) * peak * level["ltr_scale"]
                ltr_total_target = clamp(ltr_total_target, -0.98, 0.98)

                source_front_force = num(source_row, "FrontNormalForce")
                source_rear_force = num(source_row, "RearNormalForce")
                source_total_force = (
                    num(source_row, "FL_NormalForce")
                    + num(source_row, "FR_NormalForce")
                    + num(source_row, "RL_NormalForce")
                    + num(source_row, "RR_NormalForce")
                )
                total_force = source_total_force * mass_scale * (1.0 + 0.025 * peak)
                front_ratio = clamp(source_front_force / (source_front_force + source_rear_force + 1e-9), 0.30, 0.70)
                front_force = total_force * front_ratio
                rear_force = total_force - front_force
                ltr_front_target = clamp(ltr_total_target * 1.05, -0.98, 0.98)
                ltr_rear_target = clamp(ltr_total_target * 0.92, -0.98, 0.98)
                fl = max(0.0, front_force * (1.0 - ltr_front_target) / 2.0)
                fr = max(0.0, front_force * (1.0 + ltr_front_target) / 2.0)
                rl = max(0.0, rear_force * (1.0 - ltr_rear_target) / 2.0)
                rr = max(0.0, rear_force * (1.0 + ltr_rear_target) / 2.0)
                left_force = fl + rl
                right_force = fr + rr
                front_force = fl + fr
                rear_force = rl + rr
                ltr_total = clamp((right_force - left_force) / (left_force + right_force + 1e-9), -1.0, 1.0)
                ltr_front = clamp((fr - fl) / (front_force + 1e-9), -1.0, 1.0)
                ltr_rear = clamp((rr - rl) / (rear_force + 1e-9), -1.0, 1.0)

                lat_acc = num(source_row, "LatAcc") * (1.0 + (scenario["lat_gain"] - 1.0) * peak)
                lat_acc += sign * level["lat_extra"] * 9.80665 * peak
                roll_add = sign * scenario["roll_add_max"] * level["roll_scale"] * peak
                roll = clamp(num(source_row, "Roll") + roll_add, -7.0, 7.0)
                roll_rate = clamp(num(source_row, "RollRate") + sign * 0.35 * level["roll_scale"] * peak, -6.0, 6.0)
                vert_acc = num(source_row, "VertAcc")
                if event["event_type"] == "bump_or_vertical_load":
                    vert_acc += 0.35 * peak
                pitch = num(source_row, "Pitch") + (0.25 if event["event_type"] == "bump_or_vertical_load" else 0.10) * peak
                pitch_rate = num(source_row, "PitchRate") + (0.20 if event["event_type"] == "bump_or_vertical_load" else 0.08) * peak
                max_side_slip = abs(num(source_row, "MaxSideSlip")) * (1.0 + (scenario["slip_gain"] - 1.0) * peak)
                max_forward_slip = abs(num(source_row, "MaxForwardSlip")) * (1.0 + 0.08 * peak)

                left_lift = 1 if (fl < 350.0 or rl < 350.0) and abs(ltr_total) >= 0.95 else 0
                right_lift = 1 if (fr < 350.0 or rr < 350.0) and abs(ltr_total) >= 0.95 else 0
                any_lift = 1 if left_lift or right_lift else 0
                risk_grade = risk_grade_from_ltr(abs(ltr_total))
                rollover_risk = clamp((abs(ltr_total) - 0.60) / 0.38, 0.0, 1.0)

                row.update(
                    {
                        "DatasetVersion": "V1.4_SYNTHETIC_REALISTIC_TRACK_RISK",
                        "LayoutID": f"{layout_id}_{scenario_id.lower()}",
                        "RunID": run_id,
                        "ScenarioID": scenario_id,
                        "SampleIndex": str(sample_index),
                        "FrictionCondition": scenario["friction"],
                        "RoadCondition": scenario["road"],
                        "VehicleBaseMassKg": fmt(BASE_MASS_KG),
                        "CargoCount": str(scenario["cargo_count"]),
                        "TotalMassKg": fmt(scenario["mass"]),
                        "SecuredFrac": fmt(scenario["secured"]),
                        "SpeedKmh": fmt(num(source_row, "SpeedKmh") * (0.99 if scenario_id == "OVERWEIGHT_LOAD" else 1.0)),
                        "AccX": fmt(num(source_row, "AccX") * (1.0 + 0.02 * peak)),
                        "AccY": fmt(lat_acc),
                        "AccZ": fmt(vert_acc),
                        "LongAcc": fmt(num(source_row, "LongAcc") * (1.0 + 0.02 * peak)),
                        "LatAcc": fmt(lat_acc),
                        "VertAcc": fmt(vert_acc),
                        "Roll": fmt(roll),
                        "Pitch": fmt(pitch),
                        "RollRate": fmt(roll_rate),
                        "PitchRate": fmt(pitch_rate),
                        "YawRate": fmt(num(source_row, "YawRate") * (1.0 + 0.03 * peak)),
                        "AngVelX": fmt(num(source_row, "AngVelX") + roll_rate * 0.006),
                        "AngVelY": fmt(num(source_row, "AngVelY") + pitch_rate * 0.006),
                        "AngVelZ": fmt(num(source_row, "AngVelZ")),
                        "AngAccX": fmt(num(source_row, "AngAccX") * (1.0 + 0.04 * peak)),
                        "AngAccY": fmt(num(source_row, "AngAccY") * (1.0 + 0.03 * peak)),
                        "AngAccZ": fmt(num(source_row, "AngAccZ") * (1.0 + 0.03 * peak)),
                        "FL_Grounded": "0" if fl < 350.0 and abs(ltr_total) >= 0.95 else "1",
                        "FR_Grounded": "0" if fr < 350.0 and abs(ltr_total) >= 0.95 else "1",
                        "RL_Grounded": "0" if rl < 350.0 and abs(ltr_total) >= 0.95 else "1",
                        "RR_Grounded": "0" if rr < 350.0 and abs(ltr_total) >= 0.95 else "1",
                        "FL_NormalForce": fmt(fl),
                        "FR_NormalForce": fmt(fr),
                        "RL_NormalForce": fmt(rl),
                        "RR_NormalForce": fmt(rr),
                        "LeftNormalForce": fmt(left_force),
                        "RightNormalForce": fmt(right_force),
                        "FrontNormalForce": fmt(front_force),
                        "RearNormalForce": fmt(rear_force),
                        "LTR_Total": fmt(ltr_total),
                        "LTR_Front": fmt(ltr_front),
                        "LTR_Rear": fmt(ltr_rear),
                        "LeftWheelLift": str(left_lift),
                        "RightWheelLift": str(right_lift),
                        "AnyWheelLift": str(any_lift),
                        "FL_ForwardSlip": fmt(num(source_row, "FL_ForwardSlip") * (1.0 + 0.04 * peak)),
                        "FR_ForwardSlip": fmt(num(source_row, "FR_ForwardSlip") * (1.0 + 0.04 * peak)),
                        "RL_ForwardSlip": fmt(num(source_row, "RL_ForwardSlip") * (1.0 + 0.05 * peak)),
                        "RR_ForwardSlip": fmt(num(source_row, "RR_ForwardSlip") * (1.0 + 0.05 * peak)),
                        "FL_SideSlip": fmt(num(source_row, "FL_SideSlip") * (1.0 + 0.08 * peak)),
                        "FR_SideSlip": fmt(num(source_row, "FR_SideSlip") * (1.0 + 0.08 * peak)),
                        "RL_SideSlip": fmt(num(source_row, "RL_SideSlip") * (1.0 + 0.08 * peak)),
                        "RR_SideSlip": fmt(num(source_row, "RR_SideSlip") * (1.0 + 0.08 * peak)),
                        "MaxForwardSlip": fmt(max_forward_slip),
                        "MaxSideSlip": fmt(max_side_slip),
                        "LegacyRolloverRisk": fmt(rollover_risk),
                        "IsRollover": "0",
                    }
                )
                output_rows.append(row)

                metrics["roll"].append(abs(roll))
                metrics["lat_g"].append(abs(lat_acc) / 9.80665)
                metrics["ltr"].append(abs(ltr_total))
                metrics["risk"].append(rollover_risk)
                metrics["wheel_lift"].append(any_lift)
                metrics["risk_grade"].append(risk_grade)

            max_ltr = max(metrics["ltr"])
            max_grade = max(metrics["risk_grade"])
            summary_rows.append(
                {
                    "run_id": run_id,
                    "source_layout_id": layout_id,
                    "layout_id": f"{layout_id}_{scenario_id.lower()}",
                    "scenario_id": scenario_id,
                    "severity": level["name"],
                    "track_event_type": event["event_type"],
                    "event_time_s": f"{event['run_time']:.3f}",
                    "event_pos_x": f"{event['pos_x']:.3f}",
                    "event_pos_z": f"{event['pos_z']:.3f}",
                    "event_steer_angle": f"{event['steer']:.3f}",
                    "event_lat_acc": f"{event['lat_acc']:.3f}",
                    "event_ltr_total": f"{event['ltr']:.3f}",
                    "max_roll_deg": f"{max(metrics['roll']):.3f}",
                    "max_lat_accel_g": f"{max(metrics['lat_g']):.3f}",
                    "max_abs_ltr": f"{max_ltr:.3f}",
                    "max_legacy_rollover_risk": f"{max(metrics['risk']):.3f}",
                    "wheel_lift_rows": str(sum(metrics["wheel_lift"])),
                    "ltr_risk_grade": str(max_grade),
                    "ltr_risk_label": ["safe", "watch", "caution", "danger", "critical"][max_grade],
                    "is_rollover_rows": "0",
                    "note": f"{scenario['description']} - 원본 이벤트 구간 기준 소폭 증폭",
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
    "event_ltr_total",
    "max_roll_deg",
    "max_lat_accel_g",
    "max_abs_ltr",
    "max_legacy_rollover_risk",
    "wheel_lift_rows",
    "ltr_risk_grade",
    "ltr_risk_label",
    "is_rollover_rows",
    "note",
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
    writer.writerows(event_output_rows)

for path in (OUTPUT_CSV, SUMMARY_CSV, EVENTS_CSV):
    write_meta(path)

print(OUTPUT_CSV)
print(SUMMARY_CSV)
print(EVENTS_CSV)
print(f"rows={len(output_rows)} cols={len(fieldnames)} runs={len(summary_rows)} events={len(event_output_rows)}")
