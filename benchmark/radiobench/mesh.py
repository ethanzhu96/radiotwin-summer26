from pathlib import Path

import numpy as np
import trimesh


class SceneMesh:
    """Quest OBJ geometry preserved in Unity/MRUK room-local coordinates."""

    def __init__(self, mesh_path: str | Path, ray_epsilon_m: float = 0.01) -> None:
        path = Path(mesh_path)
        if not path.is_file():
            raise FileNotFoundError(f"Simplified RT room mesh does not exist: {path}")
        if not np.isfinite(ray_epsilon_m) or ray_epsilon_m < 0:
            raise ValueError("ray_epsilon_m must be finite and non-negative.")
        mesh, included_objects, excluded_objects = self._load_room_effect_meshes(path)
        if not isinstance(mesh, trimesh.Trimesh) or len(mesh.faces) == 0:
            raise ValueError(f"Room mesh contains no triangles: {path}")
        self.path = path.resolve()
        self.mesh = mesh
        self.ray_epsilon_m = float(ray_epsilon_m)
        self.included_object_count = included_objects
        self.excluded_object_count = excluded_objects

    @staticmethod
    def _load_room_effect_meshes(path: Path) -> tuple[trimesh.Trimesh, int, int]:
        """Load only MRUK EffectMesh OBJ objects, excluding runtime visualizers."""
        vertices: list[list[float]] = []
        faces: list[list[int]] = []
        include_current = False
        included_objects = 0
        excluded_objects = 0
        with path.open("r", encoding="utf-8", errors="replace") as stream:
            for raw_line in stream:
                line = raw_line.strip()
                if line.startswith("o "):
                    name = line[2:].strip()
                    include_current = "EffectMesh" in name
                    if include_current:
                        included_objects += 1
                    else:
                        excluded_objects += 1
                elif line.startswith("v "):
                    parts = line.split()
                    if len(parts) >= 4:
                        vertices.append([float(parts[1]), float(parts[2]), float(parts[3])])
                elif include_current and line.startswith("f "):
                    tokens = line.split()[1:]
                    if len(tokens) != 3:
                        continue
                    face: list[int] = []
                    for token in tokens:
                        raw_index = int(token.split("/", 1)[0])
                        face.append(raw_index - 1 if raw_index > 0 else len(vertices) + raw_index)
                    faces.append(face)
        if not vertices or not faces:
            raise ValueError(
                f"Room mesh has no EffectMesh triangle objects after filtering: {path}"
            )
        vertex_array = np.asarray(vertices, dtype=float)
        face_array = np.asarray(faces, dtype=np.int64)
        referenced = np.unique(face_array)
        compact_faces = np.searchsorted(referenced, face_array)
        mesh = trimesh.Trimesh(
            vertices=vertex_array[referenced],
            faces=compact_faces,
            process=False,
        )
        return mesh, included_objects, excluded_objects

    @property
    def bounds(self) -> np.ndarray:
        return np.asarray(self.mesh.bounds, dtype=float)

    @property
    def vertex_count(self) -> int:
        return int(len(self.mesh.vertices))

    @property
    def face_count(self) -> int:
        return int(len(self.mesh.faces))

    def is_blocked(self, start, end) -> bool:
        return bool(self.blocked_mask(start, np.asarray(end, dtype=float).reshape(1, 3))[0])

    def blocked_mask(self, start, ends) -> np.ndarray:
        start_point = np.asarray(start, dtype=float)
        points = np.asarray(ends, dtype=float)
        if start_point.shape != (3,) or not np.all(np.isfinite(start_point)):
            raise ValueError("Ray start must contain three finite coordinates.")
        if points.ndim != 2 or points.shape[1] != 3:
            raise ValueError("Ray destinations must have shape [N, 3].")
        if not np.all(np.isfinite(points)):
            raise ValueError("Ray destinations must be finite.")
        blocked = np.zeros(len(points), dtype=bool)
        if len(points) == 0:
            return blocked
        deltas = points - start_point
        distances = np.linalg.norm(deltas, axis=1)
        valid_indices = np.flatnonzero(distances >= 1e-9)
        if valid_indices.size == 0:
            return blocked
        valid_distances = distances[valid_indices]
        directions = deltas[valid_indices] / valid_distances[:, None]
        epsilons = np.minimum(self.ray_epsilon_m, valid_distances * 0.2)
        origins = start_point + directions * epsilons[:, None]
        query_lengths = valid_distances - 2.0 * epsilons
        locations, ray_indices, _ = self.mesh.ray.intersects_location(
            ray_origins=origins,
            ray_directions=directions,
            multiple_hits=False,
        )
        if len(locations):
            hit_distances = np.linalg.norm(locations - origins[ray_indices], axis=1)
            blocked[valid_indices[ray_indices]] = hit_distances < query_lengths[ray_indices]
        return blocked
