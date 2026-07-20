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


def format_metrics(label: str, result: MetricResult) -> str:
    return (
        f"{label}\n"
        + "-" * 72
        + "\nRMSE      MAE       Bias      MaxErr     Pearson r\n"
        + f"{result.rmse:8.2f}  {result.mae:8.2f}  {result.bias:8.2f}  "
        + f"{result.max_abs_error:8.2f}  {result.pearson_r:9.3f}"
    )
