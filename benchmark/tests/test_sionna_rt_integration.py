import os
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np


BENCHMARK_DIR = Path(__file__).resolve().parents[1]
if str(BENCHMARK_DIR) not in sys.path:
    sys.path.insert(0, str(BENCHMARK_DIR))

from radiobench.models.sionna_rt import SIONNA_AVAILABLE, SionnaRTModel


@unittest.skipUnless(
    SIONNA_AVAILABLE and os.environ.get("RUN_SIONNA_INTEGRATION") == "1",
    "set RUN_SIONNA_INTEGRATION=1 with sionna-rt installed",
)
class SionnaIntegrationTests(unittest.TestCase):
    def test_tiny_scene_produces_finite_predictions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            mesh = root / "floor.obj"
            mesh.write_text(
                "v -5 -1 -5\nv 5 -1 -5\nv 5 -1 5\nv -5 -1 5\n"
                "f 1 2 3\nf 1 3 4\n",
                encoding="utf-8",
            )
            positions = np.asarray([[1.0, 0.0, 1.0], [2.0, 0.0, 1.0]])
            model = SionnaRTModel(
                {
                    "progress": False,
                    "samples_per_src": 1_000,
                    "search": {
                        "relative_permittivity": [2.0, 4.0],
                        "conductivity": [0.01, 0.1],
                        "coarse_epsilon_points": 2,
                        "coarse_sigma_points": 2,
                        "refinement_rounds": 0,
                        "refinement_points": 2,
                    },
                }
            )
            model.fit(
                positions,
                [-50.0, -56.0],
                ctx={
                    "frequency_hz": 5.6e9,
                    "mesh_path": mesh,
                    "cache_dir": root / "cache",
                    "tx": [0.0, 0.0, 1.0],
                },
            )
            predictions = model.predict(positions)
            self.assertEqual(predictions.shape, (2,))
            self.assertTrue(np.all(np.isfinite(predictions)))


if __name__ == "__main__":
    unittest.main()
