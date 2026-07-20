from dataclasses import dataclass
import warnings

import numpy as np


@dataclass(frozen=True)
class MetricResult:
    rmse: float
    mae: float
    bias: float
    max_abs_error: float
    pearson_r: float


@dataclass(frozen=True)
class CoveredMetricResult:
    metrics: MetricResult
    valid_count: int
    total_count: int

    @property
    def coverage(self) -> float:
        return self.valid_count / self.total_count if self.total_count else 0.0


def calculate_metrics(y_true, y_pred) -> MetricResult:
    true = np.asarray(y_true, dtype=float)
    predicted = np.asarray(y_pred, dtype=float)
    if true.ndim != 1 or predicted.ndim != 1:
        raise ValueError("Metric inputs must be one-dimensional.")
    if true.shape != predicted.shape:
        raise ValueError(
            f"Metric input shapes must match; got {true.shape} and {predicted.shape}."
        )
    if true.size == 0:
        raise ValueError("Metric inputs cannot be empty.")
    if not np.all(np.isfinite(true)) or not np.all(np.isfinite(predicted)):
        raise ValueError("Metric inputs contain NaN or infinite values.")

    errors = predicted - true
    if true.size < 2 or np.isclose(np.std(true), 0.0) or np.isclose(np.std(predicted), 0.0):
        warnings.warn(
            "Pearson correlation is undefined because an input has insufficient variance.",
            stacklevel=2,
        )
        pearson_r = float("nan")
    else:
        pearson_r = float(np.corrcoef(true, predicted)[0, 1])

    return MetricResult(
        rmse=float(np.sqrt(np.mean(errors**2))),
        mae=float(np.mean(np.abs(errors))),
        bias=float(np.mean(errors)),
        max_abs_error=float(np.max(np.abs(errors))),
        pearson_r=pearson_r,
    )


def calculate_covered_metrics(y_true, y_pred) -> CoveredMetricResult:
    true = np.asarray(y_true, dtype=float)
    predicted = np.asarray(y_pred, dtype=float)
    if true.ndim != 1 or predicted.ndim != 1 or true.shape != predicted.shape:
        raise ValueError("Covered metric inputs must be matching one-dimensional arrays.")
    finite = np.isfinite(true) & np.isfinite(predicted)
    valid_count = int(finite.sum())
    if valid_count == 0:
        nan_metrics = MetricResult(*(float("nan"),) * 5)
        return CoveredMetricResult(nan_metrics, 0, int(true.size))
    return CoveredMetricResult(
        calculate_metrics(true[finite], predicted[finite]),
        valid_count,
        int(true.size),
    )


def format_metrics(label: str, result: MetricResult) -> str:
    return (
        f"{label}\n"
        + "-" * 72
        + "\nRMSE      MAE       Bias      MaxErr     Pearson r\n"
        + f"{result.rmse:8.2f}  {result.mae:8.2f}  {result.bias:8.2f}  "
        + f"{result.max_abs_error:8.2f}  {result.pearson_r:9.3f}"
    )
