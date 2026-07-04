"""Train a DQN agent against the road_iso Unity environment.

Usage:
    conda activate mlagents-r19
    cd /home/piai/Unity/road_iso/python_trainer
    python train_dqn.py --run-id dqn_run1
    # then switch to the Unity Editor and press Play

To train against a built standalone executable instead of the Editor, pass
--env-path pointing at the built binary (no need to press Play in that case).
"""

import argparse

from stable_baselines3 import DQN
from stable_baselines3.common.monitor import Monitor

from unity_gym_env import UnitySingleAgentGymWrapper


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--env-path",
        default=None,
        help="Path to a built Unity executable. Omit to connect to the Editor (press Play when prompted).",
    )
    parser.add_argument("--run-id", default="dqn_run")
    parser.add_argument("--total-steps", type=int, default=200_000)
    parser.add_argument("--no-graphics", action="store_true")
    args = parser.parse_args()

    env = UnitySingleAgentGymWrapper(file_name=args.env_path, no_graphics=args.no_graphics)
    env = Monitor(env)

    model = DQN(
        "MlpPolicy",
        env,
        verbose=1,
        learning_rate=1e-4,
        buffer_size=50_000,
        learning_starts=1000,
        batch_size=64,
        gamma=0.99,
        train_freq=4,
        target_update_interval=1000,
        exploration_fraction=0.1,
        exploration_final_eps=0.05,
        tensorboard_log=f"../results/{args.run_id}/tensorboard",
    )
    model.learn(total_timesteps=args.total_steps)
    model.save(f"../results/{args.run_id}/model")
    env.close()


if __name__ == "__main__":
    main()
