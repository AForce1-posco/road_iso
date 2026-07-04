"""Minimal Gym wrapper around a single-Behavior, single-Agent Unity environment.

Only supports a single discrete action branch and a single observation stream,
since that's what DQN needs (DQN requires a discrete action space). Uses the
low-level mlagents_envs API directly instead of the official gym_unity package,
because gym_unity==0.28.0 hard-pins gym==0.20.0 which conflicts with
stable-baselines3's gym==0.21 requirement.
"""

import numpy as np
import gym
from gym import spaces
from mlagents_envs.environment import UnityEnvironment
from mlagents_envs.base_env import ActionTuple


class UnitySingleAgentGymWrapper(gym.Env):
    def __init__(self, file_name=None, worker_id=0, base_port=5005, seed=0, no_graphics=False):
        super().__init__()
        self._env = UnityEnvironment(
            file_name=file_name,
            worker_id=worker_id,
            base_port=base_port,
            seed=seed,
            no_graphics=no_graphics,
        )
        self._env.reset()

        self.behavior_name = list(self._env.behavior_specs.keys())[0]
        spec = self._env.behavior_specs[self.behavior_name]

        action_spec = spec.action_spec
        if action_spec.continuous_size > 0 or len(action_spec.discrete_branches) != 1:
            raise ValueError(
                "UnitySingleAgentGymWrapper only supports a single discrete action "
                f"branch (DQN requirement). Got behavior spec: {action_spec}. "
                "Set the Agent's Behavior Parameters to a single discrete branch."
            )
        self.action_space = spaces.Discrete(action_spec.discrete_branches[0])

        obs_specs = spec.observation_specs
        if len(obs_specs) != 1:
            raise ValueError(
                "UnitySingleAgentGymWrapper only supports a single observation "
                f"stream. Got {len(obs_specs)} observation specs."
            )
        self.observation_space = spaces.Box(
            low=-np.inf, high=np.inf, shape=tuple(obs_specs[0].shape), dtype=np.float32
        )
        self._agent_id = None

    def reset(self):
        self._env.reset()
        decision_steps, _ = self._env.get_steps(self.behavior_name)
        self._agent_id = decision_steps.agent_id[0]
        return decision_steps[self._agent_id].obs[0].astype(np.float32)

    def step(self, action):
        action_tuple = ActionTuple()
        action_tuple.add_discrete(np.array([[action]], dtype=np.int32))
        self._env.set_actions(self.behavior_name, action_tuple)
        self._env.step()

        decision_steps, terminal_steps = self._env.get_steps(self.behavior_name)

        if self._agent_id in terminal_steps:
            step = terminal_steps[self._agent_id]
            done = True
        else:
            step = decision_steps[self._agent_id]
            done = False

        obs = step.obs[0].astype(np.float32)
        reward = float(step.reward)
        return obs, reward, done, {}

    def close(self):
        self._env.close()

    def seed(self, seed=None):
        pass
