class PlatformError(Exception):
    """Base error for expected platform failures."""


class ConfigurationError(PlatformError):
    pass


class AdmissionError(PlatformError):
    pass


class AcquisitionError(PlatformError):
    pass


class IntegrityError(PlatformError):
    pass
