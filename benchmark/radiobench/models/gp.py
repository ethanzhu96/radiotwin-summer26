import numpy as np
from sklearn.gaussian_process import GaussianProcessRegressor
from sklearn.gaussian_process.kernels import ConstantKernel, Matern, WhiteKernel
from sklearn.preprocessing import StandardScaler

from .base import RadioModel


class GaussianProcessRadioModel(RadioModel):
    name = "Gaussian Process"

    def __init__(
        self,
        matern_nu: float = 1.5,
        n_restarts_optimizer: int = 5,
        random_state: int = 42,
    ):
        if matern_nu <= 0:
            raise ValueError("matern_nu must be positive.")
        if n_restarts_optimizer < 0:
            raise ValueError("n_restarts_optimizer cannot be negative.")
        self.matern_nu = matern_nu
        self.n_restarts_optimizer = n_restarts_optimizer
        self.random_state = random_state
        self.scaler: StandardScaler | None = None
        self.model: GaussianProcessRegressor | None = None

    @staticmethod
    def _positions(values, label: str) -> np.ndarray:
        positions = np.asarray(values, dtype=float)
        if positions.ndim != 2 or positions.shape[1] != 3:
            raise ValueError(f"{label} must have shape [N, 3]; got {positions.shape}.")
        if positions.shape[0] == 0:
            raise ValueError(f"{label} cannot be empty.")
        if not np.all(np.isfinite(positions)):
            raise ValueError(f"{label} contains NaN or infinite values.")
        return positions

    def fit(self, train_pos, train_rssi, ctx=None):
        positions = self._positions(train_pos, "train_pos")
        rssi = np.asarray(train_rssi, dtype=float)
        if rssi.ndim != 1 or rssi.shape[0] != positions.shape[0]:
            raise ValueError(
                "train_rssi must be one-dimensional and match the train_pos row count."
            )
        if not np.all(np.isfinite(rssi)):
            raise ValueError("train_rssi contains NaN or infinite values.")

        self.scaler = StandardScaler()
        scaled_positions = self.scaler.fit_transform(positions)
        kernel = ConstantKernel(1.0, (1e-3, 1e3)) * Matern(
            length_scale=np.ones(3),
            length_scale_bounds=(1e-2, 1e2),
            nu=self.matern_nu,
        ) + WhiteKernel(noise_level=1.0, noise_level_bounds=(1e-5, 1e2))
        self.model = GaussianProcessRegressor(
            kernel=kernel,
            normalize_y=True,
            n_restarts_optimizer=self.n_restarts_optimizer,
            random_state=self.random_state,
        )
        self.model.fit(scaled_positions, rssi)
        return self

    def predict(self, positions) -> np.ndarray:
        if self.scaler is None or self.model is None:
            raise RuntimeError("Gaussian Process model must be fit before prediction.")
        checked = self._positions(positions, "positions")
        return np.asarray(self.model.predict(self.scaler.transform(checked)), dtype=float)

    @property
    def learned_kernel(self):
        if self.model is None:
            raise RuntimeError("Gaussian Process model has not been fit.")
        return self.model.kernel_
