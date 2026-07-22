import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np


BENCHMARK_DIR = Path(__file__).resolve().parents[1]
if str(BENCHMARK_DIR) not in sys.path:
    sys.path.insert(0, str(BENCHMARK_DIR))

from radiobench.models.sionna_rt import (
    SIONNA_AVAILABLE,
    SionnaRTModel,
    analytic_offset_and_rmse,
    prepare_sionna_obj,
    unity_to_sionna,
)


class SionnaHelpersTests(unittest.TestCase):
    def test_coordinate_transform_single_and_batch(self):
        np.testing.assert_array_equal(unity_to_sionna([1, 2, 3]), [1, 3, 2])
        np.testing.assert_array_equal(
            unity_to_sionna([[1, 2, 3], [4, 5, 6]]),
            [[1, 3, 2], [4, 6, 5]],
        )

    def test_mesh_transform_reverses_winding_and_preserves_source(self):
        original = "v 0 0 0\nv 1 0 0\nv 0 1 0\nvn 0 0 1\nf 1//1 2//1 3//1\n"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "triangle.obj"
            source.write_text(original, encoding="utf-8")
            transformed, mesh_hash = prepare_sionna_obj(source, root / "cache")
            result = transformed.read_text(encoding="utf-8")
            self.assertEqual(source.read_text(encoding="utf-8"), original)
            self.assertIn("v 0 0 1", result)
            self.assertIn("vn 0 1 0", result)
            self.assertIn("f 3//1 2//1 1//1", result)
            self.assertEqual(len(mesh_hash), 64)
            self.assertEqual(prepare_sionna_obj(source, root / "cache")[0], transformed)

    def test_mesh_transform_excludes_non_room_visualization_faces(self):
        source_text = (
            "o M0_RSSI_POINT_1_-50\n"
            "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"
            "o WALL_FACE_EffectMesh\n"
            "v 0 0 1\nv 1 0 1\nv 0 1 1\nf 4 5 6\n"
        )
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "mixed.obj"
            source.write_text(source_text, encoding="utf-8")
            transformed, _ = prepare_sionna_obj(source, root / "cache")
            faces = [
                line for line in transformed.read_text(encoding="utf-8").splitlines()
                if line.startswith("f ")
            ]
            self.assertEqual(faces, ["f 6 5 4"])

    def test_analytic_offset(self):
        offset, rmse = analytic_offset_and_rmse(
            [-50, -60, -70], [-80, -90, -100]
        )
        self.assertAlmostEqual(offset, 30.0)
        self.assertAlmostEqual(rmse, 0.0)

    def test_mocked_calibration_and_fit_predict_contract(self):
        calls = []

        def simulator(positions_sionna, epsilon, sigma):
            calls.append((positions_sionna.copy(), epsilon, sigma))
            spatial = positions_sionna[:, 0] * epsilon
            return spatial - 100.0, np.zeros(len(spatial), dtype=bool)

        config = {
            "progress": False,
            "calibration_max_points": 3,
            "search": {
                "relative_permittivity": [2.0, 4.0],
                "conductivity": [0.1, 1.0],
                "coarse_epsilon_points": 3,
                "coarse_sigma_points": 2,
                "refinement_rounds": 0,
                "refinement_points": 2,
            },
        }
        positions = np.column_stack((np.arange(5.0), np.zeros(5), np.ones(5)))
        measured = positions[:, 0] * 4.0 - 50.0
        model = SionnaRTModel(config).fit(
            positions,
            measured,
            ctx={"frequency_hz": 5.6e9, "simulator": simulator},
        )
        self.assertEqual(model.best_relative_permittivity, 4.0)
        self.assertEqual(model.calibration_indices.tolist(), [0, 2, 4])
        predictions = model.predict(positions[:2])
        self.assertEqual(predictions.shape, (2,))
        self.assertTrue(np.all(np.isfinite(predictions)))
        self.assertTrue(calls)

    def test_optional_dependency_error_is_actionable(self):
        if SIONNA_AVAILABLE:
            self.skipTest("Sionna is installed in this environment")
        with self.assertRaisesRegex(RuntimeError, "pip install sionna-rt"):
            SionnaRTModel({"progress": False}).fit(
                np.zeros((2, 3)),
                np.asarray([-50.0, -51.0]),
                ctx={"frequency_hz": 5.6e9, "mesh_path": "missing.obj", "tx": [0, 0, 0]},
            )


if __name__ == "__main__":
    unittest.main()
