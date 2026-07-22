from pathlib import Path
from dataclasses import dataclass

import numpy as np
import trimesh


@dataclass(frozen=True)
class DominantPlane:
    plane_id: int
    normal: np.ndarray
    offset: float
    area_m2: float
    triangle_indices: np.ndarray


@dataclass(frozen=True)
class WedgeEdge:
    edge_id: int
    point_a: np.ndarray
    point_b: np.ndarray
    length_m: float


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

    @staticmethod
    def _canonical_plane(normal: np.ndarray, point: np.ndarray) -> tuple[np.ndarray, float]:
        unit = normal / np.linalg.norm(normal)
        significant = np.flatnonzero(np.abs(unit) > 1e-8)
        if significant.size and unit[significant[0]] < 0:
            unit = -unit
        return unit, float(np.dot(unit, point))

    def extract_dominant_planes(
        self,
        top_k: int = 10,
        normal_angle_tolerance_deg: float = 10.0,
        offset_tolerance_m: float = 0.10,
    ) -> list[DominantPlane]:
        if top_k < 1:
            raise ValueError("dominant_planes_k must be positive.")
        triangles = self.mesh.triangles
        normals = self.mesh.face_normals
        areas = self.mesh.area_faces
        order = np.argsort(areas)[::-1]
        clusters: list[dict] = []
        cosine_tolerance = np.cos(np.deg2rad(normal_angle_tolerance_deg))
        for face_index in order:
            area = float(areas[face_index])
            if area <= 1e-10:
                continue
            normal, offset = self._canonical_plane(
                normals[face_index], triangles[face_index, 0]
            )
            match = None
            for cluster in clusters:
                if (
                    float(np.dot(normal, cluster["normal"])) >= cosine_tolerance
                    and abs(offset - cluster["offset"]) <= offset_tolerance_m
                ):
                    match = cluster
                    break
            if match is None:
                clusters.append(
                    {"normal": normal.copy(), "offset": offset, "area": area,
                     "faces": [int(face_index)]}
                )
            else:
                old_area = match["area"]
                total_area = old_area + area
                averaged = match["normal"] * old_area + normal * area
                match["normal"] = averaged / np.linalg.norm(averaged)
                match["offset"] = (match["offset"] * old_area + offset * area) / total_area
                match["area"] = total_area
                match["faces"].append(int(face_index))
        clusters.sort(key=lambda cluster: cluster["area"], reverse=True)
        return [
            DominantPlane(
                plane_id=index,
                normal=np.asarray(cluster["normal"], dtype=float),
                offset=float(cluster["offset"]),
                area_m2=float(cluster["area"]),
                triangle_indices=np.asarray(cluster["faces"], dtype=int),
            )
            for index, cluster in enumerate(clusters[:top_k])
        ]

    def point_on_plane_surface(
        self, point, plane: DominantPlane, tolerance_m: float
    ) -> bool:
        candidate = np.asarray(point, dtype=float)
        triangles = self.mesh.triangles[plane.triangle_indices]
        repeated = np.repeat(candidate.reshape(1, 3), len(triangles), axis=0)
        closest = trimesh.triangles.closest_point(triangles, repeated)
        return bool(np.min(np.linalg.norm(closest - repeated, axis=1)) <= tolerance_m)

    def extract_wedge_edges(
        self,
        maximum_edges: int = 30,
        minimum_edge_length_m: float = 0.08,
        minimum_dihedral_deg: float = 25.0,
        include_boundary_edges: bool = True,
    ) -> list[WedgeEdge]:
        vertices = np.asarray(self.mesh.vertices)
        faces = np.asarray(self.mesh.faces)
        normals = np.asarray(self.mesh.face_normals)
        edge_faces: dict[tuple[int, int], list[int]] = {}
        for face_index, face in enumerate(faces):
            for first, second in ((face[0], face[1]), (face[1], face[2]), (face[2], face[0])):
                key = tuple(sorted((int(first), int(second))))
                edge_faces.setdefault(key, []).append(face_index)
        candidates: list[tuple[float, np.ndarray, np.ndarray]] = []
        for (first, second), adjacent in edge_faces.items():
            point_a = vertices[first]
            point_b = vertices[second]
            length = float(np.linalg.norm(point_b - point_a))
            if length < minimum_edge_length_m:
                continue
            valid = include_boundary_edges and len(adjacent) == 1
            if len(adjacent) >= 2:
                cosine = np.clip(abs(float(np.dot(normals[adjacent[0]], normals[adjacent[1]]))), 0, 1)
                angle = np.rad2deg(np.arccos(cosine))
                valid = angle >= minimum_dihedral_deg
            if valid:
                candidates.append((length, point_a.copy(), point_b.copy()))
        candidates.sort(key=lambda item: item[0], reverse=True)
        return [
            WedgeEdge(index, point_a, point_b, length)
            for index, (length, point_a, point_b) in enumerate(candidates[:maximum_edges])
        ]
