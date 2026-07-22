import sys
from pathlib import Path
import unittest

import numpy as np
import trimesh
import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from radiobench.mesh import DominantPlane, SceneMesh
from radiobench.models.simple_rt import FullSimpleRTModel, PropagationPath


class AlwaysOnSurfaceMesh:
    @staticmethod
    def point_on_plane_surface(point, plane, tolerance):
        return True


class FullSimpleRTGeometryTests(unittest.TestCase):
    def test_mirror_and_line_plane_intersection(self):
        mirrored = FullSimpleRTModel.mirror_point(
            [1, 2, 3], np.array([1.0, 0, 0]), 0.0
        )
        np.testing.assert_allclose(mirrored, [-1, 2, 3])
        intersection = FullSimpleRTModel.line_plane_intersection(
            [-1, 2, 0], [1, 0, 0], np.array([1.0, 0, 0]), 0.0
        )
        np.testing.assert_allclose(intersection, [0, 1, 0])

    def test_valid_one_and_two_bounce_geometry(self):
        model = FullSimpleRTModel({})
        model.mesh = AlwaysOnSurfaceMesh()
        model.tx = np.array([1.0, 1.0, 0.0])
        plane_x0 = DominantPlane(0, np.array([1.0, 0, 0]), 0.0, 1.0, np.array([0]))
        one = model._one_bounce_point(np.array([1.0, -1.0, 0.0]), plane_x0)
        np.testing.assert_allclose(one, [0, 0, 0])

        plane_x3 = DominantPlane(1, np.array([1.0, 0, 0]), 3.0, 1.0, np.array([1]))
        two = model._two_bounce_points(
            np.array([2.0, 2.0, 0.0]), plane_x0, plane_x3
        )
        self.assertIsNotNone(two)
        np.testing.assert_allclose(two[0][0], 0.0)
        np.testing.assert_allclose(two[1][0], 3.0)

    def test_segment_blocking_and_wedge_extraction(self):
        scene = SceneMesh.__new__(SceneMesh)
        scene.mesh = trimesh.Trimesh(
            vertices=np.array([[1, -1, -1], [1, 1, -1], [1, 0, 1]], dtype=float),
            faces=np.array([[0, 1, 2]]),
            process=False,
        )
        scene.ray_epsilon_m = 0.01
        self.assertFalse(scene.is_blocked([0, 0, 0], [0.5, 0, 0]))
        self.assertTrue(scene.is_blocked([0, 0, 0], [2, 0, 0]))

        scene.mesh = trimesh.creation.box(extents=[2, 2, 2])
        self.assertGreater(len(scene.extract_wedge_edges(maximum_edges=30)), 0)

    def test_linear_power_sum_and_no_path_nan(self):
        model = FullSimpleRTModel({})
        paths = [
            PropagationPath("los", 1.0, 0, 0, np.zeros((2, 3))),
            PropagationPath("reflection_1", 1.0, 1, 0, np.zeros((3, 3))),
            PropagationPath("reflection_2", 1.0, 2, 0, np.zeros((4, 3))),
            PropagationPath("diffraction", 1.0, 0, 1, np.zeros((3, 3))),
        ]
        result = model._base_predictions([paths, []], [2.0, 0.5, 10.0])
        expected_power = 1.0 + 0.5**2 + 0.5**4 + 0.1
        self.assertAlmostEqual(result[0], 10 * np.log10(expected_power))
        self.assertTrue(np.isnan(result[1]))

    def test_torch_gain_graph_backpropagates(self):
        model = FullSimpleRTModel({})
        paths = [[
            PropagationPath("reflection_1", 2.0, 1, 0, np.zeros((3, 3))),
            PropagationPath("diffraction", 2.5, 0, 1, np.zeros((3, 3))),
        ]]
        tensors = model._path_tensors(paths)
        parameters = [torch.tensor(0.0, dtype=torch.float64, requires_grad=True) for _ in range(4)]
        prediction, _, _, _, _ = model._torch_predictions(
            tensors, 1, parameters[0], parameters[1], parameters[2], parameters[3]
        )
        prediction.sum().backward()
        for parameter in parameters:
            self.assertIsNotNone(parameter.grad)
            self.assertTrue(torch.isfinite(parameter.grad))


if __name__ == "__main__":
    unittest.main()
