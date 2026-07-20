from abc import ABC, abstractmethod


class RadioModel(ABC):
    """Minimal common interface for radio-field predictors."""

    name = "base"

    @abstractmethod
    def fit(self, train_pos, train_rssi, ctx=None):
        raise NotImplementedError

    @abstractmethod
    def predict(self, positions):
        raise NotImplementedError
