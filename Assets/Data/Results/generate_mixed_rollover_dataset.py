import csv
import math
import os
import uuid
from collections import defaultdict


BASE_DIR = "/Users/kimgyuri/Desktop/coding/unity/roadTest/road_iso/Assets/Data/Results"
SOURCE_CSV = os.path.join(BASE_DIR, "combined_timeseries_20260703_223624.csv")
OUTPUT_CSV = os.path.join(BASE_DIR, "combined_timeseries_mixed_rollover_20260704.csv")
SUMMARY_CSV = os.path.join(BASE_DIR, "mixed_rollover_summary_20260704.csv")


def num(row, key, default=0.0):
    try:
        return float(row[key])
    except Exception:
        return default


def clamp(value, low, high):
    return max(low, min(high, value))


def fmt(value):
    return f"{float(value):.6f}"


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


with open(SOURCE_CSV, newline="") as source_file:
    reader = csv.DictReader(source_file)
    fieldnames = reader.fieldnames
    source_rows = [row for row in reader if row.get("LayoutID") == "case01_boxes_2layer"]

if not source_rows:
    raise RuntimeError("No source rows found for case01_boxes_2layer")

source_rows = source_rows[:4938]
BASE_MASS_KG = 3500.0

families = {
    "HAZMAT_UNSECURED": {
        "cargo_count": 8,
        "mass": 420.0,
        "secured": 0.20,
        "friction": "WET",
        "road": "FLAT",
        "bank": 0.0,
        "slope": 0.0,
        "cog_x": 1.20,
        "cog_z": -0.65,
        "cog_h": 1.55,
        "description": "위험물/액체성 적재물 비고정",
        "sign": 1.0,
    },
    "OVERWEIGHT_LOAD": {
        "cargo_count": 12,
        "mass": 1450.0,
        "secured": 0.85,
        "friction": "DRY",
        "road": "FLAT",
        "bank": 0.0,
        "slope": 1.5,
        "cog_x": 0.25,
        "cog_z": -0.20,
        "cog_h": 1.20,
        "description": "과적/고중량 적재",
        "sign": 1.0,
    },
    "WRONG_COG_LEFT_HIGH": {
        "cargo_count": 7,
        "mass": 720.0,
        "secured": 0.55,
        "friction": "DRY",
        "road": "BANKED",
        "bank": 3.5,
        "slope": 0.0,
        "cog_x": -1.85,
        "cog_z": 0.45,
        "cog_h": 2.35,
        "description": "좌측 편중/높은 무게중심",
        "sign": -1.0,
    },
}

levels = [
    {
        "name": "mild",
        "ltr_amp": 0.18,
        "lat_amp": 1.12,
        "roll_amp": 4.0,
        "slip_mult": 1.10,
        "risk_gain": 0.45,
        "rollover": False,
        "partial": False,
        "peak_time": 10.8,
        "peak_width": 3.8,
    },
    {
        "name": "moderate",
        "ltr_amp": 0.34,
        "lat_amp": 1.34,
        "roll_amp": 8.0,
        "slip_mult": 1.45,
        "risk_gain": 0.65,
        "rollover": False,
        "partial": False,
        "peak_time": 11.5,
        "peak_width": 3.2,
    },
    {
        "name": "near_miss",
        "ltr_amp": 0.58,
        "lat_amp": 1.68,
        "roll_amp": 18.0,
        "slip_mult": 1.95,
        "risk_gain": 0.90,
        "rollover": False,
        "partial": False,
        "peak_time": 12.0,
        "peak_width": 2.7,
    },
    {
        "name": "partial_rollover",
        "ltr_amp": 0.76,
        "lat_amp": 1.95,
        "roll_amp": 48.0,
        "slip_mult": 2.35,
        "risk_gain": 1.00,
        "rollover": True,
        "partial": True,
        "peak_time": 9.8,
        "peak_width": 2.2,
    },
    {
        "name": "rollover",
        "ltr_amp": 0.88,
        "lat_amp": 2.25,
        "roll_amp": 96.0,
        "slip_mult": 2.85,
        "risk_gain": 1.00,
        "rollover": True,
        "partial": False,
        "peak_time": 9.2,
        "peak_width": 2.0,
    },
]

family_adjustments = {
    "HAZMAT_UNSECURED": {"ltr": 0.00, "slip": 0.35, "roll": 0.80, "lat": 0.90, "mass": 1.0, "base_ltr_weight": 0.55},
    "OVERWEIGHT_LOAD": {"ltr": -0.06, "slip": -0.10, "roll": 0.65, "lat": 0.82, "mass": 1.15, "base_ltr_weight": 0.55},
    "WRONG_COG_LEFT_HIGH": {"ltr": 0.32, "slip": 0.05, "roll": 1.10, "lat": 1.00, "mass": 1.0, "base_ltr_weight": 0.15},
}

output_rows = []
summary_rows = []

for scenario_id, family in families.items():
    family_adj = family_adjustments[scenario_id]
    for level_index, level in enumerate(levels, start=1):
        run_id = f"20260704_{scenario_id.lower()}_{level['name']}"
        layout_id = f"{scenario_id.lower()}_{level['name']}"
        metrics = defaultdict(list)
        rollover_started = False
        rollover_window_start = level["peak_time"] + (0.45 if level["partial"] else -0.10)
        rollover_window_end = level["peak_time"] + (2.00 if level["partial"] else 999.0)

        for sample_index, source_row in enumerate(source_rows):
            row = dict(source_row)
            t = num(source_row, "RunTime")
            phase = math.sin(t * 0.84) + 0.35 * math.sin(t * 1.74 + 0.65)
            peak = math.exp(-((t - level["peak_time"]) / level["peak_width"]) ** 2)
            late_peak = math.exp(-((t - level["peak_time"] - 1.8) / (level["peak_width"] * 1.25)) ** 2)
            sign = family["sign"]

            ltr_target = (
                num(source_row, "LTR_Total") * family_adj["base_ltr_weight"]
                + sign * (level["ltr_amp"] + family_adj["ltr"]) * peak
                + sign * 0.07 * phase * late_peak
            )
            if level["rollover"]:
                ltr_target += sign * 0.12 * late_peak
            ltr_target = clamp(ltr_target, -1.0, 1.0)
            ltr_front = clamp(ltr_target * 1.10 + sign * 0.04 * math.sin(t * 1.15), -1.0, 1.0)
            ltr_rear = clamp(ltr_target * 0.90 + sign * 0.03 * math.cos(t * 0.90), -1.0, 1.0)

            original_cargo_mass = num(source_row, "TotalMassKg", 213.5)
            mass_scale = (
                (BASE_MASS_KG + family["mass"] * family_adj["mass"])
                / (BASE_MASS_KG + original_cargo_mass)
            )
            total_normal_force = (
                num(source_row, "FL_NormalForce")
                + num(source_row, "FR_NormalForce")
                + num(source_row, "RL_NormalForce")
                + num(source_row, "RR_NormalForce")
            ) * mass_scale * (1.0 + 0.06 * peak)
            front_ratio = clamp(
                num(source_row, "FrontNormalForce")
                / (num(source_row, "FrontNormalForce") + num(source_row, "RearNormalForce") + 1e-9)
                + 0.025 * math.sin(t * 0.35),
                0.30,
                0.70,
            )
            front_force = total_normal_force * front_ratio
            rear_force = total_normal_force - front_force
            fl = max(0.0, front_force * (1.0 - ltr_front) / 2.0)
            fr = max(0.0, front_force * (1.0 + ltr_front) / 2.0)
            rl = max(0.0, rear_force * (1.0 - ltr_rear) / 2.0)
            rr = max(0.0, rear_force * (1.0 + ltr_rear) / 2.0)

            lift_threshold = 0.88 if level["rollover"] else 0.92
            if abs(ltr_target) > lift_threshold:
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
                num(source_row, "LatAcc") * level["lat_amp"] * family_adj["lat"]
                + sign * (1.35 + 1.65 * level["ltr_amp"]) * peak
                + sign * 0.24 * phase * late_peak
            )
            long_acc = num(source_row, "LongAcc") * (1.0 + 0.08 * peak)
            vert_acc = num(source_row, "VertAcc") + 0.42 * peak
            roll = num(source_row, "Roll") + sign * level["roll_amp"] * family_adj["roll"] * peak + sign * 0.55 * phase * late_peak
            if level["rollover"]:
                roll_transition = 88.0 / (1.0 + math.exp(-(t - level["peak_time"]) * 1.35))
                if level["partial"]:
                    recovery = 1.0 / (1.0 + math.exp(-(t - rollover_window_end) * 1.8))
                    roll += sign * (roll_transition * (1.0 - 0.72 * recovery))
                else:
                    roll += sign * roll_transition
            pitch = num(source_row, "Pitch") + 1.6 * peak
            roll_rate = num(source_row, "RollRate") + sign * level["roll_amp"] * (peak - late_peak) / max(level["peak_width"], 1.0)
            pitch_rate = num(source_row, "PitchRate") + 0.8 * (peak - late_peak)
            yaw_rate = num(source_row, "YawRate") * (1.0 + 0.22 * peak)

            max_side_slip = (
                abs(num(source_row, "MaxSideSlip")) * (level["slip_mult"] + family_adj["slip"])
                + 0.035 * peak
                + (0.05 if family["friction"] == "WET" else 0.0) * late_peak
            )
            max_forward_slip = abs(num(source_row, "MaxForwardSlip")) * (1.0 + 0.35 * level["slip_mult"] * peak) + 0.06 * peak
            rollover_condition = level["rollover"] and rollover_window_start <= t <= rollover_window_end
            if rollover_condition and (abs(roll) > 48.0 or abs(ltr_total) > 0.94):
                rollover_started = True
            if level["partial"] and t > rollover_window_end:
                rollover_started = False
            is_rollover = 1 if rollover_started else 0

            left_lift = 1 if fl < 250.0 or rl < 250.0 else 0
            right_lift = 1 if fr < 250.0 or rr < 250.0 else 0
            any_lift = 1 if left_lift or right_lift else 0
            risk = clamp((abs(ltr_total) - 0.55) / 0.45 * level["risk_gain"] + max_side_slip * 0.12, 0.0, 1.0)

            row.update(
                {
                    "DatasetVersion": "V1.2_SYNTHETIC_MIXED_ROLLOVER",
                    "LayoutID": layout_id,
                    "RunID": run_id,
                    "ScenarioID": scenario_id,
                    "SampleIndex": str(sample_index),
                    "TargetSpeedKmh": "45.000000",
                    "FrictionCondition": family["friction"],
                    "RoadCondition": family["road"],
                    "RoadBankAngleDeg": fmt(family["bank"]),
                    "RoadSlopeDeg": fmt(family["slope"]),
                    "VehicleBaseMassKg": fmt(BASE_MASS_KG),
                    "CargoCount": str(family["cargo_count"]),
                    "TotalMassKg": fmt(family["mass"]),
                    "SecuredFrac": fmt(family["secured"]),
                    "SpeedKmh": fmt(max(0.0, num(source_row, "SpeedKmh") * (0.97 if family["mass"] > 1000 else 1.0) - (1.8 * peak if level["rollover"] else 0.0))),
                    "AccX": fmt(num(source_row, "AccX") * (1.0 + 0.10 * peak)),
                    "AccY": fmt(lat_acc),
                    "AccZ": fmt(vert_acc),
                    "LongAcc": fmt(long_acc),
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
                    "AngAccX": fmt(num(source_row, "AngAccX") * (1.0 + 0.25 * peak)),
                    "AngAccY": fmt(num(source_row, "AngAccY") * (1.0 + 0.20 * peak)),
                    "AngAccZ": fmt(num(source_row, "AngAccZ") * (1.0 + 0.20 * peak)),
                    "Throttle": fmt(clamp(num(source_row, "Throttle") - (0.10 * peak if level["rollover"] else 0.015 * peak), 0.0, 1.0)),
                    "Brake": fmt(clamp(num(source_row, "Brake") + (0.24 * peak if level["rollover"] else 0.04 * peak), 0.0, 1.0)),
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
                    "FL_ForwardSlip": fmt(num(source_row, "FL_ForwardSlip") * (1.0 + 0.28 * level["slip_mult"] * peak)),
                    "FR_ForwardSlip": fmt(num(source_row, "FR_ForwardSlip") * (1.0 + 0.28 * level["slip_mult"] * peak)),
                    "RL_ForwardSlip": fmt(num(source_row, "RL_ForwardSlip") * (1.0 + 0.36 * level["slip_mult"] * peak)),
                    "RR_ForwardSlip": fmt(num(source_row, "RR_ForwardSlip") * (1.0 + 0.36 * level["slip_mult"] * peak)),
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
            metrics["shift"].append((1.0 - family["secured"]) * max_side_slip * 18.0 + (2.7 * peak if level["rollover"] else 1.1 * peak))
            metrics["rollover_rows"].append(is_rollover)
            metrics["wheel_lift_rows"].append(any_lift)

        max_ltr = max(metrics["ltr"])
        rollover_rows = sum(metrics["rollover_rows"])
        risk_grade = 3 if rollover_rows > 0 or max_ltr >= 0.92 else 2 if max_ltr >= 0.72 else 1
        summary_rows.append(
            {
                "run_id": run_id,
                "scenario_id": scenario_id,
                "severity": level["name"],
                "source_layout": f"{layout_id}.json",
                "cargo_count": family["cargo_count"],
                "total_mass_kg": f"{family['mass']:.1f}",
                "secured_frac": f"{family['secured']:.2f}",
                "init_cog_x": f"{family['cog_x']:.3f}",
                "init_cog_z": f"{family['cog_z']:.3f}",
                "init_cog_height": f"{family['cog_h']:.3f}",
                "target_speed_kmh": "45",
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
                "risk_cause": f"{family['description']} - {level['name']} 강도",
            }
        )

with open(OUTPUT_CSV, "w", newline="") as output_file:
    writer = csv.DictWriter(output_file, fieldnames=fieldnames)
    writer.writeheader()
    writer.writerows(output_rows)

summary_fields = [
    "run_id",
    "scenario_id",
    "severity",
    "source_layout",
    "cargo_count",
    "total_mass_kg",
    "secured_frac",
    "init_cog_x",
    "init_cog_z",
    "init_cog_height",
    "target_speed_kmh",
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

write_meta(OUTPUT_CSV)
write_meta(SUMMARY_CSV)

print(OUTPUT_CSV)
print(SUMMARY_CSV)
print(f"rows={len(output_rows)} cols={len(fieldnames)} runs={len(summary_rows)}")
